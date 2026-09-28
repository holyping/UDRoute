using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using Xunit;

namespace UDRoute.Tests
{
    public class NatDiagnosticTests : IDisposable
    {
        private readonly string _testDir;

        public NatDiagnosticTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "udroute_natdiag_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                {
                    Directory.Delete(_testDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void ResolveTargetServer_ExplicitHost_ParsedCorrectly()
        {
            string target = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "p.example.com:7000" });
            Assert.Equal("p.example.com:7000", target);
        }

        [Fact]
        public void ResolveTargetServer_QuickSetupSyntax_ParsedCorrectly()
        {
            string target1 = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "33890/tcp=rdp_home:pass@myproxy.org" });
            Assert.Equal("myproxy.org", target1);

            string target2 = NatDiagnosticHelper.ResolveTargetServer(new[] { "rdp_home=127.0.0.1:3389/tcp@server.com:9400", "-test" });
            Assert.Equal("server.com:9400", target2);
        }

        [Fact]
        public void ResolveTargetServer_IniFile_ParsedCorrectly()
        {
            string iniPath = Path.Combine(_testDir, "custom.ini");
            File.WriteAllText(iniPath, @"
server = p.fromini.com:8888

[service1]
target=127.0.0.1:80/tcp
");

            string target = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "-c", iniPath });
            Assert.Equal("p.fromini.com:8888", target);
        }

        [Fact]
        public void ResolveTargetServer_NoTargetSpecified_ThrowsException()
        {
            Assert.Throws<InvalidOperationException>(() => NatDiagnosticHelper.ResolveTargetServer(new[] { "-test" }));
        }

        [Fact]
        public void IsGlobalUnicastIPv6_ClassificationCorrect()
        {
            // Valid Global Unicast
            Assert.True(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("240e:3b4:5070:ccf0::e2")));
            Assert.True(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("2001:4860:4860::8888")));
            Assert.True(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("2606:4700:4700::1111")));

            // Link-local
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("fe80::1")));
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("fe80::1234:5678:abcd:ef01")));

            // Loopback
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.IPv6Loopback));

            // Unique Local Address (fc00::/7)
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("fc00::1")));
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("fd12:3456:789a::1")));

            // IPv4-mapped
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.Parse("::ffff:192.168.1.1")));

            // Unspecified
            Assert.False(NatDiagnosticHelper.IsGlobalUnicastIPv6(IPAddress.IPv6None));
        }

        [Fact]
        public async Task NatDiagnostic_LocalProxy_DirectAndConeDetected()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // 选取一个可用端口启动本地 Proxy
            using var probeSocket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int proxyPort = ((IPEndPoint)probeSocket.Client.LocalEndPoint!).Port;
            probeSocket.Close();

            var pCfg = new AppConfig
            {
                Port = proxyPort,
                EnableProxy = true,
                ConfigPath = Path.Combine(_testDir, "p.ini")
            };

            using var pEngine = new RouteEngine(pCfg);
            var pTask = pEngine.StartAsync(cts.Token);
            await Task.Delay(200, cts.Token);

            var sw = new StringWriter();
            var res = await NatDiagnosticHelper.RunAsync(new[] { $"127.0.0.1:{proxyPort}" }, sw);

            Assert.True(res.IsIpv4Direct);
            Assert.True(res.IsConeNat);
            Assert.Contains("1. 测试本机与 P 是否是 IPV4 直连", sw.ToString());
            Assert.Contains("2. 测试本机是否具备 IPV6 直连", sw.ToString());
            Assert.Contains("3. 测试本机是否处于圆锥路由下", sw.ToString());

            cts.Cancel();
            try { await pTask; } catch { }
        }

        [Fact]
        public async Task Proxy_DoesNotBindPortPlusOne_OnlySinglePort()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var probeSocket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int proxyPort = ((IPEndPoint)probeSocket.Client.LocalEndPoint!).Port;
            probeSocket.Close();

            var pCfg = new AppConfig
            {
                Port = proxyPort,
                EnableProxy = true,
                ConfigPath = Path.Combine(_testDir, "p_single.ini")
            };

            using var pEngine = new RouteEngine(pCfg);
            var pTask = pEngine.StartAsync(cts.Token);
            await Task.Delay(200, cts.Token);

            // proxyPort + 1 MUST NOT be occupied by P! Another socket can freely bind it!
            using var altSocket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, proxyPort + 1));
            Assert.NotNull(altSocket.Client.LocalEndPoint);
            altSocket.Close();

            cts.Cancel();
            try { await pTask; } catch { }
        }

        [Fact]
        public async Task NatTestReq_EphemeralSocketExchange_Succeeds()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var probeSocket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int proxyPort = ((IPEndPoint)probeSocket.Client.LocalEndPoint!).Port;
            probeSocket.Close();

            var pCfg = new AppConfig
            {
                Port = proxyPort,
                EnableProxy = true,
                ConfigPath = Path.Combine(_testDir, "p_ephem.ini")
            };

            using var pEngine = new RouteEngine(pCfg);
            var pTask = pEngine.StartAsync(cts.Token);
            await Task.Delay(200, cts.Token);

            // Client sends NatTestReq to P
            using var clientUdp = new ZeroCopyUdpSocket(0);
            var pEp = new IPEndPoint(IPAddress.Loopback, proxyPort);

            Guid testId = Guid.NewGuid();
            byte[] req = new byte[18];
            req[0] = (byte)MsgType.NatTestReq;
            testId.TryWriteBytes(req.AsSpan(1, 16));
            req[17] = NatTestFlags.None;

            await clientUdp.SendAsync(req, pEp, cts.Token);

            // Wait for NatTestResp from P
            byte[] buf = new byte[1024];
            int altPort = 0;
            var (len, remoteEp) = await clientUdp.ReceiveAsync(buf, cts.Token);
            Assert.True(len >= 17);
            Assert.Equal((byte)MsgType.NatTestResp, buf[0]);
            var (pubEp, epLen) = ProtocolHelper.ReadIPEndPoint(buf.AsSpan(17));
            if (len >= 17 + epLen + 4)
            {
                altPort = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(17 + epLen, 4));
            }

            Assert.True(altPort > 0);
            Assert.NotEqual(proxyPort, altPort);

            // Client now probes P's ephemeral port
            var altPEp = new IPEndPoint(IPAddress.Loopback, altPort);
            await clientUdp.SendAsync(req, altPEp, cts.Token);

            // Client receives response from P's ephemeral port (draining any duplicate initial packet on loopback)
            IPEndPoint? respEp = null;
            while (!cts.IsCancellationRequested)
            {
                var (rLen, rEp) = await clientUdp.ReceiveAsync(buf, cts.Token);
                if (rEp is IPEndPoint rep && rep.Port == altPort)
                {
                    respEp = rep;
                    break;
                }
            }

            Assert.NotNull(respEp);
            Assert.Equal(altPort, respEp.Port);

            cts.Cancel();
            try { await pTask; } catch { }
        }
    }
}
