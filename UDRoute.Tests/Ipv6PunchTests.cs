using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;

namespace UDRoute.Tests;

public class Ipv6PunchTests
{
    private readonly ITestOutputHelper _out;

    public Ipv6PunchTests(ITestOutputHelper output)
    {
        _out = output;
        Log.SetLogger(new TestLogger(_out));
        Log.Level = LogLevel.Trace;
    }

    [Fact]
    public void QueryAndRelayStart_ClientLocalEps_RoundTripAndTruncationSafe()
    {
        var sessionId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var clientPublicEp = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 51234);
        var clientEps = new List<IPEndPoint>
        {
            new(IPAddress.Parse("2001:db8::1234"), 51234),
            new(IPAddress.Parse("192.168.1.50"), 51234),
            new(IPAddress.Parse("240e:3b4:50df::1"), 51234)
        };

        byte[] queryPkt = ProtocolHelper.BuildQueryPacket(42, sessionId, "rdp/tcp", forceRelay: false, isReuse: true, instanceId, clientEps);
        var (targetName, nLen) = ProtocolHelper.ReadString(queryPkt.AsSpan(19));
        Assert.Equal("rdp/tcp", targetName);
        int qOffset = 19 + nLen;
        Assert.Equal(2, queryPkt[qOffset++]); // IsReuse
        Assert.Equal(instanceId, new Guid(queryPkt.AsSpan(qOffset, 16)));
        qOffset += 16;

        var parsedQueryEps = ProtocolHelper.ReadLocalEndPoints(queryPkt.AsSpan(qOffset), out int qEpRead);
        Assert.Equal(queryPkt.Length - qOffset, qEpRead);
        Assert.Equal(clientEps, parsedQueryEps);

        byte[] relayPkt = ProtocolHelper.BuildRelayStartPacket(7, sessionId, "rdp/tcp", clientPublicEp, allowRelay: true, effectiveForceRelay: false, isReuse: false, clientEps);
        var (rTarget, rnLen) = ProtocolHelper.ReadString(relayPkt.AsSpan(19));
        Assert.Equal("rdp/tcp", rTarget);
        int rOffset = 19 + rnLen;
        var (rClientPub, pubLen) = ProtocolHelper.ReadIPEndPoint(relayPkt.AsSpan(rOffset));
        Assert.Equal(clientPublicEp, rClientPub);
        rOffset += pubLen;
        Assert.Equal(1, relayPkt[rOffset++]); // AllowRelay = true

        var parsedRelayEps = ProtocolHelper.ReadLocalEndPoints(relayPkt.AsSpan(rOffset), out int rEpRead);
        Assert.Equal(relayPkt.Length - rOffset, rEpRead);
        Assert.Equal(clientEps, parsedRelayEps);

        // 截断或空列表安全回退
        var emptyEps = ProtocolHelper.ReadLocalEndPoints(ReadOnlySpan<byte>.Empty, out int emptyRead);
        Assert.Empty(emptyEps);
        Assert.Equal(0, emptyRead);

