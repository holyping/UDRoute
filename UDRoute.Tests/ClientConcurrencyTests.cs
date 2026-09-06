using System.Net;
using System.Net.Sockets;
using UDRoute;
using Xunit;

namespace UDRoute.Tests;

public class ClientConcurrencyTests
{
    [Fact]
    public async Task AcceptTcpLoopAsync_AcceptsMultipleClientsImmediately_WithoutBlockingOnHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        using var dummy = new TcpListener(IPAddress.Loopback, 0);
        dummy.Start();
        int localPort = ((IPEndPoint)dummy.LocalEndpoint).Port;
        dummy.Stop();

        var cfg = new AppConfig();
        cfg.ClientRecords.Add(new ClientRecord
        {
            Port = localPort,
            TargetName = "test_service",
            TargetServer = "127.0.0.1",
            IsTcp = true
        });

        using var udp = new ZeroCopyUdpSocket(0);
        var clientMode = new ClientMode(cfg, udp, null);

        var runTask = clientMode.RunAsync(cts.Token);

        await Task.Delay(200, cts.Token);

        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, localPort, cts.Token);
        Assert.True(client1.Connected);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, localPort, cts.Token);
        sw.Stop();

        Assert.True(client2.Connected);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Client 2 took too long: {sw.ElapsedMilliseconds}ms");

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }
}
