using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

class Program {
    static void Main() {
        string path = @"..\UDRoute.Shared\NatDiagnosticHelper.cs";
        string content = File.ReadAllText(path, Encoding.UTF8);

        // 1. ResolveTargetServer signature and logic
        string oldResolveTargetServer = @"        public static string ResolveTargetServer(string[] args)
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
                if (args[i].Equals(""-c"", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
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
                    if (arg.EndsWith("".ini"", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith(""-""))
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
                if (a.StartsWith(""-"") || a.Contains('=')) continue;
                if (i > 0 && args[i - 1].Equals(""-c"", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.EndsWith("".ini"", StringComparison.OrdinalIgnoreCase)) continue;
                return a.Trim();
            }

            // 5. 若指定了 ini 或默认当前目录下存在 udroute.ini，尝试解析获取目标 P 端
            string iniToParse = iniPath ?? ""udroute.ini"";
            string? resolvedIni = ConfigParser.ResolveIniPath(iniToParse);
            if (resolvedIni != null && File.Exists(resolvedIni))
            {
                var cfg = ConfigParser.Parse(new[] { ""-c"", resolvedIni, ""-log"", ""none"" });
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
                ""未指定目标 P 端服务器地址，请通过参数 (如 udroute -test p.example.com:9400) 或配置文件指定。"",
                ""Target Proxy server address not specified. Please specify via arguments (e.g. udroute -test p.example.com:9400) or config file.""));
        }";

        string newResolveTargetServer = @"        public static (string Target, int LocalPort, int WanPort) ResolveTargetServer(string[] args)
        {
            string target = """";
            int localPort = 0;
            int wanPort = 0;
            string? iniPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(""-c"", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    iniPath = args[i + 1];
                else if (args[i].Equals(""-port"", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out localPort);
                else if ((args[i].Equals(""-wanport"", StringComparison.OrdinalIgnoreCase) || args[i].Equals(""-alterport"", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out wanPort);
            }

            foreach (var arg in args)
            {
                int atIdx = arg.IndexOf('@');
                if (atIdx >= 0 && atIdx < arg.Length - 1)
                    return (arg.Substring(atIdx + 1).Trim(), localPort, wanPort);
            }

            if (iniPath == null)
            {
                foreach (var arg in args)
                {
                    if (arg.EndsWith("".ini"", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith(""-""))
                    {
                        iniPath = arg;
                        break;
                    }
                }
            }

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith(""-"") || a.Contains('=')) continue;
                if (i > 0 && args[i - 1].Equals(""-c"", StringComparison.OrdinalIgnoreCase)) continue;
                if (i > 0 && (args[i - 1].Equals(""-port"", StringComparison.OrdinalIgnoreCase) || args[i - 1].Equals(""-wanport"", StringComparison.OrdinalIgnoreCase) || args[i - 1].Equals(""-alterport"", StringComparison.OrdinalIgnoreCase))) continue;
                if (a.EndsWith("".ini"", StringComparison.OrdinalIgnoreCase)) continue;
                target = a.Trim();
                break;
            }

            string iniToParse = iniPath ?? ""udroute.ini"";
            string? resolvedIni = ConfigParser.ResolveIniPath(iniToParse);
            if (resolvedIni != null && File.Exists(resolvedIni))
            {
                var cfg = ConfigParser.Parse(new[] { ""-c"", resolvedIni, ""-log"", ""none"" });
                if (cfg != null)
                {
                    int cfgLocal = localPort > 0 ? localPort : cfg.Port;
                    int cfgWan = wanPort > 0 ? wanPort : cfg.WanPort;
                    
                    if (cfg.ServerRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ServerRecords[0].TargetServer))
                        return (cfg.ServerRecords[0].TargetServer, cfgLocal, cfgWan);
                    if (cfg.ClientRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ClientRecords[0].TargetServer))
                        return (cfg.ClientRecords[0].TargetServer, cfgLocal, cfgWan);
                }
            }

            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException(I18n.Text(""未指定目标 P 端服务器地址。"", ""Target Proxy server address not specified.""));

            return (target, localPort, wanPort);
        }";
        content = content.Replace(oldResolveTargetServer, newResolveTargetServer);


        string oldRun1 = @"        public static async Task<NatDiagnosticResult> RunAsync(string[] args, TextWriter? writer = null)
        {
            writer ??= Console.Out;
            string targetServer = ResolveTargetServer(args);
            if (targetServer.Equals(""this"", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(targetServer))
            {
                targetServer = ""127.0.0.1:9400"";
            }

            writer.WriteLine(""=================================================="");
            writer.WriteLine(I18n.Text(""UDRoute 网络连通性与 NAT 路由诊断测试"", ""UDRoute Network Connectivity and NAT Diagnostic Test""));
            writer.WriteLine(I18n.Text($""目标 P 端: {targetServer}"", $""Target Proxy: {targetServer}""));
            writer.WriteLine(""=================================================="");
            PrintStepStart(writer, I18n.Text(""正在解析目标服务器地址... "", ""Resolving target server address... ""));";
            
        string newRun1 = @"        public static async Task<NatDiagnosticResult> RunAsync(string[] args, TextWriter? writer = null)
        {
            writer ??= Console.Out;
            var (targetServer, localPort, wanPort) = ResolveTargetServer(args);
            if (targetServer.Equals(""this"", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(targetServer))
            {
                targetServer = ""127.0.0.1:9400"";
            }

            writer.WriteLine(""=================================================="");
            writer.WriteLine(I18n.Text(""UDRoute 网络连通性与 NAT 路由诊断测试"", ""UDRoute Network Connectivity and NAT Diagnostic Test""));
            writer.WriteLine(I18n.Text($""目标 P 端: {targetServer}"", $""Target Proxy: {targetServer}""));
            if (localPort > 0 || wanPort > 0)
            {
                writer.WriteLine(I18n.Text($""本地绑定端口: {localPort}, 映射(WAN/Alter)端口: {wanPort}"", $""Local Port: {localPort}, WAN/Alter Port: {wanPort}""));
            }
            writer.WriteLine(""=================================================="");
            PrintStepStart(writer, I18n.Text(""正在解析目标服务器地址... "", ""Resolving target server address... ""));";
        content = content.Replace(oldRun1, newRun1);

        content = content.Replace("using var udp = new ZeroCopyUdpSocket(0);", "using var udp = new ZeroCopyUdpSocket(localPort);");

        string oldRunV4 = @"            try
            {
                // ==========================================
                // 1. 测试本机与 P 是否是 IPV4 直连";
        string newRunV4 = @"            try
            {
                // ==========================================
                // 0. 从 P 端动态获取其监听的所有公网 IP
                // ==========================================
                PrintStepStart(writer, I18n.Text(""正在从 P 端获取双栈 IP 列表... "", ""Fetching dual-stack IPs from Proxy... ""));
                var (pIps, expectedInstanceId) = await ProbeServerIpsAsync(udp, allEps, pendingTests, cts.Token);
                if (pIps != null && pIps.Length > 0)
                {
                    ipv4Eps = pIps.Where(ep => ep.AddressFamily == AddressFamily.InterNetwork).ToArray();
                    ipv6Eps = pIps.Where(ep => ep.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
                    PrintStepResult(writer, I18n.Text(""成功"", ""OK""), ConsoleColor.Green);
                }
                else
                {
                    PrintStepResult(writer, I18n.Text(""失败 (回退至 DNS 解析结果)"", ""Failed (Falling back to DNS)""), ConsoleColor.DarkYellow);
                }

                // ==========================================
                // 1. 测试本机与 P 是否是 IPV4 直连";
        content = content.Replace(oldRunV4, newRunV4);

        content = content.Replace("var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token);", "var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token, expectedInstanceId);");
        content = content.Replace("var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token);", "var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token, expectedInstanceId);");
        content = content.Replace("var (isCone, natLevel, coneDetail) = await ProbeConeNatAsync(udp, primaryPIpv4Ep, mappedIpv4Ep, pendingTests, pendingTestHandlers, cts.Token);", "var (isCone, natLevel, coneDetail) = await ProbeConeNatAsync(udp, primaryPIpv4Ep, mappedIpv4Ep, pendingTests, pendingTestHandlers, cts.Token, wanPort);");


        string oldProbeServerIpsTarget = @"        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(";
        string probeServerIpsCode = @"        private static async Task<(IPEndPoint[] IPs, Guid InstanceId)> ProbeServerIpsAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint[] pEps,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1500)
        {
            var ips = new List<IPEndPoint>();
            foreach (var ep in pEps)
            {
                for (int retry = 0; retry < 2; retry++)
                {
                    if (ct.IsCancellationRequested) return (Array.Empty<IPEndPoint>(), Guid.Empty);

                    var testId = Guid.NewGuid();
                    var tcs = new TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingTests[testId] = tcs;

                    byte[] req = new byte[17];
                    req[0] = (byte)MsgType.ServerIpsReq;
                    testId.TryWriteBytes(req.AsSpan(1, 16));

                    try
                    {
                        await udp.SendAsync(req, ep, ct);
                        using var timeout = new CancellationTokenSource(timeoutMs);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                        
                        var (data, _) = await tcs.Task.WaitAsync(linked.Token);
                        if (data.Length >= 18 && (MsgType)data[0] == MsgType.ServerIpsResp)
                        {
                            byte count = data[17];
                            int offset = 18;
                            for (int i = 0; i < count; i++)
                            {
                                if (offset >= data.Length) break;
                                var (parsedEp, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(offset));
                                ips.Add(parsedEp);
                                offset += readLen;
                            }
                            Guid instanceId = Guid.Empty;
                            if (offset + 16 <= data.Length)
                            {
                                instanceId = new Guid(data.AsSpan(offset, 16));
                            }
                            if (ips.Count > 0) return (ips.ToArray(), instanceId);
                        }
                    }
                    catch { }
                    finally { pendingTests.TryRemove(testId, out _); }
                }
            }
            return (Array.Empty<IPEndPoint>(), Guid.Empty);
        }

        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(";
        content = content.Replace(oldProbeServerIpsTarget, probeServerIpsCode);

        string oldProbeV4Sig = @"        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1000)";
        string newProbeV4Sig = @"        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            Guid expectedInstanceId,
            int timeoutMs = 1000)";
        content = content.Replace(oldProbeV4Sig, newProbeV4Sig);

        string oldProbeV6Sig = @"        private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp6,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1200)";
        string newProbeV6Sig = @"        private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp6,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            Guid expectedInstanceId,
            int timeoutMs = 1200)";
        content = content.Replace(oldProbeV6Sig, newProbeV6Sig);

        string oldProbeV4Ret = @"                    if (data.Length >= 17)
                    {
                        var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, rtt);
                    }";
        string newProbeV4Ret = @"                    if (data.Length >= 17)
                    {
                        var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        if (expectedInstanceId != Guid.Empty && data.Length >= 17 + readLen + 16)
                        {
                            Guid returnedInstanceId = new Guid(data.AsSpan(17 + readLen, 16));
                            if (returnedInstanceId != expectedInstanceId) continue;
                        }
                        return (ep, rtt);
                    }";
        content = content.Replace(oldProbeV4Ret, newProbeV4Ret);

        string oldProbeV6Ret = @"                    if (data.Length >= 17)
                    {
                        var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, null, rtt);
                    }";
        string newProbeV6Ret = @"                    if (data.Length >= 17)
                    {
                        var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        if (expectedInstanceId != Guid.Empty && data.Length >= 17 + readLen + 16)
                        {
                            Guid returnedInstanceId = new Guid(data.AsSpan(17 + readLen, 16));
                            if (returnedInstanceId != expectedInstanceId) continue;
                        }
                        return (ep, null, rtt);
                    }";
        content = content.Replace(oldProbeV6Ret, newProbeV6Ret);

        string oldConeSig = @"        private static async Task<(bool? IsCone, string NatLevel, string Detail)> ProbeConeNatAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint primaryPEp,
            IPEndPoint mappedIpv4Ep,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            ConcurrentDictionary<Guid, Action<(byte[] Data, EndPoint RemoteEp)>> pendingTestHandlers,
            CancellationToken ct)";
        string newConeSig = @"        private static async Task<(bool? IsCone, string NatLevel, string Detail)> ProbeConeNatAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint primaryPEp,
            IPEndPoint mappedIpv4Ep,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            ConcurrentDictionary<Guid, Action<(byte[] Data, EndPoint RemoteEp)>> pendingTestHandlers,
            CancellationToken ct,
            int wanPort = 0)";
        content = content.Replace(oldConeSig, newConeSig);

        string oldConeDmz = @"                if (receivedFromAlt)
                {
                    // 阶段 1 成功：收到无邀约入站包并回包确认，确诊为 NAT 1/2
                    return (true, ""NAT 1/2"", I18n.Text(
                        ""圆锥路由 - NAT 1/2 (全锥 / IP受限锥)，无邀约入站连通正常，具备最高穿透力，可与任意对端 (含 NAT 4) 建立 P2P 直连"",
                        ""Cone NAT - NAT 1/2 (Full Cone / IP-Restricted Cone): Unsolicited inbound accessible, optimal traversal capability, can establish P2P direct connection with any peer (including NAT 4).""));
                }";
        string newConeDmz = @"                if (receivedFromAlt)
                {
                    bool isPortPreserved = (wanPort > 0) ? (mappedIpv4Ep.Port == wanPort) : ((udp.LocalEndPoint is IPEndPoint lep) && lep.Port == mappedIpv4Ep.Port);
                    if (isPortPreserved)
                    {
                        return (true, ""DMZ / 1:1 NAT"", I18n.Text(
                            ""1:1 NAT 或 DMZ 或已成功映射端口 (完全支持双向直连)"",
                            ""1:1 NAT, DMZ, or Successfully Port-Mapped (Fully supports bidirectional direct connection).""));
                    }
                    else
                    {
                        return (true, ""NAT 1/2"", I18n.Text(
                            ""圆锥路由 - NAT 1/2 (全锥 / IP受限锥，具备最高穿透力)"",
                            ""Cone NAT - NAT 1/2 (Full Cone / IP-Restricted Cone): Unsolicited inbound accessible, optimal traversal capability.""));
                    }
                }";
        content = content.Replace(oldConeDmz, newConeDmz);

        File.WriteAllText(path, content, Encoding.UTF8);
        Console.WriteLine(""Done writing file."");
    }
}
