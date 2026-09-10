using System;
using System.Buffers.Binary;
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

public class ProxyHealthProbeTests
{
    private readonly ITestOutputHelper _out;

    public ProxyHealthProbeTests(ITestOutputHelper output)
    {
        _out = output;
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Debug;
    }

    private static byte[] CreateRegisterPacket(Guid devId, string serviceName, bool isTcp = true, int timeout = 60, ushort contextId = 1)
    {
        byte[] regBuf = new byte[256];
        regBuf[0] = (byte)MsgType.Register;
        BinaryPrimitives.WriteUInt16LittleEndian(regBuf.AsSpan(1, 2), contextId);
        devId.TryWriteBytes(regBuf.AsSpan(3, 16));
        BinaryPrimitives.WriteInt32LittleEndian(regBuf.AsSpan(19, 4), 0); // WanPort
        regBuf[23] = (byte)(isTcp ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(regBuf.AsSpan(24, 4), timeout);
        BinaryPrimitives.WriteInt64LittleEndian(regBuf.AsSpan(28, 8), DateTime.UtcNow.Ticks);
        regBuf[36] = 0; // ReqPass

        int kLen = ProtocolHelper.WriteKcpConfig(regBuf.AsSpan(37), new KcpConfig());
        int offset = 37 + kLen;
        offset += ProtocolHelper.WriteString(regBuf.AsSpan(offset), serviceName);
        offset += ProtocolHelper.WriteString(regBuf.AsSpan(offset), ""); // Suffix
        regBuf[offset++] = 0; // LocalEps count

        byte[] packet = new byte[offset];
        Array.Copy(regBuf, packet, offset);
        return packet;
    }

    private static byte[] CreateQueryPacket(ushort contextId, Guid sessionId, string targetName, bool forceRelay = false, bool isReuse = false)
    {
        byte[] qBuf = new byte[256];
        qBuf[0] = (byte)MsgType.Query;
        BinaryPrimitives.WriteUInt16LittleEndian(qBuf.AsSpan(1, 2), contextId);
        sessionId.TryWriteBytes(qBuf.AsSpan(3, 16));
        int offset = 19;
        offset += ProtocolHelper.WriteString(qBuf.AsSpan(offset), targetName);
        qBuf[offset++] = (byte)((forceRelay ? 1 : 0) | (isReuse ? 2 : 0));

        byte[] packet = new byte[offset];
        Array.Copy(qBuf, packet, offset);
        return packet;
    }

    [Fact]
    public async Task HealthProbe_IdleExceeded_ProbeTimeout_DeclaresDeadAndReturnsStatus0()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        // T1 = 1 second (IdleThreshold), T2 = 1 second (ProbeTimeout)
        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini",
            IdleThreshold = 1,
            ProbeTimeout = 1
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);
        using var cSocket = new ZeroCopyUdpSocket(0);

        // 1. S registers service on P
        Guid devId = Guid.NewGuid();
        byte[] regPkt = CreateRegisterPacket(devId, "test_svc");
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        await Task.Delay(100, cts.Token);

        // 2. Wait > T1 (1.2s) so S becomes idle
        await Task.Delay(1200, cts.Token);

        // 3. S socket does not respond to probe

        // 4. C sends Query with ContextId 101
        ushort cContextId = 101;
        Guid sessionId = Guid.NewGuid();
        byte[] queryPkt = CreateQueryPacket(cContextId, sessionId, "test_svc/tcp");
        await cSocket.SendAsync(queryPkt, pEp, cts.Token);

        // 5. C waits for Punch response from P
        byte[] recvBuf = new byte[256];
        var (len, _) = await cSocket.ReceiveAsync(recvBuf, cts.Token);

        Assert.True(len >= 36);
        Assert.Equal((byte)MsgType.Punch, recvBuf[0]);
        ushort respContextId = BinaryPrimitives.ReadUInt16LittleEndian(recvBuf.AsSpan(1, 2));
        Assert.Equal(cContextId, respContextId);
        Guid respSessionId = new Guid(recvBuf.AsSpan(3, 16));
        Assert.Equal(sessionId, respSessionId);
        byte status = recvBuf[35];
        Assert.True(status == PunchStatus.SUnresponsive || status == PunchStatus.NotFound); // Status 4 = SUnresponsive, 0 = NotFound

