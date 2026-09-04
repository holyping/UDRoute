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
        private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<QueryResponse>> _pendingQueries = new();

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
            foreach (var s in _sessions.Values)
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

            await Task.WhenAll(tasks);
        }

        public bool TryHandleQueryResponse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 34) return false;

            Guid sessionId = new Guid(data.Slice(1, 16));
            Guid devId = new Guid(data.Slice(17, 16));
            byte status = data[33];

            if (status == 1) // P 回复的查询成功
            {
                int offset = 34;
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

                bool allowRelay = (data[offset] & 1) != 0;
                bool sForceRelay = (data[offset] & 2) != 0;
                offset++;

                if (_pendingQueries.TryRemove(sessionId, out var tcs))
                {
                    tcs.TrySetResult(new QueryResponse(true, devId, sPublicEp, sWanPort, timeout, sTimestamp, reqPass, kcpConfig, localEps, allowRelay, sForceRelay));
                    return true;
                }
            }
            else if (status == 0) // P 回复的目标不存在
            {
                if (_pendingQueries.TryRemove(sessionId, out var tcs))
                {
                    tcs.TrySetResult(new QueryResponse(false, Guid.Empty, null!, 0, 0, 0, false, null, null!));
                    return true;
                }
            }

            return false;
        }

        public async ValueTask<bool> TryHandlePunchAsync(Guid sessionId, Guid peerDevId, EndPoint remoteEp, byte status, CancellationToken ct)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (_config.ForceRelay || session.ForceRelay)
                {
                    Log.Debug($"[C] Ignoring punch for session {sessionId} because ForceRelay is enabled.");
                    return true;
                }

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

        public void TryHandleAuthRes(ReadOnlySpan<byte> span, EndPoint remoteEp)
        {
            if (span.Length < 18) return;
            Guid sessionId = new Guid(span.Slice(1, 16));
            bool success = span[17] != 0;

            if (_sessions.TryGetValue(sessionId, out var session))
            {
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

            Log.Info($"[C] TCP listening on port {rec.Port} -> {rec.TargetName}@{rec.TargetServer}");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    Guid sessionId = Guid.NewGuid();

                    Log.Debug($"[C] New TCP connection on port {rec.Port}, Session: {sessionId}");

                    string queryName = rec.TargetName.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase) || rec.TargetName.EndsWith("/udp", StringComparison.OrdinalIgnoreCase)
                        ? rec.TargetName
                        : $"{rec.TargetName}/tcp";

                    // 1. 优化模式: 直接查本地 Proxy 内存
                    if (rec.IsThis && _localProxy != null)
                    {
                        var sInfo = _localProxy.DirectQuery(queryName);
                        if (sInfo != null)
                        {
                            if (sInfo.RequiresPassword && (rec.Password == null || rec.Password.Length == 0))
                            {
                                Log.Info($"[C] Password required for {queryName}.");
                                Console.Write($"Password for {queryName}: ");
                                string input = ReadPassword();
                                rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(input));
                            }

                            // 通知 S 端发起准备与打洞 (RelayStart)
                            byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                            try
                            {
                                relayStartBuf[0] = (byte)MsgType.RelayStart;
                                sessionId.TryWriteBytes(relayStartBuf.AsSpan(1, 16));
                                int offset = 17;
                                offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), queryName);
                                offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), _udp.LocalEndPoint);
                                relayStartBuf[offset++] = (byte)(((_localProxy?.IsRelayAllowed(sInfo) ?? true) ? 1 : 0) | (rec.ForceRelay ? 2 : 0));
                                await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(relayStartBuf);
                            }

                            var session = new TunnelSession(_udp, sInfo.PublicEp, sessionId, rec.Mtu, rec.IsTcp, sInfo.KcpConfig, sInfo.Timeout);
                            session.ChannelDesc = rec.Port.ToString();
                            session.ForceRelay = rec.ForceRelay || sInfo.ForceRelay;
                            _sessions[sessionId] = session;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    if (rec.Password != null)
                                    {
                                        bool authOk = await session.AuthenticateClientAsync(rec.Password, sInfo.STimestamp, sInfo.PRecvTimeTicks, ct);
                                        if (!authOk) { Log.Warn($"[C] Auth failed for session {sessionId}"); return; }
                                    }
                                    await session.RunTcpBridgeAsync(client, ct);
                                }
                                finally { _sessions.TryRemove(sessionId, out _); session.Dispose(); }
                            }, ct);
                        }
                        else
                        {
                            Log.Warn($"[C] DirectQuery failed for {queryName}");
                            client.Close();
                        }
                        continue;
                    }

                    // 2. 常规模式: 向 P 发起 Query (UDP)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var pEndPoint = await ProtocolHelper.ResolveEndPointAsync(rec.TargetServer, Constants.DefaultProxyPort);
                            if (pEndPoint == null)
                            {
                                Log.Error($"[C] Failed to resolve P server: {rec.TargetServer}");
                                client.Close();
                                return;
                            }

                            var tcs = new TaskCompletionSource<QueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                            _pendingQueries[sessionId] = tcs;

                            // 发送 Query 包: [MsgType 1][SessionId 16][TargetName string]
                            byte[] qBuf = ArrayPool<byte>.Shared.Rent(256);
                            try
                            {
                                qBuf[0] = (byte)MsgType.Query;
                                sessionId.TryWriteBytes(qBuf.AsSpan(1, 16));
                                int qLen = 17 + ProtocolHelper.WriteString(qBuf.AsSpan(17), queryName);
                                qBuf[qLen++] = (byte)(rec.ForceRelay ? 1 : 0);
                                await _udp.SendAsync(qBuf.AsMemory(0, qLen), pEndPoint, ct);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(qBuf);
                            }

                            // 等待 P 的查询响应 (5秒超时)
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                            QueryResponse resp;
                            try
                            {
                                resp = await tcs.Task.WaitAsync(linkedCts.Token);
                            }
                            catch
                            {
                                _pendingQueries.TryRemove(sessionId, out _);
                                Log.Warn($"[C] Query timeout for session {sessionId}");
                                client.Close();
                                return;
                            }

                            if (!resp.Success)
                            {
                                Log.Warn($"[C] P returned NotFound for {queryName}");
                                client.Close();
                                return;
                            }

                            if (resp.RequiresPassword && (rec.Password == null || rec.Password.Length == 0))
                            {
                                Log.Info($"[C] Password required for {queryName}.");
                                Console.Write($"Password for {queryName}: ");
                                string input = ReadPassword();
                                rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(input));
                            }

                            if (!resp.AllowRelay)
                            {
                                Log.Info($"[C] P relay not allowed for {queryName}. Waiting for UDP punch before bridging TCP...");
                                var session = new TunnelSession(_udp, resp.ServerPublicEp, sessionId, rec.Mtu, rec.IsTcp, resp.KcpConfig, resp.Timeout);
                                session.ChannelDesc = rec.Port.ToString();
                                _sessions[sessionId] = session;

                                bool punchSuccess = await StartPunchingAsync(session, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                                if (!punchSuccess)
                                {
                                    Log.Warn($"[C] P relay is disabled and UDP punch timed out for session {sessionId}. Connection closed.");
                                    _sessions.TryRemove(sessionId, out _);
                                    session.Dispose();
                                    client.Close();
                                    return;
                                }

                                Log.Info($"[C] UDP punch succeeded without P relay! Starting direct P2P bridge for session {sessionId}.");
                                try
                                {
                                    if (rec.Password != null)
                                    {
                                        bool authOk = await session.AuthenticateClientAsync(rec.Password, resp.STimestamp, DateTime.UtcNow.Ticks, ct);
                                        if (!authOk) { Log.Warn($"[C] Auth failed for session {sessionId}"); return; }
                                    }
                                    await session.RunTcpBridgeAsync(client, ct);
                                }
                                finally
                                {
                                    if (_sessions.TryRemove(sessionId, out _))
                                    {
                                        Log.Info($"[C] Session {sessionId} closed.");
                                    }
                                    session.Dispose();
                                }
                            }
                            else
                            {
                                // 初始通过 P 中继通信，应用协商好的 KCP 参数
                                var session = new TunnelSession(_udp, pEndPoint, sessionId, rec.Mtu, rec.IsTcp, resp.KcpConfig, resp.Timeout);
                                session.ChannelDesc = rec.Port.ToString();
                                bool forceRelay = rec.ForceRelay || resp.ServerForceRelay;
                                session.ForceRelay = forceRelay;
                                _sessions[sessionId] = session;

                                // 并行启动向 S 的公网地址和 WanPort 进行 UDP 打洞
                                if (!forceRelay)
                                {
                                    _ = StartPunchingAsync(session, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                                }
                                else
                                {
                                    Log.Info($"[C] ForceRelay is enabled (Local: {rec.ForceRelay}, Remote: {resp.ServerForceRelay}) for session {sessionId}, skipping UDP punch.");
                                }

                                try
                                {
                                    if (rec.Password != null)
                                    {
                                        bool authOk = await session.AuthenticateClientAsync(rec.Password, resp.STimestamp, DateTime.UtcNow.Ticks, ct);
                                        if (!authOk) { Log.Warn($"[C] Auth failed for session {sessionId}"); return; }
                                    }
                                    await session.RunTcpBridgeAsync(client, ct);
                                }
                                finally
                                {
                                    if (_sessions.TryRemove(sessionId, out _))
                                    {
                                        Log.Info($"[C] Session {sessionId} closed.");
                                    }
                                    session.Dispose();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[C] Session {sessionId} error: {ex.Message}");
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
            byte[] punchBuf = new byte[34];
            punchBuf[0] = (byte)MsgType.Punch;
            session.SessionId.TryWriteBytes(punchBuf.AsSpan(1, 16));
            _config.DevId.TryWriteBytes(punchBuf.AsSpan(17, 16));
            punchBuf[33] = 2; // Direct Punch

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

            Log.Info($"[C] Session {session.SessionId} starting UDP punch to targets: {string.Join(", ", candidates)}");

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
            // UDP 端口代理转发循环
            using var localUdp = new ZeroCopyUdpSocket(rec.Port);
            string queryName = rec.TargetName.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase) || rec.TargetName.EndsWith("/udp", StringComparison.OrdinalIgnoreCase)
                ? rec.TargetName
                : $"{rec.TargetName}/udp";

            Log.Info($"[C] UDP listening on port {rec.Port} -> {queryName}@{rec.TargetServer}");

            byte[] poolBuf = ArrayPool<byte>.Shared.Rent(65535);
            var clientChannelMap = new ConcurrentDictionary<EndPoint, Channel<byte[]>>();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (len, clientEp) = await localUdp.ReceiveAsync(poolBuf, ct);
                        if (len == 0) continue;

                        byte[] packetCopy = poolBuf.AsSpan(0, len).ToArray();

                        if (!clientChannelMap.TryGetValue(clientEp, out var channel))
                        {
                            channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                            clientChannelMap[clientEp] = channel;

                            Guid sessionId = Guid.NewGuid();

                            _ = Task.Run(async () =>
                            {
                                TunnelSession? session = null;
                                QueryResponse? resp = null;
                                try
                                {
                                    if (rec.IsThis && _localProxy != null)
                                    {
                                        var sInfo = _localProxy.DirectQuery(queryName);
                                        if (sInfo != null)
                                        {
                                            // 通知 S 端发起准备与打洞 (RelayStart)
                                            byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                                            try
                                            {
                                                relayStartBuf[0] = (byte)MsgType.RelayStart;
                                                sessionId.TryWriteBytes(relayStartBuf.AsSpan(1, 16));
                                                int offset = 17;
                                                offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), queryName);
                                                offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), _udp.LocalEndPoint);
                                                relayStartBuf[offset++] = (byte)(((_localProxy?.IsRelayAllowed(sInfo) ?? true) ? 1 : 0) | (rec.ForceRelay ? 2 : 0));
                                                await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                                            }
                                            finally
                                            {
                                                ArrayPool<byte>.Shared.Return(relayStartBuf);
                                            }

                                            session = new TunnelSession(_udp, sInfo.PublicEp, sessionId, rec.Mtu, isTcp: false, null, sInfo.Timeout);
                                            session.ForceRelay = rec.ForceRelay || sInfo.ForceRelay;
                                        }
                                        else
                                        {
                                            Log.Warn($"[C] UDP DirectQuery failed for {queryName}");
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        var pEndPoint = await ProtocolHelper.ResolveEndPointAsync(rec.TargetServer, Constants.DefaultProxyPort);
                                        if (pEndPoint == null)
                                        {
                                            Log.Error($"[C] Failed to resolve P server: {rec.TargetServer}");
                                            return;
                                        }

                                        var tcs = new TaskCompletionSource<QueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        _pendingQueries[sessionId] = tcs;

                                        byte[] qBuf = ArrayPool<byte>.Shared.Rent(256);
                                        try
                                        {
                                            qBuf[0] = (byte)MsgType.Query;
                                            sessionId.TryWriteBytes(qBuf.AsSpan(1, 16));
                                            int qLen = 17 + ProtocolHelper.WriteString(qBuf.AsSpan(17), queryName);
                                            qBuf[qLen++] = (byte)(rec.ForceRelay ? 1 : 0);
                                            await _udp.SendAsync(qBuf.AsMemory(0, qLen), pEndPoint, ct);
                                        }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(qBuf);
                                        }

                                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                                        try
                                        {
                                            resp = await tcs.Task.WaitAsync(linkedCts.Token);
                                        }
                                        catch
                                        {
                                            _pendingQueries.TryRemove(sessionId, out _);
                                            Log.Warn($"[C] UDP Query timeout for session {sessionId}");
                                            return;
                                        }

                                        if (!resp.Success)
                                        {
                                            Log.Warn($"[C] P returned NotFound for UDP service {queryName}");
                                            return;
                                        }

                                        if (resp.RequiresPassword && (rec.Password == null || rec.Password.Length == 0))
                                        {
                                            Log.Info($"[C] Password required for {queryName}.");
                                            Console.Write($"Password for {queryName}: ");
                                            string input = ReadPassword();
                                            rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(input));
                                        }

                                        if (!resp.AllowRelay)
                                        {
                                            Log.Info($"[C] P relay not allowed for UDP service {queryName}. Waiting for UDP punch before bridging UDP...");
                                            session = new TunnelSession(_udp, resp.ServerPublicEp, sessionId, rec.Mtu, isTcp: false, null, resp.Timeout);
                                            bool punchSuccess = await StartPunchingAsync(session, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                                            if (!punchSuccess)
                                            {
                                                Log.Warn($"[C] P relay is disabled and UDP punch timed out for UDP session {sessionId}.");
                                                clientChannelMap.TryRemove(clientEp, out _);
                                                session.Dispose();
                                                return;
                                            }
                                        }
                                        else
                                        {
                                            session = new TunnelSession(_udp, pEndPoint, sessionId, rec.Mtu, isTcp: false, null, resp.Timeout);
                                            bool forceRelay = rec.ForceRelay || resp.ServerForceRelay;
                                            if (!forceRelay)
                                            {
                                                _ = StartPunchingAsync(session, resp.DevId, resp.ServerPublicEp, resp.ServerWanPort, resp.LocalEps, ct);
                                            }
                                            else
                                            {
                                                Log.Info($"[C] ForceRelay is enabled (Local: {rec.ForceRelay}, Remote: {resp.ServerForceRelay}) for UDP session {sessionId}, skipping UDP punch.");
                                            }
                                        }
                                    }

                                    session.ChannelDesc = rec.Port.ToString();
                                    _sessions[sessionId] = session;

                                    if (rec.Password != null)
                                    {
                                        long st = 0, pt = 0;
                                        if (rec.IsThis && _localProxy != null)
                                        {
                                            var info = _localProxy.DirectQuery(queryName);
                                            st = info?.STimestamp ?? 0;
                                            pt = info?.PRecvTimeTicks ?? 0;
                                        }
                                        else if (resp != null)
                                        {
                                            st = resp.STimestamp;
                                            pt = DateTime.UtcNow.Ticks;
                                        }
                                        bool authOk = await session.AuthenticateClientAsync(rec.Password, st, pt, ct);
                                        if (!authOk)
                                        {
                                            Log.Warn($"[C] UDP Auth failed for session {sessionId}");
                                            clientChannelMap.TryRemove(clientEp, out _);
                                            session.Dispose();
                                            return;
                                        }
                                    }

                                    var sessionToken = session.SessionToken;
                                    byte[] sendBuf = ArrayPool<byte>.Shared.Rent(rec.Mtu + 17);

                                    // 1. 回传循环 (S/P -> Client)
                                    var inboundTask = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            while (await session.InboundChannel.Reader.WaitToReadAsync(sessionToken))
                                            {
                                                while (session.InboundChannel.Reader.TryRead(out var p))
                                                {
                                                    session.UpdateActivity();
                                                    await localUdp.SendAsync(p, clientEp, sessionToken);
                                                }
                                            }
                                        }
                                        catch { }
                                    }, ct);

                                    // 2. 出站循环 (Client -> S/P)
                                    var outboundTask = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            while (await channel.Reader.WaitToReadAsync(sessionToken))
                                            {
                                                while (channel.Reader.TryRead(out var p))
                                                {
                                                    session.UpdateActivity();
                                                    sendBuf[0] = (byte)MsgType.Data;
                                                    sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
                                                    p.CopyTo(sendBuf.AsSpan(17));
                                                    await _udp.SendAsync(sendBuf.AsMemory(0, 17 + p.Length), session.ActiveRemoteEp, sessionToken);
                                                }
                                            }
                                        }
                                        catch { }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(sendBuf);
                                        }
                                    }, ct);

                                    await Task.WhenAny(inboundTask, outboundTask);
                                }
                                catch (Exception ex)
                                {
                                    Log.Debug($"[C] UDP Session {sessionId} error: {ex.Message}");
                                }
                                finally
                                {
                                    if (_sessions.TryRemove(sessionId, out _))
                                    {
                                        Log.Info($"[C] Session {sessionId} closed.");
                                    }
                                    clientChannelMap.TryRemove(clientEp, out _);
                                    session?.Dispose();
                                }
                            }, ct);
                        }

                        channel.Writer.TryWrite(packetCopy);
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

        private string ReadPassword()
        {
            string pass = "";
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
            return pass;
        }

        private record QueryResponse(bool Success, Guid DevId, IPEndPoint ServerPublicEp, int ServerWanPort, int Timeout, long STimestamp, bool RequiresPassword, KcpConfig? KcpConfig, List<IPEndPoint> LocalEps, bool AllowRelay = true, bool ServerForceRelay = false);
    }
}