using System;
using System.IO;
using System.Text;

class Program {
    static void Main() {
        string path = @"..\UDRoute.Shared\NatDiagnosticHelper.cs";
        string content = File.ReadAllText(path, Encoding.UTF8);

        // 1. Modify ResolveTargetServer to return (string, int, int)
        content = content.Replace("public static string ResolveTargetServer(string[] args)", "public static (string Target, int LocalPort, int WanPort) ResolveTargetServer(string[] args)");

        string rtsBody1 = "return arg.Substring(atIdx + 1).Trim();";
        content = content.Replace(rtsBody1, "return (arg.Substring(atIdx + 1).Trim(), 0, 0);");

        string rtsBody2 = "return a.Trim();\r\n            }";
        content = content.Replace(rtsBody2, "return (a.Trim(), 0, 0);\r\n            }");

        string rtsBody3 = "if (cfg != null)\r\n                {\r\n                    if (cfg.ServerRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ServerRecords[0].TargetServer))\r\n                    {\r\n                        return cfg.ServerRecords[0].TargetServer;\r\n                    }\r\n                    if (cfg.ClientRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ClientRecords[0].TargetServer))\r\n                    {\r\n                        return cfg.ClientRecords[0].TargetServer;\r\n                    }\r\n                }";
        string rtsNew3 = "if (cfg != null)\r\n                {\r\n                    if (cfg.ServerRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ServerRecords[0].TargetServer))\r\n                    {\r\n                        return (cfg.ServerRecords[0].TargetServer, cfg.Port, cfg.WanPort);\r\n                    }\r\n                    if (cfg.ClientRecords.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ClientRecords[0].TargetServer))\r\n                    {\r\n                        return (cfg.ClientRecords[0].TargetServer, cfg.Port, cfg.WanPort);\r\n                    }\r\n                }";
        content = content.Replace(rtsBody3, rtsNew3);

        // 2. Parse from args specifically for -port and -wanport
        string rtsBody4 = "string iniPath = null;\r\n            for (int i = 0; i < args.Length; i++)\r\n            {\r\n                if (args[i].Equals(\"-c\", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)\r\n                {\r\n                    iniPath = args[i + 1];\r\n                    break;\r\n                }\r\n            }";
        string rtsNew4 = "string iniPath = null;\r\n            int cmdPort = 0;\r\n            int cmdWanPort = 0;\r\n            for (int i = 0; i < args.Length; i++)\r\n            {\r\n                if (args[i].Equals(\"-c\", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)\r\n                {\r\n                    iniPath = args[i + 1];\r\n                }\r\n                else if (args[i].Equals(\"-port\", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)\r\n                {\r\n                    int.TryParse(args[i + 1], out cmdPort);\r\n                }\r\n                else if (args[i].Equals(\"-wanport\", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)\r\n                {\r\n                    int.TryParse(args[i + 1], out cmdWanPort);\r\n                }\r\n            }";
        content = content.Replace(rtsBody4, rtsNew4);

        // Modify the return inside ResolveTargetServer to override with cmdPort and cmdWanPort
        content = content.Replace("return (a.Trim(), 0, 0);", "return (a.Trim(), cmdPort, cmdWanPort);");
        content = content.Replace("return (arg.Substring(atIdx + 1).Trim(), 0, 0);", "return (arg.Substring(atIdx + 1).Trim(), cmdPort, cmdWanPort);");
        content = content.Replace("return (cfg.ServerRecords[0].TargetServer, cfg.Port, cfg.WanPort);", "return (cfg.ServerRecords[0].TargetServer, cmdPort > 0 ? cmdPort : cfg.Port, cmdWanPort > 0 ? cmdWanPort : cfg.WanPort);");
        content = content.Replace("return (cfg.ClientRecords[0].TargetServer, cfg.Port, cfg.WanPort);", "return (cfg.ClientRecords[0].TargetServer, cmdPort > 0 ? cmdPort : cfg.Port, cmdWanPort > 0 ? cmdWanPort : cfg.WanPort);");

        // Now modify RunAsync
        string runAsyncBody = "string targetServer = ResolveTargetServer(args);";
        string runAsyncNew = "var (targetServer, localPort, wanPort) = ResolveTargetServer(args);";
        content = content.Replace(runAsyncBody, runAsyncNew);

        string runAsyncBody2 = "using var udp = new ZeroCopyUdpSocket(0);";
        string runAsyncNew2 = "using var udp = new ZeroCopyUdpSocket(localPort);";
        content = content.Replace(runAsyncBody2, runAsyncNew2);

        // Find ProbeConeNatAsync call
        string pcnCall = "var (isCone, natLevel, coneDetail) = await ProbeConeNatAsync(udp, primaryPIpv4Ep, mappedIpv4Ep, pendingTests, pendingTestHandlers, cts.Token);";
        string pcnCallNew = "var (isCone, natLevel, coneDetail) = await ProbeConeNatAsync(udp, primaryPIpv4Ep, mappedIpv4Ep, pendingTests, pendingTestHandlers, cts.Token, wanPort);";
        content = content.Replace(pcnCall, pcnCallNew);

        // Find ProbeConeNatAsync signature
        string pcnSig = "private static async Task<(bool IsCone, string NatLevel, string ConeDetail)> ProbeConeNatAsync(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint primaryPEp,\r\n            IPEndPoint mappedIpv4Ep,\r\n            System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, System.Net.EndPoint RemoteEp)>> pendingTests,\r\n            System.Collections.Concurrent.ConcurrentDictionary<Guid, Action<(byte[] Data, System.Net.EndPoint RemoteEp)>> pendingTestHandlers,\r\n            CancellationToken ct)";
        string pcnSigNew = "private static async Task<(bool IsCone, string NatLevel, string ConeDetail)> ProbeConeNatAsync(\r\n            ZeroCopyUdpSocket udp,\r\n            IPEndPoint primaryPEp,\r\n            IPEndPoint mappedIpv4Ep,\r\n            System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<(byte[] Data, System.Net.EndPoint RemoteEp)>> pendingTests,\r\n            System.Collections.Concurrent.ConcurrentDictionary<Guid, Action<(byte[] Data, System.Net.EndPoint RemoteEp)>> pendingTestHandlers,\r\n            CancellationToken ct,\r\n            int wanPort = 0)";
        content = content.Replace(pcnSig, pcnSigNew);

        // DMZ check modification
        string dmzCheck = "bool isPortPreserved = (udp.LocalEndPoint is IPEndPoint lep) && lep.Port == mappedIpv4Ep.Port;";
        string dmzCheckNew = "bool isPortPreserved = (wanPort > 0) ? (mappedIpv4Ep.Port == wanPort) : ((udp.LocalEndPoint is IPEndPoint lep) && lep.Port == mappedIpv4Ep.Port);";
        content = content.Replace(dmzCheck, dmzCheckNew);

        // Add info log at start
        string printTarget = "writer.WriteLine(I18n.Text($\"目标 P 端: {targetServer}\", $\"Target Proxy: {targetServer}\"));";
        string printTargetNew = "writer.WriteLine(I18n.Text($\"目标 P 端: {targetServer}\", $\"Target Proxy: {targetServer}\"));\r\n            if (localPort > 0 || wanPort > 0) writer.WriteLine(I18n.Text($\"端口绑定: 本地 {localPort}, 映射端口(WAN) {wanPort}\", $\"Port Binding: Local {localPort}, WAN mapped {wanPort}\"));";
        content = content.Replace(printTarget, printTargetNew);

        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
