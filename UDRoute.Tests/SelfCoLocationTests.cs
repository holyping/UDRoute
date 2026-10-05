using System;
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

namespace UDRoute.Tests
{
    public class SelfCoLocationTests
    {
        private readonly ITestOutputHelper _out;

        public SelfCoLocationTests(ITestOutputHelper output)
        {
            _out = output;
            Log.SetLogger(new TestLogger(_out));
            Log.Level = LogLevel.Info;
        }

        private static int GetFreeProxyPort()
        {
            while (true)
            {
                using var c1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int p = ((IPEndPoint)c1.Client.LocalEndPoint!).Port;
                c1.Close();
                try
                {
                    using var c2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, p + 1));
                    c2.Close();
                    return p;
                }
                catch { }
            }
        }

        private static int GetFreePort(params int[] excluded)
        {
            while (true)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (excluded == null || !excluded.Contains(port))
                {
                    return port;
                }
            }
        }

        private static int GetFreeUdpPort(params int[] excluded)
        {
            while (true)
            {
                using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                client.Close();
                if (excluded == null || !excluded.Contains(port))
                {
                    return port;
                }
            }
        }

        [Fact]
        public async Task PS_CoLocated_TcpEcho_DataIntegrity()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: P and S co-located (rec.IsThis = true)
            int pPort = GetFreeProxyPort();
            var psCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "ps_dummy.ini"
            };

            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            psCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "ps_echo_tcp",
                TargetServer = "this",
                IsThis = true,
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                KcpConfig = sKcp
            });

            var psEngine = new RouteEngine(psCfg);
            _ = psEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Client C
            int cPort = GetFreePort(pPort, pPort + 1, echoPort);
            var cCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "c_dummy.ini"
            };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "ps_echo_tcp",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);
            await Task.Delay(1000, cts.Token);

            // 4. Test client connects to C and transfers 128KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[128 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            psEngine.Dispose();
            cEngine.Dispose();
        }

        [Fact]
        public async Task PS_CoLocated_UdpEcho_DataIntegrity()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend UDP Echo Server
            using var udpEchoListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int echoPort = ((IPEndPoint)udpEchoListener.Client.LocalEndPoint!).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udpEchoListener.ReceiveAsync(cts.Token);
                        await udpEchoListener.SendAsync(res.Buffer, res.Buffer.Length, res.RemoteEndPoint);
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: P and S co-located (rec.IsThis = true)
            int pPort = GetFreeProxyPort();
            var psCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "ps_udp_dummy.ini"
            };

            psCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "ps_echo_udp",
                TargetServer = "this",
                IsThis = true,
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = false
            });

            var psEngine = new RouteEngine(psCfg);
            _ = psEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Client C
            int cPort = GetFreeUdpPort(pPort, pPort + 1, echoPort);
            var cCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "c_udp_dummy.ini"
            };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "ps_echo_udp",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = false
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);
            await Task.Delay(1000, cts.Token);

            // 4. Test UDP client sends packets to C and receives echo
            using var testUdp = new UdpClient();
            var cEp = new IPEndPoint(IPAddress.Loopback, cPort);

            for (int i = 0; i < 5; i++)
            {
                byte[] sendPayload = new byte[256 + i * 100];
                Random.Shared.NextBytes(sendPayload);

                bool received = false;
                for (int attempt = 0; attempt < 5 && !received; attempt++)
                {
                    await testUdp.SendAsync(sendPayload, sendPayload.Length, cEp);
                    using var timeoutCts = new CancellationTokenSource(2000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
                    try
                    {
                        var res = await testUdp.ReceiveAsync(linked.Token);
                        Assert.Equal(sendPayload, res.Buffer);
                        received = true;
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        // Retry sending
                    }
                }
                Assert.True(received, $"Packet {i} echo response was not received.");
            }

            psEngine.Dispose();
            cEngine.Dispose();
        }

        [Fact]
        public async Task CP_CoLocated_TcpEcho_DataIntegrity()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: C and P co-located (rec.IsThis = true)
            int pPort = GetFreeProxyPort();
            int cPort = GetFreePort(pPort, pPort + 1, echoPort);
            var cpCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "cp_dummy.ini"
            };
            cpCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "cp_echo_tcp",
                TargetServer = "this",
                IsThis = true,
                IsTcp = true
            });

            var cpEngine = new RouteEngine(cpCfg);
            _ = cpEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Server S registering with Engine 1 (P)
            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            var sCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "s_dummy.ini"
            };
            sCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "cp_echo_tcp",
                TargetServer = $"127.0.0.1:{pPort}",
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                KcpConfig = sKcp
            });

            var sEngine = new RouteEngine(sCfg);
            _ = sEngine.StartAsync(cts.Token);
            await Task.Delay(1500, cts.Token);

            // 4. Test client connects to C and transfers 128KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[128 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            cpEngine.Dispose();
            sEngine.Dispose();
        }

        [Fact]
        public async Task CP_CoLocated_UdpEcho_DataIntegrity()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend UDP Echo Server
            using var udpEchoListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int echoPort = ((IPEndPoint)udpEchoListener.Client.LocalEndPoint!).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udpEchoListener.ReceiveAsync(cts.Token);
                        await udpEchoListener.SendAsync(res.Buffer, res.Buffer.Length, res.RemoteEndPoint);
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: C and P co-located (rec.IsThis = true)
            int pPort = GetFreeProxyPort();
            int cPort = GetFreeUdpPort(pPort, pPort + 1, echoPort);
            var cpCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "cp_udp_dummy.ini"
            };
            cpCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "cp_echo_udp",
                TargetServer = "this",
                IsThis = true,
                IsTcp = false
            });

            var cpEngine = new RouteEngine(cpCfg);
            _ = cpEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Server S registering with Engine 1 (P)
            var sCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "s_udp_dummy.ini"
            };
            sCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "cp_echo_udp",
                TargetServer = $"127.0.0.1:{pPort}",
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = false
            });

            var sEngine = new RouteEngine(sCfg);
            _ = sEngine.StartAsync(cts.Token);
            await Task.Delay(1500, cts.Token);

            // 4. Test UDP client sends packets to C and receives echo
            using var testUdp = new UdpClient();
            var cEp = new IPEndPoint(IPAddress.Loopback, cPort);

            for (int i = 0; i < 5; i++)
            {
                byte[] sendPayload = new byte[256 + i * 100];
                Random.Shared.NextBytes(sendPayload);

                bool received = false;
                for (int attempt = 0; attempt < 5 && !received; attempt++)
                {
                    await testUdp.SendAsync(sendPayload, sendPayload.Length, cEp);
                    using var timeoutCts = new CancellationTokenSource(2000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
                    try
                    {
                        var res = await testUdp.ReceiveAsync(linked.Token);
                        Assert.Equal(sendPayload, res.Buffer);
                        received = true;
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        // Retry sending
                    }
                }
                Assert.True(received, $"Packet {i} echo response was not received.");
            }

            cpEngine.Dispose();
            sEngine.Dispose();
        }

        [Fact]
        public async Task PS_CoLocated_TcpEcho_WithPassword_Success()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            byte[] passHash = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes("secret123"));

            // 2. Engine 1: P and S co-located (rec.IsThis = true, Password Protected)
            int pPort = GetFreeProxyPort();
            var psCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "ps_pass_dummy.ini"
            };

            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            psCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "ps_echo_pass",
                TargetServer = "this",
                IsThis = true,
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                KcpConfig = sKcp,
                AccessPassword = passHash,
                Password = passHash
            });

            var psEngine = new RouteEngine(psCfg);
            _ = psEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Client C with matching Password
            int cPort = GetFreePort(pPort, pPort + 1, echoPort);
            var cCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "c_pass_dummy.ini"
            };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "ps_echo_pass",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true,
                Password = passHash
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);
            await Task.Delay(1000, cts.Token);

            // 4. Test client connects to C and transfers 64KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[64 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            psEngine.Dispose();
            cEngine.Dispose();
        }

        [Fact]
        public async Task CP_CoLocated_TcpEcho_ForceRelay_Success()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: C and P co-located (rec.IsThis = true, ForceRelay = true)
            int pPort = GetFreeProxyPort();
            int cPort = GetFreePort(pPort, pPort + 1, echoPort);
            var cpCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "cp_force_dummy.ini",
                ForceRelay = true
            };
            cpCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "cp_force_tcp",
                TargetServer = "this",
                IsThis = true,
                IsTcp = true,
                ForceRelay = true
            });

            var cpEngine = new RouteEngine(cpCfg);
            _ = cpEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            // 3. Engine 2: Server S registering with Engine 1 (P)
            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            var sCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = 0,
                ConfigPath = "s_force_dummy.ini",
                ForceRelay = true
            };
            sCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "cp_force_tcp",
                TargetServer = $"127.0.0.1:{pPort}",
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                KcpConfig = sKcp,
                AllowRelay = true
            });

            var sEngine = new RouteEngine(sCfg);
            _ = sEngine.StartAsync(cts.Token);
            await Task.Delay(1500, cts.Token);

            // 4. Test client connects to C and transfers 64KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[64 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            cpEngine.Dispose();
            sEngine.Dispose();
        }

        [Fact]
        public async Task PS_FoolProof_WithoutThis_AutoUpgradesToMemoryDirect_TcpAndUdp()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            // 2. Engine 1: P and S in the same process, but user did NOT configure target=this!
            // Configured TargetServer = "127.0.0.1:{pPort}" and IsThis = false!
            int pPort = GetFreeProxyPort();
            var psCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "ps_foolproof_dummy.ini"
            };

            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            var sRec = new ServerRecord
            {
                Name = "echo-svc",
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                IsThis = false, // NOTE: Not configured as this!
                TargetServer = $"127.0.0.1:{pPort}", // Pointing to local Proxy via IP
                KcpConfig = sKcp
            };
            psCfg.ServerRecords.Add(sRec);

            var psEngine = new RouteEngine(psCfg);
            _ = psEngine.StartAsync(cts.Token);

            // Wait for S to register with P and receive RegisterAck with matching InstanceId
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (!sRec.IsThis || psEngine.Proxy?.DirectQuery("echo-svc/tcp")?.IsLocalServer != true))
            {
                await Task.Delay(50, cts.Token);
            }

            // Assert fool-proof auto-upgrade succeeded!
            Assert.True(sRec.IsThis, "sRec.IsThis should be auto-upgraded to true upon InstanceId match");
            var sInfo = psEngine.Proxy?.DirectQuery("echo-svc/tcp");
            Assert.NotNull(sInfo);
            Assert.True(sInfo!.IsLocalServer, "sInfo.IsLocalServer should be auto-upgraded to true upon InstanceId match");

            // 3. Engine 2: Client on separate node connecting to P
            int cPort = GetFreePort(pPort, pPort + 1, echoPort);
            int cUdpPort = GetFreeUdpPort(pPort, pPort + 1, cPort);
            var cCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = cUdpPort,
                ConfigPath = "c_foolproof_dummy.ini"
            };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                IsTcp = true,
                TargetName = "echo-svc",
                TargetServer = $"127.0.0.1:{pPort}"
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);

            // 4. Test client connects to C and transfers 64KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[64 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            psEngine.Dispose();
            cEngine.Dispose();
        }

        [Fact]
        public async Task CP_FoolProof_WithoutThis_AutoUpgradesToMemoryDirect_TcpAndUdp()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 1. Backend TCP Echo Server
            using var echoListener = new TcpListener(IPAddress.Loopback, 0);
            echoListener.Start();
            int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
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
                    catch { break; }
                }
            }, cts.Token);

            int pPort = GetFreeProxyPort();
            int sUdpPort = GetFreeUdpPort(pPort, pPort + 1);

            // 2. Engine 1: Server on separate node registering with P
            var sCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = sUdpPort,
                ConfigPath = "s_foolproof_dummy.ini"
            };
            var sKcp = new KcpConfig();
            sKcp.SetProfile("api");
            sCfg.ServerRecords.Add(new ServerRecord
            {
                Name = "echo-svc",
                TargetIp = "127.0.0.1",
                TargetPort = echoPort,
                IsTcp = true,
                TargetServer = $"127.0.0.1:{pPort}",
                KcpConfig = sKcp
            });

            var sEngine = new RouteEngine(sCfg);
            _ = sEngine.StartAsync(cts.Token);

            // 3. Engine 2: C and P in the same process, but user did NOT configure target=this!
            // Configured TargetServer = "127.0.0.1:{pPort}" and IsThis = false!
            int cPort = GetFreePort(pPort, pPort + 1, sUdpPort, echoPort);
            var cpCfg = new AppConfig
            {
                DevId = Guid.NewGuid(),
                Port = pPort,
                EnableProxy = true,
                ConfigPath = "cp_foolproof_dummy.ini"
            };
            var cRec = new ClientRecord
            {
                Port = cPort,
                IsTcp = true,
                IsThis = false, // NOTE: Not configured as this!
                TargetName = "echo-svc",
                TargetServer = $"127.0.0.1:{pPort}" // Pointing to local Proxy via IP
            };
            cpCfg.ClientRecords.Add(cRec);

            var cpEngine = new RouteEngine(cpCfg);
            _ = cpEngine.StartAsync(cts.Token);

            // Wait for S to register on P
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && cpEngine.Proxy?.DirectQuery("echo-svc/tcp") == null)
            {
                await Task.Delay(50, cts.Token);
            }

            Assert.NotNull(cpEngine.Proxy?.DirectQuery("echo-svc/tcp"));

            // 4. Test client connects to C and transfers 64KB
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, cPort, cts.Token);
            using var stream = clientTcp.GetStream();

            byte[] sendData = new byte[64 * 1024];
            Random.Shared.NextBytes(sendData);
            byte[] expectedHash = SHA256.HashData(sendData);

            byte[] recvData = new byte[sendData.Length];

            var sendTask = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < sendData.Length)
                {
                    int chunkSize = Math.Min(4096, sendData.Length - sent);
                    await stream.WriteAsync(sendData, sent, chunkSize, cts.Token);
                    sent += chunkSize;
                }
            }, cts.Token);

            var recvTask = Task.Run(async () =>
            {
                int totalRead = 0;
                while (totalRead < recvData.Length)
                {
                    int read = await stream.ReadAsync(recvData, totalRead, recvData.Length - totalRead, cts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return totalRead;
            }, cts.Token);

            await Task.WhenAll(sendTask, recvTask);
            int readBytes = await recvTask;

            Assert.Equal(sendData.Length, readBytes);
            byte[] actualHash = SHA256.HashData(recvData);
            Assert.Equal(expectedHash, actualHash);

            // Assert that cRec was auto-upgraded to IsThis = true!
            Assert.True(cRec.IsThis, "cRec.IsThis should be auto-upgraded to true upon QueryResponse InstanceId match");

            cpEngine.Dispose();
            sEngine.Dispose();
        }
    }
}

