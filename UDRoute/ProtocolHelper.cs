using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UDRoute
{
    public static class ProtocolHelper
    {
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

        // 解析标准 EndPoint（支持域名/IP 及自定义端口）
        public static async Task<IPEndPoint?> ResolveEndPointAsync(string hostAndPort, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(hostAndPort)) return null;

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
                return new IPEndPoint(ip, port);
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
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
                var addresses = await Dns.GetHostAddressesAsync(host);
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

        // 获取本机所有有效的局域网/公网 IP (IPv4 和 IPv6)
        public static List<IPEndPoint> GetLocalEndPoints(int port)
        {
            var list = new List<IPEndPoint>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (IPAddress.IsLoopback(ip.Address)) continue;
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork ||
                            (ip.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ip.Address.IsIPv6LinkLocal))
                        {
                            Logging.Log.Trace($"Found local IP: {ip.Address}");
                            list.Add(new IPEndPoint(ip.Address, port));
                        }
                    }
                }
            }
            catch { }
            return list;
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