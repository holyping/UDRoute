using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 7. Server (S模式) - 接受数据并转发到Target
    // ==========================================
    public class ServerMode
    {
        private readonly AppConfig _config;
        private readonly ProxyMode? _localProxy;
        private readonly ZeroCopyUdpSocket _udp;
        private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IPEndPoint>> _pendingEchoes = new();

        public ServerMode(AppConfig config, ZeroCopyUdpSocket udp, ProxyMode? localProxy)
        {
            _config = config;
            _udp = udp;
            _localProxy = localProxy;
        }

        public List<ServerStatusInfo> GetStatusInfo()
        {
            var list = new List<ServerStatusInfo>();
            foreach (var rec in _config.ServerRecords)
            {
                list.Add(new ServerStatusInfo
                {
                    Name = rec.Name,
                    Config = $"{rec.Name}={rec.TargetIp}:{rec.TargetPort}/{(rec.IsTcp?"tcp":"udp")}@{rec.TargetServer}",
                    Status = rec.IsThis ? "Local P-Mode" : "Registered"
                });
            }
            return list;
        }

        public List<DataChannelInfo> GetActiveChannels()
        {
            var list = new List<DataChannelInfo>();
            foreach (var s in _sessions.Values)
            {
                list.Add(new DataChannelInfo { Source = s.ActiveRemoteEp.ToString() ?? "", Target = s.ChannelDesc, IsRelayed = !s.IsDirect, Rtt = s.Rtt });
            }
            return list;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[1024];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var orilocalEps = ProtocolHelper.GetLocalEndPoints(((IPEndPoint)_udp.LocalEndPoint).Port);
                    foreach (var rec in _config.ServerRecords)
                    {
                        if (rec.IsThis && _localProxy != null)
                        {
                            _localProxy.ProcessRegisterDirect(rec, _config.DevId, _config.WanPort, _config.DevName);
                            continue;
                        }

                        var pServer = rec.TargetServer;
                        var pEndPoints = await ProtocolHelper.ResolveAllEndPointsAsync(pServer, Constants.DefaultProxyPort);
                        if (pEndPoints == null || pEndPoints.Length == 0)
                        {
                            Log.Error($"[S] Failed to resolve P server '{pServer}' for service '{rec.Name}'");
                            continue;
                        }

                        // Start STUN Echo requests to all resolved endpoints to gather public IPs
                        var localEps = orilocalEps.ToList();
                        var echoTasks = new List<Task<(IPEndPoint Target, IPEndPoint? Result)>>();
                        byte[] echoReq = new byte[17];
                        echoReq[0] = (byte)MsgType.EchoReq;

                        foreach (var pEp in pEndPoints)
                        {
                            if (pEp.AddressFamily == AddressFamily.InterNetworkV6 && ProtocolHelper.UnavailableIPv6.ContainsKey(pEp.Address))
                            {
                                continue; // 跳过被标记为不可用的 IPv6 地址
                            }

                            echoTasks.Add(Task.Run(async () =>
                            {
                                var echoId = Guid.NewGuid();
                                echoId.TryWriteBytes(echoReq.AsSpan(1, 16));
                                var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                                _pendingEchoes[echoId] = tcs;
                                await _udp.SendAsync(echoReq, pEp, ct);
                                
                                // 1 second timeout for echo (fast fail for bad IPv6 routes)
                                using var timeout = new CancellationTokenSource(1000);
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                                try { var res = await tcs.Task.WaitAsync(linked.Token); return (pEp, res); }
                                catch { _pendingEchoes.TryRemove(echoId, out _); return (pEp, (IPEndPoint?)null); }
                            }));
                        }

                        var echoResults = await Task.WhenAll(echoTasks);
                        bool ipv6NeedsReset = false;
                        var retryList = new List<IPEndPoint>();

                        foreach (var res in echoResults)
                        {
                            if (res.Result != null)
                            {
                                if (!localEps.Contains(res.Result)) localEps.Add(res.Result);
                            }
                            else if (res.Target.AddressFamily == AddressFamily.InterNetworkV6)
                            {
                                // IPv6 获取失败，准备重置重试
                                ipv6NeedsReset = true;
                                retryList.Add(res.Target);
                            }
                        }

                        if (ipv6NeedsReset)
                        {
                            await ProtocolHelper.ResetIPv6StackAsync();

                            // 重新发送 EchoReq 到失败的 IPv6 节点
                            var retryTasks = new List<Task<(IPEndPoint Target, IPEndPoint? Result)>>();
                            foreach (var pEp in retryList)
                            {
                                retryTasks.Add(Task.Run(async () =>
                                {
                                    var echoId = Guid.NewGuid();
                                    echoId.TryWriteBytes(echoReq.AsSpan(1, 16));
                                    var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                                    _pendingEchoes[echoId] = tcs;
                                    await _udp.SendAsync(echoReq, pEp, ct);

                                    using var timeout = new CancellationTokenSource(2000); // 稍微加长等待
                                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                                    try { var res = await tcs.Task.WaitAsync(linked.Token); return (pEp, res); }
                                    catch { _pendingEchoes.TryRemove(echoId, out _); return (pEp, (IPEndPoint?)null); }
                                }));
                            }

                            var retryResults = await Task.WhenAll(retryTasks);
                            foreach (var res in retryResults)
                            {
                                if (res.Result != null)
                                {
                                    if (!localEps.Contains(res.Result)) localEps.Add(res.Result);
                                }
                                else
                                {
                                    Log.Warn($"[S] P server IPv6 {res.Target.Address} unreachable after reset. Marked unavailable.");
                                    ProtocolHelper.UnavailableIPv6[res.Target.Address] = true;
                                }
                            }
                        }

                        // 构造注册包: [MsgType 1][DevId 16][WanPort 4][IsTcp 1][Timeout 4][KcpConfig 21][Name string][Suffix string][LocalEps...]
                        buffer[0] = (byte)MsgType.Register;
                        _config.DevId.TryWriteBytes(buffer.AsSpan(1, 16));
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(17, 4), _config.WanPort);
                        buffer[21] = (byte)(rec.IsTcp ? 1 : 0);
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22, 4), rec.Timeout);
                        int offset = 26;
                        offset += ProtocolHelper.WriteKcpConfig(buffer.AsSpan(offset), rec.KcpConfig);
                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), rec.Name);
                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), _config.DevName);

                        int countPos = offset++;
                        byte epCount = 0;
                        foreach (var ep in localEps)
                        {
                            if (offset + 21 > buffer.Length) break;
                            offset += ProtocolHelper.WriteIPEndPoint(buffer.AsSpan(offset), ep);
                            epCount++;
                            if (epCount >= 10) break; // 最多带 10 个
                        }
                        buffer[countPos] = epCount;

                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), _config.Username ?? "");
                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), _config.Password ?? "");

                        string proto = rec.IsTcp ? "tcp" : "udp";
                        // Now we just send the register to the first resolved P endpoint (e.g., IPv4)
                        var primaryPEp = pEndPoints[0];
                        await _udp.SendAsync(buffer.AsMemory(0, offset), primaryPEp, ct);
                        Log.Info($"[S] Registered service '{rec.Name}/{proto}' with P ({primaryPEp}) + {epCount} IPs");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"[S] KeepAlive error: {ex.Message}");
                }

                int interval = _config.ServerRecords.Count > 0 
                    ? _config.ServerRecords.Min(r => r.RegInterval > 0 ? r.RegInterval : Constants.DefaultRegInterval) 
                    : Constants.DefaultRegInterval;
                await Task.Delay(interval * 1000, ct);
            }
        }

        public void TryHandleEchoResp(ReadOnlySpan<byte> data)
        {
            if (data.Length < 17) return;
            Guid sessionId = new Guid(data.Slice(1, 16));
            if (_pendingEchoes.TryRemove(sessionId, out var tcs))
            {
                var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.Slice(17));
                tcs.TrySetResult(ep);
            }
        }

        public async ValueTask ProcessRelayStartAsync(ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            var data = packetMem.Span;
            // [MsgType 1][SessionId 16][TargetName string][ClientPublicEp]
            if (data.Length < 21) return;

            Guid sessionId = new Guid(data.Slice(1, 16));
            int offset = 17;
            var (targetName, nLen) = ProtocolHelper.ReadString(data.Slice(offset));
            offset += nLen;
            var (cPublicEp, _) = ProtocolHelper.ReadIPEndPoint(data.Slice(offset));

            Log.Info($"[S] RelayStart: Session {sessionId} for '{targetName}', Client: {cPublicEp}");

            var rec = _config.ServerRecords.FirstOrDefault(r =>
            {
                string proto = r.IsTcp ? "tcp" : "udp";
                return string.Equals(targetName, $"{r.Name}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, $"{r.Name}.{_config.DevName}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, $"{r.Name}.{_config.DevId}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, r.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (rec != null)
            {
                int mtu = rec.Mtu > 0 ? rec.Mtu : Constants.DefaultMtu;
                // 初始通过 P 进行中继 (remoteEp 即为 P 的地址)，使用对应目标服务的 KCP 配置
                var session = new TunnelSession(_udp, remoteEp, sessionId, mtu, rec.IsTcp, rec.KcpConfig, rec.Timeout);
                session.ChannelDesc = $"{rec.TargetIp}:{rec.TargetPort}";
                _sessions[sessionId] = session;

                // 向 C 发起直接 UDP 打洞
                _ = StartPunchingAsync(session, cPublicEp, ct);

                // 连接目标后端服务 (根据 TCP/UDP 分流)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (rec.IsTcp)
                        {
                            var targetClient = new TcpClient();
                            await targetClient.ConnectAsync(rec.TargetIp, rec.TargetPort, ct);
                            Log.Info($"[S] Connected to Target TCP {rec.TargetIp}:{rec.TargetPort} for Session {sessionId}");
                            await session.RunTcpBridgeAsync(targetClient, ct);
                        }
                        else
                        {
                            using var targetUdp = new ZeroCopyUdpSocket(0);
                            var targetEp = new IPEndPoint(IPAddress.Parse(rec.TargetIp), rec.TargetPort);
                            Log.Info($"[S] Started Target UDP forwarder to {rec.TargetIp}:{rec.TargetPort} for Session {sessionId}");
                            await session.RunUdpBridgeAsync(targetUdp, targetEp, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[S] Target bridge error: {ex.Message}");
                    }
                    finally
                    {
                        if (_sessions.TryRemove(sessionId, out _))
                        {
                            Log.Info($"[S] Session {sessionId} closed.");
                        }
                        session.Dispose();
                    }
                }, ct);
            }
        }

        public async ValueTask<bool> TryHandlePunchAsync(Guid sessionId, Guid peerDevId, EndPoint remoteEp, byte status, CancellationToken ct)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.SwitchToDirect(remoteEp);

                if (status == 2)
                {
                    // 回送打洞确认，避免死循环 ping-pong
                    byte[] ackBuf = new byte[34];
                    ackBuf[0] = (byte)MsgType.Punch;
                    sessionId.TryWriteBytes(ackBuf.AsSpan(1, 16));
                    _config.DevId.TryWriteBytes(ackBuf.AsSpan(17, 16));
                    ackBuf[33] = 3; // Punch ACK
                    await _udp.SendAsync(ackBuf, remoteEp, ct);
                }
                return true;
            }
            return false;
        }

        public bool TryHandleData(Guid sessionId, ReadOnlySpan<byte> payload)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.OnUdpDataReceived(payload);
                return true;
            }
            return false;
        }

        public bool TryHandleDisconnect(Guid sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.DisconnectReceived();
                return true;
            }
            return false;
        }

        private async Task StartPunchingAsync(TunnelSession session, IPEndPoint cPublicEp, CancellationToken ct)
        {
            byte[] punchBuf = new byte[34];
            punchBuf[0] = (byte)MsgType.Punch;
            session.SessionId.TryWriteBytes(punchBuf.AsSpan(1, 16));
            _config.DevId.TryWriteBytes(punchBuf.AsSpan(17, 16));
            punchBuf[33] = 2; // Direct Punch

            var candidates = new List<IPEndPoint> { cPublicEp };

            Log.Info($"[S] Session {session.SessionId} starting UDP punch to targets: {string.Join(", ", candidates)}");

            for (int i = 0; i < 8 && !session.IsDirect && !ct.IsCancellationRequested; i++)
            {
                foreach (var ep in candidates)
                {
                    await _udp.SendAsync(punchBuf, ep, ct);
                }
                await Task.Delay(100, ct);
            }

            if (session.IsDirect)
            {
                Log.Info($"[S] Session {session.SessionId} UDP punch successful! Now using direct P2P connection.");
            }
            else
            {
                Log.Info($"[S] Session {session.SessionId} UDP punch timed out. Continuing with Proxy relay.");
            }
        }
    }
}