using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
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

        // Key: name / name.suffix / name.devId | Value: Server Info
        private readonly ConcurrentDictionary<string, ServerRecordInfo> _routingTable = new(StringComparer.OrdinalIgnoreCase);

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

        public List<string> GetRegisteredServers()
        {
            var list = new List<string>();
            foreach (var kvp in _routingTable)
            {
                list.Add($"{kvp.Key} -> {kvp.Value.PublicEp} [Auth: {kvp.Value.IsAuthenticated}]");
            }
            return list;
        }

        public ServerRecordInfo? DirectQuery(string name)
        {
            return _routingTable.TryGetValue(name, out var info) ? info : null;
        }

        public Task RunAsync(CancellationToken ct) => CleanupLoopAsync(ct);

        public void ProcessRegister(ReadOnlySpan<byte> data, EndPoint remoteEp)
        {
            // 解析 S 端的注册包 [MsgType 1][DevId 16][WanPort 4][IsTcp 1][Timeout 4][KcpConfig 21][NameString][SuffixString][LocalEps...][User][Pass]
            if (data.Length < 47) return;

            Guid devId = new Guid(data.Slice(1, 16));
            int wanPort = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(17, 4));
            bool isTcp = data[21] != 0;
            string proto = isTcp ? "tcp" : "udp";
            int timeout = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));

            var (kcpConfig, kLen) = ProtocolHelper.ReadKcpConfig(data.Slice(26));
            int offset = 26 + kLen;

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
            
            if (_config.AuthMode == "strict")
            {
                if (!providedAuth || !_authManager.Authenticate(username, password))
                {
                    RejectAuth(remoteEp);
                    return;
                }
                isAuthenticated = true;
            }
            else if (_config.AuthMode == "optional")
            {
                if (providedAuth)
                {
                    if (!_authManager.Authenticate(username, password))
                    {
                        RejectAuth(remoteEp); // 密码错误，直接丢弃
                        return;
                    }
                    isAuthenticated = true;
                }
                else
                {
                    isAuthenticated = false; // 没带密码，允许进入，标记未认证
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

            // 防覆盖逻辑
            if (!CanOverwrite(key1, isAuthenticated, username)) return;

            var info = new ServerRecordInfo
            {
                DevId = devId,
                PublicEp = remoteEp,
                WanPort = wanPort,
                IsTcp = isTcp,
                KcpConfig = kcpConfig,
                Timeout = timeout,
                LastSeen = DateTime.UtcNow,
                LocalEps = localEps,
                IsAuthenticated = isAuthenticated,
                OwnerUser = username
            };

            _routingTable[key1] = info;
            _routingTable[key2] = info;
            if (key3 != null) _routingTable[key3] = info;

            Log.Info($"[P] S Registered: {name}/{proto} (User: {username}, Auth: {isAuthenticated}, Suffix: {suffix}) from {remoteEp}");
        }

        private bool CanOverwrite(string key, bool newIsAuth, string newUser)
        {
            if (_routingTable.TryGetValue(key, out var existing))
            {
                if (existing.IsAuthenticated && !newIsAuth)
                {
                    Log.Warn($"[P] 拒绝覆盖: {key} 已被认证用户占用，未认证的请求被丢弃。");
                    return false;
                }
                if (existing.IsAuthenticated && newIsAuth && existing.OwnerUser != newUser)
                {
                    Log.Warn($"[P] 拒绝覆盖: {key} 已被用户 '{existing.OwnerUser}' 占用，用户 '{newUser}' 尝试抢占被丢弃。");
                    return false;
                }
            }
            return true;
        }

        private void RejectAuth(EndPoint remoteEp)
        {
            Log.Warn($"[P] 鉴权失败：拒绝来自 {remoteEp} 的注册请求。");
            byte[] rejectBuf = new byte[1];
            rejectBuf[0] = (byte)MsgType.AuthFail;
            _ = _udp.SendAsync(rejectBuf, remoteEp, default);
        }

        public void ProcessRegisterDirect(ServerRecord rec, Guid devId, int wanPort, string devName)
        {
            string proto = rec.IsTcp ? "tcp" : "udp";
            var info = new ServerRecordInfo
            {
                DevId = devId,
                PublicEp = _udp.LocalEndPoint,
                WanPort = wanPort,
                IsTcp = rec.IsTcp,
                KcpConfig = rec.KcpConfig,
                Timeout = rec.Timeout,
                LastSeen = DateTime.UtcNow,
                IsAuthenticated = true,
                OwnerUser = "localhost"
            };

            _routingTable[$"{rec.Name}/{proto}"] = info;
            _routingTable[$"{rec.Name}.{devId}/{proto}"] = info;
            if (!string.IsNullOrEmpty(devName))
            {
                _routingTable[$"{rec.Name}.{devName}/{proto}"] = info;
            }

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
                // 记录中继映射，确保即使打洞未完成也能立刻中继数据
                _relaySessions[sessionId] = new RelaySession
                {
                    ClientEp = remoteEp,
                    ServerEp = sInfo.PublicEp,
                    LastSeen = DateTime.UtcNow
                };

                // 1. 通知 S 端发起准备与打洞 (RelayStart)
                // [MsgType 1][SessionId 16][TargetName string][ClientPublicEp]
                byte[] relayStartBuf = ArrayPool<byte>.Shared.Rent(512);
                try
                {
                    relayStartBuf[0] = (byte)MsgType.RelayStart;
                    sessionId.TryWriteBytes(relayStartBuf.AsSpan(1, 16));
                    int offset = 17;
                    offset += ProtocolHelper.WriteString(relayStartBuf.AsSpan(offset), targetName);
                    offset += ProtocolHelper.WriteIPEndPoint(relayStartBuf.AsSpan(offset), remoteEp);

                    await _udp.SendAsync(relayStartBuf.AsMemory(0, offset), sInfo.PublicEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(relayStartBuf);
                }

                // 2. 向 C 端返回 S 的地址信息用于中继与打洞 (Punch / QueryResponse)
                // [MsgType 1][SessionId 16][DevId 16][Status 1 (1=Success)][ServerPublicEp][ServerWanPort 4][Timeout 4][KcpConfig 21][LocalEps...]
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

                    await _udp.SendAsync(punchRespBuf.AsMemory(0, offset), remoteEp, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(punchRespBuf);
                }

                Log.Info($"[P] Query success: Routed {sessionId} to S ({sInfo.PublicEp})");
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

                    foreach (var kvp in _routingTable)
                    {
                        if (now - kvp.Value.LastSeen > timeout)
                        {
                            _routingTable.TryRemove(kvp.Key, out _);
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