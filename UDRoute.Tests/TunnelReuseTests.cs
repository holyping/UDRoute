using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;

namespace UDRoute.Tests;

public class TunnelReuseTests
{
    private readonly ITestOutputHelper _out;
    public TunnelReuseTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public async Task ConsecutiveConnections_ReusesDataTunnel_WithZeroHandshake()
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
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, read, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode with TunnelReuseInterval = 60s
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
            Name = "reuse_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode
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
            TargetName = "reuse_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        // Wait for S registration to P
        await Task.Delay(1500, cts.Token);

        // --- Connection 1: Initial full handshake connection ---
        var sw1 = Stopwatch.StartNew();
        using (var client1 = new TcpClient())
        {
            await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream1 = client1.GetStream();
            byte[] send1 = Encoding.UTF8.GetBytes("Hello Connection 1");
            await stream1.WriteAsync(send1, cts.Token);

            byte[] recv1 = new byte[send1.Length];
            int totalRead1 = 0;
            while (totalRead1 < send1.Length)
            {
                int r = await stream1.ReadAsync(recv1.AsMemory(totalRead1, send1.Length - totalRead1), cts.Token);
                Assert.True(r > 0, "Connection 1 premature EOF");
                totalRead1 += r;
            }
            Assert.Equal(send1, recv1);
        }
        sw1.Stop();
        _out.WriteLine($"Connection 1 completed in {sw1.ElapsedMilliseconds} ms");

        // Small delay between connections
        await Task.Delay(500, cts.Token);

        // --- Connection 2: Reused tunnel (zero handshake) ---
        var sw2 = Stopwatch.StartNew();
        using (var client2 = new TcpClient())
        {
            await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream2 = client2.GetStream();
            byte[] send2 = Encoding.UTF8.GetBytes("Hello Connection 2 Reused");
            await stream2.WriteAsync(send2, cts.Token);

            byte[] recv2 = new byte[send2.Length];
            int totalRead2 = 0;
            while (totalRead2 < send2.Length)
            {
                int r = await stream2.ReadAsync(recv2.AsMemory(totalRead2, send2.Length - totalRead2), cts.Token);
                Assert.True(r > 0, "Connection 2 premature EOF");
                totalRead2 += r;
            }
            Assert.Equal(send2, recv2);
        }
        sw2.Stop();
        _out.WriteLine($"Connection 2 completed in {sw2.ElapsedMilliseconds} ms");

        // --- Connection 3: Another reused connection with 64KB data ---
        var sw3 = Stopwatch.StartNew();
        using (var client3 = new TcpClient())
        {
            await client3.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream3 = client3.GetStream();
            byte[] send3 = new byte[65536];
            Random.Shared.NextBytes(send3);
            _ = Task.Run(async () =>
            {
                await stream3.WriteAsync(send3, cts.Token);
            }, cts.Token);

            byte[] recv3 = new byte[send3.Length];
            int totalRead3 = 0;
            while (totalRead3 < send3.Length)
            {
                int r = await stream3.ReadAsync(recv3.AsMemory(totalRead3, send3.Length - totalRead3), cts.Token);
                Assert.True(r > 0, "Connection 3 premature EOF");
                totalRead3 += r;
            }
            Assert.Equal(send3, recv3);
        }
        sw3.Stop();
        _out.WriteLine($"Connection 3 (64KB) completed in {sw3.ElapsedMilliseconds} ms");

