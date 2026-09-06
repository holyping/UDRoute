using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        private readonly Dictionary<Guid, TunnelSession> _sessions = new();
        private readonly object _sessionLock = new();
        public IEnumerable<TunnelSession> Sessions
        {
            get
            {
                lock (_sessionLock)
                {
                    return _sessions.Values.ToList();
                }
            }
        }
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IPEndPoint>> _pendingEchoes = new();
        private int _contextCounter;
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<(bool Success, string Reason)>> _pendingRegistrations = new();
        private readonly TemporyDictionary<Guid, (ushort ContextId, byte Status)> _recentRelayStarts;
        private readonly object _recentRelayStartsLock = new();

        public ServerMode(AppConfig config, ZeroCopyUdpSocket udp, ProxyMode? localProxy)
        {
            _config = config;
            _udp = udp;
            _localProxy = localProxy;
            _recentRelayStarts = new TemporyDictionary<Guid, (ushort ContextId, byte Status)>(_config.MaxRecentRequests);
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
            List<TunnelSession> sessions;
            lock (_sessionLock)
            {
                sessions = _sessions.Values.ToList();
            }
            foreach (var s in sessions)
            {
                list.Add(new DataChannelInfo { Source = s.ActiveRemoteEp.ToString() ?? "", Target = s.ChannelDesc, IsRelayed = !s.IsDirect, Rtt = s.Rtt });
            }
            return list;
        }

        public async Task RunAsync(CancellationToken ct)
        {

            foreach (var rec in _config.ServerRecords)
            {
                if (rec.IsFile)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    rec.TargetIp = "127.0.0.1";
                    rec.TargetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    rec.IsTcp = true;
                    _ = FileProtocolHelper.RunServerAsync(listener, rec.BaseDir, rec.ReadOnly, ct);
                    Log.Info($"[S] Started internal File Protocol server on 127.0.0.1:{rec.TargetPort} for base dir {rec.BaseDir}");
                }
            }

            // 启动 S 到各 P 目标服务器的 NAT KeepAlive 保活循环 (按 TargetServer 分组取最小保活间隔)
            var keepAliveTargets = _config.ServerRecords
                .Where(r => !r.IsThis && !string.IsNullOrWhiteSpace(r.TargetServer) && r.KeepAlive > 0)
                .GroupBy(r => r.TargetServer, StringComparer.OrdinalIgnoreCase)
                .Select(g => (TargetServer: g.Key, Interval: g.Min(r => r.KeepAlive)))
                .ToList();

            foreach (var target in keepAliveTargets)
            {
                _ = Task.Run(() => RunPKeepAliveAsync(target.TargetServer, target.Interval, ct), ct);
            }

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

                        ushort regContextId = (ushort)Interlocked.Increment(ref _contextCounter);
                        if (regContextId == 0) regContextId = (ushort)Interlocked.Increment(ref _contextCounter);

                        // 构造注册包: [MsgType 1][ContextId 2][DevId 16][WanPort 4][IsTcp 1][Timeout 4][Timestamp 8][ReqPass 1][KcpConfig 21][Name string][Suffix string][LocalEps...]
                        buffer[0] = (byte)MsgType.Register;
                        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), regContextId);
                        _config.DevId.TryWriteBytes(buffer.AsSpan(3, 16));
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(19, 4), _config.WanPort);
                        buffer[23] = (byte)(rec.IsTcp ? 1 : 0);
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), rec.Timeout);
                        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(28, 8), DateTime.UtcNow.Ticks);
                        bool reqPass = rec.Password != null && rec.Password.Length > 0;
                        buffer[36] = (byte)(reqPass ? 1 : 0);
                        int offset = 37;
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

                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), rec.Username ?? _config.Username ?? "");
                        byte[]? hash2Bytes = rec.Password ?? _config.Password;
                        string finalPassPayload = "";
                        
                        if (hash2Bytes != null && hash2Bytes.Length == 32)
                        {
                            string t1 = ConfigProtector.GetMachineId(); // Base64
                            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            
                            byte[] tsBytes = Encoding.UTF8.GetBytes(ts.ToString());
                            byte[] bufferToHash = new byte[32 + tsBytes.Length];
                            Buffer.BlockCopy(hash2Bytes, 0, bufferToHash, 0, 32);
                            Buffer.BlockCopy(tsBytes, 0, bufferToHash, 32, tsBytes.Length);
                            
                            byte[] hash3Bytes = ManagedSHA256.ComputeHashBytes(bufferToHash);
                            string hash3 = Convert.ToBase64String(hash3Bytes);
                            
                            finalPassPayload = $"$HW${ts}|{t1}|{hash3}";
                        }
                        
                        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), finalPassPayload);
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), rec.TunnelReuseInterval);
                        offset += 4;
                        buffer[offset++] = (byte)(rec.AllowRelay ? 1 : 0);

                        string proto = rec.IsTcp ? "tcp" : "udp";
                        var primaryPEp = pEndPoints[0];
                        byte[] regCopy = buffer.AsSpan(0, offset).ToArray();

                        var regTcs = new TaskCompletionSource<(bool Success, string Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingRegistrations[regContextId] = regTcs;

                        _ = ProtocolHelper.SendWithRetryAsync(_udp, regCopy, primaryPEp, regTcs.Task, ct);

                        using var waitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, Constants.DefaultProbeTimeout + 2)));
                        using var linkedWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, waitTimeoutCts.Token);

                        bool regSuccess = false;
                        string regReason = "";
                        try
                        {
                            var (success, reason) = await regTcs.Task.WaitAsync(linkedWaitCts.Token);
                            regSuccess = success;
                            regReason = reason;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            regReason = "Timeout waiting for proxy ACK";
                        }
                        finally
                        {
                            _pendingRegistrations.TryRemove(regContextId, out _);
                        }

                        if (regSuccess)
                        {
                            Log.Info($"[S] Registered service '{rec.Name}/{proto}' with P ({primaryPEp}) + {epCount} IPs");
                        }
                        else
                        {
                            Log.Warn($"[S] Failed to register service '{rec.Name}/{proto}' with P ({primaryPEp}): {regReason}");
                        }
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

        public void ProcessRegisterAck(ReadOnlySpan<byte> data, EndPoint remoteEp)
        {
            // [MsgType 1 = 9][ContextId 2][Status 1][Reason string (optional)]
            if (data.Length < 4) return;
            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            byte status = data[3];
            string reason = "";
            if (data.Length > 4)
            {
                (reason, _) = ProtocolHelper.ReadString(data.Slice(4));
            }

            if (_pendingRegistrations.TryRemove(contextId, out var tcs))
            {
                tcs.TrySetResult((status == 1, reason));
            }
        }

        public async ValueTask ProcessRelayStartAsync(ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            var data = packetMem.Span;
            // [MsgType 1][ContextId 2][SessionId 16][TargetName string][ClientPublicEp][Flags 1]
            if (data.Length < 23) return;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            Guid sessionId = new Guid(data.Slice(3, 16));
            int offset = 19;
            var (targetName, nLen) = ProtocolHelper.ReadString(data.Slice(offset));
            offset += nLen;
            var (cPublicEp, epLen) = ProtocolHelper.ReadIPEndPoint(data.Slice(offset));
            offset += epLen;

            bool allowRelay = (data[offset] & 1) != 0;
            bool clientForceRelay = (data[offset] & 2) != 0;
            bool isReuse = (data[offset] & 4) != 0;
            offset++;

            var rec = _config.ServerRecords.FirstOrDefault(r =>
            {
                string proto = r.IsTcp ? "tcp" : "udp";
                return string.Equals(targetName, $"{r.Name}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, $"{r.Name}.{_config.DevName}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, $"{r.Name}.{_config.DevId}/{proto}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, r.Name, StringComparison.OrdinalIgnoreCase);
            });

            byte ackStatus = (byte)(rec != null ? 1 : 0);

            if (contextId != 0)
            {
                lock (_recentRelayStartsLock)
                {
                    if (_recentRelayStarts.TryGetValue(sessionId, out var seen) && seen.ContextId == contextId)
                    {
                        // 重复握手请求（Burst 3x 重发包）：重送 RelayStartAck 并直接返回
                        SendRelayStartAck(contextId, sessionId, remoteEp, seen.Status);
                        return;
                    }
                    _recentRelayStarts[sessionId] = (contextId, ackStatus);
                }
            }

            Log.Info($"[S] RelayStart: Session {sessionId}, Context: {contextId} for '{targetName}', Client: {cPublicEp}, AllowRelay: {allowRelay}, ClientForceRelay: {clientForceRelay}, IsReuse: {isReuse}");

            if (rec != null)
            {
                SendRelayStartAck(contextId, sessionId, remoteEp, 1);
                if (!rec.AllowRelay)
                {
                    allowRelay = false;
                }

                if (!allowRelay && clientForceRelay)
                {
                    Log.Info($"[S] AllowRelay is false for session {sessionId}, ignoring client ForceRelay.");
                    clientForceRelay = false;
                }

                int mtu = rec.Mtu > 0 ? rec.Mtu : Constants.DefaultMtu;

                if (!allowRelay)
                {
                    Log.Info($"[S] P relay is disabled for session {sessionId}. Waiting for UDP punch before bridging backend target...");
                    TunnelSession? session = null;
                    bool isExisting = false;
                    lock (_sessionLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out var existingSession) && !existingSession.IsClosed)
                        {
                            session = existingSession;
                            isExisting = true;
                        }
                        else
                        {
                            session = new TunnelSession(_udp, cPublicEp, sessionId, mtu, rec.IsTcp, rec.KcpConfig, rec.Timeout, rec.TunnelReuseInterval, rec.KeepAlive);
                            session.ChannelDesc = $"{rec.TargetIp}:{rec.TargetPort}";
                            session.PasswordHash = rec.Password;
                            session.ForceRelay = false;
                            if (rec.Password == null || rec.Password.Length == 0) session.AuthTcs.TrySetResult(true);
                            _sessions[sessionId] = session;
                        }
                    }

                    if (isExisting) return;

                    var runSession = session;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool punchSuccess = await StartPunchingAsync(runSession, cPublicEp, ct);
                            if (!punchSuccess)
                            {
                                Log.Warn($"[S] P relay is disabled and UDP punch timed out for session {sessionId}. Aborting backend connection.");
                                return;
                            }

                            Log.Info($"[S] UDP punch succeeded without P relay! Connecting to backend target {rec.TargetIp}:{rec.TargetPort} for Session {sessionId}");
                            bool authOk = await runSession.AuthTcs.Task;
                            if (!authOk)
                            {
                                Log.Warn($"[S] Auth failed for session {sessionId}, aborting bridge.");
                                return;
                            }

                            await BridgeBackendAsync(runSession, rec, sessionId, ct);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[S] Target bridge error: {ex.Message}");
                        }
                        finally
                        {
                            lock (_sessionLock)
                            {
                                if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, runSession))
                                {
                                    _sessions.Remove(sessionId);
                                    Log.Info($"[S] Session {sessionId} closed.");
                                }
                            }
                            runSession.Dispose();
                        }
                    }, ct);
                }
                else
                {
                    TunnelSession? sessionToRun = null;
                    bool isReused = false;
                    bool needDisconnect = false;

                    lock (_sessionLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out var existingSession))
                        {
                            if (!existingSession.IsClosed)
                            {
                                Log.Info($"[S] Re-activating existing relay session {sessionId} for '{targetName}'.");
                                existingSession.ProxyEp = remoteEp;
                                existingSession.UpdateActivity();
                                sessionToRun = existingSession;
                                isReused = true;
                            }
                            else
                            {
                                _sessions.Remove(sessionId);
                            }
                        }

                        if (sessionToRun == null)
                        {
                            if (isReuse)
                            {
                                needDisconnect = true;
                            }
                            else
                            {
                                var session = new TunnelSession(_udp, remoteEp, sessionId, mtu, rec.IsTcp, rec.KcpConfig, rec.Timeout, rec.TunnelReuseInterval, rec.KeepAlive);
                                session.ChannelDesc = $"{rec.TargetIp}:{rec.TargetPort}";
                                session.PasswordHash = rec.Password;
                                session.ProxyEp = remoteEp;
                                session.ForceRelay = clientForceRelay;
                                if (rec.Password == null || rec.Password.Length == 0) session.AuthTcs.TrySetResult(true);
                                _sessions[sessionId] = session;
                                sessionToRun = session;
                            }
                        }
                    }

                    if (needDisconnect)
                    {
                        Log.Warn($"[S] RelayStart requested reuse for non-existent session {sessionId}. Replying Disconnect to reset peer.");
                        byte[] disc = new byte[17];
                        disc[0] = (byte)MsgType.Disconnect;
                        sessionId.TryWriteBytes(disc.AsSpan(1, 16));
                        try { await _udp.SendAsync(disc, remoteEp, default); } catch { }
                        return;
                    }

                    if (isReused && sessionToRun != null)
                    {
                        if (!clientForceRelay && !sessionToRun.IsDirect)
                        {
                            _ = StartPunchingAsync(sessionToRun, cPublicEp, ct);
                        }
                        return;
                    }

                    if (sessionToRun != null)
                    {
                        if (!clientForceRelay)
                        {
                            _ = StartPunchingAsync(sessionToRun, cPublicEp, ct);
                        }
                        else
                        {
                            Log.Info($"[S] ForceRelay requested by client, skipping UDP punch to client {cPublicEp}.");
                        }

                        var runSession = sessionToRun;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                bool authOk = await runSession.AuthTcs.Task;
                                if (!authOk)
                                {
                                    Log.Warn($"[S] Auth failed for session {sessionId}, aborting bridge.");
                                    return;
                                }

                                await BridgeBackendAsync(runSession, rec, sessionId, ct);
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"[S] Target bridge error: {ex.Message}");
                            }
                            finally
                            {
                                lock (_sessionLock)
                                {
                                    if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, runSession))
                                    {
                                        _sessions.Remove(sessionId);
                                        Log.Info($"[S] Session {sessionId} closed.");
                                    }
                                }
                                runSession.Dispose();
                            }
                        }, ct);
                    }
                }
            }
            else
            {
                SendRelayStartAck(contextId, sessionId, remoteEp, 0);
            }
        }

        private void SendRelayStartAck(ushort contextId, Guid sessionId, EndPoint remoteEp, byte status)
        {
            if (contextId == 0) return;
            byte[] ack = new byte[20];
            ack[0] = (byte)MsgType.RelayStartAck;
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(1, 2), contextId);
            sessionId.TryWriteBytes(ack.AsSpan(3, 16));
            ack[19] = status;

            try { _ = _udp.SendAsync(ack, remoteEp, default); } catch { }
        }

        private class UdpChannelEntry
        {
            public readonly ZeroCopyUdpSocket Socket;
            public long LastActive;
            public UdpChannelEntry(ZeroCopyUdpSocket socket)
            {
                Socket = socket;
                LastActive = Environment.TickCount64;
            }
        }

        private async Task BridgeBackendAsync(TunnelSession session, ServerRecord rec, Guid sessionId, CancellationToken ct)
        {
            if (rec.IsTcp)
            {
                session.OnIncomingChannel = async (channelId) =>
                {
                    try
                    {
                        var targetClient = new TcpClient();
                        await targetClient.ConnectAsync(rec.TargetIp, rec.TargetPort, ct);
                        Log.Info($"[S] Connected to Target TCP {rec.TargetIp}:{rec.TargetPort} for Session {sessionId}, Channel {channelId}");
                        await session.RunChannelBridgeAsync(channelId, targetClient, ct);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[S] Target channel {channelId} error: {ex.Message}");
                        session.CloseChannel(channelId);
                        await session.SendFrameAsync(channelId, ChannelCmd.Close, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
                    }
                };
                session.StartKcpDriver(ct);
                await session.SessionClosedTask;
            }
            else
            {
                var channelSockets = new Dictionary<uint, UdpChannelEntry>();
                var targetEp = new IPEndPoint(IPAddress.Parse(rec.TargetIp), rec.TargetPort);
                Log.Info($"[S] Started Target UDP multiplex forwarder to {rec.TargetIp}:{rec.TargetPort} for Session {sessionId}");

                session.OnIncomingUdpPacket = async (channelId, data) =>
                {
                    try
                    {
                        UdpChannelEntry? entry;
                        lock (channelSockets)
                        {
                            if (!channelSockets.TryGetValue(channelId, out entry))
                            {
                                var targetSocket = new ZeroCopyUdpSocket(0);
                                entry = new UdpChannelEntry(targetSocket);
                                channelSockets[channelId] = entry;
                                session.IncrementActiveChannel();

                                _ = Task.Run(async () =>
                                {
                                    byte[] recvBuf = ArrayPool<byte>.Shared.Rent(rec.Mtu);
                                    try
                                    {
                                        while (!session.SessionToken.IsCancellationRequested)
                                        {
                                            var (readLen, _) = await targetSocket.ReceiveAsync(recvBuf, session.SessionToken);
                                            if (readLen <= 0) break;

                                            byte[] respCopy = recvBuf.AsSpan(0, readLen).ToArray();
                                            await session.SendUdpDataAsync(channelId, respCopy, session.SessionToken);
                                        }
                                    }
                                    catch { }
                                    finally
                                    {
                                        ArrayPool<byte>.Shared.Return(recvBuf);
                                        lock (channelSockets)
                                        {
                                            if (channelSockets.TryGetValue(channelId, out var cur) && ReferenceEquals(cur, entry))
                                            {
                                                channelSockets.Remove(channelId);
                                                session.DecrementActiveChannel();
                                                targetSocket.Dispose();
                                            }
                                        }
                                    }
                                }, session.SessionToken);
                            }
                        }

                        entry.LastActive = Environment.TickCount64;
                        await entry.Socket.SendAsync(data, targetEp, session.SessionToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[S] UDP channel {channelId} target send error: {ex.Message}");
                    }
                };

                session.OnUdpChannelClosed = (channelId) =>
                {
                    lock (channelSockets)
                    {
                        if (channelSockets.Remove(channelId, out var entry))
                        {
                            session.DecrementActiveChannel();
                            entry.Socket.Dispose();
                        }
                    }
                };

                _ = Task.Run(async () =>
                {
                    int timeoutMs = (rec.Timeout > 0 ? rec.Timeout : (session.ReuseInterval > 0 && session.ReuseInterval < 60 ? session.ReuseInterval : 60)) * 1000;
                    int checkInterval = Math.Min(1000, Math.Max(200, timeoutMs / 2));
                    while (!session.SessionToken.IsCancellationRequested)
                    {
                        await Task.Delay(checkInterval, session.SessionToken).ConfigureAwait(false);
                        long now = Environment.TickCount64;
                        List<KeyValuePair<uint, UdpChannelEntry>> expired = new();
                        lock (channelSockets)
                        {
                            foreach (var kvp in channelSockets)
                            {
                                if (now - kvp.Value.LastActive > timeoutMs)
                                {
                                    expired.Add(kvp);
                                }
                            }
                            foreach (var kvp in expired)
                            {
                                if (channelSockets.TryGetValue(kvp.Key, out var cur) && ReferenceEquals(cur, kvp.Value))
                                {
                                    channelSockets.Remove(kvp.Key);
                                    session.DecrementActiveChannel();
                                    kvp.Value.Socket.Dispose();
                                    _ = session.SendUdpCloseAsync(kvp.Key, session.SessionToken);
                                }
                            }
                        }
                    }
                }, session.SessionToken);

                await session.SessionClosedTask;
            }
        }

        public async ValueTask<bool> TryHandlePunchAsync(Guid sessionId, Guid peerDevId, EndPoint remoteEp, byte status, CancellationToken ct)
        {
            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null && !session.IsClosed)
            {
                if (session.ForceRelay)
                {
                    Log.Debug($"[S] Ignoring punch for session {sessionId} because client requested ForceRelay.");
                    return true;
                }

                session.SwitchToDirect(remoteEp);

                if (status == 2)
                {
                    // 回送打洞确认，避免死循环 ping-pong
                    byte[] ackBuf = new byte[36];
                    ackBuf[0] = (byte)MsgType.Punch;
                    BinaryPrimitives.WriteUInt16LittleEndian(ackBuf.AsSpan(1, 2), 0);
                    sessionId.TryWriteBytes(ackBuf.AsSpan(3, 16));
                    _config.DevId.TryWriteBytes(ackBuf.AsSpan(19, 16));
                    ackBuf[35] = 3; // Punch ACK
                    await _udp.SendAsync(ackBuf, remoteEp, ct);
                }
                else if (status == 3)
                {
                    // 收到对方打洞确认，双向直连打通，通知 P 端释放临时中继
                    session.NotifyDirectCommunicationEstablished();
                }
                return true;
            }
            return false;
        }

        public void TryHandleAuthReq(ReadOnlySpan<byte> span, EndPoint remoteEp)
        {
            if (span.Length < 57) return;
            Guid sessionId = new Guid(span.Slice(1, 16));
            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null && !session.IsClosed)
            {
                session.EnsureDirectRouteFromPeer(remoteEp);
                if (session.PasswordHash == null || session.PasswordHash.Length == 0)
                {
                    // S doesn't require password, just ack success
                    SendAuthRes(sessionId, remoteEp, true);
                    session.AuthTcs.TrySetResult(true);
                    return;
                }

                long tAuth = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(17, 8));
                byte[] clientHash = span.Slice(25, 32).ToArray();

                // Validate timestamp (within 15 seconds)
                long nowTicks = DateTime.UtcNow.Ticks;
                if (Math.Abs(nowTicks - tAuth) > 15 * 10000000L)
                {
                    Log.Warn($"[S] AuthReq timestamp out of bounds for session {sessionId}");
                    SendAuthRes(sessionId, remoteEp, false);
                    session.AuthTcs.TrySetResult(false);
                    return;
                }

                bool isChallenge = true;
                foreach (byte b in clientHash) if (b != 0) { isChallenge = false; break; }

                if (isChallenge)
                {
                    SendAuthRes(sessionId, remoteEp, false, ConfigProtector.GetMachineId());
                    return;
                }

                byte[] hashInput = new byte[session.PasswordHash.Length + 8];
                session.PasswordHash.CopyTo(hashInput, 0);
                BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(session.PasswordHash.Length, 8), tAuth);
                byte[] expectedHash = ManagedSHA256.ComputeHashBytes(hashInput);

                if (clientHash.SequenceEqual(expectedHash))
                {
                    Log.Info($"[S] Auth succeeded for session {sessionId}");
                    SendAuthRes(sessionId, remoteEp, true);
                    session.AuthTcs.TrySetResult(true);
                }
                else
                {
                    Log.Warn($"[S] Auth failed for session {sessionId}");
                    SendAuthRes(sessionId, remoteEp, false);
                    session.AuthTcs.TrySetResult(false);
                }
            }
        }

        private void SendAuthRes(Guid sessionId, EndPoint remoteEp, bool success, string? t1 = null)
        {
            byte[] res = new byte[18 + (t1 != null ? 4 + Encoding.UTF8.GetByteCount(t1) : 0)];
            res[0] = (byte)MsgType.AuthRes;
            sessionId.TryWriteBytes(res.AsSpan(1, 16));
            res[17] = (byte)(success ? 1 : 0);
            if (t1 != null)
            {
                ProtocolHelper.WriteString(res.AsSpan(18), t1);
            }
            _ = _udp.SendAsync(res, remoteEp, default);
        }

        public bool TryHandleData(Guid sessionId, ReadOnlySpan<byte> payload, EndPoint remoteEp)
        {
            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null && !session.IsClosed)
            {
                session.EnsureDirectRouteFromPeer(remoteEp);
                session.OnUdpDataReceived(payload);
                return true;
            }

            // 防御性处理：收到未知或已失效 Session 的数据（例如 S 重启后），主动向发送方回发 Disconnect
            // 告知对端（或 P 端）该会话已不存在，促使对端立即清理失效的复用通道
            byte[] disc = new byte[17];
            disc[0] = (byte)MsgType.Disconnect;
            sessionId.TryWriteBytes(disc.AsSpan(1, 16));
            try { _ = _udp.SendAsync(disc, remoteEp, default); } catch { }
            return false;
        }

        public bool TryHandleDisconnect(Guid sessionId, EndPoint remoteEp)
        {
            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null)
            {
                return session.TryHandleDisconnect(remoteEp);
            }
            return false;
        }

        private async Task<bool> StartPunchingAsync(TunnelSession session, IPEndPoint cPublicEp, CancellationToken ct)
        {
            byte[] punchBuf = new byte[36];
            punchBuf[0] = (byte)MsgType.Punch;
            BinaryPrimitives.WriteUInt16LittleEndian(punchBuf.AsSpan(1, 2), 0);
            session.SessionId.TryWriteBytes(punchBuf.AsSpan(3, 16));
            _config.DevId.TryWriteBytes(punchBuf.AsSpan(19, 16));
            punchBuf[35] = 2; // Direct Punch

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

            return session.IsDirect;
        }

        private async Task RunPKeepAliveAsync(string pServer, int intervalSec, CancellationToken ct)
        {
            Log.Info($"[S] Started NAT KeepAlive to P server '{pServer}' (interval: {intervalSec}s)");
            byte[] pingBuf = new byte[17];
            pingBuf[0] = (byte)MsgType.EchoReq;
            Guid.NewGuid().TryWriteBytes(pingBuf.AsSpan(1, 16));

            IPEndPoint? cachedEp = null;
            DateTime lastResolve = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);

                    if (cachedEp == null || (DateTime.UtcNow - lastResolve).TotalMinutes > 5)
                    {
                        var eps = await ProtocolHelper.ResolveAllEndPointsAsync(pServer, Constants.DefaultProxyPort);
                        if (eps != null && eps.Length > 0)
                        {
                            cachedEp = eps[0];
                            lastResolve = DateTime.UtcNow;
                        }
                    }

                    if (cachedEp != null)
                    {
                        await _udp.SendAsync(pingBuf, cachedEp, ct);
                        Log.Trace($"[S] Sent NAT KeepAlive to P ({cachedEp})");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Trace($"[S] NAT KeepAlive to P failed: {ex.Message}");
                    cachedEp = null;
                }
            }
        }
    }
}