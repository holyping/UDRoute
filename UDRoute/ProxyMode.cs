using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 5. Proxy (P模式) 
    // ==========================================
    public class ProxyMode
    {
        private readonly AppConfig _config;
        private readonly ZeroCopyUdpSocket _udp;

        // Key: name / name.suffix / name.devId | Value: Server Info (鉴权用户)
        private readonly ConcurrentDictionary<string, ServerRecordInfo> _authRoutingTable = new(StringComparer.OrdinalIgnoreCase);
        // Key: name / name.suffix / name.devId | Value: Server Info (非鉴权用户)
        private readonly ConcurrentDictionary<string, ServerRecordInfo> _unauthRoutingTable = new(StringComparer.OrdinalIgnoreCase);

        // 追踪非鉴权用户（按 DevId）注册的 distinct service name 集合 (如 "rdp/tcp")
        private readonly ConcurrentDictionary<Guid, HashSet<string>> _unauthDevServices = new();
        private readonly object _routingLock = new();

        // SessionId -> Relay Session (ClientEp <-> ServerEp)
        private readonly Dictionary<Guid, RelaySession> _relaySessions = new();
        // SessionId -> Inactive/Closed Relay Session metadata (用于 C 端重用时 0-RTT 极速重新激活中继)
        private readonly Dictionary<Guid, ClosedRelayInfo> _closedRelaySessions = new();
        private readonly object _relayLock = new();
        private readonly AuthManager _authManager;

        private int _contextCounter;
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pendingProbes = new();
        private readonly Dictionary<Guid, Guid> _migratedSessions = new();
        private readonly TemporyDictionary<(Guid SessionId, ushort ContextId), byte[]> _recentQueryResponses;
        private readonly Dictionary<(Guid SessionId, ushort ContextId), Task> _inFlightQueries = new();
        private readonly object _queryLock = new();

        public int Port => (_udp.LocalEndPoint is IPEndPoint ip) ? ip.Port : (_config.Port > 0 ? _config.Port : Constants.DefaultProxyPort);

        public ProxyMode(AppConfig config, ZeroCopyUdpSocket udp)
        {
            _config = config;
            _udp = udp;
            _recentQueryResponses = new TemporyDictionary<(Guid SessionId, ushort ContextId), byte[]>(_config.MaxRecentRequests);
            _authManager = new AuthManager(config.ConfigPath, config.AuthFile);
            _authManager.Start();
        }

        public bool IsRelayAllowed(ServerRecordInfo sInfo)
        {
            if (!sInfo.AllowRelay) return false;
            if (sInfo.IsAuthenticated) return true;
            return _config.AllowUnauthRelay switch
            {
                AllowUnauthRelay.Allow => true,
                AllowUnauthRelay.Deny => false,
                _ => !_authManager.HasUsers // Default: 无预定义用户则允许，否则不允许
            };
        }

        public List<string> GetRegisteredServers()
        {
            var list = new List<string>();
            foreach (var kvp in _authRoutingTable)
            {
                list.Add($"{kvp.Key} -> {kvp.Value.PublicEp} [Auth: true, User: {kvp.Value.OwnerUser}]");
            }
            foreach (var kvp in _unauthRoutingTable)
            {
                list.Add($"{kvp.Key} -> {kvp.Value.PublicEp} [Auth: false, DevId: {kvp.Value.DevId}]");
            }
            return list;
        }

        public ServerRecordInfo? DirectQuery(string name)
        {
            // 流程上完全让鉴权用户的注册凌驾于非鉴权用户之上：优先查鉴权用户，再查非鉴权用户
            if (_authRoutingTable.TryGetValue(name, out var authInfo))
            {
                return authInfo;
            }
            if (_unauthRoutingTable.TryGetValue(name, out var unauthInfo))
            {
                return unauthInfo;
            }
            return null;
        }

        public Task RunAsync(CancellationToken ct) => CleanupLoopAsync(ct);

        public void ProcessRegister(ReadOnlySpan<byte> data, EndPoint remoteEp)
        {
            // 解析 S 端的注册包: [MsgType 1][ContextId 2][DevId 16][WanPort 4][IsTcp 1][Timeout 4][Timestamp 8][ReqPass 1][KcpConfig 21][NameString][SuffixString][LocalEps...][User][Pass]
            if (data.Length < 58) return;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            Guid devId = new Guid(data.Slice(3, 16));
            int wanPort = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(19, 4));
            bool isTcp = data[23] != 0;
            string proto = isTcp ? "tcp" : "udp";
            int timeout = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(24, 4));
            long sTimestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(28, 8));
            bool reqPass = data[36] != 0;

            var (kcpConfig, kLen) = ProtocolHelper.ReadKcpConfig(data.Slice(37));
            int offset = 37 + kLen;

            var (name, nLen) = ProtocolHelper.ReadString(data.Slice(offset));
            offset += nLen;
            var (suffix, sLen) = ProtocolHelper.ReadString(data.Slice(offset));
            offset += sLen;

            var localEps = new List<IPEndPoint>();
            if (offset < data.Length)
            {
                byte epCount = data[offset++];
                for (int i = 0; i < epCount; i++)
                {
                    if (offset >= data.Length) break;
                    var (ep, epLen) = ProtocolHelper.ReadIPEndPoint(data.Slice(offset));
                    localEps.Add(ep);
                    offset += epLen;
                }
            }

            string username = "";
            string password = "";
            if (offset < data.Length)
            {
                var (u, uLen) = ProtocolHelper.ReadString(data.Slice(offset));
                username = u;
                offset += uLen;
            }
            if (offset < data.Length)
            {
                var (p, pLen) = ProtocolHelper.ReadString(data.Slice(offset));
                password = p;
                offset += pLen;
            }

            int tunnelReuseInterval = Constants.DefaultTunnelReuseInterval;
            if (offset + 4 <= data.Length)
            {
                tunnelReuseInterval = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;
            }

            bool sAllowRelay = true;
            if (offset < data.Length)
            {
                sAllowRelay = data[offset++] != 0;
            }

            bool isAuthenticated = false;
            bool providedAuth = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
            
            if (_config.AuthMode == AuthMode.Strict)
            {
                if (!providedAuth)
                {
                    SendRegisterAck(remoteEp, contextId, 0, "No credentials provided in Strict mode");
                    Log.Warn($"[P] 鉴权失败，拒绝 {remoteEp} 的注册: No credentials provided in Strict mode");
                    return;
                }
                var (success, reason) = _authManager.Authenticate(username, password);
                if (!success)
                {
                    SendRegisterAck(remoteEp, contextId, 0, reason);
                    Log.Warn($"[P] 鉴权失败，拒绝 {remoteEp} 的注册: {reason}");
                    return;
                }
                isAuthenticated = true;
            }
            else if (_config.AuthMode == AuthMode.Optional)
            {
                if (providedAuth)
                {
                    var (success, reason) = _authManager.Authenticate(username, password);
                    if (!success)
                    {
                        SendRegisterAck(remoteEp, contextId, 0, reason);
                        Log.Warn($"[P] 鉴权失败，拒绝 {remoteEp} 的注册: {reason}");
                        return;
                    }
                    isAuthenticated = true;
                }
                else
                {
                    isAuthenticated = false; // 没提供，退级为未认证
                    username = "anonymous";
                }
            }
            else
            {
                // none 模式
                isAuthenticated = false;
                username = "anonymous";
            }

            string key1 = $"{name}/{proto}";
            string key2 = $"{name}.{devId}/{proto}";
            string? key3 = string.IsNullOrEmpty(suffix) ? null : $"{name}.{suffix}/{proto}";
            string serviceName = $"{name}/{proto}";

            var info = new ServerRecordInfo
            {
                DevId = devId,
                ServiceName = name,
                PublicEp = remoteEp,
                WanPort = wanPort,
                IsTcp = isTcp,
                KcpConfig = kcpConfig,
                Timeout = timeout,
                LastSeen = DateTime.UtcNow,
                LocalEps = localEps,
                IsAuthenticated = isAuthenticated,
                OwnerUser = username,
                STimestamp = sTimestamp,
                PRecvTimeTicks = DateTime.UtcNow.Ticks,
                RequiresPassword = reqPass,
                TunnelReuseInterval = tunnelReuseInterval,
                AllowRelay = sAllowRelay
            };

            if (isAuthenticated)
            {
                lock (_routingLock)
                {
                    // 鉴权用户防覆盖检查：已被其他鉴权用户占用的不能抢占
                    if (!CanOverwriteAuth(key1, username, remoteEp, contextId))
                    {
                        SendRegisterAck(remoteEp, contextId, 0, "权限不足");
                        Log.Trace($"[P] 鉴权失败，拒绝 {remoteEp} 的注册: 权限不足");
                        return;
                    }

                    _authRoutingTable[key1] = info;
                    _authRoutingTable[key2] = info;
                    if (key3 != null) _authRoutingTable[key3] = info;

                    // 鉴权用户凌驾于非鉴权用户之上：若非鉴权表中存在同名主 key，将其收回移除
                    _unauthRoutingTable.TryRemove(key1, out _);
                }

                SendRegisterAck(remoteEp, contextId, 1);
                Log.Info($"[P] S Registered (Auth): {name}/{proto} (User: {username}, Suffix: {suffix}) from {remoteEp}");
            }
            else
            {
                // 非鉴权用户注册逻辑
                lock (_routingLock)
                {
                    // 1. 鉴权用户的注册凌驾于非鉴权用户之上：如果鉴权字典中已存在主 key，非鉴权用户严禁注册覆盖
                    if (_authRoutingTable.ContainsKey(key1))
                    {
                        string reason = $"{key1} 已被鉴权用户占用，未鉴权用户不能注册或覆盖。";
                        SendRegisterAck(remoteEp, contextId, 0, reason);
                        Log.Warn($"[P] 拒绝注册: {reason}");
                        return;
                    }

                    bool isRenewal = _unauthDevServices.TryGetValue(devId, out var userServices) && userServices.Contains(serviceName);
                    if (!isRenewal)
                    {
                        // 新服务注册，检查数量限制
                        int userCount = userServices?.Count ?? 0;
                        if (_config.MaxUnauthNamesPerUser > 0 && userCount >= _config.MaxUnauthNamesPerUser)
                        {
                            string reason = $"非鉴权用户 {devId} 注册服务数已达上限 ({_config.MaxUnauthNamesPerUser})，丢弃注册: {name}/{proto}";
                            SendRegisterAck(remoteEp, contextId, 0, reason);
                            Log.Warn($"[P] 拒绝注册: {reason}");
                            return;
                        }

                        int totalCount = _unauthDevServices.Values.Sum(s => s.Count);
                        if (_config.MaxUnauthNamesTotal > 0 && totalCount >= _config.MaxUnauthNamesTotal)
                        {
                            string reason = $"非鉴权用户注册服务总数已达上限 ({_config.MaxUnauthNamesTotal})，丢弃注册: {name}/{proto}";
                            SendRegisterAck(remoteEp, contextId, 0, reason);
                            Log.Warn($"[P] 拒绝注册: {reason}");
                            return;
                        }

                        if (userServices == null)
                        {
                            userServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _unauthDevServices[devId] = userServices;
                        }
                        userServices.Add(serviceName);
                    }

                    _unauthRoutingTable[key1] = info;
                    _unauthRoutingTable[key2] = info;
                    if (key3 != null) _unauthRoutingTable[key3] = info;
                }

                SendRegisterAck(remoteEp, contextId, 1);
                Log.Info($"[P] S Registered (Unauth): {name}/{proto} (DevId: {devId}, Suffix: {suffix}) from {remoteEp}");
            }
        }

        private bool CanOverwriteAuth(string key, string newUser, EndPoint remoteEp, ushort contextId)
        {
            if (_authRoutingTable.TryGetValue(key, out var existing))
            {
                if (existing.OwnerUser != newUser)
                {
                    string reason = $"拒绝覆盖: {key} 已被鉴权用户 '{existing.OwnerUser}' 占用，用户 '{newUser}' 尝试抢占被丢弃。";
                    SendRegisterAck(remoteEp, contextId, 0, reason);
                    Log.Warn($"[P] 拒绝注册: {reason}");
                    return false;
                }
            }
            return true;
        }

        private void SendRegisterAck(EndPoint remoteEp, ushort contextId, byte status, string reason = "")
        {
            if (contextId == 0) return;
            int reasonBytes = string.IsNullOrEmpty(reason) ? 0 : Encoding.UTF8.GetByteCount(reason);
            byte[] ackBuf = new byte[4 + 4 + reasonBytes];
            ackBuf[0] = (byte)MsgType.RegisterAck;
            BinaryPrimitives.WriteUInt16LittleEndian(ackBuf.AsSpan(1, 2), contextId);
            ackBuf[3] = status;
            int offset = 4;
            if (status != 1 && !string.IsNullOrEmpty(reason))
            {
                offset += ProtocolHelper.WriteString(ackBuf.AsSpan(offset), reason);
            }
            _ = _udp.SendAsync(ackBuf.AsMemory(0, offset), remoteEp, default);
        }

        public void ProcessRegisterDirect(ServerRecord rec, Guid devId, int wanPort, string devName)
        {
            string proto = rec.IsTcp ? "tcp" : "udp";
            var info = new ServerRecordInfo
            {
                DevId = devId,
                ServiceName = rec.Name,
                PublicEp = _udp.LocalEndPoint,
                WanPort = wanPort,
                IsTcp = rec.IsTcp,
                KcpConfig = rec.KcpConfig,
                Timeout = rec.Timeout,
                LastSeen = DateTime.UtcNow,
                IsAuthenticated = true,
                OwnerUser = "localhost",
                STimestamp = DateTime.UtcNow.Ticks,
                PRecvTimeTicks = DateTime.UtcNow.Ticks,
                RequiresPassword = rec.Password != null && rec.Password.Length > 0,
                TunnelReuseInterval = rec.TunnelReuseInterval
            };

            string key1 = $"{rec.Name}/{proto}";
            string key2 = $"{rec.Name}.{devId}/{proto}";
            string? key3 = !string.IsNullOrEmpty(devName) ? $"{rec.Name}.{devName}/{proto}" : null;

            _authRoutingTable[key1] = info;
            _authRoutingTable[key2] = info;
            if (key3 != null) _authRoutingTable[key3] = info;

            // 收回非鉴权同名服务
            _unauthRoutingTable.TryRemove(key1, out _);

            Log.Info($"[P] S Registered (direct): {rec.Name}/{proto} (DevName: {devName}, WanPort: {wanPort})");
        }

        public async ValueTask ProcessQueryAsync(ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            var data = packetMem.Span;
            // C 端发起查询: [MsgType 1][ContextId 2][SessionId 16][TargetName string][Flags 1]
            if (data.Length < 23) return;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            Guid sessionId = new Guid(data.Slice(3, 16));
            var (targetName, nLen) = ProtocolHelper.ReadString(data.Slice(19));
            int qOffset = 19 + nLen;
            bool clientForceRelay = false;
            bool isReuse = false;
            if (qOffset < data.Length)
            {
                byte qFlags = data[qOffset++];
                clientForceRelay = (qFlags & 1) != 0;
                isReuse = (qFlags & 2) != 0;
            }

            var queryKey = (sessionId, contextId);
            byte[]? cachedResp = null;
            TaskCompletionSource<bool>? myTcs = null;
            Task? inFlight = null;

            lock (_queryLock)
            {
                if (_recentQueryResponses.TryGetValue(queryKey, out cachedResp))
                {
                    // already responded
                }
                else if (_inFlightQueries.TryGetValue(queryKey, out inFlight))
                {
                    // already processing
                }
                else
                {
                    myTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlightQueries[queryKey] = myTcs.Task;
                }
            }

            if (cachedResp != null)
            {
                try { await _udp.SendAsync(cachedResp, remoteEp, ct); } catch { }
                return;
            }

            if (inFlight != null)
            {
                try { await inFlight; } catch { }
                byte[]? resp = null;
                lock (_queryLock)
                {
                    _recentQueryResponses.TryGetValue(queryKey, out resp);
                }
                if (resp != null)
                {
                    try { await _udp.SendAsync(resp, remoteEp, ct); } catch { }
                }
                return;
            }

            try
            {
                Log.Debug($"[P] Query received for '{targetName}', Session: {sessionId}, Context: {contextId} from {remoteEp}, ClientForceRelay: {clientForceRelay}, IsReuse: {isReuse}");

                var sInfo = DirectQuery(targetName);
                if (sInfo != null)
                {
                    if (isReuse)
                    {
                        long recordedTimestamp = 0;
                        lock (_relayLock)
                        {
                            if (_relaySessions.TryGetValue(sessionId, out var activeRelay))
                            {
                                recordedTimestamp = activeRelay.ServerTimestamp;
                            }
                            else if (_closedRelaySessions.TryGetValue(sessionId, out var closedRelay))
                            {
                                recordedTimestamp = closedRelay.ServerTimestamp;
                            }
                        }

                        if (recordedTimestamp != 0 && recordedTimestamp != sInfo.STimestamp)
                        {
                            Log.Warn($"[P] S for '{targetName}' restarted since session {sessionId} was created (ts {recordedTimestamp} != {sInfo.STimestamp}). Rejecting reuse.");
                            lock (_relayLock)
                            {
                                _relaySessions.Remove(sessionId);
                                _closedRelaySessions.Remove(sessionId);
                            }

                            byte[] failBuf = ArrayPool<byte>.Shared.Rent(256);
                            try
                            {
                                failBuf[0] = (byte)MsgType.Punch;
                                BinaryPrimitives.WriteUInt16LittleEndian(failBuf.AsSpan(1, 2), contextId);
                                sessionId.TryWriteBytes(failBuf.AsSpan(3, 16));
                                Guid.Empty.TryWriteBytes(failBuf.AsSpan(19, 16));
                                failBuf[35] = PunchStatus.StaleSession; // S restarted
                                int failLen = 36 + ProtocolHelper.WriteString(failBuf.AsSpan(36), $"Target '{targetName}' restarted since session was created");

                                byte[] failCopy = failBuf.AsSpan(0, failLen).ToArray();
                                lock (_queryLock)
                                {
                                    _recentQueryResponses[queryKey] = failCopy;
                                }
                                await _udp.SendAsync(failCopy, remoteEp, ct);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(failBuf);
                            }
                            return;
                        }
                    }

                    bool allowRelay = IsRelayAllowed(sInfo);
                    bool effectiveForceRelay = clientForceRelay && allowRelay;
                    if (!allowRelay && clientForceRelay)
                    {
                        Log.Info($"[P] AllowRelay is false for '{targetName}', ignoring client ForceRelay.");
                    }

                    // 健康判断规则：当有新的握手请求时，若该通路已经超过 T1 秒没有成功通讯过，
                    // 那么这个握手请求超过 T2 秒没有被响应，则判断为通道死亡。
                    bool isIdle = (DateTime.UtcNow - sInfo.LastSeen) > TimeSpan.FromSeconds(_config.IdleThreshold);
                    if (isIdle)
                    {
                        Log.Info($"[P] Path to S for '{targetName}' idle > {_config.IdleThreshold}s (LastSeen: {sInfo.LastSeen:HH:mm:ss}). Probing S at {sInfo.PublicEp} (Timeout: {_config.ProbeTimeout}s)...");

                        ushort probeContextId = (ushort)Interlocked.Increment(ref _contextCounter);
                        if (probeContextId == 0) probeContextId = (ushort)Interlocked.Increment(ref _contextCounter);

                        var probeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingProbes[probeContextId] = probeTcs;

                        // 通知 S 端发起准备与打洞 (RelayStart，带 ContextId 连发3次)
                        byte[] probeBuf = ArrayPool<byte>.Shared.Rent(512);
                        try
                        {
                            probeBuf[0] = (byte)MsgType.RelayStart;
                            BinaryPrimitives.WriteUInt16LittleEndian(probeBuf.AsSpan(1, 2), probeContextId);
                            sessionId.TryWriteBytes(probeBuf.AsSpan(3, 16));
                            int offset = 19;
                            offset += ProtocolHelper.WriteString(probeBuf.AsSpan(offset), targetName);
                            offset += ProtocolHelper.WriteIPEndPoint(probeBuf.AsSpan(offset), remoteEp);
                            probeBuf[offset++] = (byte)((allowRelay ? 1 : 0) | (effectiveForceRelay ? 2 : 0) | (isReuse ? 4 : 0));

                            byte[] probeCopy = probeBuf.AsSpan(0, offset).ToArray();
                            _ = ProtocolHelper.SendWithRetryAsync(_udp, probeCopy, sInfo.PublicEp, probeTcs.Task, ct);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(probeBuf);
                        }

                        bool probeSuccess = false;
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ProbeTimeout)))
                        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                        {
                            try
                            {
                                probeSuccess = await probeTcs.Task.WaitAsync(linkedCts.Token);
                            }
                            catch
                            {
                                probeSuccess = false;
                            }
                            finally
                            {
                                _pendingProbes.TryRemove(probeContextId, out _);
                            }
                        }

                        if (!probeSuccess)
                        {
                            Log.Warn($"[P] Health probe timed out after {_config.ProbeTimeout}s for '{targetName}' at {sInfo.PublicEp}. Declaring channel dead.");

                            byte[] failBuf = ArrayPool<byte>.Shared.Rent(256);
                            try
                            {
                                failBuf[0] = (byte)MsgType.Punch;
                                BinaryPrimitives.WriteUInt16LittleEndian(failBuf.AsSpan(1, 2), contextId);
                                sessionId.TryWriteBytes(failBuf.AsSpan(3, 16));
                                Guid.Empty.TryWriteBytes(failBuf.AsSpan(19, 16));
                                failBuf[35] = PunchStatus.SUnresponsive; // Health probe timed out / KeepAlive failed
                                int failLen = 36 + ProtocolHelper.WriteString(failBuf.AsSpan(36), $"Target '{targetName}' is unresponsive (health probe timed out / keepalive failed)");

                                byte[] failCopy = failBuf.AsSpan(0, failLen).ToArray();
                                lock (_queryLock)
                                {
                                    _recentQueryResponses[queryKey] = failCopy;
                                }
                                await _udp.SendAsync(failCopy, remoteEp, ct);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(failBuf);
                            }
                            return;
                        }

                        sInfo.LastSeen = DateTime.UtcNow;
                        Log.Info($"[P] Health probe succeeded for '{targetName}' at {sInfo.PublicEp}.");
                    }
                    else
                    {
                        // 活跃通路：通知 S 端 (RelayStart 连发3次)
                        byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                        try
                        {
                            ushort relayContextId = (ushort)Interlocked.Increment(ref _contextCounter);
                            if (relayContextId == 0) relayContextId = (ushort)Interlocked.Increment(ref _contextCounter);

                            relayStartBuf[0] = (byte)MsgType.RelayStart;
                            BinaryPrimitives.WriteUInt16LittleEndian(relayStartBuf.AsSpan(1, 2), relayContextId);
                            sessionId.TryWriteBytes(relayStartBuf.AsSpan(3, 16));
                            int offset = 19;
                            offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), targetName);
                            offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), remoteEp);
                            relayStartBuf[offset++] = (byte)((allowRelay ? 1 : 0) | (effectiveForceRelay ? 2 : 0) | (isReuse ? 4 : 0));

                            byte[] relayCopy = relayStartBuf.AsSpan(0, offset).ToArray();
                            var relayTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            _pendingProbes[relayContextId] = relayTcs;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await ProtocolHelper.SendWithRetryAsync(_udp, relayCopy, sInfo.PublicEp, relayTcs.Task, ct);
                                }
                                finally
                                {
                                    _pendingProbes.TryRemove(relayContextId, out _);
                                }
                            });
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(relayStartBuf);
                        }
                    }

                    if (allowRelay)
                    {
                        lock (_relayLock)
                        {
                            // 新通道建立，旧通道删除，如果仍有旧的通讯连接依赖该通道，则都走该通道，但通讯连接lastactive时间不变
                            var oldSessionIds = new List<Guid>();
                            foreach (var kvp in _relaySessions)
                            {
                                if (kvp.Key != sessionId &&
                                    ProtocolHelper.AreEndPointsEqual(kvp.Value.ClientEp, remoteEp) &&
                                    string.Equals(kvp.Value.TargetName, targetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    oldSessionIds.Add(kvp.Key);
                                }
                            }
                            foreach (var kvp in _closedRelaySessions)
                            {
                                if (kvp.Key != sessionId &&
                                    ProtocolHelper.AreEndPointsEqual(kvp.Value.ClientEp, remoteEp) &&
                                    string.Equals(kvp.Value.TargetName, targetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    oldSessionIds.Add(kvp.Key);
                                }
                            }

                            foreach (var oldId in oldSessionIds)
                            {
                                _relaySessions.Remove(oldId);
                                _closedRelaySessions.Remove(oldId);
                                _migratedSessions[oldId] = sessionId;
                            }

                            _closedRelaySessions.Remove(sessionId);
                            _relaySessions[sessionId] = new RelaySession
                            {
                                ClientEp = remoteEp,
                                ServerEp = sInfo.PublicEp,
                                LastSeen = DateTime.UtcNow,
                                TimeoutSeconds = sInfo.Timeout > 0 ? sInfo.Timeout : Constants.DefaultTimeout,
                                ServerTimestamp = sInfo.STimestamp,
                                TargetName = targetName
                            };
                        }
                    }

                    // 2. 向 C 端返回 S 的地址信息用于中继与打洞 (Punch / QueryResponse) - 单次发送，严禁连发
                    // [MsgType 1][ContextId 2][SessionId 16][DevId 16][Status 1 (1=Success)][ServerPublicEp][ServerWanPort 4][Timeout 4][Timestamp 8][ReqPass 1][KcpConfig 21][LocalEps...][AllowRelay 1]
                    byte[] punchRespBuf = ArrayPool<byte>.Shared.Rent(1024);
                    try
                    {
                        punchRespBuf[0] = (byte)MsgType.Punch;
                        BinaryPrimitives.WriteUInt16LittleEndian(punchRespBuf.AsSpan(1, 2), contextId);
                        sessionId.TryWriteBytes(punchRespBuf.AsSpan(3, 16));
                        sInfo.DevId.TryWriteBytes(punchRespBuf.AsSpan(19, 16));
                        punchRespBuf[35] = 1; // Success
                        int offset = 36;
                        offset += ProtocolHelper.WriteIPEndPoint(punchRespBuf.AsSpan(offset), sInfo.PublicEp);
                        BinaryPrimitives.WriteInt32LittleEndian(punchRespBuf.AsSpan(offset, 4), sInfo.WanPort);
                        offset += 4;
                        BinaryPrimitives.WriteInt32LittleEndian(punchRespBuf.AsSpan(offset, 4), sInfo.Timeout);
                        offset += 4;

                        long elapsed = DateTime.UtcNow.Ticks - sInfo.PRecvTimeTicks;
                        long approxTime = sInfo.STimestamp + elapsed;
                        BinaryPrimitives.WriteInt64LittleEndian(punchRespBuf.AsSpan(offset, 8), approxTime);
                        offset += 8;
                        punchRespBuf[offset++] = (byte)(sInfo.RequiresPassword ? 1 : 0);

                        offset += ProtocolHelper.WriteKcpConfig(punchRespBuf.AsSpan(offset), sInfo.KcpConfig);

                        int countPos = offset++;
                        byte epCount = 0;
                        foreach (var ep in sInfo.LocalEps)
                        {
                            if (offset + 21 > punchRespBuf.Length) break;
                            offset += ProtocolHelper.WriteIPEndPoint(punchRespBuf.AsSpan(offset), ep);
                            epCount++;
                            if (epCount >= 10) break;
                        }
                        punchRespBuf[countPos] = epCount;
                        punchRespBuf[offset++] = (byte)(allowRelay ? 1 : 0);
                        BinaryPrimitives.WriteInt32LittleEndian(punchRespBuf.AsSpan(offset, 4), sInfo.TunnelReuseInterval);
                        offset += 4;

                        byte[] punchCopy = punchRespBuf.AsSpan(0, offset).ToArray();
                        lock (_queryLock)
                        {
                            _recentQueryResponses[queryKey] = punchCopy;
                        }
                        await _udp.SendAsync(punchCopy, remoteEp, ct);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(punchRespBuf);
                    }

                    Log.Info($"[P] Query success: Routed {sessionId} to S ({sInfo.PublicEp}), AllowRelay={allowRelay}");
                }
                else
                {
                    // 未找到 S 记录，回送失败响应 (单次发送，严禁连发)
                    // [MsgType 1][ContextId 2][SessionId 16][DevId 16 (Empty)][Status 1 (0=NotFound)]
                    byte[] failBuf = ArrayPool<byte>.Shared.Rent(256);
                    try
                    {
                        failBuf[0] = (byte)MsgType.Punch;
                        BinaryPrimitives.WriteUInt16LittleEndian(failBuf.AsSpan(1, 2), contextId);
                        sessionId.TryWriteBytes(failBuf.AsSpan(3, 16));
                        Guid.Empty.TryWriteBytes(failBuf.AsSpan(19, 16));
                        failBuf[35] = PunchStatus.NotFound; // NotFound
                        int failLen = 36 + ProtocolHelper.WriteString(failBuf.AsSpan(36), $"Service '{targetName}' is not registered on P");

                        byte[] failCopy = failBuf.AsSpan(0, failLen).ToArray();
                        lock (_queryLock)
                        {
                            _recentQueryResponses[queryKey] = failCopy;
                        }
                        await _udp.SendAsync(failCopy, remoteEp, ct);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(failBuf);
                    }

                    Log.Warn($"[P] Query failed: Target '{targetName}' not found for Session: {sessionId}");
                }
            }
            finally
            {
                if (myTcs != null)
                {
                    lock (_queryLock)
                    {
                        _inFlightQueries.Remove(queryKey);
                    }
                    myTcs.TrySetResult(true);
                }
            }
        }

        public void ProcessRelayStartAck(ReadOnlySpan<byte> data, EndPoint remoteEp)
        {
            // [MsgType 1 = 14][ContextId 2][SessionId 16][Status 1]
            if (data.Length < 20) return;

            ushort contextId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2));
            Guid sessionId = new Guid(data.Slice(3, 16));
            byte status = data[19];

            if (_pendingProbes.TryRemove(contextId, out var tcs))
            {
                tcs.TrySetResult(status == 1);
            }
        }

        public int RelaySessionCount
        {
            get
            {
                lock (_relayLock)
                {
                    return _relaySessions.Count;
                }
            }
        }
        public bool HasRelaySession(Guid sessionId)
        {
            lock (_relayLock)
            {
                return _relaySessions.ContainsKey(sessionId);
            }
        }

        public async ValueTask<bool> TryRelayDataAsync(Guid sessionId, ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            RelaySession? sessionToForward = null;
            bool sendDisconnect = false;
            bool isMigrated = false;

            lock (_relayLock)
            {
                if (_relaySessions.TryGetValue(sessionId, out var session))
                {
                    session.LastSeen = DateTime.UtcNow;
                    sessionToForward = session;
                }
                else
                {
                    Guid curId = sessionId;
                    while (_migratedSessions.TryGetValue(curId, out var nextId))
                    {
                        curId = nextId;
                    }

                    if (_relaySessions.TryGetValue(curId, out var migratedSession))
                    {
                        sessionToForward = migratedSession;
                        isMigrated = true;
                    }
                    else if (_closedRelaySessions.Remove(sessionId, out var closedInfo))
                    {
                        var restoredSession = new RelaySession
                        {
                            ClientEp = ProtocolHelper.AreEndPointsEqual(remoteEp, closedInfo.ClientEp) ? remoteEp : closedInfo.ClientEp,
                            ServerEp = ProtocolHelper.AreEndPointsEqual(remoteEp, closedInfo.ServerEp) ? remoteEp : closedInfo.ServerEp,
                            LastSeen = DateTime.UtcNow,
                            TimeoutSeconds = closedInfo.TimeoutSeconds,
                            ServerTimestamp = closedInfo.ServerTimestamp,
                            TargetName = closedInfo.TargetName
                        };
                        _relaySessions[sessionId] = restoredSession;
                        sessionToForward = restoredSession;
                        Log.Info($"[P] Closed/inactive relay session {sessionId} re-established upon receiving data packet from {remoteEp}.");
                    }
                    else
                    {
                        sendDisconnect = true;
                    }
                }
            }

            if (sessionToForward != null)
            {
                if (ProtocolHelper.AreEndPointsEqual(remoteEp, sessionToForward.ClientEp))
                {
                    await _udp.SendAsync(packetMem, sessionToForward.ServerEp, ct);
                    return true;
                }
                else if (ProtocolHelper.AreEndPointsEqual(remoteEp, sessionToForward.ServerEp))
                {
                    if (!isMigrated)
                    {
                        sessionToForward.LastSeen = DateTime.UtcNow;
                    }
                    if (_authRoutingTable.TryGetValue(sessionToForward.TargetName, out var authS))
                    {
                        authS.LastSeen = DateTime.UtcNow;
                    }
                    else if (_unauthRoutingTable.TryGetValue(sessionToForward.TargetName, out var unauthS))
                    {
                        unauthS.LastSeen = DateTime.UtcNow;
                    }
                    await _udp.SendAsync(packetMem, sessionToForward.ClientEp, ct);
                    return true;
                }
                return false;
            }

            if (sendDisconnect)
            {
                // P 端无此中继会话（从未建立或已彻底过期），向发送方回发 Disconnect，避免客户端死等
                byte[] discBuf = new byte[17];
                discBuf[0] = (byte)MsgType.Disconnect;
                sessionId.TryWriteBytes(discBuf.AsSpan(1, 16));
                try { await _udp.SendAsync(discBuf, remoteEp, CancellationToken.None); } catch { }
            }
            return false;
        }

        public async ValueTask<bool> TryRelayDisconnectAsync(Guid sessionId, ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            RelaySession? session = null;
            lock (_relayLock)
            {
                _closedRelaySessions.Remove(sessionId);
                _relaySessions.Remove(sessionId, out session);
            }

            if (session != null)
            {
                try
                {
                    if (ProtocolHelper.AreEndPointsEqual(remoteEp, session.ClientEp))
                    {
                        await _udp.SendAsync(packetMem, session.ServerEp, ct);
                    }
                    else if (ProtocolHelper.AreEndPointsEqual(remoteEp, session.ServerEp))
                    {
                        await _udp.SendAsync(packetMem, session.ClientEp, ct);
                    }
                    Log.Info($"[P] Session {sessionId} disconnected by peer, relayed notice and closed relay session on P.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[P] Session {sessionId} failed to relay disconnect notice: {ex.Message}");
                }
                return true;
            }
            return false;
        }

        public void HandleRelayEnd(Guid sessionId, EndPoint remoteEp)
        {
            lock (_relayLock)
            {
                _closedRelaySessions.Remove(sessionId);
                if (_relaySessions.TryGetValue(sessionId, out var session))
                {
                    // 仅允许参与该会话的 Client 或 Server 结束中继
                    if (ProtocolHelper.AreEndPointsEqual(remoteEp, session.ClientEp) || ProtocolHelper.AreEndPointsEqual(remoteEp, session.ServerEp))
                    {
                        _relaySessions.Remove(sessionId);
                        Log.Info($"[P] Session {sessionId} direct punch confirmed between S and C. Relay session ended on P without notifying peers.");
                    }
                }
            }
        }

        private async Task CleanupLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                    // 1. 清理过期路由
                    var timeout = TimeSpan.FromSeconds(_config.RegTimeout);
                    var now = DateTime.UtcNow;

                    lock (_routingLock)
                    {
                        // 清理鉴权表
                        var expiredAuthKeys = new List<string>();
                        foreach (var kvp in _authRoutingTable)
                        {
                            if (now - kvp.Value.LastSeen > timeout)
                            {
                                expiredAuthKeys.Add(kvp.Key);
                            }
                        }
                        foreach (var key in expiredAuthKeys)
                        {
                            _authRoutingTable.TryRemove(key, out _);
                        }

                        // 清理非鉴权表并归还配额
                        var expiredKeys = new List<string>();
                        foreach (var kvp in _unauthRoutingTable)
                        {
                            if (now - kvp.Value.LastSeen > timeout)
                            {
                                expiredKeys.Add(kvp.Key);
                            }
                        }

                        foreach (var key in expiredKeys)
                        {
                            if (_unauthRoutingTable.TryRemove(key, out var info))
                            {
                                if (_unauthDevServices.TryGetValue(info.DevId, out var services))
                                {
                                    string sName = $"{info.ServiceName}/{(info.IsTcp ? "tcp" : "udp")}";
                                    services.Remove(sName);
                                    if (services.Count == 0)
                                    {
                                        _unauthDevServices.TryRemove(info.DevId, out _);
                                    }
                                }
                            }
                        }
                    }

                    // 2. 清理超时未通讯的中继会话 (P 端执行 TunnelReuseInterval=0 策略，超时即释放内部活跃映射)
                    // 注意：不向对端发送 Disconnect，保留 S 和 C 端的 300 秒复用窗口，并将轻量路由信息转入 _closedRelaySessions 供重用时恢复
                    lock (_relayLock)
                    {
                        var expiredSessions = new List<KeyValuePair<Guid, RelaySession>>();
                        foreach (var kvp in _relaySessions)
                        {
                            int toSec = kvp.Value.TimeoutSeconds > 0 ? kvp.Value.TimeoutSeconds : Constants.DefaultTimeout;
                            if (now - kvp.Value.LastSeen > TimeSpan.FromSeconds(toSec))
                            {
                                expiredSessions.Add(kvp);
                            }
                        }

                        foreach (var kvp in expiredSessions)
                        {
                            _relaySessions.Remove(kvp.Key);
                            Log.Info($"[P] Relay session {kvp.Key} idle for {kvp.Value.TimeoutSeconds}s exceeding timeout, released from P active sessions.");
                            _closedRelaySessions[kvp.Key] = new ClosedRelayInfo
                            {
                                ClientEp = kvp.Value.ClientEp,
                                ServerEp = kvp.Value.ServerEp,
                                TimeoutSeconds = kvp.Value.TimeoutSeconds,
                                ClosedAt = now,
                                ServerTimestamp = kvp.Value.ServerTimestamp,
                                TargetName = kvp.Value.TargetName
                            };
                        }

                        // 3. 清理长期未被重用的已释放会话元数据 (10分钟超时彻底清除，避免内存膨胀)
                        var expiredClosedKeys = new List<Guid>();
                        foreach (var kvp in _closedRelaySessions)
                        {
                            if (now - kvp.Value.ClosedAt > TimeSpan.FromMinutes(10))
                            {
                                expiredClosedKeys.Add(kvp.Key);
                            }
                        }
                        foreach (var key in expiredClosedKeys)
                        {
                            _closedRelaySessions.Remove(key);
                        }

                        // 4. 清理目标会话已彻底释放的迁移映射
                        var deadMigratedKeys = new List<Guid>();
                        foreach (var kvp in _migratedSessions)
                        {
                            if (!_relaySessions.ContainsKey(kvp.Value) && !_closedRelaySessions.ContainsKey(kvp.Value))
                            {
                                deadMigratedKeys.Add(kvp.Key);
                            }
                        }
                        foreach (var key in deadMigratedKeys)
                        {
                            _migratedSessions.Remove(key);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"[P] Cleanup error: {ex.Message}");
                }
            }
        }

        private class RelaySession
        {
            public EndPoint ClientEp { get; set; } = null!;
            public EndPoint ServerEp { get; set; } = null!;
            public DateTime LastSeen { get; set; }
            public int TimeoutSeconds { get; set; }
            public long ServerTimestamp { get; set; }
            public string TargetName { get; set; } = "";
        }

        private class ClosedRelayInfo
        {
            public EndPoint ClientEp { get; set; } = null!;
            public EndPoint ServerEp { get; set; } = null!;
            public int TimeoutSeconds { get; set; }
            public DateTime ClosedAt { get; set; }
            public long ServerTimestamp { get; set; }
            public string TargetName { get; set; } = "";
        }
    }
}