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
            var (target, _, _) = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "p.example.com:7000" });
            Assert.Equal("p.example.com:7000", target);
        }

        [Fact]
        public void ResolveTargetServer_QuickSetupSyntax_ParsedCorrectly()
        {
            var (target1, _, _) = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "33890/tcp=rdp_home:pass@myproxy.org" });
            Assert.Equal("myproxy.org", target1);

            var (target2, _, _) = NatDiagnosticHelper.ResolveTargetServer(new[] { "rdp_home=127.0.0.1:3389/tcp@server.com:9400", "-test" });
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

            var (target, _, _) = NatDiagnosticHelper.ResolveTargetServer(new[] { "-test", "-c", iniPath });
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
            Assert.Contains("正在解析目标服务器地址... 成功", sw.ToString());
            Assert.Contains("正在测试本机与 P 端的 IPv4 直连状态... 成功", sw.ToString());
            Assert.Contains("正在探测本机 NAT 路由类型... NAT 0", sw.ToString());
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

            // ==========================================
            // 测试 1：阶段 1 探测 (NAT 1/2 无邀约入站与单次应答)
            // ==========================================
            // 客户端向 P 发送测试请求
            await clientUdp.SendAsync(req, pEp, cts.Token);

            // P 作为主动方，从临时端口向客户端发送 Stage1Probe
            byte[] buf = new byte[1024];
            int altPort = 0;
            var (len, remoteEp) = await clientUdp.ReceiveAsync(buf, cts.Token);
            Assert.True(len >= 18);
            Assert.Equal((byte)MsgType.NatTestResp, buf[0]);
            Assert.Equal(NatTestFlags.Stage1Probe, buf[17]);
            var (pubEp, epLen) = ProtocolHelper.ReadIPEndPoint(buf.AsSpan(18));
            if (len >= 18 + epLen + 4)
            {
                altPort = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(18 + epLen, 4));
            }

            Assert.True(altPort > 0);
            Assert.NotEqual(proxyPort, altPort);
            Assert.Equal(altPort, ((IPEndPoint)remoteEp).Port);

            // 客户端作为回复方：单次回送 Stage1Ack
            byte[] ack = new byte[18];
            ack[0] = (byte)MsgType.NatTestResp;
            testId.TryWriteBytes(ack.AsSpan(1, 16));
            ack[17] = NatTestFlags.Stage1Ack;
            await clientUdp.SendAsync(ack, remoteEp, cts.Token);

            // ==========================================
            // 测试 2：阶段 2 探测 (阶段 1 超时后转由客户端主动发包，P 端临时端口回复)
            // ==========================================
            Guid testId2 = Guid.NewGuid();
            byte[] req2 = new byte[18];
            req2[0] = (byte)MsgType.NatTestReq;
            testId2.TryWriteBytes(req2.AsSpan(1, 16));
            req2[17] = NatTestFlags.None;

            await clientUdp.SendAsync(req2, pEp, cts.Token);

            // 客户端忽略阶段 1 的 Stage1Probe (不回复)，等待 P 端在阶段 1 超时后通过主端口发送 Stage2Notify
            int altPort2 = 0;
            while (!cts.IsCancellationRequested)
            {
                var (rLen, rEp) = await clientUdp.ReceiveAsync(buf, cts.Token);
                if (rLen >= 18 && (MsgType)buf[0] == MsgType.NatTestResp && new Guid(buf.AsSpan(1, 16)) == testId2)
                {
                    if (buf[17] == NatTestFlags.Stage2Notify && ((IPEndPoint)rEp).Port == proxyPort)
                    {
                        var (_, pLen) = ProtocolHelper.ReadIPEndPoint(buf.AsSpan(18));
                        altPort2 = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(18 + pLen, 4));
                        break;
                    }
                }
            }

            Assert.True(altPort2 > 0);

            // 客户端作为主动方，向 P 端临时端口 altPort2 发包探测
            var altPEp2 = new IPEndPoint(IPAddress.Loopback, altPort2);
            await clientUdp.SendAsync(req2, altPEp2, cts.Token);

            // P 端临时端口作为回复方，单次向客户端回复映射地址
            IPEndPoint? respEp2 = null;
            while (!cts.IsCancellationRequested)
            {
                var (rLen, rEp) = await clientUdp.ReceiveAsync(buf, cts.Token);
                if (rLen >= 18 && (MsgType)buf[0] == MsgType.NatTestResp && new Guid(buf.AsSpan(1, 16)) == testId2)
                {
                    if (((IPEndPoint)rEp).Port == altPort2)
                    {
                        respEp2 = (IPEndPoint)rEp;
                        break;
                    }
                }
            }

            Assert.NotNull(respEp2);
            Assert.Equal(altPort2, respEp2.Port);

            cts.Cancel();
            try { await pTask; } catch { }
        }
    }
}
