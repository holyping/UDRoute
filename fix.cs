using System.IO;
using System.Text;
class Program {
    static void Main() {
        string path = @"UDRoute.Shared\NatDiagnosticHelper.cs";
        string content = File.ReadAllText(path, Encoding.UTF8);
        
        string target1 = "        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(";
        string replacement1 = @"        private static async Task<(IPEndPoint[] IPs, Guid InstanceId)> ProbeServerIpsAsync(
            ZeroCopyUdpSocket udp,
            IPEndPoint[] pEps,
            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,
            CancellationToken ct,
            int timeoutMs = 1500)
        {
            var ips = new System.Collections.Generic.List<IPEndPoint>();
            foreach (var ep in pEps)
            {
                for (int retry = 0; retry < 2; retry++)
                {
                    if (ct.IsCancellationRequested) return (System.Array.Empty<IPEndPoint>(), System.Guid.Empty);

                    var testId = System.Guid.NewGuid();
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
                            System.Guid instanceId = System.Guid.Empty;
                            if (offset + 16 <= data.Length)
                            {
                                instanceId = new System.Guid(data.AsSpan(offset, 16));
                            }
                            if (ips.Count > 0) return (ips.ToArray(), instanceId);
                        }
                    }
                    catch { }
                    finally { pendingTests.TryRemove(testId, out _); }
                }
            }
            return (System.Array.Empty<IPEndPoint>(), System.Guid.Empty);
        }

        private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(";
        content = content.Replace(target1, replacement1);

        string target2 = "var pIps = await ProbeServerIpsAsync(udp, allEps, pendingTests, cts.Token);";
        string replacement2 = "var (pIps, expectedInstanceId) = await ProbeServerIpsAsync(udp, allEps, pendingTests, cts.Token);";
        content = content.Replace(target2, replacement2);

        string target3 = "var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token);";
        string replacement3 = "var probeRes = await ProbePIpv4Async(udp, primaryPIpv4Ep, pendingTests, cts.Token, expectedInstanceId);";
        content = content.Replace(target3, replacement3);

        string target4 = "var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token);";
        string replacement4 = "var p6Res = await ProbePIpv6Async(udp, primaryPIpv6Ep, pendingTests, cts.Token, expectedInstanceId);";
        content = content.Replace(target4, replacement4);

        string target5 = "private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint pEp,\r\n            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,\r\n            CancellationToken ct,\r\n            int timeoutMs = 1000)";
        string replacement5 = "private static async Task<(IPEndPoint? PublicEp, long RttMs)> ProbePIpv4Async(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint pEp,\r\n            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,\r\n            CancellationToken ct,\r\n            System.Guid expectedInstanceId,\r\n            int timeoutMs = 1000)";
        content = content.Replace(target5, replacement5);

        string target6 = "var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));\r\n                        return (ep, rtt);";
        string replacement6 = "var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));\r\n                        if (expectedInstanceId != System.Guid.Empty && data.Length >= 17 + readLen + 16)\r\n                        {\r\n                            System.Guid returnedInstanceId = new System.Guid(data.AsSpan(17 + readLen, 16));\r\n                            if (returnedInstanceId != expectedInstanceId) continue;\r\n                        }\r\n                        return (ep, rtt);";
        content = content.Replace(target6, replacement6);

        string target7 = "private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint pEp6,\r\n            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,\r\n            CancellationToken ct,\r\n            int timeoutMs = 1200)";
        string replacement7 = "private static async Task<(IPEndPoint? PublicEp, int? AltPort, long RttMs)> ProbePIpv6Async(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint pEp6,\r\n            ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, EndPoint RemoteEp)>> pendingTests,\r\n            CancellationToken ct,\r\n            System.Guid expectedInstanceId,\r\n            int timeoutMs = 1200)";
        content = content.Replace(target7, replacement7);

        string target8 = "var (ep, _) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));\r\n                        return (ep, null, rtt);";
        string replacement8 = "var (ep, readLen) = ProtocolHelper.ReadIPEndPoint(data.AsSpan(17));\r\n                        if (expectedInstanceId != System.Guid.Empty && data.Length >= 17 + readLen + 16)\r\n                        {\r\n                            System.Guid returnedInstanceId = new System.Guid(data.AsSpan(17 + readLen, 16));\r\n                            if (returnedInstanceId != expectedInstanceId) continue;\r\n                        }\r\n                        return (ep, null, rtt);";
        content = content.Replace(target8, replacement8);

        int dmzIndex = content.IndexOf("if (receivedFromAlt)");
        int dmzEnd = content.IndexOf(\"}\", content.IndexOf(\"}\", dmzIndex) + 1);
        string newDmz = "if (receivedFromAlt)\r\n                {\r\n                    bool isPortPreserved = (udp.LocalEndPoint is IPEndPoint lep) && lep.Port == mappedIpv4Ep.Port;\r\n                    if (isPortPreserved)\r\n                    {\r\n                        return (true, \"DMZ / 1:1 NAT\", I18n.Text(\"1:1 NAT 或 DMZ 主机 (无端口映射，完全支持双向直连)\", \"1:1 NAT or DMZ Host (No port translation, fully supports bidirectional direct connection).\"));\r\n                    }\r\n                    else\r\n                    {\r\n                        return (true, \"NAT 1/2\", I18n.Text(\"圆锥路由 - NAT 1/2 (全锥 / IP受限锥)\", \"Cone NAT - NAT 1/2 (Full Cone / IP-Restricted Cone)\"));\r\n                    }\r\n                }";
        content = content.Substring(0, dmzIndex) + newDmz + content.Substring(dmzEnd + 1);

        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
