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

            // 6. 若均未指定，则抛出异常要求用户明确指定目标 P 端
            throw new InvalidOperationException(I18n.Text(
                "未指定目标 P 端服务器地址，请通过参数 (如 udroute -test p.example.com:9400) 或配置文件指定。",
                "Target Proxy server address not specified. Please specify via arguments (e.g. udroute -test p.example.com:9400) or config file."));
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
            writer.WriteLine(I18n.Text("UDRoute 网络连通性与 NAT 路由诊断测试", "UDRoute Network Connectivity and NAT Diagnostic Test"));
            writer.WriteLine(I18n.Text($"目标 P 端: {targetServer}", $"Target Proxy: {targetServer}"));
            writer.WriteLine("==================================================");
            writer.WriteLine(I18n.Text("[*] 正在解析目标服务器地址...", "[*] Resolving target server address..."));

            var allEps = await ProtocolHelper.ResolveAllEndPointsAsync(targetServer, Constants.DefaultProxyPort);
            var ipv4Eps = allEps.Where(ep => ep.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var ipv6Eps = allEps.Where(ep => ep.AddressFamily == AddressFamily.InterNetworkV6).ToArray();

            var result = new NatDiagnosticResult { TargetServer = targetServer };

            if (allEps.Length == 0)
            {
                writer.WriteLine(I18n.Text($"[!] 错误: 无法解析 P 端地址 '{targetServer}'。", $"[!] Error: Unable to resolve Proxy address '{targetServer}'."));
                result.IsIpv4Direct = false;
                result.Ipv4Detail = I18n.Text($"无法解析域名或地址: {targetServer}", $"Failed to resolve domain or address: {targetServer}");
                result.IsIpv6Direct = false;
                result.Ipv6Detail = I18n.Text("域名解析失败", "Domain resolution failed");
                result.IsConeNat = null;
                result.ConeDetail = I18n.Text("无法连接 P 端", "Unable to connect to Proxy");
                PrintSummary(result, writer);
                return result;
            }

            using var udp = new ZeroCopyUdpSocket(0);
            var localBindEp = (IPEndPoint)udp.LocalEndPoint;
            var allLocalIps = ProtocolHelper.GetLocalIPAddresses();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pendingTests = new ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>>();
            var pendingTestHandlers = new ConcurrentDictionary<Guid, Action<(byte[] Data, EndPoint RemoteEp)>>();

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

                        // 处理 UDRoute 内部协议消息 (EchoResp 或 NatTestResp)
                        MsgType type = (MsgType)span[0];
                        if ((type == MsgType.EchoResp || type == MsgType.NatTestResp) && len >= 17)
                        {
                            Guid id = new Guid(span.Slice(1, 16));
                            if (pendingTestHandlers.TryGetValue(id, out var handler))
                            {
                                byte[] copy = span.ToArray();
                                handler((copy, remoteEp));
                                continue;
                            }
                            if (pendingTests.TryRemove(id, out var tcs))
                            {
                                byte[] copy = span.ToArray();
                                tcs.TrySetResult((copy, remoteEp));
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
                writer.WriteLine(I18n.Text("[*] 正在测试本机与 P 端的 IPv4 直连状态...", "[*] Testing IPv4 direct connectivity with Proxy..."));
                IPEndPoint? mappedIpv4Ep = null;
                long ipv4Rtt = 0;
                IPEndPoint? primaryPIpv4Ep = ipv4Eps.FirstOrDefault();

                if (primaryPIpv4Ep == null)
                {
                    result.IsIpv4Direct = false;
                    result.Ipv4Detail = I18n.Text("P 端未解析到有效的 IPv4 地址", "Proxy does not have a valid IPv4 address resolved");
                }
                else
                {
                    var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token);
                    if (probeRes.PublicEp != null)
                    {
                        mappedIpv4Ep = probeRes.PublicEp;
                        ipv4Rtt = probeRes.RttMs;

                        // 判断本机是否与 P 是 IPv4 直连：
                        // 如果 P 端接收到的公网 IP 与本机任一网卡分配的 IPv4 地址一致（或同处局域网/直连路由），说明无 NAT 转换
                        bool matchesLocal = allLocalIps.Any(a => a.Equals(mappedIpv4Ep.Address)) ||
                                            IPAddress.IsLoopback(mappedIpv4Ep.Address) ||
                                            primaryPIpv4Ep.Address.Equals(mappedIpv4Ep.Address);

                        if (matchesLocal)
                        {
                            result.IsIpv4Direct = true;
                            result.Ipv4Detail = I18n.Text(
                                $"公网 IPV4 直连 (本机 IP: {mappedIpv4Ep.Address}, 延迟: {ipv4Rtt}ms, 无 NAT 转换)",
                                $"Public IPv4 direct (Local IP: {mappedIpv4Ep.Address}, RTT: {ipv4Rtt}ms, no NAT translation)");
                        }
                        else
                        {
                            result.IsIpv4Direct = false;
                            var localIpv4 = allLocalIps.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                            string localIpStr = localIpv4?.ToString() ?? I18n.Text("内网未知", "Unknown");
                            result.Ipv4Detail = I18n.Text(
                                $"处于 NAT 路由后方 (本地内网 IP: {localIpStr}, P 端检测公网 IP: {mappedIpv4Ep}, 延迟: {ipv4Rtt}ms)",
                                $"Behind NAT router (Local private IP: {localIpStr}, Proxy detected public IP: {mappedIpv4Ep}, RTT: {ipv4Rtt}ms)");
                        }
                    }
                    else
                    {
                        result.IsIpv4Direct = false;
                        result.Ipv4Detail = I18n.Text(
                            $"无法通过 IPv4 连接到 P 端 ({primaryPIpv4Ep})，响应超时",
                            $"Unable to connect to Proxy via IPv4 ({primaryPIpv4Ep}); response timed out");
                    }
                }

                // ==========================================
                // 2. 测试本机是否具备 IPV6 直连
                // ==========================================
                writer.WriteLine(I18n.Text("[*] 正在测试本机是否具备 IPv6 直连能力...", "[*] Testing local IPv6 direct connectivity..."));
                var localGlobalIpv6List = allLocalIps.Where(IsGlobalUnicastIPv6).ToList();

                if (localGlobalIpv6List.Count == 0)
                {
                    result.IsIpv6Direct = false;
                    result.Ipv6Detail = I18n.Text("本机未分配公网 IPv6 地址 (未检测到有效的全球单播地址)", "No public IPv6 address assigned to local machine (no valid global unicast address detected)");
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
                            result.Ipv6Detail = I18n.Text(
                                $"具备公网 IPV6 直连能力 (本机 IPv6: {myIpv6}, P 端检测 IPv6: {p6Res.PublicEp.Address}, 延迟: {p6Res.RttMs}ms)",
                                $"Public IPv6 direct connectivity available (Local IPv6: {myIpv6}, Proxy detected IPv6: {p6Res.PublicEp.Address}, RTT: {p6Res.RttMs}ms)");
                        }
                        else
                        {
                            result.IsIpv6Direct = false;
                            result.Ipv6Detail = I18n.Text(
                                $"本机已分配公网 IPv6 地址 ({myIpv6})，但向 P 端 ({primaryPIpv6Ep}) 发起 IPv6 探测超时",
                                $"Local machine has public IPv6 ({myIpv6}), but IPv6 probe to Proxy ({primaryPIpv6Ep}) timed out");
                        }
                    }
                    else
                    {
                        result.IsIpv6Direct = true;
                        result.Ipv6Detail = I18n.Text(
                            $"本机具备公网 IPv6 地址 ({myIpv6}) (目标 P 端未配置 IPv6 地址)",
                            $"Local machine has public IPv6 ({myIpv6}) (Target Proxy does not have IPv6 address configured)");
                    }
                }

                // ==========================================
                // 3. 测试本机是否处于圆锥路由下
                // ==========================================
                writer.WriteLine(I18n.Text("[*] 正在探测本机 NAT 路由类型 (圆锥 vs 对称)...", "[*] Probing local NAT routing type (Cone vs Symmetric)..."));

                if (result.IsIpv4Direct == true)
                {
                    result.IsConeNat = true;
                    result.ConeDetail = I18n.Text(
                        "公网直连无 NAT (具备圆锥特性，端口直接暴露于公网，完全支持 P2P 直连打洞)",
                        "Direct public connection without NAT (behaves as Cone, ports exposed directly, fully supports P2P hole punching)");
                }
                else if (mappedIpv4Ep != null && primaryPIpv4Ep != null)
                {
                    var (isCone, coneDetail) = await ProbeConeNatAsync(udp, primaryPIpv4Ep, mappedIpv4Ep, pendingTests, pendingTestHandlers, cts.Token);
                    result.IsConeNat = isCone;
                    result.ConeDetail = coneDetail;
                }
                else
                {
                    result.IsConeNat = null;
                    result.ConeDetail = I18n.Text("由于未能连通 P 端 IPv4，无法测试 NAT 路由类型", "Unable to test NAT routing type because IPv4 connection to Proxy failed");
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
            writer.WriteLine(I18n.Text("------------------ 测试结果汇总 ------------------", "------------------ Test Results Summary ------------------"));
            writer.WriteLine(I18n.Text("1. 测试本机与 P 是否是 IPV4 直连:", "1. Test IPv4 direct connection to Proxy:"));
            string v4ResultStr = res.IsIpv4Direct == true ? I18n.Text("是 (IPV4 直连)", "Yes (IPv4 Direct)") :
                                 res.IsIpv4Direct == false ? I18n.Text("否 (非 IPV4 直连)", "No (Not IPv4 Direct)") :
                                 I18n.Text("未知", "Unknown");
            writer.WriteLine(I18n.Text($"   [结果] {v4ResultStr}", $"   [Result] {v4ResultStr}"));
            writer.WriteLine(I18n.Text($"   [详情] {res.Ipv4Detail}", $"   [Detail] {res.Ipv4Detail}"));
            writer.WriteLine();
            writer.WriteLine(I18n.Text("2. 测试本机是否具备 IPV6 直连:", "2. Test local IPv6 direct connectivity:"));
            string v6ResultStr = res.IsIpv6Direct ? I18n.Text("是 (具备 IPV6 直连能力)", "Yes (IPv6 Direct Capable)") :
                                 I18n.Text("否 (不具备 IPV6 直连能力)", "No (Not IPv6 Direct Capable)");
            writer.WriteLine(I18n.Text($"   [结果] {v6ResultStr}", $"   [Result] {v6ResultStr}"));
            writer.WriteLine(I18n.Text($"   [详情] {res.Ipv6Detail}", $"   [Detail] {res.Ipv6Detail}"));
            writer.WriteLine();
            writer.WriteLine(I18n.Text("3. 测试本机是否处于圆锥路由下:", "3. Test whether local NAT is Cone NAT:"));
            string coneResultStr = res.IsConeNat == true ? I18n.Text("是 (处于圆锥路由下)", "Yes (Behind Cone NAT)") :
                                   res.IsConeNat == false ? I18n.Text("否 (处于对称路由下)", "No (Behind Symmetric NAT)") :
                                   I18n.Text("未知 (探测受限)", "Unknown (Diagnostic inconclusive)");
            writer.WriteLine(I18n.Text($"   [结果] {coneResultStr}", $"   [Result] {coneResultStr}"));
            writer.WriteLine(I18n.Text($"   [详情] {res.ConeDetail}", $"   [Detail] {res.ConeDetail}"));
            writer.WriteLine("--------------------------------------------------");
        }

        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(
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

                byte[] echoReq = new byte[17];
                echoReq[0] = (byte)MsgType.EchoReq;
                testId.TryWriteBytes(echoReq.AsSpan(1, 16));

                var sw = Stopwatch.StartNew();
                try
                {
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
                        var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, rtt);
                    }
                }
                catch
                {
                    pendingTests.TryRemove(testId, out _);
                }
            }

            return (null, 0);
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

        private static async Task<(bool? IsCone, string Detail)> ProbeConeNatAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint primaryPEp,
            IPEndPoint mappedIpv4Ep,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            ConcurrentDictionary<Guid, Action<(byte[] Data, EndPoint RemoteEp)>> pendingTestHandlers,
            CancellationToken ct)
        {
            Guid testId = Guid.NewGuid();
            var primaryTcs = new TaskCompletionSource<(IPEndPoint PublicEp, int TempPort)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var altDirectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool receivedFromAlt = false;
            int tempPort = 0;

            pendingTestHandlers[testId] = item =>
            {
                var (data, rEp) = item;
                if (rEp is IPEndPoint ipEp && data.Length >= 17)
                {
                    var (pubEp, epLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                    int alt = (data.Length >= 17 + epLen + 4) ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(17 + epLen, 4)) : 0;

                    if (ipEp.Port != primaryPEp.Port)
                    {
                        // 未经由客户端向该端口打洞前，直接收到了来自 P 端临时随机端口的数据包 -> 全锥型 NAT
                        receivedFromAlt = true;
                        altDirectTcs.TrySetResult(true);
                    }
                    else
                    {
                        tempPort = alt;
                        primaryTcs.TrySetResult((pubEp, alt));
                    }
                }
            };

            try
            {
                byte[] req = new byte[18];
                req[0] = (byte)MsgType.NatTestReq;
                testId.TryWriteBytes(req.AsSpan(1, 16));
                req[17] = NatTestFlags.None;

                for (int retry = 0; retry < 3; retry++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await udp.SendAsync(req, primaryPEp, ct);
                    }
                    catch { }

                    using var timeoutCts = new CancellationTokenSource(1200);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        await primaryTcs.Task.WaitAsync(linked.Token);
                        break;
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                if (primaryTcs.Task.IsCompletedSuccessfully)
                {
                    // 给全锥型接收 150ms 缓冲时间
                    if (!receivedFromAlt)
                    {
                        using var waitAltCts = new CancellationTokenSource(150);
                        using var linkedAlt = CancellationTokenSource.CreateLinkedTokenSource(ct, waitAltCts.Token);
                        try { await altDirectTcs.Task.WaitAsync(linkedAlt.Token); } catch { }
                    }

                    if (receivedFromAlt)
                    {
                        return (true, I18n.Text(
                            "圆锥路由 - 全锥型 (Full Cone NAT / NAT1)，映射行为与目标无关 (EIM) 且允许外部任意端口直接入站，P2P 穿透极佳",
                            "Cone NAT - Full Cone (NAT 1): Endpoint-Independent Mapping (EIM) and direct unsolicited inbound allowed, optimal for P2P traversal."));
                    }

                    if (tempPort > 0)
                    {
                        // 从同一个本地 Socket 向 P 端临时随机端口发包，检验端口映射一致性 (限制锥型 vs 对称型)
                        var altEp = new IPEndPoint(primaryPEp.Address, tempPort);
                        var altProbeTcs = new TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Guid probeId = Guid.NewGuid();
                        pendingTests[probeId] = altProbeTcs;

                        byte[] probeReq = new byte[18];
                        probeReq[0] = (byte)MsgType.NatTestReq;
                        probeId.TryWriteBytes(probeReq.AsSpan(1, 16));
                        probeReq[17] = NatTestFlags.None;

                        for (int r = 0; r < 3; r++)
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                await udp.SendAsync(probeReq, altEp, ct);
                            }
                            catch { }

                            using var probeTimeout = new CancellationTokenSource(1000);
                            using var linkedProbe = CancellationTokenSource.CreateLinkedTokenSource(ct, probeTimeout.Token);
                            try
                            {
                                var (altData, _) = await altProbeTcs.Task.WaitAsync(linkedProbe.Token);
                                if (altData.Length >= 17)
                                {
                                    var (altPubEp, _) = ProtocolHelper.ReadIPEndPoint(altData.AsSpan(17));
                                    if (mappedIpv4Ep.Port == altPubEp.Port)
                                    {
                                        return (true, I18n.Text(
                                            $"圆锥路由 - 限制锥型 (Restricted Cone NAT)，映射行为与目标无关 (EIM，端口映射保持一致: {mappedIpv4Ep.Port} == {altPubEp.Port})，支持 UDP P2P 打洞直连",
                                            $"Cone NAT - Restricted Cone (NAT 2/3): Endpoint-Independent Mapping (EIM, port mapping consistent: {mappedIpv4Ep.Port} == {altPubEp.Port}), supports UDP P2P hole punching."));
                                    }
                                    else
                                    {
                                        return (false, I18n.Text(
                                            $"对称路由 (Symmetric NAT / NAT4)，映射端口随目标变化 ({mappedIpv4Ep.Port} != {altPubEp.Port})，常规打洞无法直连，需通过 P 端流量中继",
                                            $"Symmetric NAT (NAT 4): Endpoint-Dependent Mapping (EDM, mapped port varies by destination: {mappedIpv4Ep.Port} != {altPubEp.Port}), direct P2P hole punching unsupported, relay via Proxy required."));
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested)
                            {
                                continue;
                            }
                        }
                    }
                }
            }
            finally
            {
                pendingTestHandlers.TryRemove(testId, out _);
            }

            return (null, I18n.Text(
                "未能从 P 端临时端口获取探测响应，NAT 路由类型探测受限",
                "Failed to obtain response from Proxy ephemeral port; NAT diagnostic inconclusive."));
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