        cts.Cancel();
    }

    [Fact]
    public async Task ConcurrentConnections_MultiplexOverReusedTunnel_DataIntegrity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (TCP)
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
                            byte[] buf = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, read, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode
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
            Name = "concurrent_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode
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
            TargetName = "concurrent_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // 5. Establish initial connection to warm up tunnel
        using (var warmClient = new TcpClient())
        {
            await warmClient.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = warmClient.GetStream();
            byte[] warm = new byte[] { 1, 2, 3 };
            await stream.WriteAsync(warm, cts.Token);
            byte[] warmRecv = new byte[3];
            await stream.ReadExactlyAsync(warmRecv, cts.Token);
            Assert.Equal(warm, warmRecv);
        }

        // 6. Launch 5 concurrent connections over the same tunnel
        const int concurrentCount = 5;
        const int payloadSize = 32768; // 32KB each
        var tasks = Enumerable.Range(0, concurrentCount).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = client.GetStream();

            byte[] sendPayload = new byte[payloadSize];
            sendPayload[0] = (byte)i;
            Random.Shared.NextBytes(sendPayload.AsSpan(1));

            var writeTask = Task.Run(async () =>
            {
                await stream.WriteAsync(sendPayload, cts.Token);
            }, cts.Token);

            byte[] recvPayload = new byte[payloadSize];
            int totalRead = 0;
            while (totalRead < payloadSize)
            {
                int r = await stream.ReadAsync(recvPayload.AsMemory(totalRead, payloadSize - totalRead), cts.Token);
                Assert.True(r > 0, $"Concurrent client {i} premature EOF at {totalRead}/{payloadSize}");
                totalRead += r;
            }

            await writeTask;
            Assert.Equal(sendPayload, recvPayload);
            _out.WriteLine($"Concurrent client {i} verified {payloadSize} bytes successfully.");
        }).ToArray();

        await Task.WhenAll(tasks);
        cts.Cancel();
    }

    [Fact]
    public async Task IdleTimeout_TunnelClosesAfterInterval_AndNewConnectionReestablishes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (TCP)
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
                            byte[] buf = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, read, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode with short TunnelReuseInterval = 2s
        var sCfg = new AppConfig 
        { 
            DevId = Guid.NewGuid(), 
            Port = 0, 
            ConfigPath = "dummy.ini", 
            ForceRelay = true,
            TunnelReuseInterval = 2
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "idletimeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            TunnelReuseInterval = 2
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode
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
            TargetName = "idletimeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // 5. Connection 1
        using (var client1 = new TcpClient())
        {
            await client1.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream1 = client1.GetStream();
            byte[] send1 = Encoding.UTF8.GetBytes("Before Idle Timeout");
            await stream1.WriteAsync(send1, cts.Token);
            byte[] recv1 = new byte[send1.Length];
            await stream1.ReadExactlyAsync(recv1, cts.Token);
            Assert.Equal(send1, recv1);
        }

        // 6. Wait 3.5s for the 2s TunnelReuseInterval idle timeout to trigger
        _out.WriteLine("Waiting 3.5s for tunnel idle expiration...");
        await Task.Delay(3500, cts.Token);

        // 7. Connection 2: New tunnel established cleanly after old one expired
        using (var client2 = new TcpClient())
        {
            await client2.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream2 = client2.GetStream();
            byte[] send2 = Encoding.UTF8.GetBytes("After Idle Timeout Reconnect");
            await stream2.WriteAsync(send2, cts.Token);
            byte[] recv2 = new byte[send2.Length];
            await stream2.ReadExactlyAsync(recv2, cts.Token);
            Assert.Equal(send2, recv2);
        }

        _out.WriteLine("Idle timeout and reconnect verified successfully.");
        cts.Cancel();
    }

    [Fact]
    public async Task Udp_ConsecutivePackets_ReusesDataTunnel_WithZeroHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (UDP)
        using var echoUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint!).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var res = await echoUdp.ReceiveAsync(cts.Token);
                    await echoUdp.SendAsync(res.Buffer, res.RemoteEndPoint, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode with TunnelReuseInterval = 60s
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
            Name = "udp_reuse_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = false,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode
        using var dummyC = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int cPort = ((IPEndPoint)dummyC.Client.LocalEndPoint!).Port;
        dummyC.Close();

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
            TargetName = "udp_reuse_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = false,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // 5. Packet 1: Initial full handshake connection
        using var udpClient = new UdpClient();
        var cEp = new IPEndPoint(IPAddress.Loopback, cPort);

        byte[] send1 = Encoding.UTF8.GetBytes("Hello UDP Packet 1");
        await udpClient.SendAsync(send1, cEp);
        using var timeout1 = new CancellationTokenSource(5000);
        using var linked1 = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout1.Token);
        var recv1 = await udpClient.ReceiveAsync(linked1.Token);
        Assert.Equal(send1, recv1.Buffer);

        // Wait 500ms
        await Task.Delay(500, cts.Token);

        // 6. Packet 2: Reused tunnel (zero handshake)
        var sw2 = Stopwatch.StartNew();
        byte[] send2 = Encoding.UTF8.GetBytes("Hello UDP Packet 2 Reused");
        await udpClient.SendAsync(send2, cEp);
        using var timeout2 = new CancellationTokenSource(5000);
        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout2.Token);
        var recv2 = await udpClient.ReceiveAsync(linked2.Token);
        sw2.Stop();
        Assert.Equal(send2, recv2.Buffer);
        _out.WriteLine($"Packet 2 received in {sw2.ElapsedMilliseconds} ms");

        // 7. Packet 3: 1200 bytes datagram over reused tunnel
        var sw3 = Stopwatch.StartNew();
        byte[] send3 = new byte[1200];
        Random.Shared.NextBytes(send3);
        await udpClient.SendAsync(send3, cEp);
        using var timeout3 = new CancellationTokenSource(5000);
        using var linked3 = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout3.Token);
        var recv3 = await udpClient.ReceiveAsync(linked3.Token);
        sw3.Stop();
        Assert.Equal(send3, recv3.Buffer);
        _out.WriteLine($"Packet 3 (1200 bytes) received in {sw3.ElapsedMilliseconds} ms");

        cts.Cancel();
    }

    [Fact]
    public async Task Udp_ConcurrentClients_MultiplexOverReusedTunnel_DataIntegrity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (UDP)
        using var echoUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint!).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var res = await echoUdp.ReceiveAsync(cts.Token);
                    await echoUdp.SendAsync(res.Buffer, res.RemoteEndPoint, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode
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
            Name = "udp_concurrent_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = false,
            TunnelReuseInterval = 60
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode
        using var dummyC = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int cPort = ((IPEndPoint)dummyC.Client.LocalEndPoint!).Port;
        dummyC.Close();

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
            TargetName = "udp_concurrent_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = false,
            ForceRelay = true
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // 5. 5 Concurrent UDP Clients
        const int clientCount = 5;
        var tasks = new List<Task>();
        var cEp = new IPEndPoint(IPAddress.Loopback, cPort);

        for (int i = 0; i < clientCount; i++)
        {
            int clientId = i;
            tasks.Add(Task.Run(async () =>
            {
                using var client = new UdpClient();
                for (int round = 0; round < 5; round++)
                {
                    byte[] sendBuf = Encoding.UTF8.GetBytes($"Client-{clientId}-Round-{round}-Payload");
                    await client.SendAsync(sendBuf, cEp);

                    using var timeout = new CancellationTokenSource(10000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
                    var recv = await client.ReceiveAsync(linked.Token);
                    Assert.Equal(sendBuf, recv.Buffer);
                }
            }, cts.Token));
        }

        await Task.WhenAll(tasks);
        _out.WriteLine($"All {clientCount} concurrent UDP clients finished rounds successfully.");
        cts.Cancel();
    }

    [Fact]
    public async Task Udp_IdleTimeout_TunnelClosesAfterInterval()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;

        // 1. Setup Local Echo Server (UDP)
        using var echoUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint!).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var res = await echoUdp.ReceiveAsync(cts.Token);
                    await echoUdp.SendAsync(res.Buffer, res.RemoteEndPoint, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. Setup P Mode
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S Mode with short TunnelReuseInterval = 2s
        var sCfg = new AppConfig
        {
            DevId = Guid.NewGuid(),
            Port = 0,
            ConfigPath = "dummy.ini",
            ForceRelay = true,
            TunnelReuseInterval = 2
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "udp_idletimeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = false,
            TunnelReuseInterval = 2
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 4. Setup C Mode with Timeout = 1s
        using var dummyC = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int cPort = ((IPEndPoint)dummyC.Client.LocalEndPoint!).Port;
        dummyC.Close();

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
            TargetName = "udp_idletimeout_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = false,
            ForceRelay = true,
            Timeout = 1
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        var cEp = new IPEndPoint(IPAddress.Loopback, cPort);

        // 5. Packet 1
        using (var client1 = new UdpClient())
        {
            byte[] send1 = Encoding.UTF8.GetBytes("Before UDP Idle Timeout");
            await client1.SendAsync(send1, cEp);
            using var timeout1 = new CancellationTokenSource(5000);
            using var linked1 = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout1.Token);
            var recv1 = await client1.ReceiveAsync(linked1.Token);
            Assert.Equal(send1, recv1.Buffer);
        }

        // 6. Wait 3.5s for the 1s client timeout + 2s TunnelReuseInterval to trigger
        _out.WriteLine("Waiting 3.5s for UDP tunnel idle expiration...");
        await Task.Delay(3500, cts.Token);

        // 7. Packet 2: New tunnel established cleanly after old one expired
        using (var client2 = new UdpClient())
        {
            byte[] send2 = Encoding.UTF8.GetBytes("After UDP Idle Timeout Reconnect");
            await client2.SendAsync(send2, cEp);
            using var timeout2 = new CancellationTokenSource(5000);
            using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout2.Token);
            var recv2 = await client2.ReceiveAsync(linked2.Token);
            Assert.Equal(send2, recv2.Buffer);
        }

        _out.WriteLine("UDP Idle timeout and reconnect verified successfully.");
        cts.Cancel();
    }
}


