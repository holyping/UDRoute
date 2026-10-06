using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Text;
using UDRoute.Logging;
using UDRoute.Shared.Interfaces;
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

        public ProxyMode? Proxy => _proxy;
        public ServerMode? Server => _server;
        public ClientMode? Client => _client;

        private readonly IEngineCallback? _callback;

        public RouteEngine(AppConfig config, IEngineCallback? callback = null)
        {
            _config = config;
            _callback = callback;
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

            if (_proxy != null && _server != null)
            {
                _proxy.LocalServerRelayStartHandler = (mem, ep, ct) => _server.ProcessRelayStartAsync(mem, ep, ct);
            }

            // 核心 UDP 接收与分发循环
            tasks.Add(ReceiveUdpLoopAsync(ct));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // 吞掉正常退出时的取消异常
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
                                    byte[] qCopy = mem.ToArray();
                                    _ = _proxy.ProcessQueryAsync(qCopy, remoteEp, ct);
                                }
                                break;

                            case MsgType.RelayStart:
                                if (_server != null)
                                {
                                    byte[] rCopy = mem.ToArray();
                                    await _server.ProcessRelayStartAsync(rCopy, remoteEp, ct);
                                }
                                break;

                            case MsgType.RelayStartAck:
                                if (_proxy != null)
                                {
                                    _proxy.ProcessRelayStartAck(span, remoteEp);
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
                                if (span.Length >= 17)
                                {
                                    var echoSessionId = new Guid(span.Slice(1, 16));
                                    _server?.TryUpdatePeerEndpoint(echoSessionId, remoteEp);
                                    _client?.TryUpdatePeerEndpoint(echoSessionId, remoteEp);
                                }
                                await HandleEchoReqAsync(mem, remoteEp, ct);
                                break;

                            case MsgType.EchoResp:
                                if (span.Length >= 17)
                                {
                                    var echoSessionId = new Guid(span.Slice(1, 16));
                                    _server?.TryUpdatePeerEndpoint(echoSessionId, remoteEp);
                                    _client?.TryUpdatePeerEndpoint(echoSessionId, remoteEp);
                                }
                                _server?.TryHandleEchoResp(span, remoteEp);
                                _client?.TryHandleEchoResp(span);
                                break;

                            case MsgType.RegisterAck:
                                _server?.ProcessRegisterAck(span, remoteEp);
                                break;

                            case MsgType.AuthReq:
                                if (_server != null && _server.TryHandleAuthReq(span, remoteEp)) break;
                                if (_proxy != null && span.Length >= 17) await _proxy.TryRelayDataAsync(new Guid(span.Slice(1, 16)), mem, remoteEp, ct);
                                break;

                            case MsgType.AuthRes:
                                if (_client != null && _client.TryHandleAuthRes(span, remoteEp)) break;
                                if (_proxy != null && span.Length >= 17) await _proxy.TryRelayDataAsync(new Guid(span.Slice(1, 16)), mem, remoteEp, ct);
                                break;

                            case MsgType.RelayEnd:
                                if (_proxy != null && span.Length >= 17)
                                {
                                    _proxy.HandleRelayEnd(new Guid(span.Slice(1, 16)), remoteEp);
                                }
                                break;

                            case MsgType.NatTestReq:
                                if (span.Length >= 17)
                                {
                                    Guid testId = new Guid(span.Slice(1, 16));
                                    byte flags = span.Length > 17 ? span[17] : (byte)0;
                                    _ = HandleEphemeralNatTestAsync(testId, flags, remoteEp, ct);
                                }
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
                    catch (ObjectDisposedException)
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
            // [MsgType 1][SessionId 16] (optional [DevId 16])
            if (mem.Length < 17) return;
            var span = mem.Span;
            byte[] respBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
            try
            {
                respBuf[0] = (byte)MsgType.EchoResp;
                span.Slice(1, 16).CopyTo(respBuf.AsSpan(1));
                int offset = 17;
                offset += ProtocolHelper.WriteIPEndPoint(respBuf.AsSpan(offset), remoteEp);

                // 如果携带有 DevId 且本机启用了 Proxy 模式，检查该 DevId 是否已注册服务
                if (span.Length >= 33 && _proxy != null)
                {
                    Guid devId = new Guid(span.Slice(17, 16));
                    bool isRegistered = _proxy.HasRegisteredServices(devId);
                    respBuf[offset++] = (byte)(isRegistered ? 1 : 0);
                    if (!isRegistered)
                    {
                        Log.Info($"[P] Received KeepAlive from unregistered DevId {devId} ({remoteEp}). Replying with NeedRegister signal.");
                    }
                }

                await _udp!.SendAsync(respBuf.AsMemory(0, offset), remoteEp, ct);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(respBuf);
            }
        }

        private async Task HandleEphemeralNatTestAsync(Guid testId, byte flags, EndPoint remoteEp, CancellationToken ct)
        {
            // P端动态创建随机端口临时 Socket，与 S/C 端进行两阶段 NAT 诊断握手
            ZeroCopyUdpSocket? tempUdp = null;
            try
            {
                tempUdp = new ZeroCopyUdpSocket(0);
                int tempPort = (tempUdp.LocalEndPoint is IPEndPoint tip) ? tip.Port : 0;
                if (tempPort == 0) return;

                // =========================================================================
                // 阶段 1：测试无邀约入站放行能力 (判别 NAT 1/2)
                // P 端为主动方：从临时端口向客户端发包，带重发 (3次，间隔 150ms)
                // 客户端为回复方：收到即回，不主动重发
                // =========================================================================
                byte[] stage1ProbeBuf = ArrayPool<byte>.Shared.Rent(64);
                int stage1ProbeLen;
                try
                {
                    stage1ProbeBuf[0] = (byte)MsgType.NatTestResp;
                    testId.TryWriteBytes(stage1ProbeBuf.AsSpan(1, 16));
                    stage1ProbeBuf[17] = NatTestFlags.Stage1Probe;
                    int offset = 18;
                    offset += ProtocolHelper.WriteIPEndPoint(stage1ProbeBuf.AsSpan(offset), remoteEp);
                    BinaryPrimitives.WriteInt32LittleEndian(stage1ProbeBuf.AsSpan(offset, 4), tempPort);
                    offset += 4;
                    stage1ProbeLen = offset;
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(stage1ProbeBuf);
                    throw;
                }

                using var stage1Cts = new CancellationTokenSource(800);
                using var linkedStage1 = CancellationTokenSource.CreateLinkedTokenSource(ct, stage1Cts.Token);
                bool stage1Success = false;

                // P 端主动方发包任务 (重发 3 次防丢包)
                var sendTask = Task.Run(async () =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (linkedStage1.IsCancellationRequested || stage1Success) break;
                        try
                        {
                            await tempUdp.SendAsync(stage1ProbeBuf.AsMemory(0, stage1ProbeLen), remoteEp, linkedStage1.Token);
                        }
                        catch { }
                        try { await Task.Delay(150, linkedStage1.Token); } catch { break; }
                    }
                });

                // P 端在临时端口等待客户端的单次回包 (Stage1Ack)
                byte[] recvBuf = ArrayPool<byte>.Shared.Rent(1024);
                try
                {
                    while (!linkedStage1.IsCancellationRequested)
                    {
                        var (len, fromEp) = await tempUdp.ReceiveAsync(recvBuf, linkedStage1.Token);
                        if (len >= 17 && ((MsgType)recvBuf[0] == MsgType.NatTestResp || (MsgType)recvBuf[0] == MsgType.NatTestReq))
                        {
                            Guid recvId = new Guid(recvBuf.AsSpan(1, 16));
                            if (recvId == testId)
                            {
                                stage1Success = true;
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }

                ArrayPool<byte>.Shared.Return(stage1ProbeBuf);

                if (stage1Success)
                {
                    // 阶段 1 成功：客户端确认为 NAT 1/2，直接结束，省去阶段 2 开销
                    return;
                }

                // =========================================================================
                // 阶段 2：阶段 1 超时，P 端通过主端口通知客户端临时端口号
                // 客户端转为主动方：向 P 端临时端口发包探测 (客户端负责重发)
                // P 端转为回复方：收到即回，不主动重发 (单次回送客户端映射端口)
                // =========================================================================
                if (_udp != null)
                {
                    byte[] stage2NotifyBuf = ArrayPool<byte>.Shared.Rent(64);
                    try
                    {
                        stage2NotifyBuf[0] = (byte)MsgType.NatTestResp;
                        testId.TryWriteBytes(stage2NotifyBuf.AsSpan(1, 16));
                        stage2NotifyBuf[17] = NatTestFlags.Stage2Notify;
                        int off = 18;
                        off += ProtocolHelper.WriteIPEndPoint(stage2NotifyBuf.AsSpan(off), remoteEp);
                        BinaryPrimitives.WriteInt32LittleEndian(stage2NotifyBuf.AsSpan(off, 4), tempPort);
                        off += 4;
                        // 主端口发送通知 (发 2 次防主端口单包偶发丢失)
                        await _udp.SendAsync(stage2NotifyBuf.AsMemory(0, off), remoteEp, ct);
                        await Task.Delay(50, ct);
                        await _udp.SendAsync(stage2NotifyBuf.AsMemory(0, off), remoteEp, ct);
                    }
                    catch { }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(stage2NotifyBuf);
                    }
                }

                // P 端临时 Socket 作为回复方：等待客户端发包，收到每个探测包单次回送一次 (不重发)
                using var stage2Cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                using var linkedStage2 = CancellationTokenSource.CreateLinkedTokenSource(ct, stage2Cts.Token);

                try
                {
                    while (!linkedStage2.IsCancellationRequested)
                    {
                        var (len, probeRemoteEp) = await tempUdp.ReceiveAsync(recvBuf, linkedStage2.Token);
                        if (len < 17) continue;

                        var span = recvBuf.AsSpan(0, len);
                        MsgType type = (MsgType)span[0];
                        Guid probeTestId = new Guid(span.Slice(1, 16));

                        if ((type == MsgType.NatTestReq || type == MsgType.EchoReq) && probeTestId == testId)
                        {
                            // 收到客户端探测包，回复方单次回送 (不重发)：带上客户端在新端口上的映射公网地址
                            byte[] replyBuf = ArrayPool<byte>.Shared.Rent(64);
                            try
                            {
                                replyBuf[0] = (byte)MsgType.NatTestResp;
                                testId.TryWriteBytes(replyBuf.AsSpan(1, 16));
                                replyBuf[17] = NatTestFlags.None;
                                int off = 18;
                                off += ProtocolHelper.WriteIPEndPoint(replyBuf.AsSpan(off), probeRemoteEp);
                                BinaryPrimitives.WriteInt32LittleEndian(replyBuf.AsSpan(off, 4), tempPort);
                                off += 4;
                                await tempUdp.SendAsync(replyBuf.AsMemory(0, off), probeRemoteEp, linkedStage2.Token);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(replyBuf);
                            }

                            // 简短等待 (200ms) 以便吸收客户端可能已经在途的重发包并应答，然后平稳退出
                            using var drainCts = new CancellationTokenSource(200);
                            using var linkedDrain = CancellationTokenSource.CreateLinkedTokenSource(linkedStage2.Token, drainCts.Token);
                            try
                            {
                                while (!linkedDrain.IsCancellationRequested)
                                {
                                    var (dLen, dEp) = await tempUdp.ReceiveAsync(recvBuf, linkedDrain.Token);
                                    if (dLen >= 17 && new Guid(recvBuf.AsSpan(1, 16)) == testId)
                                    {
                                        byte[] dReply = ArrayPool<byte>.Shared.Rent(64);
                                        try
                                        {
                                            dReply[0] = (byte)MsgType.NatTestResp;
                                            testId.TryWriteBytes(dReply.AsSpan(1, 16));
                                            dReply[17] = NatTestFlags.None;
                                            int dOff = 18;
                                            dOff += ProtocolHelper.WriteIPEndPoint(dReply.AsSpan(dOff), dEp);
                                            BinaryPrimitives.WriteInt32LittleEndian(dReply.AsSpan(dOff, 4), tempPort);
                                            dOff += 4;
                                            await tempUdp.SendAsync(dReply.AsMemory(0, dOff), dEp, linkedDrain.Token);
                                        }
                                        finally { ArrayPool<byte>.Shared.Return(dReply); }
                                    }
                                }
                            }
                            catch (OperationCanceledException) { }
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    ArrayPool<byte>.Shared.Return(recvBuf);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[P] Ephemeral NAT test error: {ex.Message}");
            }
            finally
            {
                tempUdp?.Dispose();
            }
        }


        private async ValueTask DispatchPunchAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            var span = mem.Span;
            if (span.Length < 36) return;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1, 2));
            Guid sessionId = new Guid(span.Slice(3, 16));
            Guid peerInstanceId = new Guid(span.Slice(19, 16));
            byte status = span[35];

            // 非打洞请求/确认包 -> 统一属于 Client 端的查询响应 (Success, NotFound, SUnresponsive, StaleSession 等)
            if (status != PunchStatus.PunchReq && status != PunchStatus.PunchAck)
            {
                if (_client != null && _client.TryHandleQueryResponse(span))
                {
                    return;
                }
            }
            else // 直接打洞包(PunchReq=2) 或 打洞确认包(PunchAck=3)
            {
                // 1. 尝试匹配 ClientMode 活动会话
                if (_client != null && await _client.TryHandlePunchAsync(sessionId, peerInstanceId, remoteEp, status, ct))
                {
                    return;
                }

                // 2. 尝试匹配 ServerMode 活动会话
                if (_server != null && await _server.TryHandlePunchAsync(sessionId, peerInstanceId, remoteEp, status, ct))
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

            if (_client != null && _client.TryHandleDisconnect(sessionId, remoteEp)) return;
            if (_server != null && _server.TryHandleDisconnect(sessionId, remoteEp)) return;
            if (_proxy != null && await _proxy.TryRelayDisconnectAsync(sessionId, mem, remoteEp, ct)) return;
        }

        private async ValueTask DispatchDataAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            var span = mem.Span;
            if (span.Length < 17) return;

            Guid sessionId = new Guid(span.Slice(1, 16));
            var payload = span.Slice(17);

            // 1. 尝试由 ClientMode 处理
            if (_client != null && _client.TryHandleData(sessionId, payload, remoteEp))
            {
                return;
            }

            // 2. 尝试由 ServerMode 处理
            if (_server != null && _server.TryHandleData(sessionId, payload, remoteEp))
            {
                return;
            }

            // 3. 尝试由 ProxyMode 进行中继转发
            if (_proxy != null && await _proxy.TryRelayDataAsync(sessionId, mem, remoteEp, ct))
            {
                return;
            }
        }

        public List<DataChannelInfo> GetActiveChannels()
        {
            var activeChannels = new List<DataChannelInfo>();
            if (_server != null) activeChannels.AddRange(_server.GetActiveChannels());
            if (_client != null) activeChannels.AddRange(_client.GetActiveChannels());
            return activeChannels;
        }

        public string GetAppStatusString()
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

            var activeChannels = GetActiveChannels();

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
