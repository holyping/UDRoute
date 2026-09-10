using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 6. Client (C模式) - 监听本地连接并打洞
    // ==========================================
    public class ClientMode
    {
        private readonly AppConfig _config;
        private readonly ProxyMode? _localProxy;
        private readonly ZeroCopyUdpSocket _udp; // 与P和S通讯
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
        private int _contextCounter;
        private class PendingQueryRequest
        {
            public Guid SessionId { get; }
            public ushort ContextId { get; }
            public TaskCompletionSource<QueryResponse> Tcs { get; }
            public PendingQueryRequest(Guid sessionId, ushort contextId, TaskCompletionSource<QueryResponse> tcs)
            {
                SessionId = sessionId;
                ContextId = contextId;
                Tcs = tcs;
            }
        }
        private readonly Dictionary<ushort, PendingQueryRequest> _pendingQueries = new();
        private readonly object _pendingQueriesLock = new();
        private readonly Dictionary<string, TunnelSession> _reusableTunnels = new();
        private readonly object _tunnelStateLock = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _tunnelLocks = new();
        private class UdpTunnelContext
        {
            public readonly object Lock = new();
            public readonly Dictionary<EndPoint, uint> ClientToChannel = new();
            public readonly Dictionary<uint, EndPoint> ChannelToClient = new();
            public readonly Dictionary<EndPoint, long> ClientLastActive = new();
        }
        private readonly ConcurrentDictionary<Guid, UdpTunnelContext> _udpContexts = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IPEndPoint>> _pendingEchoes = new();

        public ClientMode(AppConfig config, ZeroCopyUdpSocket udp, ProxyMode? localProxy)
        {
            _config = config;
            _udp = udp;
            _localProxy = localProxy;
        }

        public List<ClientStatusInfo> GetStatusInfo()
        {
            var list = new List<ClientStatusInfo>();
            foreach (var rec in _config.ClientRecords)
            {
                list.Add(new ClientStatusInfo
                {
                    Config = $"{rec.Port}/{(rec.IsTcp?"tcp":"udp")}={rec.TargetName}@{rec.TargetServer}",
                    Status = "Waiting"
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
                list.Add(new DataChannelInfo { Source = s.ChannelDesc, Target = s.ActiveRemoteEp.ToString() ?? "", IsRelayed = !s.IsDirect, Rtt = s.Rtt });
            }
            return list;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var tasks = new List<Task>();
            foreach (var rec in _config.ClientRecords)
            {
                if (rec.IsTcp)
                    tasks.Add(AcceptTcpLoopAsync(rec, ct));
                else
                    tasks.Add(AcceptUdpLoopAsync(rec, ct));
            }

            // 启动 C 到各 P 目标服务器的 NAT KeepAlive 保活循环 (使用全局 KeepAlive 间隔)
            if (_config.KeepAlive > 0)
            {
                var distinctPServers = _config.ClientRecords
                    .Where(r => !r.IsThis && !string.IsNullOrWhiteSpace(r.TargetServer))
                    .Select(r => r.TargetServer)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var pServer in distinctPServers)
                {
                    tasks.Add(RunPKeepAliveAsync(pServer, _config.KeepAlive, ct));
                }
            }

            await Task.WhenAll(tasks);
        }

        public bool TryHandleQueryResponse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 36) return false;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            Guid sessionId = new Guid(data.Slice(3, 16));
            Guid devId = new Guid(data.Slice(19, 16));
            byte status = data[35];

            PendingQueryRequest? req = null;
            lock (_pendingQueriesLock)
            {
                if (contextId != 0)
                {
                    _pendingQueries.Remove(contextId, out req);
                }
                if (req == null)
                {
                    ushort matchedKey = 0;
                    foreach (var kvp in _pendingQueries)
                    {
                        if (kvp.Value.SessionId == sessionId)
                        {
                            matchedKey = kvp.Key;
                            req = kvp.Value;
                            break;
                        }
                    }
                    if (matchedKey != 0)
                    {
                        _pendingQueries.Remove(matchedKey);
                    }
                }
            }

            if (req == null) return false; // 重复响应包自动丢弃

            if (status == 1) // P 回复的查询成功
            {
                try
                {
                    int offset = 36;
                    var (sPublicEp, epLen) = ProtocolHelper.ReadIPEndPoint(data.Slice(offset));
                    offset += epLen;
                    int sWanPort = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    int timeout = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    long sTimestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
                    offset += 8;
                    bool reqPass = data[offset++] != 0;

                    KcpConfig? kcpConfig = null;
                    if (data.Length >= offset + 21)
                    {
                        var (cfg, kLen) = ProtocolHelper.ReadKcpConfig(data.Slice(offset));
                        kcpConfig = cfg;
                        offset += kLen;
                    }

                    var localEps = new List<IPEndPoint>();
                    if (offset < data.Length)
                    {
                        byte epCount = data[offset++];
                        for (int i = 0; i < epCount; i++)
                        {
                            if (offset >= data.Length) break;
                            var (ep, localEpLen) = ProtocolHelper.ReadIPEndPoint(data.Slice(offset));
                            localEps.Add(ep);
                            offset += localEpLen;
                        }
                    }

                    bool allowRelay = false;
                    if (offset < data.Length) allowRelay = data[offset++] != 0;

                    int tunnelReuseInterval = Constants.DefaultTunnelReuseInterval;
                    if (offset + 4 <= data.Length)
                    {
                        tunnelReuseInterval = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                        offset += 4;
                    }

                    long localRecvTicks = DateTime.UtcNow.Ticks;
                    req.Tcs.TrySetResult(new QueryResponse(true, status, null, devId, sPublicEp, sWanPort, timeout, sTimestamp, reqPass, kcpConfig, localEps, allowRelay, tunnelReuseInterval) { RecvLocalTicks = localRecvTicks });
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[C] TryHandleQueryResponse parsing failed: {ex}");
                    req.Tcs.TrySetResult(new QueryResponse(false, 0, $"Parse error: {ex.Message}", devId, null!, 0, 0, 0, false, null, null!));
                    return true;
                }
            }
            else // status != 1: 查询失败 (NotFound=0, SUnresponsive=4, StaleSession=5 等)
            {
                string? errorMsg = null;
                if (data.Length > 36)
                {
                    try
                    {
                        var (msg, _) = ProtocolHelper.ReadString(data.Slice(36));
                        errorMsg = msg;
                    }
                    catch { }
                }
                req.Tcs.TrySetResult(new QueryResponse(false, status, errorMsg, Guid.Empty, null!, 0, 0, 0, false, null, null!));
                return true;
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
                    Log.Debug($"[C] Ignoring punch for session {sessionId} because ForceRelay is enabled.");
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

        public void TryHandleAuthRes(ReadOnlySpan<byte> span, EndPoint remoteEp)
        {
            if (span.Length < 18) return;
            Guid sessionId = new Guid(span.Slice(1, 16));
            bool success = span[17] != 0;

            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null && !session.IsClosed)
            {
                session.EnsureDirectRouteFromPeer(remoteEp);
                if (success)
                {
                    Log.Info($"[C] Auth succeeded for session {sessionId}");
                    session.AuthTcs.TrySetResult(true);
                }
                else
                {
                    if (span.Length > 18)
                    {
                        var (t1, _) = ProtocolHelper.ReadString(span.Slice(18));
                        session.ServerT1 = t1;
                    }
                    else
                    {
                        Log.Warn($"[C] Auth failed for session {sessionId}");
                        session.AuthTcs.TrySetResult(false);
                    }
                }
            }
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
            return false;
        }

        public bool TryHandleDisconnect(Guid sessionId, EndPoint remoteEp)
        {
            PendingQueryRequest? req = null;
            lock (_pendingQueriesLock)
            {
                ushort matchedKey = 0;
                foreach (var kvp in _pendingQueries)
                {
                    if (kvp.Value.SessionId == sessionId)
                    {
                        matchedKey = kvp.Key;
                        req = kvp.Value;
                        break;
                    }
                }
                if (matchedKey != 0)
                {
                    _pendingQueries.Remove(matchedKey);
                }
            }
            if (req != null)
            {
                req.Tcs.TrySetResult(new QueryResponse(false, PunchStatus.NotFound, "Disconnected", Guid.Empty, null!, 0, 0, 0, false, null, null!));
            }
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

        public void TryHandleEchoResp(ReadOnlySpan<byte> data)
        {
            if (data.Length < 17) return;
            Guid sessionId = new Guid(data.Slice(1, 16));
            if (_pendingEchoes.TryRemove(sessionId, out var tcs))
            {
                IPEndPoint? ep = null;
                if (data.Length > 17)
                {
                    (ep, _) = ProtocolHelper.ReadIPEndPoint(data.Slice(17));
                }
                tcs.TrySetResult(ep!);
            }
        }

        public void TryUpdatePeerEndpoint(Guid sessionId, EndPoint remoteEp)
        {
            TunnelSession? session;
            lock (_sessionLock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session != null && !session.IsClosed)
            {
                session.EnsureDirectRouteFromPeer(remoteEp);
            }
        }

        private async Task AcceptTcpLoopAsync(ClientRecord rec, CancellationToken ct)
        {
            TcpListener listener = new TcpListener(IPAddress.IPv6Any, rec.Port);
            listener.Server.DualMode = true;
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"[C] Failed to start TCP listener on port {rec.Port}: {ex.Message}");
                throw;
            }

            string queryName = rec.TargetName.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase) || rec.TargetName.EndsWith("/udp", StringComparison.OrdinalIgnoreCase)
                ? rec.TargetName
                : $"{rec.TargetName}/tcp";

            Log.Info($"[C] TCP listening on port {rec.Port} -> {queryName}@{rec.TargetServer}");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            for (int retry = 0; retry < 2; retry++)
                            {
                                var session = await GetOrCreateTunnelSessionAsync(rec, queryName, ct, forceNew: retry > 0);
                                if (session == null)
                                {
                                    client.Close();
                                    return;
                                }

                                try
                                {
                                    await session.OpenAndBridgeChannelAsync(client, ct);
                                    break;
                                }
                                catch (Exception ex) when (retry == 0)
                                {
                                    Log.Warn($"[C] Data tunnel {session.SessionId} failed to open channel: {ex.Message}. Evicting stale tunnel and retrying with a fresh session...");
                                    string tunnelKey = $"{rec.TargetServer}@{queryName}";
                                    lock (_tunnelStateLock)
                                    {
                                        if (_reusableTunnels.TryGetValue(tunnelKey, out var curr) && ReferenceEquals(curr, session))
                                        {
                                            _reusableTunnels.Remove(tunnelKey);
                                        }
                                    }
                                    lock (_sessionLock)
                                    {
                                        if (_sessions.TryGetValue(session.SessionId, out var curS) && ReferenceEquals(curS, session))
                                        {
                                            _sessions.Remove(session.SessionId);
                                        }
                                    }
                                    session.Dispose();
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[C] TCP bridge error: {ex.Message}");
                            client.Close();
                        }
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"[C] Accept TCP error: {ex.Message}");
                    await Task.Delay(100, ct);
                }
            }
        }

        private async Task<bool> StartPunchingAsync(TunnelSession session, Guid expectedDevId, IPEndPoint sPublicEp, int sWanPort, List<IPEndPoint> localEps, CancellationToken ct)
        {
            byte[] punchBuf = new byte[36];
            punchBuf[0] = (byte)MsgType.Punch;
            BinaryPrimitives.WriteUInt16LittleEndian(punchBuf.AsSpan(1, 2), 0);
            session.SessionId.TryWriteBytes(punchBuf.AsSpan(3, 16));
            _config.DevId.TryWriteBytes(punchBuf.AsSpan(19, 16));
            punchBuf[35] = 2; // Direct Punch

            var candidates = new List<IPEndPoint> { sPublicEp };
            if (sWanPort > 0 && sWanPort != sPublicEp.Port)
            {
                candidates.Add(new IPEndPoint(sPublicEp.Address, sWanPort));
            }
            if (localEps != null)
            {
                foreach (var ep in localEps)
                {
                    if (!candidates.Contains(ep)) candidates.Add(ep);
                }
            }

            Log.Info($"[C] Session {session.SessionId} starting UDP punch to targets: {string.Join(", ", candidates)} (duration: {Constants.DefaultPunchRetries * Constants.DefaultPunchIntervalMs / 1000}s, interval: {Constants.DefaultPunchIntervalMs}ms)");

            for (int i = 0; i < Constants.DefaultPunchRetries && !session.IsDirect && !ct.IsCancellationRequested; i++)
            {
                foreach (var ep in candidates)
                {
                    await _udp.SendAsync(punchBuf, ep, ct);
                }
                await Task.Delay(Constants.DefaultPunchIntervalMs, ct);
            }

            if (session.IsDirect)
            {
                Log.Info($"[C] Session {session.SessionId} UDP punch successful! Now using direct P2P connection.");
            }
            else
            {
                Log.Info($"[C] Session {session.SessionId} UDP punch timed out. Continuing with Proxy relay.");
            }

            return session.IsDirect;
        }

        private async Task AcceptUdpLoopAsync(ClientRecord rec, CancellationToken ct)
        {
            using var localUdp = new ZeroCopyUdpSocket(rec.Port);
            string queryName = rec.TargetName.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase) || rec.TargetName.EndsWith("/udp", StringComparison.OrdinalIgnoreCase)
                ? rec.TargetName
                : $"{rec.TargetName}/udp";

            Log.Info($"[C] UDP listening on port {rec.Port} -> {queryName}@{rec.TargetServer}");

            byte[] poolBuf = ArrayPool<byte>.Shared.Rent(65535);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (len, clientEp) = await localUdp.ReceiveAsync(poolBuf, ct);
                        if (len == 0) continue;

                        byte[] packetCopy = poolBuf.AsSpan(0, len).ToArray();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var session = await GetOrCreateTunnelSessionAsync(rec, queryName, ct);
                                if (session == null || session.IsClosed)
                                {
                                    session = await GetOrCreateTunnelSessionAsync(rec, queryName, ct, forceNew: true);
                                    if (session == null || session.IsClosed) return;
                                }

                                if (!_udpContexts.TryGetValue(session.SessionId, out var ctx))
                                {
                                    lock (session)
                                    {
                                        if (!_udpContexts.TryGetValue(session.SessionId, out ctx))
                                        {
                                            ctx = new UdpTunnelContext();
                                            _udpContexts[session.SessionId] = ctx;

                                            session.OnClientUdpDataReceived = (chId, data) =>
                                            {
                                                if (_udpContexts.TryGetValue(session.SessionId, out var currentCtx))
                                                {
                                                    EndPoint? targetClientEp = null;
                                                    lock (currentCtx.Lock)
                                                    {
                                                        if (currentCtx.ChannelToClient.TryGetValue(chId, out var ep))
                                                        {
                                                            targetClientEp = ep;
                                                        }
                                                    }
                                                    if (targetClientEp != null)
                                                    {
                                                        _ = Task.Run(async () =>
                                                        {
                                                            try
                                                            {
                                                                await localUdp.SendAsync(data, targetClientEp, session.SessionToken);
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                Log.Warn($"[C] Failed to send reply to client {targetClientEp}: {ex.Message}");
                                                            }
                                                        });
                                                    }
                                                    else
                                                    {
                                                        Log.Warn($"[C] Received reply for unknown ch={chId}!");
                                                    }
                                                }
                                            };

                                            session.OnUdpChannelClosed = (chId) =>
                                            {
                                                if (_udpContexts.TryGetValue(session.SessionId, out var currentCtx))
                                                {
                                                    lock (currentCtx.Lock)
                                                    {
                                                        if (currentCtx.ChannelToClient.Remove(chId, out var cEp))
                                                        {
                                                            currentCtx.ClientToChannel.Remove(cEp);
                                                            currentCtx.ClientLastActive.Remove(cEp);
                                                            session.DecrementActiveChannel();
                                                        }
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
                                                    if (!_udpContexts.TryGetValue(session.SessionId, out var currentCtx))
                                                        break;
                                                    long now = Environment.TickCount64;
                                                    List<KeyValuePair<EndPoint, uint>> toClose = new();
                                                    lock (currentCtx.Lock)
                                                    {
                                                        var expiredEps = new List<EndPoint>();
                                                        foreach (var kvp in currentCtx.ClientLastActive)
                                                        {
                                                            if (now - kvp.Value > timeoutMs)
                                                            {
                                                                expiredEps.Add(kvp.Key);
                                                            }
                                                        }
                                                        foreach (var ep in expiredEps)
                                                        {
                                                            currentCtx.ClientLastActive.Remove(ep);
                                                            if (currentCtx.ClientToChannel.Remove(ep, out uint chId))
                                                            {
                                                                currentCtx.ChannelToClient.Remove(chId);
                                                                session.DecrementActiveChannel();
                                                                toClose.Add(new KeyValuePair<EndPoint, uint>(ep, chId));
                                                            }
                                                        }
                                                    }
                                                    foreach (var item in toClose)
                                                    {
                                                        _ = session.SendUdpCloseAsync(item.Value, session.SessionToken);
                                                    }
                                                }
                                            }, session.SessionToken);
                                        }
                                    }
                                }

                                uint channelId;
                                lock (ctx.Lock)
                                {
                                    if (!ctx.ClientToChannel.TryGetValue(clientEp, out channelId))
                                    {
                                        channelId = session.AllocateChannelId();
                                        ctx.ClientToChannel[clientEp] = channelId;
                                        ctx.ChannelToClient[channelId] = clientEp;
                                        session.IncrementActiveChannel();
                                        Log.Info($"[C] Assigned UDP channel {channelId} for client {clientEp} on Session {session.SessionId}");
                                    }
                                    ctx.ClientLastActive[clientEp] = Environment.TickCount64;
                                }
                                await session.SendUdpDataAsync(channelId, packetCopy, session.SessionToken);
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"[C] UDP forward packet error: {ex.Message}");
                            }
                        }, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[C] Accept UDP error: {ex.Message}");
                        await Task.Delay(100, ct);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuf);
            }
        }

        private async Task<bool> EnsureRelayActiveAsync(TunnelSession session, ClientRecord rec, string queryName, CancellationToken ct)
        {
            if (session.IsClosed) return false;

            if (rec.IsThis && _localProxy != null)
            {
                var sInfo = _localProxy.DirectQuery(queryName);
                if (sInfo == null) return false;
                // 通知 S 端刷新中继
                byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                try
                {
                    relayStartBuf[0] = (byte)MsgType.RelayStart;
                    BinaryPrimitives.WriteUInt16LittleEndian(relayStartBuf.AsSpan(1, 2), 0);
                    session.SessionId.TryWriteBytes(relayStartBuf.AsSpan(3, 16));
                    int offset = 19;
                    offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), queryName);
                    offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), _udp.LocalEndPoint);
                    bool allowRelay = _localProxy.IsRelayAllowed(sInfo);
                    bool effectiveForceRelay = rec.ForceRelay && allowRelay;
                    relayStartBuf[offset++] = (byte)((allowRelay ? 1 : 0) | (effectiveForceRelay ? 2 : 0) | 4);
                    await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(relayStartBuf);
                }
                return true;
            }

            var pEndPoint = session.ProxyEp ?? await ProtocolHelper.ResolveEndPointAsync(rec.TargetServer, Constants.DefaultProxyPort);
            if (pEndPoint == null) return false;

            ushort contextId = (ushort)Interlocked.Increment(ref _contextCounter);
            if (contextId == 0) contextId = (ushort)Interlocked.Increment(ref _contextCounter);

            var tcs = new TaskCompletionSource<QueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingQueriesLock)
            {
                _pendingQueries[contextId] = new PendingQueryRequest(session.SessionId, contextId, tcs);
            }

            byte[] qBuf = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                qBuf[0] = (byte)MsgType.Query;
                BinaryPrimitives.WriteUInt16LittleEndian(qBuf.AsSpan(1, 2), contextId);
                session.SessionId.TryWriteBytes(qBuf.AsSpan(3, 16));
                int qLen = 19 + ProtocolHelper.WriteString(qBuf.AsSpan(19), queryName);
                qBuf[qLen++] = (byte)((rec.ForceRelay ? 1 : 0) | 2); // Bit 0: ForceRelay, Bit 1: IsReuse

                byte[] qCopy = qBuf.AsSpan(0, qLen).ToArray();
                _ = ProtocolHelper.SendWithRetryAsync(_udp, qCopy, pEndPoint, tcs.Task, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(qBuf);
            }

            int waitTimeoutSec = Math.Max(Constants.DefaultProbeTimeout + 2, 7);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(waitTimeoutSec));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var resp = await tcs.Task.WaitAsync(linkedCts.Token);
                if (!resp.Success)
                {
                    switch (resp.StatusCode)
                    {
                        case PunchStatus.SUnresponsive:
                            Log.Warn($"[C] Relay refresh failed for '{queryName}': Target service S is unresponsive (health probe timed out / keepalive failed). {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                            break;
                        case PunchStatus.StaleSession:
                            Log.Warn($"[C] Relay refresh failed for '{queryName}': Target server S restarted, session is stale. {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                            break;
                        case PunchStatus.NotFound:
                        default:
                            Log.Warn($"[C] Relay refresh failed for '{queryName}': Service not registered on P. {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                            break;
                    }
                    return false;
                }
                return resp.AllowRelay;
            }
            catch
            {
                lock (_pendingQueriesLock)
                {
                    _pendingQueries.Remove(contextId);
                }
                return false;
            }
        }

        private async Task<bool> EnsureDirectActiveAsync(TunnelSession session, CancellationToken ct)
        {
            var targetEp = session.ActiveRemoteEp;
            if (targetEp == null) return false;

            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingEchoes[session.SessionId] = tcs;

            byte[] echoReq = ArrayPool<byte>.Shared.Rent(17);
            try
            {
                echoReq[0] = (byte)MsgType.EchoReq;
                session.SessionId.TryWriteBytes(echoReq.AsSpan(1, 16));
                await _udp.SendAsync(echoReq.AsMemory(0, 17), targetEp, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(echoReq);
            }

            using var timeoutCts = new CancellationTokenSource(600);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await tcs.Task.WaitAsync(linkedCts.Token);
                return true;
            }
            catch
            {
                _pendingEchoes.TryRemove(session.SessionId, out _);
                return false;
            }
        }

        private async Task<TunnelSession?> GetOrCreateTunnelSessionAsync(ClientRecord rec, string queryName, CancellationToken ct, bool forceNew = false)
        {
            string tunnelKey = $"{rec.TargetServer}@{queryName}";
            var sem = _tunnelLocks.GetOrAdd(tunnelKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                TunnelSession? existing = null;
                lock (_tunnelStateLock)
                {
                    if (!forceNew && _reusableTunnels.TryGetValue(tunnelKey, out existing))
                    {
                        if (existing.IsClosed)
                        {
                            _reusableTunnels.Remove(tunnelKey);
                            existing = null;
                        }
                    }
                }

                if (existing != null)
                {
                    if (!existing.IsDirect && existing.ActiveChannelCount == 0)
                    {
                        bool relayOk = await EnsureRelayActiveAsync(existing, rec, queryName, ct);
                        if (!relayOk)
                        {
                            Log.Warn($"[C] Reused relay tunnel {existing.SessionId} failed to re-activate on P (target server may have restarted or closed). Recreating tunnel...");
                            lock (_tunnelStateLock)
                            {
                                if (_reusableTunnels.TryGetValue(tunnelKey, out var cur) && ReferenceEquals(cur, existing))
                                {
                                    _reusableTunnels.Remove(tunnelKey);
                                }
                            }
                            existing.Dispose();
                            existing = null;
                        }
                    }
                    else if (existing.IsDirect && existing.ActiveChannelCount == 0)
                    {
                        bool directOk = await EnsureDirectActiveAsync(existing, ct);
                        if (!directOk)
                        {
                            Log.Warn($"[C] Reused direct tunnel {existing.SessionId} failed echo probe (peer unreachable or network changed). Recreating tunnel...");
                            lock (_tunnelStateLock)
                            {
                                if (_reusableTunnels.TryGetValue(tunnelKey, out var cur) && ReferenceEquals(cur, existing))
                                {
                                    _reusableTunnels.Remove(tunnelKey);
                                }
                            }
                            existing.Dispose();
                            existing = null;
                        }
                    }

                    if (existing != null)
                    {
                        if (rec.IsTcp)
                        {
                            Log.Info($"[C] Reusing active data tunnel {existing.SessionId} for {queryName} on port {rec.Port}");
                        }
                        return existing;
                    }
                }

                lock (_tunnelStateLock)
                {
                    _reusableTunnels.Remove(tunnelKey);
                }

                var session = await CreateTunnelSessionAsync(rec, queryName, ct).ConfigureAwait(false);
                if (session != null)
                {
                    lock (_tunnelStateLock)
                    {
                        if (session.ReuseInterval >= 0)
                        {
                            _reusableTunnels[tunnelKey] = session;
                        }
                    }

                    _ = session.SessionClosedTask.ContinueWith(t =>
                    {
                        lock (_tunnelStateLock)
                        {
                            if (_reusableTunnels.TryGetValue(tunnelKey, out var cur) && ReferenceEquals(cur, session))
                            {
                                _reusableTunnels.Remove(tunnelKey);
                            }
                        }
                        lock (_sessionLock)
                        {
                            if (_sessions.TryGetValue(session.SessionId, out var curS) && ReferenceEquals(curS, session))
                            {
                                _sessions.Remove(session.SessionId);
                                Log.Info($"[C] Session {session.SessionId} closed.");
                            }
                        }
                        _udpContexts.TryRemove(session.SessionId, out _);
                        session.Dispose();
                    });
                }
                return session;
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task<TunnelSession?> CreateTunnelSessionAsync(ClientRecord rec, string queryName, CancellationToken ct)
        {
            Guid sessionId = Guid.NewGuid();

            if (rec.IsThis && _localProxy != null)
            {
                var sInfo = _localProxy.DirectQuery(queryName);
                if (sInfo != null)
                {
                    long queryRecvLocalTicks = DateTime.UtcNow.Ticks;
                    if (sInfo.RequiresPassword && (rec.Password == null || rec.Password.Length == 0))
                    {
                        if (rec.IsConsolePipe)
                        {
                            Log.Warn($"[C] Password required for {queryName}, but running in con: pipe mode without password.");
                        }
                        else
                        {
                            Log.Info($"[C] Password required for {queryName}.");
                            string input = PromptAndReadPassword(queryName);
                            if (input.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                            {
                                rec.Password = Convert.FromBase64String(input.Substring(9));
                            }
                            else
                            {
                                rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(input));
                            }
                        }
                    }

                    bool allowRelay = _localProxy?.IsRelayAllowed(sInfo) ?? true;
                    bool effectiveForceRelay = rec.ForceRelay && allowRelay;
                    if (!allowRelay && rec.ForceRelay)
                    {
                        Log.Info($"[C] AllowRelay is false for {queryName}, ignoring ForceRelay.");
                    }

                    // 通知 S 端发起准备与打洞 (RelayStart)
                    byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                    try
                    {
                        relayStartBuf[0] = (byte)MsgType.RelayStart;
                        BinaryPrimitives.WriteUInt16LittleEndian(relayStartBuf.AsSpan(1, 2), 0);
                        sessionId.TryWriteBytes(relayStartBuf.AsSpan(3, 16));
                        int offset = 19;
                        offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), queryName);
                        offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), _udp.LocalEndPoint);
                        relayStartBuf[offset++] = (byte)((allowRelay ? 1 : 0) | (effectiveForceRelay ? 2 : 0));
                        await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(relayStartBuf);
                    }

                    var session = new TunnelSession(_udp, sInfo.PublicEp, sessionId, rec.Mtu, rec.IsTcp, sInfo.KcpConfig, sInfo.Timeout, sInfo.TunnelReuseInterval, rec.KeepAlive);
                    session.ChannelDesc = rec.Port.ToString();
                    session.ForceRelay = effectiveForceRelay;
                    lock (_sessionLock)
                    {
                        _sessions[sessionId] = session;
                    }

                    if (rec.Password != null)
                    {
                        long approxSTimestamp = sInfo.STimestamp + (queryRecvLocalTicks - sInfo.PRecvTimeTicks);
                        bool authOk = await session.AuthenticateClientAsync(rec.Password, approxSTimestamp, queryRecvLocalTicks, ct);
                        if (!authOk)
                        {
                            Log.Warn($"[C] Auth failed for session {sessionId}");
                            lock (_sessionLock)
                            {
                                if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, session))
                                {
                                    _sessions.Remove(sessionId);
                                }
                            }
                            session.Dispose();
                            return null;
                        }
                    }

                    return session;
                }
                else
                {
                    Log.Warn($"[C] DirectQuery failed for {queryName}");
                    return null;
                }
            }

            // 常规模式: 向 P 发起 Query (UDP，带 ContextId 连发3次)
            var pEndPoint = await ProtocolHelper.ResolveEndPointAsync(rec.TargetServer, Constants.DefaultProxyPort);
            if (pEndPoint == null)
            {
                Log.Error($"[C] Failed to resolve P server: {rec.TargetServer}");
                return null;
            }

            ushort contextId = (ushort)Interlocked.Increment(ref _contextCounter);
            if (contextId == 0) contextId = (ushort)Interlocked.Increment(ref _contextCounter);

            var tcs = new TaskCompletionSource<QueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingQueriesLock)
            {
                _pendingQueries[contextId] = new PendingQueryRequest(sessionId, contextId, tcs);
            }

            byte[] qBuf = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                qBuf[0] = (byte)MsgType.Query;
                BinaryPrimitives.WriteUInt16LittleEndian(qBuf.AsSpan(1, 2), contextId);
                sessionId.TryWriteBytes(qBuf.AsSpan(3, 16));
                int qLen = 19 + ProtocolHelper.WriteString(qBuf.AsSpan(19), queryName);
                qBuf[qLen++] = (byte)(rec.ForceRelay ? 1 : 0);

                byte[] qCopy = qBuf.AsSpan(0, qLen).ToArray();
                _ = ProtocolHelper.SendWithRetryAsync(_udp, qCopy, pEndPoint, tcs.Task, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(qBuf);
            }

            int waitTimeoutSec = Math.Max(Constants.DefaultProbeTimeout + 2, 7);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(waitTimeoutSec));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            QueryResponse resp;
            try
            {
                resp = await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch
            {
                lock (_pendingQueriesLock)
                {
                    _pendingQueries.Remove(contextId);
                }
                Log.Warn($"[C] Query timeout for session {sessionId}: P server '{rec.TargetServer}' did not respond within {waitTimeoutSec}s (network unreachable or P offline).");
                return null;
            }

            if (!resp.Success)
            {
                switch (resp.StatusCode)
                {
                    case PunchStatus.SUnresponsive:
                        Log.Error($"[C] Connection failed for '{queryName}': Target service S is unresponsive (health probe timed out / NAT keepalive failed). {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                        break;
                    case PunchStatus.StaleSession:
                        Log.Warn($"[C] Connection failed for '{queryName}': Target server S restarted since session was created. {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                        break;
                    case PunchStatus.NotFound:
                    default:
                        Log.Warn($"[C] Connection failed for '{queryName}': Service '{queryName}' not registered on P server '{rec.TargetServer}'. {(resp.ErrorMessage != null ? $"Details: {resp.ErrorMessage}" : "")}");
                        break;
                }
                return null;
            }

            if (resp.RequiresPassword && (rec.Password == null || rec.Password.Length == 0))
            {
                if (rec.IsConsolePipe)
                {
                    Log.Warn($"[C] Password required for {queryName}, but running in con: pipe mode without password.");
                }
                else
                {
                    Log.Info($"[C] Password required for {queryName}.");
                    string input = PromptAndReadPassword(queryName);
                    if (input.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                    {
                        rec.Password = Convert.FromBase64String(input.Substring(9));
                    }
                    else
                    {
                        rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(input));
                    }
                }
            }
            TunnelSession tunnelSession;
            if (!resp.AllowRelay)
            {
                if (rec.ForceRelay)
                {
                    Log.Info($"[C] Server/Proxy does not allow relay (AllowRelay=false) for {queryName}, ignoring ForceRelay.");
                }
                Log.Info($"[C] P relay not allowed for {queryName}. Waiting for UDP punch before starting tunnel...");
                tunnelSession = new TunnelSession(_udp, resp.ServerPublicEp, sessionId, rec.Mtu, rec.IsTcp, resp.KcpConfig, resp.Timeout, resp.TunnelReuseInterval, rec.KeepAlive);
                tunnelSession.ChannelDesc = rec.Port.ToString();
                tunnelSession.ForceRelay = false;
                lock (_sessionLock)
                {
                    _sessions[sessionId] = tunnelSession;
                }

                bool punchSuccess = await StartPunchingAsync(tunnelSession, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                if (!punchSuccess)
                {
                    Log.Warn($"[C] P relay is disabled and UDP punch timed out for session {sessionId}.");
                    lock (_sessionLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, tunnelSession))
                        {
                            _sessions.Remove(sessionId);
                        }
                    }
                    tunnelSession.Dispose();
                    return null;
                }
            }
            else
            {
                tunnelSession = new TunnelSession(_udp, pEndPoint, sessionId, rec.Mtu, rec.IsTcp, resp.KcpConfig, resp.Timeout, resp.TunnelReuseInterval, rec.KeepAlive);
                tunnelSession.ChannelDesc = rec.Port.ToString();
                bool isForceRelay = rec.ForceRelay;
                tunnelSession.ForceRelay = isForceRelay;
                tunnelSession.ProxyEp = pEndPoint;
                lock (_sessionLock)
                {
                    _sessions[sessionId] = tunnelSession;
                }

                if (!isForceRelay)
                {
                    _ = StartPunchingAsync(tunnelSession, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                }
                else
                {
                    Log.Info($"[C] ForceRelay is enabled for session {sessionId}, skipping UDP punch.");
                }
            }

            if (rec.Password != null)
            {
                bool authOk = await tunnelSession.AuthenticateClientAsync(rec.Password, resp.STimestamp, resp.RecvLocalTicks, ct);
                if (!authOk)
                {
                    Log.Warn($"[C] Auth failed for session {sessionId}");
                    lock (_sessionLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, tunnelSession))
                        {
                            _sessions.Remove(sessionId);
                        }
                    }
                    tunnelSession.Dispose();
                    return null;
                }
            }

            return tunnelSession;
        }

        public static Action<string>? CustomPasswordPromptWriter;
        public static Func<string>? CustomPasswordReader;

        private string PromptAndReadPassword(string queryName)
        {
            if (CustomPasswordPromptWriter != null)
                CustomPasswordPromptWriter($"[C] Password for {queryName}: ");
            else
                Console.Write($"\r[C] Password for {queryName}: ");

            if (CustomPasswordReader != null)
                return CustomPasswordReader();

            string pass = "";
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (pass.Length > 0) pass = pass.Substring(0, pass.Length - 1);
                    }
                    else if (key.KeyChar != '\0')
                    {
                        pass += key.KeyChar;
                    }
                }
                Console.WriteLine();
            }
            catch (InvalidOperationException)
            {
                pass = Console.ReadLine() ?? "";
            }
            return pass;
        }

        private async Task RunPKeepAliveAsync(string pServer, int intervalSec, CancellationToken ct)
        {
            Log.Info($"[C] Enabled on-demand NAT KeepAlive to P server '{pServer}' (active only when relay connections are working, interval: {intervalSec}s)");
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

                    // 仅当有正在工作的走P中继连接时才向P发送保活包，无连接时停止保活以节省P端算力与带宽
                    bool hasCandidateSessions;
                    lock (_sessionLock)
                    {
                        hasCandidateSessions = _sessions.Values.Any(s => !s.IsClosed && !s.IsDirect && s.ActiveChannelCount > 0);
                    }

                    if (!hasCandidateSessions)
                    {
                        continue;
                    }

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
                        bool hasActiveForEp;
                        lock (_sessionLock)
                        {
                            hasActiveForEp = _sessions.Values.Any(s =>
                                !s.IsClosed &&
                                !s.IsDirect &&
                                s.ActiveChannelCount > 0 &&
                                (s.ProxyEp == null || ProtocolHelper.AreEndPointsEqual(s.ProxyEp, cachedEp)));
                        }

                        if (hasActiveForEp)
                        {
                            await _udp.SendAsync(pingBuf, cachedEp, ct);
                            Log.Trace($"[C] Sent NAT KeepAlive to P ({cachedEp}) for active working relay session");
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Trace($"[C] NAT KeepAlive to P failed: {ex.Message}");
                    cachedEp = null;
                }
            }
        }

        private record QueryResponse(
            bool Success,
            byte StatusCode,
            string? ErrorMessage,
            Guid DevId,
            IPEndPoint ServerPublicEp,
            int ServerWanPort,
            int Timeout,
            long STimestamp,
            bool RequiresPassword,
            KcpConfig? KcpConfig,
            List<IPEndPoint> LocalEps,
            bool AllowRelay = true,
            int TunnelReuseInterval = Constants.DefaultTunnelReuseInterval)
        {
            /// <summary>
            /// 收到 P 响应时的本地时间 Ticks
            /// 差值 ClockOffset = STimestamp - RecvLocalTicks
            /// 加密/鉴权时计算最终 ts：finalTs = DateTime.UtcNow.Ticks + ClockOffset
            /// </summary>
            public long RecvLocalTicks { get; init; } = DateTime.UtcNow.Ticks;
        }
    }
}