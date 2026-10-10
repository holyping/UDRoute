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

        private CancellationTokenSource? _engineCts;

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
            _engineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var effectiveCt = _engineCts.Token;

            _udp = new ZeroCopyUdpSocket(_config.Port);
            var boundEp = _udp.LocalEndPoint;
            Log.Info($"[RouteEngine] Core UDP socket bound to {boundEp}");

            var tasks = new List<Task>();

            if (_config.EnableProxy)
            {
                _proxy = new ProxyMode(_config, _udp);
                tasks.Add(_proxy.RunAsync(effectiveCt));
                Log.Info($"[P] Proxy running on UDP {_proxy.Port}");
            }

            if (_config.ServerRecords.Count > 0)
            {
                _server = new ServerMode(_config, _udp, _proxy); // 传入_proxy以支持 @this 优化模式
                tasks.Add(_server.RunAsync(effectiveCt));
                Log.Info($"[S] Server mode active. DevId: {_config.DevId}");
            }

            if (_config.ClientRecords.Count > 0)
            {
                _client = new ClientMode(_config, _udp, _proxy);
                tasks.Add(_client.RunAsync(effectiveCt));
                Log.Info($"[C] Client mode active.");
            }

            if (_proxy != null && _server != null)
            {
                _proxy.LocalServerRelayStartHandler = (mem, ep, c) => _server.ProcessRelayStartAsync(mem, ep, c);
            }

            // 核心 UDP 接收与分发循环
            tasks.Add(ReceiveUdpLoopAsync(ct));

            if (_config.ControllerPassword != null && _config.ControllerPassword.Length > 0)
            {
                tasks.Add(RunTcpControllerLoopAsync(effectiveCt));
                Log.Info($"[Controller] Remote controller active on TCP {_config.ControllerPort}");
            }

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

                            case MsgType.ServerIpsReq:
                                await HandleServerIpsReqAsync(mem, remoteEp, ct);
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

                _config.InstanceId.TryWriteBytes(respBuf.AsSpan(offset, 16));
                offset += 16;

                await _udp!.SendAsync(respBuf.AsMemory(0, offset), remoteEp, ct);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(respBuf);
            }
        }

        private async ValueTask HandleServerIpsReqAsync(ReadOnlyMemory<byte> mem, EndPoint remoteEp, CancellationToken ct)
        {
            if (mem.Length < 17) return;
            var span = mem.Span;
            byte[] respBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(512);
            try
            {
                respBuf[0] = (byte)MsgType.ServerIpsResp;
                span.Slice(1, 16).CopyTo(respBuf.AsSpan(1));
                int offset = 17;

                var allLocalIps = ProtocolHelper.GetLocalIPAddresses();
                var globalIps = allLocalIps.Where(ip => 
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || 
                    NatDiagnosticHelper.IsGlobalUnicastIPv6(ip)).ToArray();

                respBuf[offset++] = (byte)globalIps.Length;
                int pPort = _udp?.LocalEndPoint is IPEndPoint ep ? ep.Port : Constants.DefaultProxyPort;

                foreach (var ip in globalIps)
                {
                    if (offset + 21 > respBuf.Length) break;
                    offset += ProtocolHelper.WriteIPEndPoint(respBuf.AsSpan(offset), new IPEndPoint(ip, pPort));
                }

                if (offset + 16 <= respBuf.Length)
                {
                    _config.InstanceId.TryWriteBytes(respBuf.AsSpan(offset, 16));
                    offset += 16;
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

            // 4. 所有模式均未处理 (如果非 Proxy 模式节点收到未知或已失效 Session 的数据，回发 Disconnect 促使对端清理)
            if (_proxy == null)
            {
                byte[] disc = new byte[17];
                disc[0] = (byte)MsgType.Disconnect;
                sessionId.TryWriteBytes(disc.AsSpan(1, 16));
                try { await _udp!.SendAsync(disc, remoteEp, default); } catch { }
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

        private async Task RunTcpControllerLoopAsync(CancellationToken ct)
        {
            TcpListener? listener = null;
            try
            {
                listener = Socket.OSSupportsIPv6 && !ProtocolHelper.DisableIPv6
                    ? TcpListener.Create(_config.ControllerPort)
                    : new TcpListener(IPAddress.Any, _config.ControllerPort);
                listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"[Controller] Failed to start TCP controller listener on port {_config.ControllerPort}: {ex.Message}");
                return;
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var tcpClient = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleTcpControllerClientAsync(tcpClient, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Error($"[Controller] Controller listener loop error: {ex.Message}");
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private async Task HandleTcpControllerClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    using var readTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);
                    var token = linkedCts.Token;

                    byte[] lenBuf = new byte[4];
                    await stream.ReadExactlyAsync(lenBuf, token);
                    int totalLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                    if (totalLen < 62 || totalLen > 65536)
                    {
                        return;
                    }

                    byte[] reqBuf = new byte[totalLen];
                    await stream.ReadExactlyAsync(reqBuf, token);

                    var span = reqBuf.AsSpan();
                    if ((MsgType)span[0] != MsgType.ControlReq)
                    {
                        return;
                    }

                    var requestId = new Guid(span.Slice(1, 16));
                    var action = (ControlAction)span[17];
                    long timestamp = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(18, 8));
                    var authHash = span.Slice(26, 32);

                    // Anti-replay: 5 minutes tolerance
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (Math.Abs(nowTicks - timestamp) > TimeSpan.FromMinutes(5).Ticks)
                    {
                        await SendTcpControlRespAsync(stream, requestId, action, false, I18n.Text("认证失败: 时间偏差过大或重放校验失败。", "Authentication failed: time drift exceeds 5 minutes or replay detected."), token);
                        return;
                    }

                    // ControllerPassword required
                    if (_config.ControllerPassword == null || _config.ControllerPassword.Length == 0)
                    {
                        await SendTcpControlRespAsync(stream, requestId, action, false, I18n.Text("错误: 目标主机未配置 ControllerPassword，拒绝控制。", "Error: ControllerPassword not configured on remote host."), token);
                        return;
                    }

                    byte[] hashInput = new byte[_config.ControllerPassword.Length + 8];
                    _config.ControllerPassword.CopyTo(hashInput, 0);
                    BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(_config.ControllerPassword.Length, 8), timestamp);
                    byte[] expectedHash = ManagedSHA256.ComputeHashBytes(hashInput);

                    if (!authHash.SequenceEqual(expectedHash))
                    {
                        await SendTcpControlRespAsync(stream, requestId, action, false, I18n.Text("认证失败: 远程控制密码错误。", "Authentication failed: invalid ControllerPassword."), token);
                        return;
                    }

                    int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(58, 4));
                    var items = new List<string>();
                    int offset = 62;
                    for (int i = 0; i < count; i++)
                    {
                        if (offset >= span.Length) break;
                        var (item, readLen) = ProtocolHelper.ReadString(span.Slice(offset));
                        items.Add(item);
                        offset += readLen;
                    }

                    bool success = true;
                    string message = "";

                    switch (action)
                    {
                        case ControlAction.Add:
                        {
                            var msgs = new List<string>();
                            bool anyFail = false;
                            foreach (var item in items)
                            {
                                var (addOk, addMsg) = await AddEndpointFromControlAsync(item);
                                if (!addOk) anyFail = true;
                                msgs.Add(addMsg);
                            }
                            success = !anyFail;
                            message = string.Join("\n", msgs);
                            break;
                        }
                        case ControlAction.Delete:
                        {
                            var msgs = new List<string>();
                            bool anyFail = false;
                            foreach (var item in items)
                            {
                                var (delOk, delMsg) = await RemoveEndpointFromControlAsync(item);
                                if (!delOk) anyFail = true;
                                msgs.Add(delMsg);
                            }
                            success = !anyFail;
                            message = string.Join("\n", msgs);
                            break;
                        }
                        case ControlAction.List:
                        {
                            success = true;
                            message = GetEndpointsListString();
                            break;
                        }
                        default:
                            success = false;
                            message = $"Unknown control action {(byte)action}";
                            break;
                    }

                    await SendTcpControlRespAsync(stream, requestId, action, success, message, token);
                }
                catch (Exception ex)
                {
                    Log.Debug($"[Controller] TCP client handler exception: {ex.Message}");
                }
            }
        }

        private static async ValueTask SendTcpControlRespAsync(NetworkStream stream, Guid requestId, ControlAction action, bool success, string message, CancellationToken ct)
        {
            int msgBytes = Encoding.UTF8.GetByteCount(message);
            int bodyLen = 1 + 16 + 1 + 1 + 4 + msgBytes;
            byte[] buf = new byte[4 + bodyLen];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), bodyLen);
            buf[4] = (byte)MsgType.ControlResp;
            requestId.TryWriteBytes(buf.AsSpan(5, 16));
            buf[21] = (byte)action;
            buf[22] = (byte)(success ? 1 : 0);
            ProtocolHelper.WriteString(buf.AsSpan(23), message);

            await stream.WriteAsync(buf, ct);
            await stream.FlushAsync(ct);
        }

        private async Task<(bool Success, string Message)> AddEndpointFromControlAsync(string endpointStr)
        {
            if (string.IsNullOrWhiteSpace(endpointStr))
                return (false, "Endpoint definition cannot be empty.");

            int eqIdx = endpointStr.IndexOf('=');
            if (eqIdx <= 0)
                return (false, "Endpoint definition must contain '=' (e.g. 3443=xeno@www.qzsoft.top).");

            string left = endpointStr.Substring(0, eqIdx).Trim();
            string right = endpointStr.Substring(eqIdx + 1).Trim();

            if (char.IsDigit(left[0]))
            {
                var (cRec, _) = ConfigParser.ParseClientEndpoint(left, right, _config);
                if (cRec == null)
                    return (false, $"Invalid C-endpoint syntax: '{endpointStr}'");

                return await AddClientEndpointAsync(cRec, endpointStr);
            }
            else
            {
                var sRec = ConfigParser.ParseServerEndpoint(left, right, _config);
                if (sRec == null)
                    return (false, $"Invalid S-endpoint syntax: '{endpointStr}'");

                return await AddServerEndpointAsync(sRec, endpointStr);
            }
        }

        public async Task<(bool Success, string Message)> AddClientEndpointAsync(ClientRecord rec, string? rawLine = null)
        {
            lock (_config.ClientRecords)
            {
                if (_config.ClientRecords.Any(c => c.Port == rec.Port && c.IsTcp == rec.IsTcp))
                {
                    return (false, $"Port {rec.Port}/{(rec.IsTcp ? "tcp" : "udp")} is already in use by another C-endpoint.");
                }
            }

            if (_client == null)
            {
                _client = new ClientMode(_config, _udp!, _proxy);
                _ = _client.RunAsync(_engineCts != null ? _engineCts.Token : CancellationToken.None);
            }

            var (started, err) = _client.AddClientEndpoint(rec);
            if (!started)
            {
                return (false, $"Failed to start listener on port {rec.Port}: {err}");
            }

            lock (_config.ClientRecords)
            {
                _config.ClientRecords.Add(rec);
            }

            ConfigFileHelper.AddClientRecordToIni(_config.ConfigPath, rec, rawLine);
            Log.Info($"[Control] Added C-endpoint {rec.Port}/{(rec.IsTcp ? "tcp" : "udp")}={rec.TargetName}@{rec.TargetServer}");
            return (true, $"Added C-endpoint: {rec.Port}/{(rec.IsTcp ? "tcp" : "udp")}={rec.TargetName}@{rec.TargetServer}");
        }

        public async Task<(bool Success, string Message)> AddServerEndpointAsync(ServerRecord rec, string? rawLine = null)
        {
            lock (_config.ServerRecords)
            {
                if (_config.ServerRecords.Any(s => s.Name.Equals(rec.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, $"Server service name '{rec.Name}' already exists.");
                }
            }

            if (_server == null)
            {
                _server = new ServerMode(_config, _udp!, _proxy);
                if (_proxy != null)
                {
                    _proxy.LocalServerRelayStartHandler = (mem, ep, ct) => _server.ProcessRelayStartAsync(mem, ep, ct);
                }
                _ = _server.RunAsync(_engineCts != null ? _engineCts.Token : CancellationToken.None);
            }

            var (started, err) = _server.AddServerEndpoint(rec);
            if (!started)
            {
                return (false, $"Failed to start S-endpoint '{rec.Name}': {err}");
            }

            lock (_config.ServerRecords)
            {
                _config.ServerRecords.Add(rec);
            }

            ConfigFileHelper.AddServerRecordToIni(_config.ConfigPath, rec, rawLine);
            Log.Info($"[Control] Added S-endpoint {rec.Name}");
            return (true, $"Added S-endpoint: {rec.Name}");
        }

        private async Task<(bool Success, string Message)> RemoveEndpointFromControlAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return (false, "Target endpoint name or port cannot be empty.");

            int eqIdx = target.IndexOf('=');
            string left = (eqIdx > 0 ? target.Substring(0, eqIdx) : target).Trim();

            if (char.IsDigit(left[0]))
            {
                var parts = left.Split('/');
                if (int.TryParse(parts[0], out int port))
                {
                    bool? isTcp = parts.Length > 1
                        ? (parts[1].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? true : parts[1].Equals("udp", StringComparison.OrdinalIgnoreCase) ? false : null)
                        : null;

                    return await RemoveClientEndpointAsync(port, isTcp);
                }
            }

            return await RemoveServerEndpointAsync(left);
        }

        public async Task<(bool Success, string Message)> RemoveClientEndpointAsync(int port, bool? isTcp = null)
        {
            ClientRecord? rec;
            lock (_config.ClientRecords)
            {
                rec = _config.ClientRecords.FirstOrDefault(c => c.Port == port && (isTcp == null || c.IsTcp == isTcp.Value));
                if (rec != null)
                {
                    _config.ClientRecords.Remove(rec);
                }
            }

            if (rec == null)
            {
                return (false, $"C-endpoint on port {port} not found.");
            }

            if (_client != null)
            {
                _client.RemoveClientEndpoint(rec);
            }

            ConfigFileHelper.RemoveClientRecordFromIni(_config.ConfigPath, rec.Port, rec.IsTcp);
            Log.Info($"[Control] Removed C-endpoint {rec.Port}/{(rec.IsTcp ? "tcp" : "udp")}");
            return (true, $"Removed C-endpoint: {rec.Port}/{(rec.IsTcp ? "tcp" : "udp")}");
        }

        public async Task<(bool Success, string Message)> RemoveServerEndpointAsync(string name)
        {
            ServerRecord? rec;
            lock (_config.ServerRecords)
            {
                rec = _config.ServerRecords.FirstOrDefault(s =>
                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(name + "/file", StringComparison.OrdinalIgnoreCase) ||
                    (s.Name.EndsWith("/file", StringComparison.OrdinalIgnoreCase) && s.Name.Substring(0, s.Name.Length - 5).Equals(name, StringComparison.OrdinalIgnoreCase)));

                if (rec != null)
                {
                    _config.ServerRecords.Remove(rec);
                }
            }

            if (rec == null)
            {
                return (false, $"S-endpoint '{name}' not found.");
            }

            if (_server != null)
            {
                _server.RemoveServerEndpoint(rec);
            }

            ConfigFileHelper.RemoveServerRecordFromIni(_config.ConfigPath, rec.Name);
            Log.Info($"[Control] Removed S-endpoint {rec.Name}");
            return (true, $"Removed S-endpoint: {rec.Name}");
        }

        public string GetEndpointsListString()
        {
            var sb = new StringBuilder();
            if (_proxy != null)
            {
                sb.AppendLine($"[Proxy Mode] UDP Port {_proxy.Port}");
            }

            List<ClientRecord> clients;
            lock (_config.ClientRecords)
            {
                clients = _config.ClientRecords.ToList();
            }

            sb.AppendLine($"[Client Endpoints] (Total: {clients.Count})");
            if (clients.Count == 0)
            {
                sb.AppendLine("  (None)");
            }
            else
            {
                foreach (var c in clients)
                {
                    sb.AppendLine($"  - {c.Port}/{(c.IsTcp ? "tcp" : "udp")}={c.TargetName}@{c.TargetServer}");
                }
            }

            List<ServerRecord> servers;
            lock (_config.ServerRecords)
            {
                servers = _config.ServerRecords.ToList();
            }

            sb.AppendLine($"[Server Endpoints] (Total: {servers.Count})");
            if (servers.Count == 0)
            {
                sb.AppendLine("  (None)");
            }
            else
            {
                foreach (var s in servers)
                {
                    string target = s.IsFile ? $"{s.BaseDir};/file" : $"{s.TargetIp}:{s.TargetPort}/{(s.IsTcp ? "tcp" : "udp")}";
                    sb.AppendLine($"  - {s.Name}={target}@{s.TargetServer}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        public void Dispose()
        {
            _engineCts?.Cancel();
            _engineCts?.Dispose();
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
