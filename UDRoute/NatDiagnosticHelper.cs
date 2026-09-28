using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UDRoute
{
    public class NatDiagnosticResult
    {
        public string TargetServer { get; set; } = string.Empty;
        public bool? IsIpv4Direct { get; set; }
        public string Ipv4Detail { get; set; } = string.Empty;
        public bool IsIpv6Direct { get; set; }
        public string Ipv6Detail { get; set; } = string.Empty;
        public bool? IsConeNat { get; set; }
        public string ConeDetail { get; set; } = string.Empty;
    }

    public static class NatDiagnosticHelper
    {
        private static readonly string[] FallbackStunServers = new[]
        {
            "stun.syncthing.net:3478",
            "stun.qq.com:3478",
            "stun.miwifi.com:3478",
            "stun.cloudflare.com:3478"
        };

        private static readonly IPAddress[] FallbackIpv6Endpoints = new[]
        {
            IPAddress.Parse("2400:3200::1"),        // AliDNS
            IPAddress.Parse("2001:4860:4860::8888"), // Google DNS
            IPAddress.Parse("2606:4700:4700::1111")  // Cloudflare DNS
        };

        public static string ResolveTargetServer(string[] args)
        {
            // 1. 检查是否存在带有 '@' 的快捷命令 (如 3389/tcp=rdp@p.example.com 或 name@p.example.com)
            foreach (var arg in args)
            {
                int atIdx = arg.IndexOf('@');
                if (atIdx >= 0 && atIdx < arg.Length - 1)
                {
                    return arg.Substring(atIdx + 1).Trim();
                }
            }

            // 2. 检查是否有 -c <ini_file> 指定的配置文件
            string? iniPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-c", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    iniPath = args[i + 1];
                    break;
                }
            }

            // 3. 检查是否有直接传入的 .ini 文件
            if (iniPath == null)
            {
                foreach (var arg in args)
                {
                    if (arg.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith("-"))
                    {
                        iniPath = arg;
                        break;
                    }
                }
            }

            // 4. 检查是否有独立的非选项参数作为目标地址 (如 udroute -test p.example.com:9400)
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("-") || a.Contains('=')) continue;
                if (i > 0 && args[i - 1].Equals("-c", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                return a.Trim();
            }

            // 5. 若指定了 ini 或默认当前目录下存在 udroute.ini，尝试解析获取目标 P 端
            string iniToParse = iniPath ?? "udroute.ini";
            string? resolvedIni = ConfigParser.ResolveIniPath(iniToParse);
            if (resolvedIni != null && File.Exists(resolvedIni))
            {
                var cfg = ConfigParser.Parse(new[] { "-c", resolvedIni, "-log", "none" });
                if (cfg != null)
                {
                    if (cfg.ServerRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ServerRecords[0].TargetServer))
                    {
                        return cfg.ServerRecords[0].TargetServer;
                    }
                    if (cfg.ClientRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ClientRecords[0].TargetServer))
                    {
                        return cfg.ClientRecords[0].TargetServer;
                    }
                }
            }

            // 6. 默认公共测试服务器
            return "www.qzsoft.top:9400";
        }

        public static async Task<NatDiagnosticResult> RunAsync(string[] args, TextWriter? writer = null)
        {
            writer ??= Console.Out;
            string targetServer = ResolveTargetServer(args);
            if (targetServer.Equals("this", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(targetServer))
            {
                targetServer = "127.0.0.1:9400";
            }

            writer.WriteLine("==================================================");
            writer.WriteLine("UDRoute 网络连通性与 NAT 路由诊断测试");
            writer.WriteLine($"目标 P 端: {targetServer}");
            writer.WriteLine("==================================================");
            writer.WriteLine("[*] 正在解析目标服务器地址...");

            var allEps = await ProtocolHelper.ResolveAllEndPointsAsync(targetServer, Constants.DefaultProxyPort);
            var ipv4Eps = allEps.Where(ep => ep.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var ipv6Eps = allEps.Where(ep => ep.AddressFamily == AddressFamily.InterNetworkV6).ToArray();

            var result = new NatDiagnosticResult { TargetServer = targetServer };

            if (allEps.Length == 0)
            {
                writer.WriteLine($"[!] 错误: 无法解析 P 端地址 '{targetServer}'。");
                result.IsIpv4Direct = false;
                result.Ipv4Detail = $"无法解析域名或地址: {targetServer}";
                result.IsIpv6Direct = false;
                result.Ipv6Detail = "域名解析失败";
                result.IsConeNat = null;
                result.ConeDetail = "无法连接 P 端";
                PrintSummary(result, writer);
                return result;
            }

            using var udp = new ZeroCopyUdpSocket(0);
            var localBindEp = (IPEndPoint)udp.LocalEndPoint;
            var allLocalIps = ProtocolHelper.GetLocalIPAddresses();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pendingTests = new ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>>();
            var pendingStun = new ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>>();
            var pendingDns = new ConcurrentDictionary<ushort, TaskCompletionSource<bool>>();

            // 启动单 Socket 后台数据包接收循环
            var receiveLoopTask = Task.Run(async () =>
            {
                byte[] buf = new byte[65535];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var (len, remoteEp) = await udp.ReceiveAsync(buf, cts.Token);
                        if (len < 1) continue;

                        var span = buf.AsSpan(0, len);

                        // 1. 处理 UDRoute 内部协议消息 (EchoResp 或 NatTestResp)
                        MsgType type = (MsgType)span[0];
                        if ((type == MsgType.EchoResp || type == MsgType.NatTestResp) && len >= 17)
                        {
                            Guid id = new Guid(span.Slice(1, 16));
                            if (pendingTests.TryRemove(id, out var tcs))
                            {
                                byte[] copy = span.ToArray();
                                tcs.TrySetResult((copy, remoteEp));
                                continue;
                            }
                        }

                        // 2. 处理标准 STUN 响应 (0x0101)
                        if (len >= 20 && span[0] == 0x01 && span[1] == 0x01)
                        {
                            string txKey = Convert.ToHexString(span.Slice(8, 12));
                            if (pendingStun.TryRemove(txKey, out var stunTcs))
                            {
                                var parsed = ParseStunResponse(span);
                                if (parsed != null)
                                {
                                    stunTcs.TrySetResult(parsed);
                                    continue;
                                }
                            }
                        }

                        // 3. 处理 DNS 响应 (用于 IPv6 可达性验证)
                        if (len >= 12 && (remoteEp is IPEndPoint rep && rep.Port == 53))
                        {
                            ushort dnsId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0, 2));
                            if (pendingDns.TryRemove(dnsId, out var dnsTcs))
                            {
                                dnsTcs.TrySetResult(true);
                                continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { }
                }
            });

            try
            {
                // ==========================================
                // 1. 测试本机与 P 是否是 IPV4 直连
                // ==========================================
                writer.WriteLine("[*] 正在测试本机与 P 端的 IPv4 直连状态...");
                IPEndPoint? mappedIpv4Ep = null;
                int? pAltPort = null;
                long ipv4Rtt = 0;
                IPEndPoint? primaryPIpv4Ep = ipv4Eps.FirstOrDefault();

                if (primaryPIpv4Ep == null)
                {
                    result.IsIpv4Direct = false;
                    result.Ipv4Detail = "P 端未解析到有效的 IPv4 地址";
                }
                else
                {
                    var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token);
                    if (probeRes.PublicEp != null)
                    {
                        mappedIpv4Ep = probeRes.PublicEp;
                        pAltPort = probeRes.AltPort;
                        ipv4Rtt = probeRes.RttMs;

                        // 判断本机是否与 P 是 IPv4 直连：
                        // 如果 P 端接收到的公网 IP 与本机任一网卡分配的 IPv4 地址一致（或同处局域网/直连路由），说明无 NAT 转换
                        bool matchesLocal = allLocalIps.Any(a => a.Equals(mappedIpv4Ep.Address)) ||
                                            IPAddress.IsLoopback(mappedIpv4Ep.Address) ||
                                            primaryPIpv4Ep.Address.Equals(mappedIpv4Ep.Address);

                        if (matchesLocal)
                        {
                            result.IsIpv4Direct = true;
                            result.Ipv4Detail = $"公网 IPV4 直连 (本机 IP: {mappedIpv4Ep.Address}, 延迟: {ipv4Rtt}ms, 无 NAT 转换)";
                        }
                        else
                        {
                            result.IsIpv4Direct = false;
                            var localIpv4 = allLocalIps.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                            string localIpStr = localIpv4?.ToString() ?? "内网未知";
                            result.Ipv4Detail = $"处于 NAT 路由后方 (本地内网 IP: {localIpStr}, P 端检测公网 IP: {mappedIpv4Ep}, 延迟: {ipv4Rtt}ms)";
                        }
                    }
                    else
                    {
                        result.IsIpv4Direct = false;
                        result.Ipv4Detail = $"无法通过 IPv4 连接到 P 端 ({primaryPIpv4Ep})，响应超时";
                    }
                }

                // ==========================================
                // 2. 测试本机是否具备 IPV6 直连
                // ==========================================
                writer.WriteLine("[*] 正在测试本机是否具备 IPv6 直连能力...");
                var localGlobalIpv6List = allLocalIps.Where(IsGlobalUnicastIPv6).ToList();

                if (localGlobalIpv6List.Count == 0)
                {
                    result.IsIpv6Direct = false;
                    result.Ipv6Detail = "本机未分配公网 IPv6 地址 (未检测到有效的全球单播地址)";
                }
                else
                {
                    var myIpv6 = localGlobalIpv6List[0];
                    if (ipv6Eps.Length > 0)
                    {
                        var primaryPIpv6Ep = ipv6Eps[0];
                        var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token);
                        if (p6Res.PublicEp != null)
                        {
                            result.IsIpv6Direct = true;
                            result.Ipv6Detail = $"具备公网 IPV6 直连能力 (本机 IPv6: {myIpv6}, P 端检测 IPv6: {p6Res.PublicEp.Address}, 延迟: {p6Res.RttMs}ms)";
                        }
                        else
                        {
                            // P 端 IPv6 未响应，通过公网公共 IPv6 节点做回退验证
                            bool publicIpv6Ok = await ProbePublicIPv6ReachabilityAsync(udp, pendingDns, cts.Token);
                            if (publicIpv6Ok)
                            {
                                result.IsIpv6Direct = true;
                                result.Ipv6Detail = $"具备公网 IPV6 直连能力 (本机已分配公网 IPv6: {myIpv6}，外网 IPv6 连通正常，注: P 端 IPv6 未响应)";
                            }
                            else
                            {
                                result.IsIpv6Direct = false;
                                result.Ipv6Detail = $"本机已分配 IPv6 地址 ({myIpv6})，但外网 IPv6 路由不可达";
                            }
                        }
                    }
                    else
                    {
                        // P 端未解析到 AAAA 记录，测试外网公共 IPv6 连通性
                        bool publicIpv6Ok = await ProbePublicIPv6ReachabilityAsync(udp, pendingDns, cts.Token);
                        if (publicIpv6Ok)
                        {
                            result.IsIpv6Direct = true;
                            result.Ipv6Detail = $"具备公网 IPV6 直连能力 (本机已分配公网 IPv6: {myIpv6}，外网 IPv6 连通正常，注: P 端未配置 AAAA 记录)";
                        }
                        else
                        {
                            result.IsIpv6Direct = false;
                            result.Ipv6Detail = $"本机已分配 IPv6 地址 ({myIpv6})，但外网 IPv6 连通测试未通过";
                        }
                    }
                }

                // ==========================================
                // 3. 测试本机是否处于圆锥路由下
                // ==========================================
                writer.WriteLine("[*] 正在探测本机 NAT 路由类型 (圆锥 vs 对称)...");

                if (result.IsIpv4Direct == true)
                {
                    result.IsConeNat = true;
                    result.ConeDetail = "公网直连无 NAT (具备圆锥特性，端口直接暴露于公网，完全支持 P2P 直连打洞)";
                }
                else if (mappedIpv4Ep != null && primaryPIpv4Ep != null)
                {
                    int primaryPort = primaryPIpv4Ep.Port;
                    int altPortToTest = pAltPort.HasValue && pAltPort.Value > 0 ? pAltPort.Value : primaryPort + 1;

                    // 3.1 尝试通过 P 端的辅助端口反向探测 Full Cone (全锥型)
                    bool fullConePassed = await ProbeFullConeAsync(udp, primaryPIpv4Ep, pendingTests, cts.Token);
                    if (fullConePassed)
                    {
                        result.IsConeNat = true;
                        result.ConeDetail = "圆锥路由 - 全锥型 (Full Cone NAT / NAT1)，映射行为与目标无关 (EIM) 且允许外部任意端口直接入站，P2P 穿透极佳";
                    }
                    else
                    {
                        // 3.2 从同一个本地 Socket 向 P 端的辅助端口发送探测，检查端口映射一致性
                        var altEp = new IPEndPoint(primaryPIpv4Ep.Address, altPortToTest);
                        var altRes = await ProbePIpv4Async(udp, altEp, pendingTests, cts.Token, timeoutMs: 1200);

                        if (altRes.PublicEp != null)
                        {
                            if (mappedIpv4Ep.Port == altRes.PublicEp.Port)
                            {
                                result.IsConeNat = true;
                                result.ConeDetail = $"圆锥路由 - 限制锥型 (Restricted Cone NAT)，映射行为与目标无关 (EIM，端口映射保持一致: {mappedIpv4Ep.Port} == {altRes.PublicEp.Port})，支持 UDP P2P 打洞直连";
                            }
                            else
                            {
                                result.IsConeNat = false;
                                result.ConeDetail = $"对称路由 (Symmetric NAT / NAT4)，映射端口随目标变化 ({mappedIpv4Ep.Port} != {altRes.PublicEp.Port})，常规打洞无法直连，需通过 P 端流量中继";
                            }
                        }
                        else
                        {
                            // 3.3 若 P 端辅助端口未开放或受防火墙阻断，回退到公网 STUN 节点进行交叉验证
                            var stunMappedEp = await ProbePublicStunAsync(udp, pendingStun, cts.Token);
                            if (stunMappedEp != null)
                            {
                                if (mappedIpv4Ep.Port == stunMappedEp.Port)
                                {
                                    result.IsConeNat = true;
                                    result.ConeDetail = $"圆锥路由 - 锥型 NAT (Cone NAT)，访问不同外部节点时分配的外网端口一致 (P 端映射端口 {mappedIpv4Ep.Port} == STUN 映射端口 {stunMappedEp.Port})，支持 UDP P2P 打洞直连";
                                }
                                else
                                {
                                    result.IsConeNat = false;
                                    result.ConeDetail = $"对称路由 (Symmetric NAT / NAT4)，访问不同外部节点时分配了不同外网端口 (P 端映射端口 {mappedIpv4Ep.Port} != STUN 映射端口 {stunMappedEp.Port})，常规打洞无法直连，需通过 P 端流量中继";
                                }
                            }
                            else
                            {
                                result.IsConeNat = null;
                                result.ConeDetail = $"未能连接到第二探测节点比对端口映射，建议开放 P 端辅助端口 UDP {altPortToTest}";
                            }
                        }
                    }
                }
                else
                {
                    result.IsConeNat = null;
                    result.ConeDetail = "由于未能连通 P 端 IPv4，无法测试 NAT 路由类型";
                }
            }
            finally
            {
                cts.Cancel();
                try { await receiveLoopTask; } catch { }
            }

            PrintSummary(result, writer);
            return result;
        }

        private static void PrintSummary(NatDiagnosticResult res, TextWriter writer)
        {
            writer.WriteLine();
            writer.WriteLine("------------------ 测试结果汇总 ------------------");
            writer.WriteLine("1. 测试本机与 P 是否是 IPV4 直连:");
            writer.WriteLine($"   [结果] {(res.IsIpv4Direct == true ? "是 (IPV4 直连)" : res.IsIpv4Direct == false ? "否 (非 IPV4 直连)" : "未知")}");
            writer.WriteLine($"   [详情] {res.Ipv4Detail}");
            writer.WriteLine();
            writer.WriteLine("2. 测试本机是否具备 IPV6 直连:");
            writer.WriteLine($"   [结果] {(res.IsIpv6Direct ? "是 (具备 IPV6 直连能力)" : "否 (不具备 IPV6 直连能力)")}");
            writer.WriteLine($"   [详情] {res.Ipv6Detail}");
            writer.WriteLine();
            writer.WriteLine("3. 测试本机是否处于圆锥路由下:");
            writer.WriteLine($"   [结果] {(res.IsConeNat == true ? "是 (处于圆锥路由下)" : res.IsConeNat == false ? "否 (处于对称路由下)" : "未知 (探测受限)")}");
            writer.WriteLine($"   [详情] {res.ConeDetail}");
            writer.WriteLine("--------------------------------------------------");
        }

        private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv4Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1000)
        {
            for (int retry = 0; retry < 3; retry++)
            {
                if (ct.IsCancellationRequested) break;

                var testId = Guid.NewGuid();
                var tcs = new TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingTests[testId] = tcs;

                // 优先发送 NatTestReq (15)，同时准备 EchoReq (7) 兼容老版本 P 端
                byte[] req = new byte[18];
                req[0] = (byte)MsgType.NatTestReq;
                testId.TryWriteBytes(req.AsSpan(1, 16));
                req[17] = NatTestFlags.None;

                byte[] echoReq = new byte[17];
                echoReq[0] = (byte)MsgType.EchoReq;
                testId.TryWriteBytes(echoReq.AsSpan(1, 16));

                var sw = Stopwatch.StartNew();
                try
                {
                    await udp.SendAsync(req, pEp, ct);
                    await udp.SendAsync(echoReq, pEp, ct);
                }
                catch
                {
                    pendingTests.TryRemove(testId, out _);
                    continue;
                }

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    var (data, _) = await tcs.Task.WaitAsync(linked.Token);
                    sw.Stop();
                    long rtt = sw.ElapsedMilliseconds;

                    if (data.Length >= 17)
                    {
                        MsgType type = (MsgType)data[0];
                        var (ep, epLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        int? altPort = null;
                        if (type == MsgType.NatTestResp && data.Length >= 17 + epLen + 4)
                        {
                            altPort = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(17 + epLen, 4));
                        }
                        return (ep, altPort, rtt);
                    }
                }
                catch
                {
                    pendingTests.TryRemove(testId, out _);
                }
            }

            return (null, null, 0);
        }

        private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp6,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1200)
        {
            for (int retry = 0; retry < 2; retry++)
            {
                if (ct.IsCancellationRequested) break;

                var testId = Guid.NewGuid();
                var tcs = new TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingTests[testId] = tcs;

                byte[] echoReq = new byte[17];
                echoReq[0] = (byte)MsgType.EchoReq;
                testId.TryWriteBytes(echoReq.AsSpan(1, 16));

                var sw = Stopwatch.StartNew();
                try
                {
                    await udp.SendAsync(echoReq, pEp6, ct);
                }
                catch
                {
                    pendingTests.TryRemove(testId, out _);
                    continue;
                }

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    var (data, _) = await tcs.Task.WaitAsync(linked.Token);
                    sw.Stop();
                    long rtt = sw.ElapsedMilliseconds;

                    if (data.Length >= 17)
                    {
                        var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, null, rtt);
                    }
                }
                catch
                {
                    pendingTests.TryRemove(testId, out _);
                }
            }

            return (null, null, 0);
        }

        private static async Task<bool> ProbeFullConeAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct)
        {
            var testId = Guid.NewGuid();
            var tcs = new TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingTests[testId] = tcs;

            byte[] req = new byte[18];
            req[0] = (byte)MsgType.NatTestReq;
            testId.TryWriteBytes(req.AsSpan(1, 16));
            req[17] = NatTestFlags.ReqSendFromAltPort; // 请求从辅助端口回送

            try
            {
                await udp.SendAsync(req, pEp, ct);
            }
            catch
            {
                pendingTests.TryRemove(testId, out _);
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(1000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var (_, remoteEp) = await tcs.Task.WaitAsync(linked.Token);
                // 确认回包来源端口不等于主端口，证明未事先打洞的外网端口能直接穿透入站 (Full Cone)
                if (remoteEp is IPEndPoint rep && rep.Port != pEp.Port)
                {
                    return true;
                }
            }
            catch
            {
                pendingTests.TryRemove(testId, out _);
            }

            return false;
        }

        private static async Task<IPEndPoint?> ProbePublicStunAsync(
            ZeroCopyUdpSocket udp,
            ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> pendingStun,
            CancellationToken ct)
        {
            foreach (var stunHost in FallbackStunServers)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var resolved = await ProtocolHelper.ResolveAllEndPointsAsync(stunHost, 3478);
                    var ipv4Stun = resolved.FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork);
                    if (ipv4Stun == null) continue;

                    byte[] txId = new byte[12];
                    Random.Shared.NextBytes(txId);
                    string txKey = Convert.ToHexString(txId);

                    var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingStun[txKey] = tcs;

                    // 构造标准 STUN Binding Request (20 字节)
                    byte[] stunReq = new byte[20];
                    stunReq[0] = 0x00; stunReq[1] = 0x01; // Binding Request
                    stunReq[2] = 0x00; stunReq[3] = 0x00; // Message Length = 0
                    stunReq[4] = 0x21; stunReq[5] = 0x12; stunReq[6] = 0xA4; stunReq[7] = 0x42; // Magic Cookie
                    Buffer.BlockCopy(txId, 0, stunReq, 8, 12);

                    await udp.SendAsync(stunReq, ipv4Stun, ct);

                    using var timeoutCts = new CancellationTokenSource(1200);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    var mappedEp = await tcs.Task.WaitAsync(linked.Token);
                    if (mappedEp != null)
                    {
                        return mappedEp;
                    }
                }
                catch
                {
                    // 忽略单个 STUN 失败，尝试下一个
                }
            }

            return null;
        }

        private static async Task<bool> ProbePublicIPv6ReachabilityAsync(
            ZeroCopyUdpSocket udp,
            ConcurrentDictionary<ushort, TaskCompletionSource<bool>> pendingDns,
            CancellationToken ct)
        {
            foreach (var ip6 in FallbackIpv6Endpoints)
            {
                if (ct.IsCancellationRequested) break;

                ushort dnsId = (ushort)Random.Shared.Next(1, 65535);
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingDns[dnsId] = tcs;

                // 构造基础 DNS Query 包 (17 字节) 探测外部 IPv6 连通性
                byte[] dnsQuery = new byte[17];
                BinaryPrimitives.WriteUInt16BigEndian(dnsQuery.AsSpan(0, 2), dnsId);
                dnsQuery[2] = 0x01; dnsQuery[3] = 0x00; // Standard Query
                dnsQuery[4] = 0x00; dnsQuery[5] = 0x01; // 1 Question
                dnsQuery[12] = 0x00;                    // Root domain '.'
                dnsQuery[13] = 0x00; dnsQuery[14] = 0x01; // Type A
                dnsQuery[15] = 0x00; dnsQuery[16] = 0x01; // Class IN

                var targetEp = new IPEndPoint(ip6, 53);

                try
                {
                    await udp.SendAsync(dnsQuery, targetEp, ct);
                    using var timeoutCts = new CancellationTokenSource(1200);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    bool res = await tcs.Task.WaitAsync(linked.Token);
                    if (res) return true;
                }
                catch
                {
                    pendingDns.TryRemove(dnsId, out _);
                }
            }

            return false;
        }

        private static IPEndPoint? ParseStunResponse(ReadOnlySpan<byte> span)
        {
            if (span.Length < 20) return null;
            ushort msgLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
            int offset = 20;
            int end = Math.Min(span.Length, 20 + msgLen);

            IPEndPoint? fallbackMapped = null;

            while (offset + 4 <= end)
            {
                ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
                ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
                offset += 4;

                if (offset + attrLen > span.Length) break;

                // XOR-MAPPED-ADDRESS (0x0020)
                if (attrType == 0x0020 && attrLen >= 8)
                {
                    byte family = span[offset + 1];
                    if (family == 0x01) // IPv4
                    {
                        ushort xorPort = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
                        int port = xorPort ^ 0x2112;

                        uint xorIp = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
                        uint ipVal = xorIp ^ 0x2112A442;
                        byte[] ipBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(ipBytes, ipVal);
                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
                // MAPPED-ADDRESS (0x0001)
                else if (attrType == 0x0001 && attrLen >= 8 && fallbackMapped == null)
                {
                    byte family = span[offset + 1];
                    if (family == 0x01) // IPv4
                    {
                        int port = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
                        var ipBytes = span.Slice(offset + 4, 4).ToArray();
                        fallbackMapped = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }

                offset += (attrLen + 3) & ~3;
            }

            return fallbackMapped;
        }

        public static bool IsGlobalUnicastIPv6(IPAddress addr)
        {
            if (addr.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast) return false;
            if (IPAddress.IsLoopback(addr) || addr.IsIPv4MappedToIPv6) return false;

            byte[] bytes = addr.GetAddressBytes();
            // ULA (fc00::/7)
            if ((bytes[0] & 0xFE) == 0xFC) return false;

            bool allZero = true;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) { allZero = false; break; }
            }
            if (allZero) return false;

            return true;
        }
    }
}
