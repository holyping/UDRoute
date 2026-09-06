using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;

namespace UDRoute.Tests;

public class ProxyResourceManagementTests
{
    private readonly ITestOutputHelper _out;
    public ProxyResourceManagementTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public async Task Disconnect_ImmediatelyEndsRelaySessionOnP()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S (ForceRelay = true, TunnelReuseInterval = 0 so disconnect is immediate)
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 0
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "p_disc_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 0
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C (ForceRelay = true)
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = true,
            TunnelReuseInterval = 0
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "p_disc_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Connect and verify session exists on P
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = client.GetStream();
            byte[] msg = Encoding.UTF8.GetBytes("Test Disconnect");
            await stream.WriteAsync(msg, cts.Token);

            byte[] recv = new byte[msg.Length];
            int total = 0;
            while (total < msg.Length)
            {
                int r = await stream.ReadAsync(recv.AsMemory(total, msg.Length - total), cts.Token);
                Assert.True(r > 0);
                total += r;
            }
            Assert.Equal(msg, recv);

            // While connected, P must have the relay session
            Assert.NotNull(pEngine.Proxy);
            Assert.True(pEngine.Proxy.RelaySessionCount > 0, "P should have active relay session while connected");
        } // client closes here, triggering channel close and SendDisconnectAsync

        // 6. Give short time for Disconnect to reach P and be relayed
        await Task.Delay(300, cts.Token);

        // P must have immediately removed the session upon relaying Disconnect!
        Assert.NotNull(pEngine.Proxy);
        Assert.Equal(0, pEngine.Proxy.RelaySessionCount);
        _out.WriteLine("Verified: P immediately ended relay session upon disconnect.");
    }

    [Fact]
    public async Task RelayEnd_WhenDirectPunchSucceeds_EndsSessionOnP_WithoutNotifyingPeers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S (Normal punch mode, ForceRelay = false, TunnelReuseInterval = 60)
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = false,
            TunnelReuseInterval = 60
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "p_punch_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C (ForceRelay = false)
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = false
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "p_punch_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = false
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Connect client, perform direct punch & communicate
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        var stream = client.GetStream();
        byte[] msg = Encoding.UTF8.GetBytes("Direct Punch Test");
        await stream.WriteAsync(msg, cts.Token);

        byte[] recv = new byte[msg.Length];
        int total = 0;
        while (total < msg.Length)
        {
            int r = await stream.ReadAsync(recv.AsMemory(total, msg.Length - total), cts.Token);
            Assert.True(r > 0);
            total += r;
        }
        Assert.Equal(msg, recv);

        // 6. Give short time (200-400ms) for punch confirmation & RelayEnd to reach P
        await Task.Delay(400, cts.Token);

        // Assert that P's RelaySession is ended!
        Assert.NotNull(pEngine.Proxy);
        Assert.Equal(0, pEngine.Proxy.RelaySessionCount);
        _out.WriteLine("Verified: P relay session ended after punch confirmed, count is 0.");

        // 7. Verify direct communication continues WITHOUT being broken!
        byte[] msg2 = Encoding.UTF8.GetBytes("Direct Communication Still Alive");
        await stream.WriteAsync(msg2, cts.Token);
        byte[] recv2 = new byte[msg2.Length];
        int total2 = 0;
        while (total2 < msg2.Length)
        {
            int r = await stream.ReadAsync(recv2.AsMemory(total2, msg2.Length - total2), cts.Token);
            Assert.True(r > 0, "Direct session must NOT be broken by P ending relay session");
            total2 += r;
        }
        Assert.Equal(msg2, recv2);
        _out.WriteLine("Verified: Direct P2P communication unaffected by P session release.");
    }

    [Fact]
    public async Task TunnelReuseIntervalZero_ReusesWhileActive_ClosesImmediatelyWhenIdle()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S with TunnelReuseInterval = 0
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 0
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "reuse_zero_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 0
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = true,
            TunnelReuseInterval = 0
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "reuse_zero_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Client 1 connects and stays open
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        var stream1 = client1.GetStream();
        byte[] msg1 = Encoding.UTF8.GetBytes("Client 1 Msg");
        await stream1.WriteAsync(msg1, cts.Token);
        byte[] recv1 = new byte[msg1.Length];
        await stream1.ReadExactlyAsync(recv1, cts.Token);
        Assert.Equal(msg1, recv1);

        // 6. While Client 1 is still open, Client 2 connects (reusing active tunnel!)
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        var stream2 = client2.GetStream();
        byte[] msg2 = Encoding.UTF8.GetBytes("Client 2 Concurrent Msg");
        await stream2.WriteAsync(msg2, cts.Token);
        byte[] recv2 = new byte[msg2.Length];
        await stream2.ReadExactlyAsync(recv2, cts.Token);
        Assert.Equal(msg2, recv2);

        _out.WriteLine("Verified: Concurrent connections reused tunnel with TunnelReuseInterval=0.");

        // 7. Now close both clients
        client1.Close();
        client2.Close();

        // 8. Give short delay for disconnect to take effect
        await Task.Delay(400, cts.Token);

        // When idle with ReuseInterval=0, tunnel closes immediately (P session count is 0)
        Assert.NotNull(pEngine.Proxy);
        Assert.Equal(0, pEngine.Proxy.RelaySessionCount);
        _out.WriteLine("Verified: Tunnel closed immediately when idle with TunnelReuseInterval=0.");
    }

    [Fact]
    public async Task IdleTimeout_EndsRelaySessionOnP()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S with short Timeout = 2s
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 60
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "timeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            Timeout = 2,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = true
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "timeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Connect and send initial traffic
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        var stream = client.GetStream();
        byte[] msg = Encoding.UTF8.GetBytes("Ping before idle");
        await stream.WriteAsync(msg, cts.Token);
        byte[] recv = new byte[msg.Length];
        await stream.ReadExactlyAsync(recv, cts.Token);
        Assert.Equal(msg, recv);

        // Session must be active on P
        Assert.NotNull(pEngine.Proxy);
        Assert.True(pEngine.Proxy.RelaySessionCount > 0, "P should have active relay session");

        // 6. Stop all traffic, wait for Timeout (2s) + cleanup interval (5s) ~ 7.5s
        _out.WriteLine("Waiting for idle timeout on P...");
        await Task.Delay(7500, cts.Token);

        // 7. P's cleanup loop must have reaped the idle session!
        Assert.Equal(0, pEngine.Proxy.RelaySessionCount);
        _out.WriteLine("Verified: P automatically reaped idle session exceeding Timeout.");
    }

    [Fact]
    public async Task DirectSession_IgnoresDisconnectSignalFromProxy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S (ForceRelay = false, punches to direct)
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = false
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "p_ignore_disc",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = false
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "p_ignore_disc",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = false
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Connect and punch through to direct mode
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
        var stream = client.GetStream();
        byte[] msg = Encoding.UTF8.GetBytes("Pre-Disconnect Message");
        await stream.WriteAsync(msg, cts.Token);
        byte[] recv = new byte[msg.Length];
        await stream.ReadExactlyAsync(recv, cts.Token);
        Assert.Equal(msg, recv);

        // Wait for punch to settle and direct mode to be active
        await Task.Delay(400, cts.Token);

        // 6. Simulate P sending a Disconnect packet to C with the session ID
        // Find C's active session
        var cSessions = cEngine.Client;
        Assert.NotNull(cSessions);

        // Send a fake Disconnect packet from P's endpoint
        var fakeProxyEp = new IPEndPoint(IPAddress.Loopback, pPort);
        foreach (var session in cEngine.Client!.Sessions)
        {
            Assert.True(session.IsDirect, "Session should be in direct mode");
            // Direct call to TryHandleDisconnect with fakeProxyEp
            bool handled = session.TryHandleDisconnect(fakeProxyEp);
            Assert.True(handled);
            Assert.False(session.IsClosed, "Direct session must NOT be closed when disconnect comes from Proxy!");
        }

        // 7. Verify direct communication continues seamlessly!
        byte[] msg2 = Encoding.UTF8.GetBytes("Post-Fake-Disconnect Message");
        await stream.WriteAsync(msg2, cts.Token);
        byte[] recv2 = new byte[msg2.Length];
        await stream.ReadExactlyAsync(recv2, cts.Token);
        Assert.Equal(msg2, recv2);
        _out.WriteLine("Verified: Session ignored disconnect signal from Proxy and remained direct!");
    }

    [Fact]
    public void ReceiveDirectData_WhenInRelayMode_AutomaticallySwitchesToDirect()
    {
        using var socket = new ZeroCopyUdpSocket(0);
        var proxyEp = new IPEndPoint(IPAddress.Loopback, 9400);
        var peerDirectEp = new IPEndPoint(IPAddress.Loopback, 18888);
        Guid sessionId = Guid.NewGuid();

        // Initially in relay mode pointing to Proxy
        var session = new TunnelSession(socket, proxyEp, sessionId, 1400, true);
        session.ProxyEp = proxyEp;
        Assert.False(session.IsDirect);
        Assert.Equal(proxyEp, session.ActiveRemoteEp);

        // When data arrives directly from peerDirectEp (not proxyEp)
        session.EnsureDirectRouteFromPeer(peerDirectEp);

        // Must have automatically switched to direct mode!
        Assert.True(session.IsDirect);
        Assert.Equal(peerDirectEp, session.ActiveRemoteEp);
        _out.WriteLine("Verified: Session automatically switched to direct mode upon receiving peer direct signal.");
    }

    [Fact]
    public async Task ServerRestart_ClientAutoRecoversAndRecreatesTunnel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S (ForceRelay = true, TunnelReuseInterval = 60)
        using var dummySUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int sPort = ((IPEndPoint)dummySUdp.Client.LocalEndPoint!).Port;
        dummySUdp.Close();

        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = sPort, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 60
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "restart_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 60,
            RegInterval = 1
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C (ForceRelay = true, TunnelReuseInterval = 60)
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 60
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "restart_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Connect Client 1, verify normal traffic
        using (var client1 = new TcpClient())
        {
            await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream1 = client1.GetStream();
            byte[] msg1 = Encoding.UTF8.GetBytes("Message before S restart");
            await stream1.WriteAsync(msg1, cts.Token);
            byte[] recv1 = new byte[msg1.Length];
            await stream1.ReadExactlyAsync(recv1, cts.Token);
            Assert.Equal(msg1, recv1);
        }

        // 6. Stop S (simulate S crash/restart)
        _out.WriteLine("Stopping S...");
        sEngine.Dispose();
        await Task.Delay(500, cts.Token);

        // 7. Restart S on the same port and re-register
        _out.WriteLine("Restarting S...");
        var sEngine2 = new RouteEngine(sCfg);
        _ = sEngine2.StartAsync(cts.Token);
        await Task.Delay(1200, cts.Token); // Wait for S to re-register with P

        // 8. Connect Client 2 to C:
        // C will initially attempt to reuse the old session, but S will send Disconnect.
        // C should automatically self-heal and reconnect via a fresh session!
        using (var client2 = new TcpClient())
        {
            await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream2 = client2.GetStream();
            byte[] msg2 = Encoding.UTF8.GetBytes("Message after S restart");
            await stream2.WriteAsync(msg2, cts.Token);
            byte[] recv2 = new byte[msg2.Length];
            await stream2.ReadExactlyAsync(recv2, cts.Token);
            Assert.Equal(msg2, recv2);
            _out.WriteLine("Verified: C automatically recovered from S restart and transferred data!");
        }
    }

    [Fact]
    public async Task ProxyRelay_UnknownSessionData_ReturnsDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // Send a fake data packet with unknown Guid to P
        using var testSocket = new ZeroCopyUdpSocket(0);
        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        Guid fakeSession = Guid.NewGuid();
        byte[] fakeData = new byte[25];
        fakeData[0] = (byte)MsgType.Data;
        fakeSession.TryWriteBytes(fakeData.AsSpan(1, 16));
        fakeData[17] = (byte)MuxType.Kcp;

        await testSocket.SendAsync(fakeData, pEp, cts.Token);

        // Expect P to return Disconnect
        byte[] recvBuf = new byte[64];
        var (len, _) = await testSocket.ReceiveAsync(recvBuf, cts.Token);
        Assert.True(len >= 17);
        Assert.Equal((byte)MsgType.Disconnect, recvBuf[0]);
        Guid recvSession = new Guid(recvBuf.AsSpan(1, 16));
        Assert.Equal(fakeSession, recvSession);
        _out.WriteLine("Verified: P returned Disconnect for unknown session data.");
    }

    [Fact]
    public async Task Server_UnknownSessionData_ReturnsDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummySUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int sPort = ((IPEndPoint)dummySUdp.Client.LocalEndPoint!).Port;
        dummySUdp.Close();

        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = sPort, ConfigPath = "dummy.ini" };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "dummy_s",
            TargetServer = "127.0.0.1:9400",
            TargetIp = "127.0.0.1",
            TargetPort = 80
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // Send a fake data packet with unknown Guid to S
        using var testSocket = new ZeroCopyUdpSocket(0);
        var sEp = new IPEndPoint(IPAddress.Loopback, sPort);
        Guid fakeSession = Guid.NewGuid();
        byte[] fakeData = new byte[25];
        fakeData[0] = (byte)MsgType.Data;
        fakeSession.TryWriteBytes(fakeData.AsSpan(1, 16));
        fakeData[17] = (byte)MuxType.Kcp;

        await testSocket.SendAsync(fakeData, sEp, cts.Token);

        // Expect S to return Disconnect
        byte[] recvBuf = new byte[64];
        var (len, _) = await testSocket.ReceiveAsync(recvBuf, cts.Token);
        Assert.True(len >= 17);
        Assert.Equal((byte)MsgType.Disconnect, recvBuf[0]);
        Guid recvSession = new Guid(recvBuf.AsSpan(1, 16));
        Assert.Equal(fakeSession, recvSession);
        _out.WriteLine("Verified: S returned Disconnect for unknown session data.");
    }

    [Fact]
    public async Task IdleTimeoutOnP_WhenClientReusesTunnel_PReestablishesSessionAndCommunicatesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S with short Timeout = 2s, but TunnelReuseInterval = 60s
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 60
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "reestablish_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            Timeout = 2,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C with ForceRelay = true
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = true
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "reestablish_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Client connection 1
        using (var client1 = new TcpClient())
        {
            await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            var stream1 = client1.GetStream();
            byte[] msg1 = Encoding.UTF8.GetBytes("Connection 1 Msg");
            await stream1.WriteAsync(msg1, cts.Token);
            byte[] recv1 = new byte[msg1.Length];
            await stream1.ReadExactlyAsync(recv1, cts.Token);
            Assert.Equal(msg1, recv1);
        }

        // Verify P has active session while running
        Assert.NotNull(pEngine.Proxy);
        Assert.True(pEngine.Proxy.RelaySessionCount > 0);

        // 6. Idle until P's cleanup loop releases the session on P (Timeout = 2s + 5s cleanup = ~7.5s)
        _out.WriteLine("Waiting for idle session release on P...");
        await Task.Delay(7500, cts.Token);

        // P's active session count must be 0!
        Assert.Equal(0, pEngine.Proxy.RelaySessionCount);
        _out.WriteLine("Verified: P reaped idle relay session (P count is 0).");

        // 7. Client connection 2: Reuses the existing tunnel!
        // P must automatically re-establish the session upon receiving C's data and complete transfer!
        using (var client2 = new TcpClient())
        {
            await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            var stream2 = client2.GetStream();
            byte[] msg2 = Encoding.UTF8.GetBytes("Connection 2 Reused Msg After P Reaped");
            await stream2.WriteAsync(msg2, cts.Token);
            byte[] recv2 = new byte[msg2.Length];
            await stream2.ReadExactlyAsync(recv2, cts.Token);
            Assert.Equal(msg2, recv2);
        }

        // Verify P now has the session re-established!
        Assert.True(pEngine.Proxy.RelaySessionCount > 0, "P should have re-established relay session upon data packet arrival");
        _out.WriteLine("Verified: P automatically re-established relay session and communication succeeded.");
    }

    [Fact]
    public async Task TunnelReuse_WhenServerRestartsWhileIdle_ClientRecoversWithFreshTunnel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[4096];
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, r, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S1
        var sDevId = Guid.NewGuid();
        var sCfg = new AppConfig 
        { 
            DevId = sDevId, 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 60
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "restart_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 60
        });
        var sEngine1 = new RouteEngine(sCfg);
        _ = sEngine1.StartAsync(cts.Token);

        // 4. Setup C
        using var cDummy = new TcpListener(IPAddress.Loopback, 0);
        cDummy.Start();
        int cPort = ((IPEndPoint)cDummy.LocalEndpoint).Port;
        cDummy.Stop();

        var cCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini",
            ForceRelay = true
        };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "restart_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1000, cts.Token);

        // 5. Client connection 1
        using (var client1 = new TcpClient())
        {
            await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            var stream1 = client1.GetStream();
            byte[] msg1 = Encoding.UTF8.GetBytes("Before Server Restart");
            await stream1.WriteAsync(msg1, cts.Token);
            byte[] recv1 = new byte[msg1.Length];
            await stream1.ReadExactlyAsync(recv1, cts.Token);
            Assert.Equal(msg1, recv1);
        }

        // 6. Now simulate S shutdown and restart (memory wiped)
        _out.WriteLine("Shutting down S1 and starting fresh S2...");
        sEngine1.Dispose();
        await Task.Delay(500, cts.Token);

        // Start S2 (fresh process/memory) with same registration
        var sEngine2 = new RouteEngine(sCfg);
        _ = sEngine2.StartAsync(cts.Token);
        await Task.Delay(1000, cts.Token);

        // 7. Client connection 2 arrives
        // C initially tries to reuse old tunnel -> S2 returns Disconnect -> C automatically retries with fresh tunnel -> Success!
        using (var client2 = new TcpClient())
        {
            await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            var stream2 = client2.GetStream();
            byte[] msg2 = Encoding.UTF8.GetBytes("After Server Restart Reconnection");
            await stream2.WriteAsync(msg2, cts.Token);
            byte[] recv2 = new byte[msg2.Length];
            await stream2.ReadExactlyAsync(recv2, cts.Token);
            Assert.Equal(msg2, recv2);
        }

        _out.WriteLine("Verified: Client cleanly recovered after server restart while tunnel was idle.");
        sEngine2.Dispose();
    }
}

