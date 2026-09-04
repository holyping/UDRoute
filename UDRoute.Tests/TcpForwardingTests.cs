using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;

namespace UDRoute.Tests;

public class TcpForwardingTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly CancellationTokenSource _cts = new();

    public TcpForwardingTests(ITestOutputHelper output)
    {
        _out = output;
        Log.SetLogger(new TestLogger(_out));
        Log.Instance.Level = LogLevel.Info;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<(RouteEngine p, RouteEngine s, RouteEngine c, TcpListener echoListener, int cPort)> SetupClusterAsync(bool forceRelay, string profile = "normal")
    {
        // 1. Echo server that accepts multiple connections concurrently
        var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[32768];
                            int read;
                            while ((read = await stream.ReadAsync(buf, 0, buf.Length, _cts.Token)) > 0)
                            {
                                await stream.WriteAsync(buf, 0, read, _cts.Token);
                            }
                        }
                    }, _cts.Token);
                }
            }
            catch { }
        });

        int pPort = GetFreePort();
        int cPort = GetFreePort();

        // 2. Proxy (P)
        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(_cts.Token);

        await Task.Delay(100);

        // 3. Server (S)
        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini", ForceRelay = forceRelay };
        var sKcp = new KcpConfig();
        sKcp.SetProfile(profile);
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "echo_service",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            KcpConfig = sKcp
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(_cts.Token);

        // 4. Client (C)
        var cCfg = new AppConfig { DevId = Guid.NewGuid(), ForceRelay = forceRelay, Port = 0, ConfigPath = "dummy.ini" };
        cCfg.ClientRecords.Add(new ClientRecord
        {
            Port = cPort,
            TargetName = "echo_service",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            ForceRelay = forceRelay
        });
        var cEngine = new RouteEngine(cCfg);
        _ = cEngine.StartAsync(_cts.Token);

        // Wait for S to register to P and C to be ready
        await Task.Delay(1500, _cts.Token);

        return (pEngine, sEngine, cEngine, echoListener, cPort);
    }

    [Theory]
    [InlineData(true)]  // P 中继转发模式 (ForceRelay = true)
    [InlineData(false)] // P2P 直连打洞模式 (ForceRelay = false)
    public async Task Concurrency_MultipleClients_DataIntegrity(bool forceRelay)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, testCts.Token);
        var ct = linkedCts.Token;

        var cluster = await SetupClusterAsync(forceRelay, "normal");
        try
        {
            const int clientCount = 10;
            const int dataSizePerClient = 64 * 1024; // 64 KB per client
            var tasks = new List<Task>();

            for (int i = 0; i < clientCount; i++)
            {
                int clientId = i;
                tasks.Add(Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, cluster.cPort, ct);
                    using var stream = client.GetStream();

                    byte[] sendData = new byte[dataSizePerClient];
                    RandomNumberGenerator.Fill(sendData);
                    byte[] recvData = new byte[dataSizePerClient];

                    var readTask = Task.Run(async () =>
                    {
                        int total = 0;
                        while (total < recvData.Length)
                        {
                            int r = await stream.ReadAsync(recvData, total, recvData.Length - total, ct);
                            if (r == 0) break;
                            total += r;
                        }
                        return total;
                    }, ct);

                    // Send in chunks
                    int offset = 0;
                    int chunk = 4096;
                    while (offset < sendData.Length)
                    {
                        int len = Math.Min(chunk, sendData.Length - offset);
                        await stream.WriteAsync(sendData, offset, len, ct);
                        offset += len;
                    }

                    int totalRead = await readTask;
                    Assert.Equal(dataSizePerClient, totalRead);
                    Assert.True(sendData.SequenceEqual(recvData), $"Client {clientId} data mismatch!");
                }, ct));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            cluster.echoListener.Stop();
        }
    }

    [Theory]
    [InlineData(true)]  // P 中继转发
    [InlineData(false)] // P2P 直连
    public async Task ConnectionChurn_RepeatedConnectAndDisconnect(bool forceRelay)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, testCts.Token);
        var ct = linkedCts.Token;

        var cluster = await SetupClusterAsync(forceRelay, "normal");
        try
        {
            const int iterations = 20;
            for (int i = 0; i < iterations; i++)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, cluster.cPort, ct);
                using var stream = client.GetStream();

                byte[] sendData = new byte[8192];
                RandomNumberGenerator.Fill(sendData);
                byte[] recvData = new byte[sendData.Length];

                var readTask = Task.Run(async () =>
                {
                    int total = 0;
                    while (total < recvData.Length)
                    {
                        int r = await stream.ReadAsync(recvData, total, recvData.Length - total, ct);
                        if (r == 0) break;
                        total += r;
                    }
                    return total;
                }, ct);

                await stream.WriteAsync(sendData, 0, sendData.Length, ct);

                int totalRead = await readTask;
                Assert.Equal(sendData.Length, totalRead);
                Assert.True(sendData.SequenceEqual(recvData), $"Iteration {i} data mismatch!");

                client.Close();
                await Task.Delay(20, ct);
            }
        }
        finally
        {
            cluster.echoListener.Stop();
        }
    }

    [Theory]
    [InlineData(true)]  // P 中继转发大数据包 (5MB)
    [InlineData(false)] // P2P 直连大数据包 (5MB)
    public async Task LargePayload_5MB_DataIntegrity(bool forceRelay)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, testCts.Token);
        var ct = linkedCts.Token;

        var cluster = await SetupClusterAsync(forceRelay, "normal");
        try
        {
            const int totalBytes = 5 * 1024 * 1024; // 5 MB
            byte[] sendData = new byte[totalBytes];
            RandomNumberGenerator.Fill(sendData);
            byte[] sendHash = SHA256.HashData(sendData);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, cluster.cPort, ct);
            using var stream = client.GetStream();

            byte[] recvData = new byte[totalBytes];
            var readTask = Task.Run(async () =>
            {
                int total = 0;
                while (total < totalBytes)
                {
                    int r = await stream.ReadAsync(recvData, total, totalBytes - total, ct);
                    if (r == 0) break;
                    total += r;
                }
                return total;
            }, ct);

            // Write in 32KB chunks
            int offset = 0;
            int chunk = 32768;
            while (offset < totalBytes)
            {
                int len = Math.Min(chunk, totalBytes - offset);
                await stream.WriteAsync(sendData, offset, len, ct);
                offset += len;
            }

            int totalRead = await readTask;
            Assert.Equal(totalBytes, totalRead);

            byte[] recvHash = SHA256.HashData(recvData);
            Assert.True(sendHash.SequenceEqual(recvHash), "5MB large payload SHA256 mismatch!");
        }
        finally
        {
            cluster.echoListener.Stop();
        }
    }
}