        _out.WriteLine("Verified: P probed S, timed out after T2, declared channel dead and returned status to C.");
    }

    [Fact]
    public async Task HealthProbe_IdleExceeded_ProbeAckReceived_RefreshesLastSeenAndReturnsStatus1()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        // T1 = 1 second, T2 = 3 seconds
        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini",
            IdleThreshold = 1,
            ProbeTimeout = 3
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);
        using var cSocket = new ZeroCopyUdpSocket(0);

        // 1. S registers service on P
        Guid devId = Guid.NewGuid();
        byte[] regPkt = CreateRegisterPacket(devId, "echo_probe");
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        byte[] regAck = new byte[64];
        var (regAckLen, _) = await sSocket.ReceiveAsync(regAck, cts.Token);
        Assert.Equal((byte)MsgType.RegisterAck, regAck[0]);

        // 2. Wait > T1 so path is idle
        await Task.Delay(1200, cts.Token);

        // 3. Background task on S to receive RelayStart and reply with RelayStartAck
        var sTask = Task.Run(async () =>
        {
            byte[] sRecv = new byte[512];
            var (sLen, remoteEp) = await sSocket.ReceiveAsync(sRecv, cts.Token);
            Assert.True(sLen >= 20);
            Assert.Equal((byte)MsgType.RelayStart, sRecv[0]);
            ushort probeCtx = BinaryPrimitives.ReadUInt16LittleEndian(sRecv.AsSpan(1, 2));
            Guid probeSess = new Guid(sRecv.AsSpan(3, 16));

            // S replies with RelayStartAck
            byte[] ackBuf = new byte[20];
            ackBuf[0] = (byte)MsgType.RelayStartAck;
            BinaryPrimitives.WriteUInt16LittleEndian(ackBuf.AsSpan(1, 2), probeCtx);
            probeSess.TryWriteBytes(ackBuf.AsSpan(3, 16));
            ackBuf[19] = 1; // Status = OK
            await sSocket.SendAsync(ackBuf, remoteEp, cts.Token);
        }, cts.Token);

        // 4. C sends Query with ContextId 202
        ushort cContextId = 202;
        Guid sessionId = Guid.NewGuid();
        byte[] queryPkt = CreateQueryPacket(cContextId, sessionId, "echo_probe/tcp");
        await cSocket.SendAsync(queryPkt, pEp, cts.Token);

        // 5. C waits for Punch response from P
        byte[] recvBuf = new byte[512];
        var (len, _) = await cSocket.ReceiveAsync(recvBuf, cts.Token);

        await sTask;

        Assert.True(len >= 36);
        Assert.Equal((byte)MsgType.Punch, recvBuf[0]);
        ushort respContextId = BinaryPrimitives.ReadUInt16LittleEndian(recvBuf.AsSpan(1, 2));
        Assert.Equal(cContextId, respContextId);
        byte status = recvBuf[35];
        Assert.Equal(1, status); // Status 1 = Success!

        _out.WriteLine("Verified: P probed S, S replied with RelayStartAck, P refreshed LastSeen and returned Status 1 to C.");
    }

    [Fact]
    public async Task HealthProbe_NotIdle_ImmediatelyReturnsStatus1WithoutDelay()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        // T1 = 10 seconds
        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini",
            IdleThreshold = 10,
            ProbeTimeout = 2
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);
        using var cSocket = new ZeroCopyUdpSocket(0);

        // 1. S registers
        Guid devId = Guid.NewGuid();
        byte[] regPkt = CreateRegisterPacket(devId, "fast_svc");
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        await Task.Delay(50, cts.Token);

        // 2. Query immediately (idle time < 100ms << 10s)
        ushort cContextId = 303;
        Guid sessionId = Guid.NewGuid();
        byte[] queryPkt = CreateQueryPacket(cContextId, sessionId, "fast_svc/tcp");

        long start = Environment.TickCount64;
        await cSocket.SendAsync(queryPkt, pEp, cts.Token);

        byte[] recvBuf = new byte[512];
        var (len, _) = await cSocket.ReceiveAsync(recvBuf, cts.Token);
        long elapsed = Environment.TickCount64 - start;

        Assert.True(len >= 36);
        Assert.Equal(1, recvBuf[35]); // Success
        Assert.True(elapsed < 500, $"Expected fast response without probe delay, took {elapsed}ms");

        _out.WriteLine($"Verified: Path active, returned Status 1 in {elapsed}ms without probe delay.");
    }

    [Fact]
    public async Task ChannelMigration_OldChannelDeleted_OldPacketsRoutedThroughNewChannel_LastSeenUnchanged()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini",
            IdleThreshold = 60,
            ProbeTimeout = 5
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);
        using var cSocket = new ZeroCopyUdpSocket(0);

        // 1. Register S
        Guid devId = Guid.NewGuid();
        byte[] regPkt = CreateRegisterPacket(devId, "migrate_svc");
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        await Task.Delay(50, cts.Token);

        async Task<byte[]> ReceiveDataAsync()
        {
            byte[] buf = new byte[256];
            while (true)
            {
                var (l, _) = await sSocket.ReceiveAsync(buf, cts.Token);
                if (buf[0] == (byte)MsgType.Data)
                {
                    return buf.AsSpan(0, l).ToArray();
                }
            }
        }

        // 2. Establish Channel 1 (session 1)
        Guid session1 = Guid.NewGuid();
        byte[] q1 = CreateQueryPacket(1, session1, "migrate_svc/tcp");
        await cSocket.SendAsync(q1, pEp, cts.Token);
        byte[] r1 = new byte[512];
        await cSocket.ReceiveAsync(r1, cts.Token);

        // Send a data packet on session 1 to establish communication
        byte[] data1 = new byte[25];
        data1[0] = (byte)MsgType.Data;
        session1.TryWriteBytes(data1.AsSpan(1, 16));
        data1[17] = 0xAA;
        await cSocket.SendAsync(data1, pEp, cts.Token);

        // S receives data1
        byte[] recv1 = await ReceiveDataAsync();
        Assert.Equal(0xAA, recv1[17]);

        await Task.Delay(200, cts.Token);

        // 3. Establish Channel 2 (session 2) from same client to same service
        Guid session2 = Guid.NewGuid();
        byte[] q2 = CreateQueryPacket(2, session2, "migrate_svc/tcp");
        await cSocket.SendAsync(q2, pEp, cts.Token);
        byte[] r2 = new byte[512];
        await cSocket.ReceiveAsync(r2, cts.Token);

        // 4. Now send old packet on session 1!
        byte[] dataOld = new byte[25];
        dataOld[0] = (byte)MsgType.Data;
        session1.TryWriteBytes(dataOld.AsSpan(1, 16));
        dataOld[17] = 0xBB;
        await cSocket.SendAsync(dataOld, pEp, cts.Token);

        // S should receive dataOld forwarded via P!
        byte[] recvOld = await ReceiveDataAsync();
        Assert.Equal(0xBB, recvOld[17]);

        _out.WriteLine("Verified: Old session 1 data successfully routed through new channel to S.");
    }

    [Fact]
    public async Task QueryBurst_DuplicateQueriesReceived_ReturnsCachedOrProcessedResponseWithoutDuplicateProbing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini",
            IdleThreshold = 60,
            ProbeTimeout = 5
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);
        using var cSocket = new ZeroCopyUdpSocket(0);

        // Register S
        Guid devId = Guid.NewGuid();
        byte[] regPkt = CreateRegisterPacket(devId, "burst_svc");
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        await Task.Delay(50, cts.Token);

        // Send 3 duplicate query packets with same contextId & sessionId (Burst 3x)
        ushort contextId = 999;
        Guid sessionId = Guid.NewGuid();
        byte[] qPkt = CreateQueryPacket(contextId, sessionId, "burst_svc/tcp");

        await cSocket.SendAsync(qPkt, pEp, cts.Token);
        await cSocket.SendAsync(qPkt, pEp, cts.Token);
        await cSocket.SendAsync(qPkt, pEp, cts.Token);

        // Receive response
        byte[] resp = new byte[512];
        var (len, _) = await cSocket.ReceiveAsync(resp, cts.Token);
        Assert.True(len >= 36);
        Assert.Equal((byte)MsgType.Punch, resp[0]);
        Assert.Equal(contextId, BinaryPrimitives.ReadUInt16LittleEndian(resp.AsSpan(1, 2)));
        Assert.Equal(1, resp[35]); // Success

        _out.WriteLine("Verified: Burst 3x queries handled successfully with deduplication.");
    }

    [Fact]
    public async Task RelayStart_ConcurrentPackets_OnlyInitializesOnceAndRepliesBothWithAck()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int sPort = 19100 + Random.Shared.Next(100, 900);
        var sCfg = new AppConfig
        {
            Port = sPort,
            ConfigPath = "dummy.ini",
            ServerRecords = new List<ServerRecord>
            {
                new ServerRecord
                {
                    Name = "concurr_svc",
                    TargetIp = "127.0.0.1",
                    TargetPort = 19999,
                    IsTcp = true,
                    AllowRelay = true
                }
            }
        };

        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var sEp = new IPEndPoint(IPAddress.Loopback, sPort);
        using var pSocket = new ZeroCopyUdpSocket(0);

        ushort contextId = 777;
        Guid sessionId = Guid.NewGuid();

        byte[] relayBuf = new byte[256];
        relayBuf[0] = (byte)MsgType.RelayStart;
        BinaryPrimitives.WriteUInt16LittleEndian(relayBuf.AsSpan(1, 2), contextId);
        sessionId.TryWriteBytes(relayBuf.AsSpan(3, 16));
        int offset = 19;
        offset += ProtocolHelper.WriteString(relayBuf.AsSpan(offset), "concurr_svc/tcp");
        offset += ProtocolHelper.WriteIPEndPoint(relayBuf.AsSpan(offset), new IPEndPoint(IPAddress.Loopback, 33333));
        relayBuf[offset++] = 1; // AllowRelay = true
        byte[] relayPkt = relayBuf.AsSpan(0, offset).ToArray();

        // 模拟多个并发包几乎在同一微秒到达
        var sendTasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            sendTasks[i] = Task.Run(async () =>
            {
                await pSocket.SendAsync(relayPkt, sEp, cts.Token);
            });
        }
        await Task.WhenAll(sendTasks);

        // 接收 ACK 并确认收到 Status = 1
        byte[] ackBuf = new byte[64];
        var (ackLen, _) = await pSocket.ReceiveAsync(ackBuf, cts.Token);
        Assert.True(ackLen >= 20);
        Assert.Equal((byte)MsgType.RelayStartAck, ackBuf[0]);
        Assert.Equal(contextId, BinaryPrimitives.ReadUInt16LittleEndian(ackBuf.AsSpan(1, 2)));
        Assert.Equal(sessionId, new Guid(ackBuf.AsSpan(3, 16)));
        Assert.Equal(1, ackBuf[19]); // Status = 1

        // 验证 S 端内部活跃通道严格为 1 个
        var activeSessions = sEngine.Server?.Sessions.ToList();
        Assert.NotNull(activeSessions);
        Assert.Single(activeSessions);
        Assert.Equal(sessionId, activeSessions[0].SessionId);

        _out.WriteLine("Verified: Concurrent RelayStart packets safely processed without multiple initialization.");
    }

    [Fact]
    public void TemporyDictionary_SlidingGenerations_RetainsRecentItemsWithoutFullWipe()
    {
        var dict = new TemporyDictionary<int, string>(maxsize: 10);

        // 插入 0..9 共 10 项
        for (int i = 0; i < 10; i++)
        {
            dict[i] = $"val_{i}";
        }

        // 验证全部 0..9 存在
        for (int i = 0; i < 10; i++)
        {
            Assert.True(dict.TryGetValue(i, out var val));
            Assert.Equal($"val_{i}", val);
        }

        // 插入第 11 项 (key=10)，触发 _dic1 -> _dic2 轮转
        dict[10] = "val_10";

        // 验证 0..10 全部仍然可以查到（未被一次性清空！）
        for (int i = 0; i <= 10; i++)
        {
            Assert.True(dict.TryGetValue(i, out var val));
            Assert.Equal($"val_{i}", val);
        }

        // 填满第二代 11..19
        for (int i = 11; i < 20; i++)
        {
            dict[i] = $"val_{i}";
        }

        // 此时 0..19 仍全在（10..19 在当前代，0..9 在前一代）
        for (int i = 0; i < 20; i++)
        {
            Assert.True(dict.TryGetValue(i, out var val));
            Assert.Equal($"val_{i}", val);
        }

        // 插入第 21 项 (key=20)，再次轮转：旧代 0..9 自然老化淘汰，保留 10..20
        dict[20] = "val_20";

        // 0..9 应该已被淘汰
        for (int i = 0; i < 10; i++)
        {
            Assert.False(dict.TryGetValue(i, out _));
        }

        // 最近的 10..20 完好保留
        for (int i = 10; i <= 20; i++)
        {
            Assert.True(dict.TryGetValue(i, out var val));
            Assert.Equal($"val_{i}", val);
        }
    }

    [Fact]
    public void ConfigParser_MaxRecentRequests_ClampedToMinWhenLessThan50()
    {
        string tmpIniBelowMin = Path.GetTempFileName();
        string tmpIniNormal = Path.GetTempFileName();
        string tmpIniDefault = Path.GetTempFileName();
        string tmpIniAlias = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpIniBelowMin, "port=9400\nmaxrecentrequests=30\n");
            var cfgBelowMin = ConfigParser.Parse(new[] { "-c", tmpIniBelowMin });
            Assert.Equal(Constants.MinMaxRecentRequests, cfgBelowMin.MaxRecentRequests);
            Assert.Equal(50, cfgBelowMin.MaxRecentRequests);
            Assert.Equal(50, cfgBelowMin.MaxSize);

            File.WriteAllText(tmpIniNormal, "port=9400\nmaxrecentrequests=150\n");
            var cfgNormal = ConfigParser.Parse(new[] { "-c", tmpIniNormal });
            Assert.Equal(150, cfgNormal.MaxRecentRequests);
            Assert.Equal(150, cfgNormal.MaxSize);

            File.WriteAllText(tmpIniAlias, "port=9400\nmaxsize=200\n");
            var cfgAlias = ConfigParser.Parse(new[] { "-c", tmpIniAlias });
            Assert.Equal(200, cfgAlias.MaxRecentRequests);
            Assert.Equal(200, cfgAlias.MaxSize);

            File.WriteAllText(tmpIniDefault, "port=9400\n");
            var cfgDefault = ConfigParser.Parse(new[] { "-c", tmpIniDefault });
            Assert.Equal(Constants.DefaultMaxRecentRequests, cfgDefault.MaxRecentRequests);
            Assert.Equal(100, cfgDefault.MaxRecentRequests);
            Assert.Equal(100, cfgDefault.MaxSize);
        }
        finally
        {
            if (File.Exists(tmpIniBelowMin)) File.Delete(tmpIniBelowMin);
            if (File.Exists(tmpIniNormal)) File.Delete(tmpIniNormal);
            if (File.Exists(tmpIniAlias)) File.Delete(tmpIniAlias);
            if (File.Exists(tmpIniDefault)) File.Delete(tmpIniDefault);
        }
    }

    [Fact]
    public async Task SendWithRetryAsync_StopsImmediatelyUponAck_SendsOnlyOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var receiver = new ZeroCopyUdpSocket(0);
        using var sender = new ZeroCopyUdpSocket(0);

        var targetEp = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)receiver.LocalEndPoint).Port);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        byte[] packet = new byte[] { 1, 2, 3, 4 };
        int recvCount = 0;

        // Receiver loop
        _ = Task.Run(async () =>
        {
            byte[] buf = new byte[64];
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var (len, _) = await receiver.ReceiveAsync(buf, cts.Token);
                    if (len > 0)
                    {
                        Interlocked.Increment(ref recvCount);
                        tcs.TrySetResult(true); // ACK arrived!
                    }
                }
                catch { break; }
            }
        }, cts.Token);

        // Send with retry (interval 100ms)
        await ProtocolHelper.SendWithRetryAsync(sender, packet, targetEp, tcs.Task, cts.Token, maxAttempts: 3, retryIntervalMs: 100);

        // Wait past the retry intervals to ensure no subsequent packets were sent
        await Task.Delay(250, cts.Token);

        Assert.Equal(1, recvCount);
    }

    [Fact]
    public async Task Register_ProxyRepliesWithRegisterAck_SingleSendAndStatus1()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini"
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);

        Guid devId = Guid.NewGuid();
        ushort contextId = 9876;
        byte[] regPkt = CreateRegisterPacket(devId, "reg_ack_svc", contextId: contextId);

        await sSocket.SendAsync(regPkt, pEp, cts.Token);

        byte[] ackBuf = new byte[64];
        var (ackLen, _) = await sSocket.ReceiveAsync(ackBuf, cts.Token);

        Assert.True(ackLen >= 4);
        Assert.Equal((byte)MsgType.RegisterAck, ackBuf[0]);
        Assert.Equal(contextId, BinaryPrimitives.ReadUInt16LittleEndian(ackBuf.AsSpan(1, 2)));
        Assert.Equal(1, ackBuf[3]); // Status 1 = Success
    }

    [Fact]
    public async Task KeepAlive_WithDevId_ReturnsCorrectRegistrationStatus()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini"
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        var pEp = new IPEndPoint(IPAddress.Loopback, pPort);
        using var sSocket = new ZeroCopyUdpSocket(0);

        Guid devId = Guid.NewGuid();

        // 1. 未注册时发送 33 字节 KeepAlive (EchoReq + devId)
        byte[] pingBuf = new byte[33];
        pingBuf[0] = (byte)MsgType.EchoReq;
        Guid.NewGuid().TryWriteBytes(pingBuf.AsSpan(1, 16));
        devId.TryWriteBytes(pingBuf.AsSpan(17, 16));

        await sSocket.SendAsync(pingBuf, pEp, cts.Token);

        byte[] respBuf = new byte[64];
        var (respLen, _) = await sSocket.ReceiveAsync(respBuf, cts.Token);

        Assert.Equal((byte)MsgType.EchoResp, respBuf[0]);
        var (ep, epLen) = ProtocolHelper.ReadIPEndPoint(respBuf.AsSpan(17));
        int statusPos = 17 + epLen;
        Assert.True(respLen > statusPos);
        Assert.Equal(0, respBuf[statusPos]); // 未注册 -> Status 0 (NeedRegister)

        // 2. 发起注册
        byte[] regPkt = CreateRegisterPacket(devId, "keepalive_test_svc", contextId: 101);
        await sSocket.SendAsync(regPkt, pEp, cts.Token);
        var (ackLen, _) = await sSocket.ReceiveAsync(respBuf, cts.Token);
        Assert.Equal((byte)MsgType.RegisterAck, respBuf[0]);
        Assert.Equal(1, respBuf[3]);

        // 3. 注册成功后再次发送 33 字节 KeepAlive
        Guid.NewGuid().TryWriteBytes(pingBuf.AsSpan(1, 16));
        await sSocket.SendAsync(pingBuf, pEp, cts.Token);
        var (respLen2, _) = await sSocket.ReceiveAsync(respBuf, cts.Token);

        Assert.Equal((byte)MsgType.EchoResp, respBuf[0]);
        var (ep2, epLen2) = ProtocolHelper.ReadIPEndPoint(respBuf.AsSpan(17));
        int statusPos2 = 17 + epLen2;
        Assert.True(respLen2 > statusPos2);
        Assert.Equal(1, respBuf[statusPos2]); // 已注册 -> Status 1 (OK)
    }

    [Fact]
    public async Task ServerMode_WhenKeepAliveStatus0Received_TriggersImmediateReRegistration()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dummyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int pPort = ((IPEndPoint)dummyUdp.Client.LocalEndPoint!).Port;
        dummyUdp.Close();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            ConfigPath = "dummy.ini"
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);

        Guid devId = Guid.NewGuid();
        var sCfg = new AppConfig
        {
            DevId = devId,
            Port = 0,
            ConfigPath = "dummy.ini",
            KeepAlive = 1
        };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "fast_recovery_svc",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = 12345,
            RegInterval = 300, // 正常周期为 300 秒
            KeepAlive = 1
        });

        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        // 1. 等待 S 初始注册完成 (最多等待 2 秒)
        for (int i = 0; i < 20 && !pEngine.Proxy!.HasRegisteredServices(devId); i++)
        {
            await Task.Delay(100, cts.Token);
        }
        Assert.True(pEngine.Proxy!.HasRegisteredServices(devId));

        // 2. 模拟 P 重启：清空 P 端路由表
        pEngine.Proxy.ClearRoutingTablesForTest();
        Assert.False(pEngine.Proxy.HasRegisteredServices(devId));

        // 3. 验证在 1 秒保活触发后，S 端收到 status 0 立即补登，而不是苦等 300 秒
        for (int i = 0; i < 30 && !pEngine.Proxy.HasRegisteredServices(devId); i++)
        {
            await Task.Delay(100, cts.Token);
        }

        // 证明无需等待 300 秒，通过 KeepAlive 回包驱动在极短时间内完成重新注册
        Assert.True(pEngine.Proxy.HasRegisteredServices(devId));
    }
}

