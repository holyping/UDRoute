using System.Buffers;
using System.Net;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 3. 核心引擎 (P, C, S 混合调度，共用单端口Socket)
    // ==========================================
    public class RouteEngine : IDisposable
    {
        private readonly AppConfig _config;
        private ZeroCopyUdpSocket? _udp;
        private ProxyMode? _proxy;
        private ServerMode? _server;
        private ClientMode? _client;

        public RouteEngine(AppConfig config)
        {
            _config = config;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _udp = new ZeroCopyUdpSocket(_config.Port);
            var boundEp = _udp.LocalEndPoint;
            Log.Info($"[RouteEngine] Core UDP socket bound to {boundEp}");

            var tasks = new List<Task>();

            if (_config.EnableProxy)
            {
                _proxy = new ProxyMode(_config, _udp);
                tasks.Add(_proxy.RunAsync(ct));
                Log.Info($"[P] Proxy running on UDP {_proxy.Port}");
            }

            if (_config.ServerRecords.Count > 0)
            {
                _server = new ServerMode(_config, _udp, _proxy); // 传入_proxy以支持 @this 优化模式
                tasks.Add(_server.RunAsync(ct));
                Log.Info($"[S] Server mode active. DevId: {_config.DevId}");
            }

            if (_config.ClientRecords.Count > 0)
            {
                _client = new ClientMode(_config, _udp, _proxy);
                tasks.Add(_client.RunAsync(ct));
                Log.Info($"[C] Client mode active.");
            }

            // 核心 UDP 接收与分发循环
            tasks.Add(ReceiveUdpLoopAsync(ct));
            tasks.Add(StartStatusServerAsync(ct));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // 吞掉正常退出时的取消异常
            }
        }

        private async Task StartStatusServerAsync(CancellationToken ct)
        {
            string pipeName = $"udroute_ctrl_{Environment.ProcessId}";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipeServer = new System.IO.Pipes.NamedPipeServerStream(
                        pipeName, 
                        System.IO.Pipes.PipeDirection.Out, 
                        1, 
                        System.IO.Pipes.PipeTransmissionMode.Byte, 
                        System.IO.Pipes.PipeOptions.Asynchronous);
                    
                    await pipeServer.WaitForConnectionAsync(ct);

                    string statusStr = GetAppStatusString();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(statusStr);
                    
                    await pipeServer.WriteAsync(bytes, ct);
                    pipeServer.Disconnect();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"[IPC] Status server error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task ReceiveUdpLoopAsync(CancellationToken ct)
        {
            byte[] poolBuf = ArrayPool<byte>.Shared.Rent(65535);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (len, remoteEp) = await _udp!.ReceiveAsync(poolBuf, ct);
                        if (len < 1) continue;

                        var mem = poolBuf.AsMemory(0, len);
                        var span = mem.Span;
                        MsgType type = (MsgType)span[0];

                        switch (type)
                        {
                            case MsgType.Register:
                                if (_proxy != null)
                                {
                                    _proxy.ProcessRegister(span, remoteEp);
                                }
                                break;

                            case MsgType.Query:
                                if (_proxy != null)
                                {
                                    _ = _proxy.ProcessQueryAsync(mem, remoteEp, ct);
                                }
                                break;

                            case MsgType.RelayStart:
                                if (_server != null)
                                {
                                    _ = _server.ProcessRelayStartAsync(mem, remoteEp, ct);
                                }
                                break;

                            case MsgType.Punch:
                                await DispatchPunchAsync(mem, remoteEp, ct);
                                break;

                            case MsgType.Data:
                                await DispatchDataAsync(mem, remoteEp, ct);
                                break;

                            case MsgType.Disconnect:
                                await DispatchDisconnectAsync(mem, remoteEp, ct);
                                break;

                            case MsgType.EchoReq:
                                await HandleEchoReqAsync(mem, remoteEp, ct);
                                break;

                            case MsgType.EchoResp:
                                _server?.TryHandleEchoResp(span);
                                break;

                            case MsgType.AuthFail:
                                Log.Error($"[S] 鉴权失败：收到来自代理服务器({remoteEp})的拒绝连接响应！请检查配置中的 Username 和 Password。");
                                break;

                            case MsgType.RegFail:
                                if (span.Length >= 5)
                                {
                                    var (reason, _) = ProtocolHelper.ReadString(span.Slice(1));
                                    Log.Warn($"[S] 注册被代理服务器({remoteEp})拒绝: {reason}");
                                }
                                break;

                            case MsgType.AuthReq:
                                _server?.TryHandleAuthReq(span, remoteEp);
                                break;

                            case MsgType.AuthRes:
                                _client?.TryHandleAuthRes(span, remoteEp);
                                break;

                            default:
                                Log.Debug($"[RouteEngine] Unknown MsgType {(byte)type} from {remoteEp}");
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[RouteEngine] Receive loop error: {ex.Message}");
                        await Task.Delay(100, ct);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuf);
            }
        }

        private async ValueTask HandleEchoReqAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            // [MsgType 1][SessionId 16]
            if (mem.Length < 17) return;
            var span = mem.Span;
            byte[] respBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
            try
            {
                respBuf[0] = (byte)MsgType.EchoResp;
                span.Slice(1, 16).CopyTo(respBuf.AsSpan(1));
                int offset = 17;
                offset += ProtocolHelper.WriteIPEndPoint(respBuf.AsSpan(offset), remoteEp);
                await _udp!.SendAsync(respBuf.AsMemory(0, offset), remoteEp, ct);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(respBuf);
            }
        }

        private async ValueTask DispatchPunchAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            var span = mem.Span;
            if (span.Length < 34) return;

            byte status = span[33];
            Guid sessionId = new Guid(span.Slice(1, 16));
            Guid devId = new Guid(span.Slice(17, 16));

            // Status 0 (NotFound) / Status 1 (Success) -> 属于 Client 端的查询响应
            if (status == 0 || status == 1)
            {
                if (_client != null && _client.TryHandleQueryResponse(span))
                {
                    return;
                }
            }
            else if (status == 2 || status == 3) // 直接打洞包(2) 或 打洞确认包(3)
            {
                // 1. 尝试匹配 ClientMode 活动会话
                if (_client != null && await _client.TryHandlePunchAsync(sessionId, devId, remoteEp, status, ct))
                {
                    return;
                }

                // 2. 尝试匹配 ServerMode 活动会话
                if (_server != null && await _server.TryHandlePunchAsync(sessionId, devId, remoteEp, status, ct))
                {
                    return;
                }
            }
        }

        private async ValueTask DispatchDisconnectAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            var span = mem.Span;
            if (span.Length < 17) return;

            Guid sessionId = new Guid(span.Slice(1, 16));

            if (_client != null && _client.TryHandleDisconnect(sessionId)) return;
            if (_server != null && _server.TryHandleDisconnect(sessionId)) return;
            if (_proxy != null && await _proxy.TryRelayDataAsync(sessionId, mem, remoteEp, ct)) return;
        }

        private async ValueTask DispatchDataAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            var span = mem.Span;
            if (span.Length < 17) return;

            Guid sessionId = new Guid(span.Slice(1, 16));
            var payload = span.Slice(17);

            // 1. 尝试由 ClientMode 处理
            if (_client != null && _client.TryHandleData(sessionId, payload))
            {
                return;
            }

            // 2. 尝试由 ServerMode 处理
            if (_server != null && _server.TryHandleData(sessionId, payload))
            {
                return;
            }

            // 3. 尝试由 ProxyMode 进行中继转发
            if (_proxy != null && await _proxy.TryRelayDataAsync(sessionId, mem, remoteEp, ct))
            {
                return;
            }
        }

        private string GetAppStatusString()
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(_config.DevName))
                sb.AppendLine($"Service Name: {_config.DevName}");

            if (_proxy != null)
            {
                sb.AppendLine("\n[Proxy Mode]");
                sb.AppendLine($"Listening Port: {_proxy.Port}");
                foreach (var s in _proxy.GetRegisteredServers())
                    sb.AppendLine($" - Registered: {s}");
            }

            if (_server != null)
            {
                var sConfigs = _server.GetStatusInfo();
                if (sConfigs.Count > 0)
                {
                    sb.AppendLine("\n[Server Mode]");
                    foreach (var sc in sConfigs)
                        sb.AppendLine($" - {sc.Config} ({sc.Status})");
                }
            }

            if (_client != null)
            {
                var cConfigs = _client.GetStatusInfo();
                if (cConfigs.Count > 0)
                {
                    sb.AppendLine("\n[Client Mode]");
                    foreach (var cc in cConfigs)
                        sb.AppendLine($" - {cc.Config} ({cc.Status})");
                }
            }

            var activeChannels = new List<DataChannelInfo>();
            if (_server != null) activeChannels.AddRange(_server.GetActiveChannels());
            if (_client != null) activeChannels.AddRange(_client.GetActiveChannels());

            if (activeChannels.Count > 0)
            {
                sb.AppendLine($"\n[Active Data Channels: {activeChannels.Count}]");
                foreach (var ch in activeChannels)
                {
                    string relayStr = ch.IsRelayed ? "Relayed via Proxy" : "Direct UDP Punch";
                    string rttStr = ch.Rtt > 0 ? $" (RTT: {ch.Rtt}ms)" : "";
                    sb.AppendLine($" - {ch.Source} <--> {ch.Target} [{relayStr}]{rttStr}");
                }
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            _udp?.Dispose();
        }
    }

    public class ServerStatusInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Config { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class ClientStatusInfo
    {
        public string Config { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class DataChannelInfo
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool IsRelayed { get; set; }
        public int Rtt { get; set; }
    }
}