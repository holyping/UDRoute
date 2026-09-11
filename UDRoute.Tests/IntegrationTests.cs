using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;
using System.Linq;
using System.Security.Cryptography;

namespace UDRoute.Tests;

public class IntegrationTests
{
    private readonly ITestOutputHelper _out;
    public IntegrationTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public async Task ProxyRelay_KcpApi_TcpDataIntegrity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Trace;

        // 1. Setup Local Echo Server (TCP) representing the actual service S targets
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        var echoTask = Task.Run(async () =>
        {
            try
            {
                using var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                byte[] buf = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                {
                    await stream.WriteAsync(buf, 0, read, cts.Token);
                }
            }
            catch { }
        });

        // 2. Setup Proxy (P Mode)
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        var pTask = pEngine.StartAsync(cts.Token);
        await Task.Delay(200);

        // 3. Setup Server (S Mode)
        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini", ForceRelay = true };
        var sKcp = new KcpConfig();
        sKcp.SetProfile("api");
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "echo_service",
            TargetServer = $"127.0.0.1:{pPort}", // Proxy
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            KcpConfig = sKcp
        });
        var sEngine = new RouteEngine(sCfg);
        var sTask = sEngine.StartAsync(cts.Token);

        // 4. Setup Client (C Mode)
        var cCfg = new AppConfig { DevId = Guid.NewGuid(), ForceRelay = true, Port = 0, ConfigPath = "dummy.ini" }; // FORCE RELAY
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();
        
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "echo_service",
            TargetServer = $"127.0.0.1:{pPort}", // Proxy
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        var cTask = cEngine.StartAsync(cts.Token);

        // Wait for registration and initialization
        await Task.Delay(2000, cts.Token);

        // 5. Connect C and transfer 1MB data
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        using var stream = clientTcp.GetStream();

        byte[] sendData = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(sendData);
        byte[] recvData = new byte[sendData.Length];

        var readTask = Task.Run(async () =>
        {
            int totalRead = 0;
            while (totalRead < recvData.Length)
            {
                int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        });

        // Write in chunks
        int offset = 0;
        int chunk = 8192;
        while (offset < sendData.Length)
        {
            int len = Math.Min(chunk, sendData.Length - offset);
            await stream.WriteAsync(sendData, offset, len, cts.Token);
            offset += len;
        }

        int bytesRead = await readTask;
        Assert.Equal(sendData.Length, bytesRead);

        // Verify Data Integrity
        Assert.True(sendData.SequenceEqual(recvData), "Data integrity verification failed!");

        cts.Cancel();
        echoListener.Stop();
        try { await Task.WhenAll(pTask, sTask, cTask, echoTask); } catch { }
    }

    [Fact]
    public async Task AllowRelayFalse_WithClientForceRelayTrue_IgnoresForceRelay_PunchesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (TCP)
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        var echoTask = Task.Run(async () =>
        {
            try
            {
                using var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                byte[] buf = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                {
                    await stream.WriteAsync(buf, 0, read, cts.Token);
                }
            }
            catch { }
        });

        // 2. Setup Proxy (P Mode)
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            DevId = Guid.NewGuid(),
            ConfigPath = "dummy.ini",
            AuthMode = AuthMode.None,
            AllowUnauthRelay = AllowUnauthRelay.Allow,
            EnableProxy = true
        };
        var pEngine = new RouteEngine(pCfg);
        var pTask = pEngine.StartAsync(cts.Token);

        // 3. Setup Server (S Mode) with AllowRelay = false
        var sCfg = new AppConfig
        {
            DevId = Guid.NewGuid(),
            Port = 0,
            ConfigPath = "dummy.ini"
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "no_relay_service",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            AllowRelay = false // S forbids relay!
        });
        var sEngine = new RouteEngine(sCfg);
        var sTask = sEngine.StartAsync(cts.Token);

        // 4. Setup Client (C Mode) with ForceRelay = true
        using var dummyC = new TcpListener(IPAddress.Loopback, 0);
        dummyC.Start();
        int cPort = ((IPEndPoint)dummyC.LocalEndpoint).Port;
        dummyC.Stop();

        var cCfg = new AppConfig
        {
            DevId = Guid.NewGuid(),
            Port = 0,
            ConfigPath = "dummy.ini",
            ForceRelay = true // C requests ForceRelay!
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "no_relay_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true // C explicitly requests ForceRelay!
        });
        var cEngine = new RouteEngine(cCfg);
        var cTask = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // 5. Connect via C Mode local port
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        using var stream = tcpClient.GetStream();

        byte[] testData = System.Text.Encoding.UTF8.GetBytes("Testing AllowRelay=false with ForceRelay=true conflict resolution");
        await stream.WriteAsync(testData, cts.Token);

        byte[] recvBuf = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < recvBuf.Length)
        {
            int r = await stream.ReadAsync(recvBuf.AsMemory(totalRead, recvBuf.Length - totalRead), cts.Token);
            if (r == 0) break;
            totalRead += r;
        }

        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, recvBuf);

        cts.Cancel();
        echoListener.Stop();
        try { await Task.WhenAll(pTask, sTask, cTask, echoTask); } catch { }
    }

    [Fact]
    public async Task Punch_LoopbackProtection_DropsSelfInstanceId_AcceptsDifferentInstanceId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var cCfg = new AppConfig
        {
            DevId = Guid.NewGuid(),
            Port = 0,
            ConfigPath = "dummy.ini"
        };
        var dummySocket = new ZeroCopyUdpSocket(0);
        var cClient = new ClientMode(cCfg, dummySocket, null);

        // Manually create a session on Client
        var sessionId = Guid.NewGuid();
        var dummyRemote = new IPEndPoint(IPAddress.Loopback, 9999);
        var session = new TunnelSession(dummySocket, dummyRemote, sessionId, 1400, false, null, 10, 10, 10);
        cClient.AddSession(session);

        // 1. Send Punch packet with self InstanceId -> MUST be dropped, IsDirect must stay false
        bool handledSelf = await cClient.TryHandlePunchAsync(sessionId, cCfg.InstanceId, dummyRemote, 2, cts.Token);
        Assert.True(handledSelf);
        Assert.False(session.IsDirect);

        // 2. Send Punch packet with different InstanceId (peer) -> MUST be accepted, IsDirect becomes true
        var peerInstanceId = Guid.NewGuid();
        bool handledPeer = await cClient.TryHandlePunchAsync(sessionId, peerInstanceId, dummyRemote, 2, cts.Token);
        Assert.True(handledPeer);
        Assert.True(session.IsDirect);
        Assert.Equal(peerInstanceId, session.PeerInstanceId);

        session.Dispose();
        dummySocket.Dispose();
    }
}

