import sys

with open('UDRoute.Shared/NatDiagnosticHelper.cs', 'r', encoding='utf-8') as f:
    content = f.read()

target1 = '''        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async('''
replacement1 = '''        private static async Task<(IPEndPoint[] IPs, Guid InstanceId)> ProbeServerIpsAsync(
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

        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async('''
content = content.replace(target1, replacement1)

target2 = '''            try
            {
                // ==========================================
                // 1. 测试本机与 P 是否是 IPV4 直连'''
replacement2 = '''            try
            {
                // ==========================================
                // 0. 从 P 端动态获取其监听的所有公网 IP
                // ==========================================
                PrintStepStart(writer, I18n.Text("正在从 P 端获取双栈 IP 列表... ", "Fetching dual-stack IPs from Proxy... "));
                var (pIps, expectedInstanceId) = await ProbeServerIpsAsync(udp, allEps, pendingTests, cts.Token);
                if (pIps != null && pIps.Length > 0)
                {
                    ipv4Eps = pIps.Where(ep => ep.AddressFamily == AddressFamily.InterNetwork).ToArray();
                    ipv6Eps = pIps.Where(ep => ep.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
                    PrintStepResult(writer, I18n.Text("成功", "OK"), ConsoleColor.Green);
                }
                else
                {
                    PrintStepResult(writer, I18n.Text("失败 (回退至 DNS 解析结果)", "Failed (Falling back to DNS)"), ConsoleColor.DarkYellow);
                }

                // ==========================================
                // 1. 测试本机与 P 是否是 IPV4 直连'''
content = content.replace(target2, replacement2)

target3 = '''var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token);'''
replacement3 = '''var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token, expectedInstanceId);'''
content = content.replace(target3, replacement3)

target4 = '''var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token);'''
replacement4 = '''var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token, expectedInstanceId);'''
content = content.replace(target4, replacement4)

target5 = '''private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1000)'''
replacement5 = '''private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            Guid expectedInstanceId,
            int timeoutMs = 1000)'''
content = content.replace(target5, replacement5)

target6 = '''var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, rtt);'''
replacement6 = '''var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        if (expectedInstanceId != Guid.Empty && data.Length >= 17 + readLen + 16)
                        {
                            Guid returnedInstanceId = new Guid(data.AsSpan(17 + readLen, 16));
                            if (returnedInstanceId != expectedInstanceId) continue;
                        }
                        return (ep, rtt);'''
content = content.replace(target6, replacement6)


target7 = '''private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp6,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1200)'''
replacement7 = '''private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(
            ZeroCopyUdpSocket udp,
            IPEndPoint pEp6,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            Guid expectedInstanceId,
            int timeoutMs = 1200)'''
content = content.replace(target7, replacement7)

target8 = '''var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        return (ep, null, rtt);'''
replacement8 = '''var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));
                        if (expectedInstanceId != Guid.Empty && data.Length >= 17 + readLen + 16)
                        {
                            Guid returnedInstanceId = new Guid(data.AsSpan(17 + readLen, 16));
                            if (returnedInstanceId != expectedInstanceId) continue;
                        }
                        return (ep, null, rtt);'''
content = content.replace(target8, replacement8)

idx = content.find("if (receivedFromAlt)")
idx2 = content.find("}", content.find("}", idx) + 1)
new_dmz = '''if (receivedFromAlt)
                {
                    bool isPortPreserved = (udp.LocalEndPoint is IPEndPoint lep) && lep.Port == mappedIpv4Ep.Port;
                    if (isPortPreserved)
                    {
                        return (true, "DMZ / 1:1 NAT", I18n.Text(
                            "1:1 NAT 或 DMZ 主机 (无端口映射，等同于公网直连能力，完全支持双向直连)",
                            "1:1 NAT or DMZ Host (No port translation, equivalent to direct connection, fully supports bidirectional direct connection)."));
                    }
                    else
                    {
                        return (true, "NAT 1/2", I18n.Text(
                            "圆锥路由 - NAT 1/2 (全锥 / IP受限锥，具备最高穿透力)",
                            "Cone NAT - NAT 1/2 (Full Cone / IP-Restricted Cone): Unsolicited inbound accessible, optimal traversal capability."));
                    }
                }'''
content = content[:idx] + new_dmz + content[idx2 + 1:]

with open('UDRoute.Shared/NatDiagnosticHelper.cs', 'w', encoding='utf-8') as f:
    f.write(content)
