using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace UDRoute.Tests
{
    public class RemoteControlTests : IDisposable
    {
        private readonly string _testDir;

        public RemoteControlTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "udroute_ctrl_test_" + Guid.NewGuid().ToString("N"));
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
        public async Task RemoteControl_Disabled_WhenNoControllerPassword()
        {
            int ctrlPort = GetFreeTcpPort();
            var cfg = new AppConfig
            {
                Port = 0,
                ControllerPort = ctrlPort,
                ControllerPassword = null // Not set
            };

            using var cts = new CancellationTokenSource();
            using var engine = new RouteEngine(cfg);
            _ = engine.StartAsync(cts.Token);
            await Task.Delay(100);

            // TCP Port should not be opened
            using var tcp = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, ctrlPort));

            cts.Cancel();
        }

        [Fact]
        public async Task RemoteControl_AddDeleteList_MultipleEndpoints_OverTcp()
        {
            int ctrlPort = GetFreeTcpPort();
            int cPort1 = GetFreeTcpPort();
            int cPort2 = GetFreeTcpPort();

            string plainPass = "adminCtrl123";
            byte[] pwdHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(plainPass));

            var cfg = new AppConfig
            {
                Port = 0,
                ControllerPort = ctrlPort,
                ControllerPassword = pwdHash
            };

            using var cts = new CancellationTokenSource();
            using var engine = new RouteEngine(cfg);
            _ = engine.StartAsync(cts.Token);
            await Task.Delay(150);

            // 1. Initial List -> 0 endpoints
            var (listOk1, listMsg1) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.List, new List<string>(), plainPass);
            Assert.True(listOk1);
            Assert.Contains("(Total: 0)", listMsg1);

            // 2. Add multiple C-endpoints at once
            var addPayloads = new List<string>
            {
                $"{cPort1}=xeno1@www.qzsoft.top",
                $"{cPort2}=xeno2@www.qzsoft.top"
            };
            var (addOk, addMsg) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.Add, addPayloads, plainPass);
            Assert.True(addOk, addMsg);
            Assert.Contains(cPort1.ToString(), addMsg);
            Assert.Contains(cPort2.ToString(), addMsg);

            // Verify both TCP listeners are running
            using (var tcp1 = new TcpClient())
            {
                await tcp1.ConnectAsync(IPAddress.Loopback, cPort1);
                Assert.True(tcp1.Connected);
            }
            using (var tcp2 = new TcpClient())
            {
                await tcp2.ConnectAsync(IPAddress.Loopback, cPort2);
                Assert.True(tcp2.Connected);
            }

            // 3. Add S-endpoint
            var addServerPayloads = new List<string> { "web=127.0.0.1:8080/tcp@www.qzsoft.top" };
            var (addSOk, addSMsg) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.Add, addServerPayloads, plainPass);
            Assert.True(addSOk, addSMsg);
            Assert.Contains("web", addSMsg);

            // 4. List -> should show 2 client and 1 server endpoints
            var (listOk2, listMsg2) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.List, new List<string>(), plainPass);
            Assert.True(listOk2);
            Assert.Contains("(Total: 2)", listMsg2);
            Assert.Contains(cPort1.ToString(), listMsg2);
            Assert.Contains(cPort2.ToString(), listMsg2);
            Assert.Contains("web", listMsg2);

            // 5. Delete multiple endpoints in one command (cPort1 and cPort2 and web)
            var delPayloads = new List<string> { cPort1.ToString(), cPort2.ToString(), "web" };
            var (delOk, delMsg) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.Delete, delPayloads, plainPass);
            Assert.True(delOk, delMsg);

            // Verify listeners are stopped
            await Task.Delay(150);
            using (var tcp1 = new TcpClient())
            {
                await Assert.ThrowsAnyAsync<SocketException>(() => tcp1.ConnectAsync(IPAddress.Loopback, cPort1));
            }
            using (var tcp2 = new TcpClient())
            {
                await Assert.ThrowsAnyAsync<SocketException>(() => tcp2.ConnectAsync(IPAddress.Loopback, cPort2));
            }

            // 6. List -> 0 endpoints
            var (listOk3, listMsg3) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.List, new List<string>(), plainPass);
            Assert.True(listOk3);
            Assert.Contains("(Total: 0)", listMsg3);

            cts.Cancel();
        }

        [Fact]
        public async Task RemoteControl_WrongPassword_Rejected()
        {
            int ctrlPort = GetFreeTcpPort();
            string correctPass = "SecureKey123";
            byte[] pwdHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(correctPass));

            var cfg = new AppConfig
            {
                Port = 0,
                ControllerPort = ctrlPort,
                ControllerPassword = pwdHash
            };

            using var cts = new CancellationTokenSource();
            using var engine = new RouteEngine(cfg);
            _ = engine.StartAsync(cts.Token);
            await Task.Delay(150);

            // 1. Wrong password -> should fail
            var (fail1, msg1) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.List, new List<string>(), "wrongPassword");
            Assert.False(fail1);
            Assert.Contains("认证失败", msg1);

            // 2. Correct password -> should succeed
            var (ok1, msg2) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.List, new List<string>(), correctPass);
            Assert.True(ok1);
            Assert.Contains("Client Endpoints", msg2);

            cts.Cancel();
        }

        [Fact]
        public async Task RemoteControl_IniPersistence_HashesPlainPassword_And_UpdatesConfigFile()
        {
            string iniPath = Path.Combine(_testDir, "udroute.ini");
            int ctrlPort = GetFreeTcpPort();
            int freeUdpPort = GetFreeUdpPort();

            File.WriteAllText(iniPath, $@"Port={freeUdpPort}
ControllerPort={ctrlPort}
ControllerPassword=MyPlainSecret999

[existing_srv]
target=127.0.0.1:80/tcp
server=p.com
");

            var cfg = ConfigParser.Parse(new[] { "-c", iniPath });
            Assert.NotNull(cfg);
            Assert.NotNull(cfg.ControllerPassword);

            // Verify ini was auto-updated with _HASH256_
            string iniAfterParse = File.ReadAllText(iniPath);
            Assert.Contains("ControllerPassword=_HASH256_", iniAfterParse);
            Assert.DoesNotContain("MyPlainSecret999", iniAfterParse);

            using var cts = new CancellationTokenSource();
            using var engine = new RouteEngine(cfg);
            _ = engine.StartAsync(cts.Token);
            await Task.Delay(500);

            int cPort = GetFreeTcpPort();

            // Add endpoint remotely using plain password
            var (addOk, addMsg) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.Add, new List<string> { $"{cPort}=xeno@www.qzsoft.top" }, "MyPlainSecret999");
            Assert.True(addOk, addMsg);

            string iniAfterAdd = File.ReadAllText(iniPath);
            Assert.Contains($"{cPort}=xeno@www.qzsoft.top", iniAfterAdd);

            // Delete endpoint remotely
            var (delOk, _) = await RemoteControlHelper.SendControlCommandAsync("127.0.0.1", ctrlPort, ControlAction.Delete, new List<string> { cPort.ToString() }, "MyPlainSecret999");
            Assert.True(delOk);

            string iniAfterDel = File.ReadAllText(iniPath);
            Assert.DoesNotContain($"{cPort}=xeno@www.qzsoft.top", iniAfterDel);

            cts.Cancel();
        }

        [Fact]
        public async Task RemoteControl_CliRunAsync_EndToEnd()
        {
            int ctrlPort = GetFreeTcpPort();
            int cPort1 = GetFreeTcpPort();
            int cPort2 = GetFreeTcpPort();
            string password = "myCliPassword";

            var cfg = new AppConfig
            {
                Port = 0,
                ControllerPort = ctrlPort,
                ControllerPassword = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(password))
            };

            using var cts = new CancellationTokenSource();
            using var engine = new RouteEngine(cfg);
            _ = engine.StartAsync(cts.Token);
            await Task.Delay(150);

            string hostWithPass = $"127.0.0.1:{ctrlPort}/{password}";

            // CLI -add with multiple endpoints
            Environment.ExitCode = -1;
            await RemoteControlHelper.RunAsync(new[]
            {
                "-add", hostWithPass,
                $"{cPort1}=xeno1@www.qzsoft.top",
                $"{cPort2}=xeno2@www.qzsoft.top"
            });
            Assert.Equal(0, Environment.ExitCode);

            // CLI -list
            Environment.ExitCode = -1;
            await RemoteControlHelper.RunAsync(new[] { "-list", hostWithPass });
            Assert.Equal(0, Environment.ExitCode);

            // CLI -del with multiple identifiers (one port only, one with '=')
            Environment.ExitCode = -1;
            await RemoteControlHelper.RunAsync(new[]
            {
                "-del", hostWithPass,
                cPort1.ToString(),
                $"{cPort2}=xeno2@www.qzsoft.top" // test stripping '=...'
            });
            Assert.Equal(0, Environment.ExitCode);

            cts.Cancel();
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static int GetFreeUdpPort()
        {
            using var udp = new UdpClient(0);
            return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        }
    }
}