        byte[] truncated = new byte[] { 2, 6, 1, 2, 3 }; // 声称2个IPv6但长度不足
        var safeEps = ProtocolHelper.ReadLocalEndPoints(truncated, out _);
        Assert.Empty(safeEps);
    }

    [Fact]
    public void BuildPunchCandidates_DeduplicatesAndRespectsDisableIPv6()
    {
        var pubEp = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 40000);
        var v6Ep = new IPEndPoint(IPAddress.Parse("2001:db8::99"), 40000);
        var peerEps = new List<IPEndPoint>
        {
            v6Ep,
            new(IPAddress.Parse("2001:db8::99"), 40000), // duplicate
            new(IPAddress.Parse("::ffff:203.0.113.5"), 40000), // IPv4-mapped duplicate of pubEp
            new(IPAddress.Parse("192.168.10.20"), 40000)
        };

        bool prevDisable = ProtocolHelper.DisableIPv6;
        try
        {
            ProtocolHelper.DisableIPv6 = false;
            var candidatesWithV6 = ProtocolHelper.BuildPunchCandidates(pubEp, 0, peerEps, 50000, "Test");
            Assert.Contains(pubEp, candidatesWithV6);
            Assert.Contains(v6Ep, candidatesWithV6);
            Assert.Equal(3, candidatesWithV6.Count);

            ProtocolHelper.DisableIPv6 = true;
            var candidatesNoV6 = ProtocolHelper.BuildPunchCandidates(pubEp, 0, peerEps, 50000, "Test");
            Assert.DoesNotContain(v6Ep, candidatesNoV6);
            Assert.Equal(2, candidatesNoV6.Count);
        }
        finally
        {
            ProtocolHelper.DisableIPv6 = prevDisable;
        }
    }

    [Fact]
    public async Task Server_PunchesClientIPv6Candidate_EvenWhenQuerySentOverIPv4()
    {
        if (!Socket.OSSupportsIPv6)
        {
            _out.WriteLine("OS does not support IPv6, skipping test.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 1. 启动 P (代理节点)
        using var dummyP = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyP.Client.LocalEndPoint!).Port;
        dummyP.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini"
        };
        using var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        // 2. 启动 S (AllowRelay = false，仅允许直连打洞，且通过 IPv4 向 P 注册)
        var sCfg = new AppConfig
        {
            DevId = Guid.NewGuid(),
            Port = 0,
            ConfigPath = "dummy.ini"
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "v6_punch_svc",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = 80,
            IsTcp = true,
            AllowRelay = false
        });
        using var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 等待 S 注册成功
        for (int i = 0; i < 30 && pEngine.Proxy?.DirectQuery("v6_punch_svc/tcp") == null; i++)
        {
            await Task.Delay(100, cts.Token);
        }
        Assert.NotNull(pEngine.Proxy?.DirectQuery("v6_punch_svc/tcp"));

        // 3. 模拟 C 使用双栈 Socket 通过 IPv4 向 P 发 Query，但在 LocalEps 中携带 C 的 IPv6 地址 [::1]
        using var cSocket = new ZeroCopyUdpSocket(0);
        int cPort = ((IPEndPoint)cSocket.LocalEndPoint).Port;
        var pEpV4 = new IPEndPoint(IPAddress.Loopback, pPort);
        Guid sessionId = Guid.NewGuid();
        Guid cInstanceId = Guid.NewGuid();

        var cLocalEps = new List<IPEndPoint>
        {
            new(IPAddress.IPv6Loopback, cPort)
        };
        byte[] queryPkt = ProtocolHelper.BuildQueryPacket(101, sessionId, "v6_punch_svc/tcp", forceRelay: false, isReuse: false, cInstanceId, cLocalEps);
        await cSocket.SendAsync(queryPkt, pEpV4, cts.Token);

        // 4. 校验 C 通过 IPv6 收到了 S 发出的 PunchReq 包
        bool receivedSQueryResp = false;
        bool receivedSPunchOverIpv6 = false;
        byte[] recvBuf = new byte[1024];

        while (!cts.IsCancellationRequested && !(receivedSQueryResp && receivedSPunchOverIpv6))
        {
            var (len, remoteEp) = await cSocket.ReceiveAsync(recvBuf, cts.Token);
            if (len < 36 || (MsgType)recvBuf[0] != MsgType.Punch) continue;

            Guid pktSession = new Guid(recvBuf.AsSpan(3, 16));
            if (pktSession != sessionId) continue;

            byte status = recvBuf[35];
            if (status == 1)
            {
                receivedSQueryResp = true;
            }
            else if (status == PunchStatus.PunchReq && remoteEp is IPEndPoint ipEp && ipEp.AddressFamily == AddressFamily.InterNetworkV6)
            {
                receivedSPunchOverIpv6 = true;
                _out.WriteLine($"C received PunchReq from S over IPv6: {ipEp}");

                // 回复 PunchAck 给 S 的 IPv6 地址，验证 S 切换到 IPv6 直连
                byte[] ack = new byte[36];
                ack[0] = (byte)MsgType.Punch;
                BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(1, 2), 0);
                sessionId.TryWriteBytes(ack.AsSpan(3, 16));
                cInstanceId.TryWriteBytes(ack.AsSpan(19, 16));
                ack[35] = PunchStatus.PunchAck;
                await cSocket.SendAsync(ack, ipEp, cts.Token);
            }
        }

        Assert.True(receivedSQueryResp);
        Assert.True(receivedSPunchOverIpv6);

        // 5. 等待 S 端会话切换为 IPv6 直连
        TunnelSession? sSession = null;
        for (int i = 0; i < 20; i++)
        {
            sSession = sEngine.Server?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (sSession != null && sSession.IsDirect) break;
            await Task.Delay(50, cts.Token);
        }

        Assert.NotNull(sSession);
        Assert.True(sSession.IsDirect);
        Assert.Equal(AddressFamily.InterNetworkV6, ((IPEndPoint)sSession.ActiveRemoteEp).AddressFamily);
    }

    [Fact]
    public void GetLocalEndPoints_DeduplicatesAndPrioritizesGlobalIPv6()
    {
        var eps = ProtocolHelper.GetLocalEndPoints(45678);
        var seen = new HashSet<IPAddress>();
        bool seenV4 = false;
        foreach (var ep in eps)
        {
            Assert.Equal(45678, ep.Port);
            Assert.False(IPAddress.IsLoopback(ep.Address));
            Assert.True(seen.Add(ep.Address), $"Duplicate IP returned: {ep.Address}");
            if (ep.AddressFamily == AddressFamily.InterNetwork)
            {
                seenV4 = true;
            }
            else if (ProtocolHelper.IsGlobalUnicastIPv6(ep.Address))
            {
                Assert.False(seenV4, "Global Unicast IPv6 should appear before IPv4 candidates.");
            }
        }
    }

    [Fact]
    public async Task EndToEnd_ClientProxyServer_AllowRelayFalse_BidirectionalPunch_DataIntegrity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf.AsMemory(0, r), cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        using var dummyP = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyP.Client.LocalEndPoint!).Port;
        dummyP.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        using var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "e2e_v6_punch",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            AllowRelay = false
        });
        using var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        using var dummyC = new TcpListener(IPAddress.Loopback, 0);
        dummyC.Start();
        int cTcpPort = ((IPEndPoint)dummyC.LocalEndpoint).Port;
        dummyC.Stop();

        var cCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cTcpPort,
            TargetName = "e2e_v6_punch",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true
        });
        using var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        for (int i = 0; i < 30 && pEngine.Proxy?.DirectQuery("e2e_v6_punch/tcp") == null; i++)
        {
            await Task.Delay(100, cts.Token);
        }
        await Task.Delay(200, cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, cTcpPort, cts.Token);
        using var netStream = tcp.GetStream();
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Bidirectional IPv6/IPv4 punch verified!");
        await netStream.WriteAsync(payload, cts.Token);

        byte[] recv = new byte[payload.Length];
        await netStream.ReadExactlyAsync(recv, cts.Token);
        Assert.Equal(payload, recv);

        echoListener.Stop();
    }
}
