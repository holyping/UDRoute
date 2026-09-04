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
        private readonly object _unauthLock = new();

        // SessionId -> Relay Session (ClientEp <-> ServerEp)
        private readonly ConcurrentDictionary<Guid, RelaySession> _relaySessions = new();
        private readonly AuthManager _authManager;

        public int Port => (_udp.LocalEndPoint is IPEndPoint ip) ? ip.Port : (_config.Port > 0 ? _config.Port : Constants.DefaultProxyPort);

        public ProxyMode(AppConfig config, ZeroCopyUdpSocket udp)
        {
            _config = config;
            _udp = udp;
            _authManager = new AuthManager(config.ConfigPath, config.AuthFile);
            _authManager.Start();
        }

        public bool IsRelayAllowed(ServerRecordInfo sInfo)
        {
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
            // 解析 S 端的注册包: [MsgType 1][DevId 16][WanPort 4][IsTcp 1][Timeout 4][Timestamp 8][ReqPass 1][KcpConfig 21][NameString][SuffixString][LocalEps...][User][Pass]
            if (data.Length < 56) return;

            Guid devId = new Guid(data.Slice(1, 16));
            int wanPort = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(17, 4));
            bool isTcp = data[21] != 0;
            string proto = isTcp ? "tcp" : "udp";
            int timeout = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));
            long sTimestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(26, 8));
            bool reqPass = data[34] != 0;

            var (kcpConfig, kLen) = ProtocolHelper.ReadKcpConfig(data.Slice(35));
            int offset = 35 + kLen;

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

            bool isAuthenticated = false;
            bool providedAuth = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
            
            if (_config.AuthMode == AuthMode.Strict)
            {
                if (!providedAuth)
                {
                    RejectAuth(remoteEp, "No credentials provided in Strict mode");
                    return;
                }
                var (success, reason) = _authManager.Authenticate(username, password);
                if (!success)
                {
                    RejectAuth(remoteEp, reason);
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
                        RejectAuth(remoteEp, reason); // 鉴权失败直接拒绝
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
                RequiresPassword = reqPass
            };

            if (isAuthenticated)
            {
                // 鉴权用户防覆盖检查：已被其他鉴权用户占用的不能抢占
                if (!CanOverwriteAuth(key1, username, remoteEp)) return;

                _authRoutingTable[key1] = info;
                _authRoutingTable[key2] = info;
                if (key3 != null) _authRoutingTable[key3] = info;

                // 鉴权用户凌驾于非鉴权用户之上：若非鉴权表中存在同名主 key，将其收回移除
                _unauthRoutingTable.TryRemove(key1, out _);

                Log.Info($"[P] S Registered (Auth): {name}/{proto} (User: {username}, Suffix: {suffix}) from {remoteEp}");
            }
            else
            {
                // 非鉴权用户注册逻辑
                // 1. 鉴权用户的注册凌驾于非鉴权用户之上：如果鉴权字典中已存在主 key，非鉴权用户严禁注册覆盖
                if (_authRoutingTable.ContainsKey(key1))
                {
                    RejectRegistration(remoteEp, $"{key1} 已被鉴权用户占用，未鉴权用户不能注册或覆盖。");
                    return;
                }

                lock (_unauthLock)
                {
                    bool isRenewal = _unauthDevServices.TryGetValue(devId, out var userServices) && userServices.Contains(serviceName);
                    if (!isRenewal)
                    {
                        // 新服务注册，检查数量限制
                        int userCount = userServices?.Count ?? 0;
                        if (_config.MaxUnauthNamesPerUser > 0 && userCount >= _config.MaxUnauthNamesPerUser)
                        {
                            RejectRegistration(remoteEp, $"非鉴权用户 {devId} 注册服务数已达上限 ({_config.MaxUnauthNamesPerUser})，丢弃注册: {name}/{proto}");
                            return;
                        }

                        int totalCount = _unauthDevServices.Values.Sum(s => s.Count);
                        if (_config.MaxUnauthNamesTotal > 0 && totalCount >= _config.MaxUnauthNamesTotal)
                        {
                            RejectRegistration(remoteEp, $"非鉴权用户注册服务总数已达上限 ({_config.MaxUnauthNamesTotal})，丢弃注册: {name}/{proto}");
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

                Log.Info($"[P] S Registered (Unauth): {name}/{proto} (DevId: {devId}, Suffix: {suffix}) from {remoteEp}");
            }
        }

        private bool CanOverwriteAuth(string key, string newUser, EndPoint remoteEp)
        {
            if (_authRoutingTable.TryGetValue(key, out var existing))
            {
                if (existing.OwnerUser != newUser)
                {
                    RejectRegistration(remoteEp, $"拒绝覆盖: {key} 已被鉴权用户 '{existing.OwnerUser}' 占用，用户 '{newUser}' 尝试抢占被丢弃。");
                    return false;
                }
            }
            return true;
        }

        private void RejectAuth(EndPoint remoteEp, string reason)
        {
            Log.Warn($"[P] 鉴权失败，拒绝 {remoteEp} 的注册: {reason}");
            byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
            byte[] rejectBuf = new byte[1 + reasonBytes.Length];
            rejectBuf[0] = (byte)MsgType.AuthFail;
            Buffer.BlockCopy(reasonBytes, 0, rejectBuf, 1, reasonBytes.Length);
            _ = _udp.SendAsync(rejectBuf, remoteEp, default);
        }

        private void RejectRegistration(EndPoint remoteEp, string reason)
        {
            Log.Warn($"[P] 拒绝注册: {reason}");
            byte[] rejectBuf = new byte[1 + 256];
            rejectBuf[0] = (byte)MsgType.RegFail;
            int offset = 1;
            offset += ProtocolHelper.WriteString(rejectBuf.AsSpan(offset), reason);
            _ = _udp.SendAsync(rejectBuf.AsMemory(0, offset), remoteEp, default);
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
                RequiresPassword = rec.Password != null && rec.Password.Length > 0
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
            // C 端发起查询: [MsgType 1][SessionId 16][TargetName string]
            if (data.Length < 21) return;

            Guid sessionId = new Guid(data.Slice(1, 16));
            var (targetName, _) = ProtocolHelper.ReadString(data.Slice(17));

            Log.Debug($"[P] Query received for '{targetName}', Session: {sessionId} from {remoteEp}");

            var sInfo = DirectQuery(targetName);
            if (sInfo != null)
            {
                bool allowRelay = IsRelayAllowed(sInfo);

                if (allowRelay)
                {
                    // 仅在允许转发时记录中继映射，确保即使打洞未完成也能立刻中继数据
                    _relaySessions[sessionId] = new RelaySession
                    {
                        ClientEp = remoteEp,
                        ServerEp = sInfo.PublicEp,
                        LastSeen = DateTime.UtcNow
                    };
                }

                // 1. 通知 S 端发起准备与打洞 (RelayStart)
                // [MsgType 1][SessionId 16][TargetName string][ClientPublicEp][AllowRelay 1]
                byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                try
                {
                    relayStartBuf[0] = (byte)MsgType.RelayStart;
                    sessionId.TryWriteBytes(relayStartBuf.AsSpan(1, 16));
                    int offset = 17;
                    offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), targetName);
                    offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), remoteEp);
                    relayStartBuf[offset++] = (byte)(allowRelay ? 1 : 0);

                    await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(relayStartBuf);
                }

                // 2. 向 C 端返回 S 的地址信息用于中继与打洞 (Punch / QueryResponse)
                // [MsgType 1][SessionId 16][DevId 16][Status 1 (1=Success)][ServerPublicEp][ServerWanPort 4][Timeout 4][Timestamp 8][ReqPass 1][KcpConfig 21][LocalEps...][AllowRelay 1]
                byte[] punchRespBuf = ArrayPool<byte>.Shared.Rent(1024);
                try
                {
                    punchRespBuf[0] = (byte)MsgType.Punch;
                    sessionId.TryWriteBytes(punchRespBuf.AsSpan(1, 16));
                    sInfo.DevId.TryWriteBytes(punchRespBuf.AsSpan(17, 16));
                    punchRespBuf[33] = 1; // Success
                    int offset = 34;
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

                    await _udp.SendAsync(punchRespBuf.AsMemory(0, offset), remoteEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(punchRespBuf);
                }

                Log.Info($"[P] Query success: Routed {sessionId} to S ({sInfo.PublicEp}), AllowRelay={allowRelay}");
            }
            else
            {
                // 未找到 S 记录，回送失败响应
                // [MsgType 1][SessionId 16][DevId 16 (Empty)][Status 1 (0=NotFound)]
                byte[] failBuf = ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    failBuf[0] = (byte)MsgType.Punch;
                    sessionId.TryWriteBytes(failBuf.AsSpan(1, 16));
                    Guid.Empty.TryWriteBytes(failBuf.AsSpan(17, 16));
                    failBuf[33] = 0; // NotFound

                    await _udp.SendAsync(failBuf.AsMemory(0, 34), remoteEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(failBuf);
                }

                Log.Warn($"[P] Query failed: Target '{targetName}' not found for Session: {sessionId}");
            }
        }

        public async ValueTask<bool> TryRelayDataAsync(Guid sessionId, ReadOnlyMemory<byte> packetMem, EndPoint remoteEp, CancellationToken ct)
        {
            if (_relaySessions.TryGetValue(sessionId, out var session))
            {
                session.LastSeen = DateTime.UtcNow;

                if (remoteEp.Equals(session.ClientEp))
                {
                    await _udp.SendAsync(packetMem, session.ServerEp, ct);
                    return true;
                }
                else if (remoteEp.Equals(session.ServerEp))
                {
                    await _udp.SendAsync(packetMem, session.ClientEp, ct);
                    return true;
                }
            }
            return false;
        }

        private async Task CleanupLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);

                    // 1. 清理过期路由
                    var timeout = TimeSpan.FromSeconds(_config.RegTimeout);
                    var now = DateTime.UtcNow;

                    // 清理鉴权表
                    foreach (var kvp in _authRoutingTable)
                    {
                        if (now - kvp.Value.LastSeen > timeout)
                        {
                            _authRoutingTable.TryRemove(kvp.Key, out _);
                        }
                    }

                    // 清理非鉴权表并归还配额
                    lock (_unauthLock)
                    {
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

                    // 2. 清理非活跃中继会话 (5分钟无数据)
                    var sessionTimeout = TimeSpan.FromMinutes(5);
                    foreach (var kvp in _relaySessions)
                    {
                        if (now - kvp.Value.LastSeen > sessionTimeout)
                        {
                            _relaySessions.TryRemove(kvp.Key, out _);
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
        }
    }
}