using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UDRoute
{
    public static class ProtocolHelper
    {
        public static bool DisableIPv6 { get; set; } = false;

        // 编码字符串: 4字节长度 + UTF8内容 (零拷贝方式写入Span)
        public static int WriteString(Span<byte> buffer, string text)
        {
            int bytesCount = Encoding.UTF8.GetByteCount(text);
            BinaryPrimitives.WriteInt32LittleEndian(buffer, bytesCount);
            Encoding.UTF8.GetBytes(text, buffer.Slice(4));
            return 4 + bytesCount;
        }

        public static (string text, int readLen) ReadString(ReadOnlySpan<byte> buffer)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            return (Encoding.UTF8.GetString(buffer.Slice(4, len)), 4 + len);
        }

        public static bool AreEndPointsEqual(EndPoint? ep1, EndPoint? ep2)
        {
            if (ep1 == null && ep2 == null) return true;
            if (ep1 == null || ep2 == null) return false;
            if (ep1.Equals(ep2)) return true;

            if (ep1 is IPEndPoint ip1 && ep2 is IPEndPoint ip2)
            {
                if (ip1.Port != ip2.Port) return false;
                var addr1 = ip1.Address.IsIPv4MappedToIPv6 ? ip1.Address.MapToIPv4() : ip1.Address;
                var addr2 = ip2.Address.IsIPv4MappedToIPv6 ? ip2.Address.MapToIPv4() : ip2.Address;
                return addr1.Equals(addr2);
            }
            return false;
        }

        // 编码 EndPoint (IPv4 / IPv6)
        public static int WriteIPEndPoint(Span<byte> buffer, EndPoint endPoint)
        {
            if (endPoint is not IPEndPoint ipEp)
            {
                ipEp = (IPEndPoint)endPoint;
            }

            var address = ipEp.Address;
            if (address.AddressFamily == AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6)
            {
                var ipv4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                buffer[0] = 4;
                ipv4.TryWriteBytes(buffer.Slice(1, 4), out _);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5), ipEp.Port);
                return 9;
            }
            else
            {
                buffer[0] = 6;
                address.TryWriteBytes(buffer.Slice(1, 16), out _);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(17), ipEp.Port);
                return 21;
            }
        }

        // 解码 EndPoint
        public static (IPEndPoint ep, int readLen) ReadIPEndPoint(ReadOnlySpan<byte> buffer)
        {
            byte family = buffer[0];
            if (family == 4)
            {
                var ip = new IPAddress(buffer.Slice(1, 4));
                int port = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(5));
                return (new IPEndPoint(ip, port), 9);
            }
            else
            {
                var ip = new IPAddress(buffer.Slice(1, 16));
                int port = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(17));
                return (new IPEndPoint(ip, port), 21);
            }
        }

        // 编码 KCP 配置参数 (21字节)
        public static int WriteKcpConfig(Span<byte> buffer, KcpConfig config)
        {
            buffer[0] = (byte)(config.NoDelay ? 1 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), config.Interval);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5, 4), config.Resend);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(9, 4), config.Nc);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(13, 4), config.SndWnd);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(17, 4), config.RcvWnd);
            return 21;
        }

        // 解码 KCP 配置参数
        public static (KcpConfig config, int readLen) ReadKcpConfig(ReadOnlySpan<byte> buffer)
        {
            var config = new KcpConfig
            {
                NoDelay = buffer[0] != 0,
                Interval = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1, 4)),
                Resend = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(5, 4)),
                Nc = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(9, 4)),
                SndWnd = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(13, 4)),
                RcvWnd = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(17, 4))
            };
            return (config, 21);
        }

        public static (string Host, int Port) ParseHostAndPort(string hostAndPort, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(hostAndPort)) return ("", defaultPort);
            string host = hostAndPort.Trim();
            int port = defaultPort;

            int lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && !host.EndsWith("]"))
            {
                var portStr = host.Substring(lastColon + 1);
                var hostStr = host.Substring(0, lastColon).Trim('[', ']');
                if (int.TryParse(portStr, out int p))
                {
                    port = p;
                    host = hostStr;
                }
            }
            else
            {
                host = host.Trim('[', ']');
            }
            return (host, port);
        }

        // 解析标准 EndPoint（支持域名/IP 及自定义端口）
        public static async Task<IPEndPoint?> ResolveEndPointAsync(string hostAndPort, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(hostAndPort)) return null;

            var (host, port) = ParseHostAndPort(hostAndPort, defaultPort);

            int lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && !host.EndsWith("]"))
            {
                var portStr = host.Substring(lastColon + 1);
                var hostStr = host.Substring(0, lastColon).Trim('[', ']');
                if (int.TryParse(portStr, out int p))
                {
                    port = p;
                    host = hostStr;
                }
            }
            else
            {
                host = host.Trim('[', ']');
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return new IPEndPoint(ip, port);
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(3000);
                var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
                if (DisableIPv6) addresses = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                if (addresses.Length > 0)
                {
                    return new IPEndPoint(addresses[0], port);
                }
            }
            catch { }

            return null;
        }

        // 解析标准 EndPoint 并返回所有解析到的 IP (双栈双发用)
        public static async Task<IPEndPoint[]> ResolveAllEndPointsAsync(string hostAndPort, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(hostAndPort)) return Array.Empty<IPEndPoint>();

            string host = hostAndPort.Trim();
            int port = defaultPort;

            int lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && !host.EndsWith("]"))
            {
                var portStr = host.Substring(lastColon + 1);
                var hostStr = host.Substring(0, lastColon).Trim('[', ']');
                if (int.TryParse(portStr, out int p))
                {
                    port = p;
                    host = hostStr;
                }
            }
            else
            {
                host = host.Trim('[', ']');
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return new[] { new IPEndPoint(ip, port) };
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(3000);
                var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
                if (DisableIPv6) addresses = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                var eps = new List<IPEndPoint>();
                foreach (var a in addresses)
                {
                    eps.Add(new IPEndPoint(a, port));
                }
                return eps.ToArray();
            }
            catch { }

            return Array.Empty<IPEndPoint>();
        }

        public static bool IsGlobalUnicastIPv6(IPAddress addr)
        {
            if (addr.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast) return false;
            if (IPAddress.IsLoopback(addr) || addr.IsIPv4MappedToIPv6) return false;

            Span<byte> bytes = stackalloc byte[16];
            if (!addr.TryWriteBytes(bytes, out int written) || written != 16) return false;
            if ((bytes[0] & 0xFE) == 0xFC) return false;

            for (int i = 0; i < 16; i++)
            {
                if (bytes[i] != 0) return true;
            }
            return false;
        }

        private static int GetLocalEndpointPriority(UnicastIPAddressInformation ip)
        {
            if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                bool isDeprecated = false;
                bool isRandom = false;
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var dad = ip.DuplicateAddressDetectionState;
                        isDeprecated = dad == DuplicateAddressDetectionState.Deprecated || dad == DuplicateAddressDetectionState.Invalid;
                    }
                    catch { }
                    try
                    {
                        isRandom = ip.SuffixOrigin == SuffixOrigin.Random;
                    }
                    catch { }
                }

                if (IsGlobalUnicastIPv6(ip.Address))
                {
                    if (isDeprecated) return 4;
                    return isRandom ? 0 : 1;
                }
                return 3;
            }

            Span<byte> v4Bytes = stackalloc byte[4];
            if (ip.Address.TryWriteBytes(v4Bytes, out int len) && len == 4 && v4Bytes[0] == 169 && v4Bytes[1] == 254)
            {
                return 5;
            }
            return 2;
        }

        // 获取本机所有有效的局域网/公网 IP (IPv4 和 IPv6)
        public static List<IPEndPoint> GetLocalEndPoints(int port)
        {
            var candidates = new List<(IPEndPoint Ep, int Priority)>();
            var seenIps = new HashSet<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (IPAddress.IsLoopback(ip.Address)) continue;
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork ||
                            (!DisableIPv6 && ip.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ip.Address.IsIPv6LinkLocal))
                        {
                            if (seenIps.Add(ip.Address))
                            {
                                Logging.Log.Trace($"Found local IP: {ip.Address}");
                                candidates.Add((new IPEndPoint(ip.Address, port), GetLocalEndpointPriority(ip)));
                            }
                        }
                    }
                }
            }
            catch { }

            candidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            var list = new List<IPEndPoint>(candidates.Count);
            foreach (var item in candidates)
            {
                list.Add(item.Ep);
            }
            return list;
        }

        public static int WriteLocalEndPoints(Span<byte> buffer, IReadOnlyList<IPEndPoint>? endpoints, int maxCount = 10)
        {
            if (buffer.Length < 1) return 0;
            int offset = 1;
            byte count = 0;

            if (endpoints != null)
            {
                foreach (var ep in endpoints)
                {
                    if (ep.Port <= 0 || ep.Port > 65535) continue;
                    int needed = (ep.AddressFamily == AddressFamily.InterNetwork || ep.Address.IsIPv4MappedToIPv6) ? 9 : 21;
                    if (offset + needed > buffer.Length) break;
                    offset += WriteIPEndPoint(buffer.Slice(offset), ep);
                    count++;
                    if (count >= maxCount) break;
                }
            }

            buffer[0] = count;
            return offset;
        }

        public static List<IPEndPoint> ReadLocalEndPoints(ReadOnlySpan<byte> buffer, out int bytesRead)
        {
            var list = new List<IPEndPoint>();
            bytesRead = 0;
            if (buffer.Length < 1) return list;

            byte count = buffer[0];
            int offset = 1;
            for (int i = 0; i < count && offset < buffer.Length; i++)
            {
                byte family = buffer[offset];
                int needed = family == 4 ? 9 : (family == 6 ? 21 : -1);
                if (needed < 0 || offset + needed > buffer.Length) break;

                var (ep, len) = ReadIPEndPoint(buffer.Slice(offset));
                offset += len;
                if (ep.Port > 0 && ep.Port <= 65535)
                {
                    list.Add(ep);
                }
            }

            bytesRead = offset;
            return list;
        }

        public static byte[] BuildQueryPacket(
            ushort contextId,
            Guid sessionId,
            string queryName,
            bool forceRelay,
            bool isReuse,
            Guid instanceId,
            IReadOnlyList<IPEndPoint>? localEps)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(512);
            try
            {
                buf[0] = (byte)MsgType.Query;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1, 2), contextId);
                sessionId.TryWriteBytes(buf.AsSpan(3, 16));
                int offset = 19 + WriteString(buf.AsSpan(19), queryName);
                buf[offset++] = (byte)((forceRelay ? 1 : 0) | (isReuse ? 2 : 0));
                instanceId.TryWriteBytes(buf.AsSpan(offset, 16));
                offset += 16;

                if (localEps != null && localEps.Count > 0)
                {
                    offset += WriteLocalEndPoints(buf.AsSpan(offset), localEps);
                }

                return buf.AsSpan(0, offset).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public static byte[] BuildRelayStartPacket(
            ushort contextId,
            Guid sessionId,
            string targetName,
            EndPoint clientPublicEp,
            bool allowRelay,
            bool effectiveForceRelay,
            bool isReuse,
            IReadOnlyList<IPEndPoint>? clientLocalEps)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(512);
            try
            {
                buf[0] = (byte)MsgType.RelayStart;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1, 2), contextId);
                sessionId.TryWriteBytes(buf.AsSpan(3, 16));
                int offset = 19;
                offset += WriteString(buf.AsSpan(offset), targetName);
                offset += WriteIPEndPoint(buf.AsSpan(offset), clientPublicEp);
                buf[offset++] = (byte)((allowRelay ? 1 : 0) | (effectiveForceRelay ? 2 : 0) | (isReuse ? 4 : 0));

                if (clientLocalEps != null && clientLocalEps.Count > 0)
                {
                    offset += WriteLocalEndPoints(buf.AsSpan(offset), clientLocalEps);
                }

                return buf.AsSpan(0, offset).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public static List<IPEndPoint> BuildPunchCandidates(
            IPEndPoint? primaryRemoteEp,
            int wanPort,
            IReadOnlyList<IPEndPoint>? peerLocalEps,
            int myLocalPort,
            string logPrefix)
        {
            var localIps = GetLocalIPAddresses();
            var candidates = new List<IPEndPoint>();

            void TryAddPublicCandidate(IPEndPoint? ep)
            {
                if (ep == null || ep.Port <= 0 || ep.Port > 65535) return;
                if (DisableIPv6 && ep.AddressFamily == AddressFamily.InterNetworkV6) return;
                if (myLocalPort > 0 && ep.Port == myLocalPort && (localIps.Contains(ep.Address) || IPAddress.IsLoopback(ep.Address))) return;
                if (!candidates.Any(c => AreEndPointsEqual(c, ep))) candidates.Add(ep);
            }

            TryAddPublicCandidate(primaryRemoteEp);
            if (primaryRemoteEp != null && wanPort > 0 && wanPort != primaryRemoteEp.Port)
            {
                TryAddPublicCandidate(new IPEndPoint(primaryRemoteEp.Address, wanPort));
            }

            if (peerLocalEps != null)
            {
                foreach (var ep in peerLocalEps)
                {
                    if (ep.Port <= 0 || ep.Port > 65535) continue;
                    if (DisableIPv6 && ep.AddressFamily == AddressFamily.InterNetworkV6) continue;

                    if (localIps.Contains(ep.Address))
                    {
                        Logging.Log.Debug($"[{logPrefix}] Skipping candidate endpoint {ep}: IP matches local machine IP.");
                        continue;
                    }

                    if (IPAddress.IsLoopback(ep.Address) && myLocalPort > 0 && ep.Port == myLocalPort)
                    {
                        continue;
                    }

                    if (!candidates.Any(c => AreEndPointsEqual(c, ep))) candidates.Add(ep);
                }
            }

            return candidates;
        }

        public static async Task RunUdpPunchLoopAsync(
            ZeroCopyUdpSocket udp,
            TunnelSession session,
            Guid selfInstanceId,
            IReadOnlyList<IPEndPoint> candidates,
            string logPrefix,
            CancellationToken ct)
        {
            if (candidates.Count == 0)
            {
                Logging.Log.Info($"[{logPrefix}] Session {session.SessionId}: All candidate endpoints were filtered out (no remote targets). Skipping UDP punch and continuing with Proxy relay.");
                return;
            }

            byte[] punchBuf = new byte[36];
            punchBuf[0] = (byte)MsgType.Punch;
            BinaryPrimitives.WriteUInt16LittleEndian(punchBuf.AsSpan(1, 2), 0);
            session.SessionId.TryWriteBytes(punchBuf.AsSpan(3, 16));
            selfInstanceId.TryWriteBytes(punchBuf.AsSpan(19, 16));
            punchBuf[35] = PunchStatus.PunchReq;

            Logging.Log.Info($"[{logPrefix}] Session {session.SessionId} starting UDP punch to targets: {string.Join(", ", candidates)} (duration: {Constants.DefaultPunchRetries * Constants.DefaultPunchIntervalMs / 1000}s, interval: {Constants.DefaultPunchIntervalMs}ms)");

            for (int i = 0; i < Constants.DefaultPunchRetries && !session.IsDirect && !ct.IsCancellationRequested; i++)
            {
                foreach (var ep in candidates)
                {
                    try
                    {
                        await udp.SendAsync(punchBuf, ep, ct);
                    }
                    catch { }
                }
                await Task.Delay(Constants.DefaultPunchIntervalMs, ct);
            }
        }

        // 根据远端目标地址类型获取本机可达的本地端点（优先局域网/公网IP，若对端为回环则返回回环）
        public static IPEndPoint GetReachableLocalEndPoint(EndPoint targetEp, int wanPort, int localPort)
        {
            int portToUse = wanPort > 0 ? wanPort : localPort;
            if (targetEp is IPEndPoint tip)
            {
                if (IPAddress.IsLoopback(tip.Address))
                {
                    return new IPEndPoint(tip.Address, portToUse);
                }
                if (tip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var localEps = GetLocalEndPoints(portToUse);
                    var v6 = localEps.FirstOrDefault(ep => ep.AddressFamily == AddressFamily.InterNetworkV6 && !ep.Address.IsIPv6LinkLocal);
                    if (v6 != null) return v6;
                    return new IPEndPoint(IPAddress.IPv6Loopback, portToUse);
                }
                else
                {
                    var localEps = GetLocalEndPoints(portToUse);
                    var v4 = localEps.FirstOrDefault(ep => ep.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 != null) return v4;
                    return new IPEndPoint(IPAddress.Loopback, portToUse);
                }
            }
            return new IPEndPoint(IPAddress.Loopback, portToUse);
        }

        // 获取本机所有网卡已分配的单播 IP 地址集合（不包含回环）
        public static HashSet<IPAddress> GetLocalIPAddresses()
        {
            var set = new HashSet<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (DisableIPv6 && ip.Address.AddressFamily == AddressFamily.InterNetworkV6) continue;

                        if (!IPAddress.IsLoopback(ip.Address))
                        {
                            set.Add(ip.Address);
                        }
                    }
                }
            }
            catch { }
            return set;
        }

        // IPv6 故障黑名单 (针对特定的 Proxy IPV6 地址)
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<IPAddress, bool> UnavailableIPv6 = new();

        // 判断当前程序是否有高权限（Windows: 管理员 / Linux: root）
        public static bool IsElevatedPrivilege()
        {
            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    {
                        var principal = new System.Security.Principal.WindowsPrincipal(identity);
                        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    }
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    // Linux 一般检查运行用户是否为 root
                    return Environment.UserName == "root";
                }
            }
            catch { }
            return false; // 无法判断时默认视为没有权限
        }

        // 尝试重置系统的 IPv6 协议栈 (支持 Linux 和 Windows)
        public static async Task ResetIPv6StackAsync()
        {
            if (!IsElevatedPrivilege())
            {
                Logging.Log.Error("[IPv6] Insufficient privileges. Administrator/root rights are required to reset the IPv6 stack.");
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 设定 10 秒超时防卡死

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    Logging.Log.Info("[IPv6] Triggering Linux IPv6 stack reset via sysctl...");
                    var psi1 = new System.Diagnostics.ProcessStartInfo("sysctl", "-w net.ipv6.conf.all.disable_ipv6=1") { CreateNoWindow = true };
                    using (var p1 = System.Diagnostics.Process.Start(psi1)) { if (p1 != null) await p1.WaitForExitAsync(cts.Token); }
                    
                    await Task.Delay(500); 
                    
                    var psi2 = new System.Diagnostics.ProcessStartInfo("sysctl", "-w net.ipv6.conf.all.disable_ipv6=0") { CreateNoWindow = true };
                    using (var p2 = System.Diagnostics.Process.Start(psi2)) { if (p2 != null) await p2.WaitForExitAsync(cts.Token); }
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    Logging.Log.Info("[IPv6] Triggering Windows IPv6 stack reset via PowerShell...");
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command \"Disable-NetAdapterBinding -Name '*' -ComponentID ms_tcpip6; Start-Sleep -Milliseconds 500; Enable-NetAdapterBinding -Name '*' -ComponentID ms_tcpip6\"") 
                    { 
                        CreateNoWindow = true
                    };
                    using (var p = System.Diagnostics.Process.Start(psi)) { if (p != null) await p.WaitForExitAsync(cts.Token); }
                }
                
                await Task.Delay(2000); // 给 SLAAC 分配新 IP 的时间
                Logging.Log.Info("[IPv6] IPv6 stack reset completed.");
            }
            catch (OperationCanceledException)
            {
                Logging.Log.Error("[IPv6] IPv6 stack reset command timed out (10s limit).");
            }
            catch (Exception ex)
            {
                Logging.Log.Error($"[IPv6] Failed to reset IPv6 stack: {ex.Message}");
            }
        }

        public static async Task SendWithRetryAsync(
            ZeroCopyUdpSocket udp,
            byte[] packet,
            EndPoint remoteEp,
            Task? completedCheck,
            CancellationToken ct,
            int maxAttempts = 3,
            int retryIntervalMs = Constants.DefaultRetryIntervalMs)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (completedCheck != null && completedCheck.IsCompleted) break;
                try
                {
                    await udp.SendAsync(packet, remoteEp, ct);
                }
                catch { }

                if (i < maxAttempts - 1)
                {
                    if (completedCheck != null)
                    {
                        try
                        {
                            var delayTask = Task.Delay(retryIntervalMs, ct);
                            var completed = await Task.WhenAny(completedCheck, delayTask);
                            if (completed == completedCheck || completedCheck.IsCompleted)
                            {
                                break;
                            }
                        }
                        catch { break; }
                    }
                    else
                    {
                        try { await Task.Delay(10, ct); } catch { break; }
                    }
                }
            }
        }
    }
}