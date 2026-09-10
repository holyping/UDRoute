using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UDRoute;

namespace UDRoute.Tests;

public class AuthTests : IDisposable
{
    private readonly string _testDir;

    public AuthTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "udroute_test_" + Guid.NewGuid().ToString("N"));
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
    public void DefaultPwdPath_ResolvedCorrectly()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        using var auth = new AuthManager(iniPath);
        // Default should be udroute.pwd
        auth.Start();
        Assert.False(auth.Authenticate("any", "any").success);
    }

    [Fact]
    public void PlaintextAutoHashed_And_AuthVerified()
    {
        string pwdFile = Path.Combine(_testDir, "test.pwd");
        File.WriteAllLines(pwdFile, new[]
        {
            "# Comments and empty lines",
            "",
            "alice : mySecretPass123",
            "bob:hunter2"
        });

        using (var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile))
        {
            auth.Start();

            // Correct auth
            Assert.True(auth.Authenticate("alice", "mySecretPass123").success);
            Assert.True(auth.Authenticate("bob", "hunter2").success);

            // Case-insensitive username check
            Assert.True(auth.Authenticate("ALICE", "mySecretPass123").success);

            // Wrong credentials
            Assert.False(auth.Authenticate("alice", "wrongpwd").success);
            Assert.False(auth.Authenticate("bob", "wrongpwd").success);
            Assert.False(auth.Authenticate("charlie", "hunter2").success);
        }

        // Verify that the file was automatically converted to _HASH256_ on disk
        string[] content = File.ReadAllLines(pwdFile);
        Assert.Contains(content, l => l.StartsWith("alice:_HASH256_"));
        Assert.Contains(content, l => l.StartsWith("bob:_HASH256_"));
        Assert.DoesNotContain(content, l => l.Contains("mySecretPass123"));
    }

    [Fact]
    public void HashedFile_LoadedDirectlyWithoutRehashing()
    {
        string pwdFile = Path.Combine(_testDir, "hashed.pwd");
        string hashB = "_HASH256_" + Convert.ToBase64String(ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes("test1234")));
        File.WriteAllText(pwdFile, $"admin:{hashB}\n");

        using (var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile))
        {
            auth.Start();
            Assert.True(auth.Authenticate("admin", "test1234").success);
            Assert.False(auth.Authenticate("admin", "wrong").success);
        }

        string text = File.ReadAllText(pwdFile);
        Assert.Contains($"admin:{hashB}", text);
    }

    [Fact]
    public async Task Watcher_DynamicReload_UpdatesWithoutRestart()
    {
        string pwdFile = Path.Combine(_testDir, "dynamic.pwd");
        File.WriteAllLines(pwdFile, new[] { "user1:pass1" });

        using var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile);
        auth.Start();

        Assert.True(auth.Authenticate("user1", "pass1").success);
        Assert.False(auth.Authenticate("user2", "pass2").success);

        // Dynamically add user2 to file while running
        await Task.Delay(200);
        File.AppendAllText(pwdFile, "user2:pass2\n");

        // Wait for FileSystemWatcher debounce (500ms in AuthManager + small buffer)
        await Task.Delay(1000);

        Assert.True(auth.Authenticate("user2", "pass2").success, "Dynamic user2 should be authenticated after file change");
    }

    [Fact]
    public void ReadOnlyFile_FallbackGracefullyInMemory()
    {
        string pwdFile = Path.Combine(_testDir, "readonly.pwd");
        File.WriteAllLines(pwdFile, new[] { "alice:secret_plaintext" });
        File.SetAttributes(pwdFile, FileAttributes.ReadOnly);

        try
        {
            using var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile);
            // Should not crash even though it cannot write back to readonly file
            auth.Start();

            // Authentication in memory should still succeed
            Assert.True(auth.Authenticate("alice", "secret_plaintext").success);
        }
        finally
        {
            File.SetAttributes(pwdFile, FileAttributes.Normal);
        }
    }

    // =========================================================================
    // ProxyMode Registration & Auth Level Tests
    // =========================================================================

    private byte[] BuildRegisterPacket(string serviceName, bool isTcp, string devName, Guid devId, string? username, string? password, ushort contextId = 1, bool reqPass = false)
    {
        byte[] buffer = new byte[1024];
        buffer[0] = (byte)MsgType.Register;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), contextId);
        devId.TryWriteBytes(buffer.AsSpan(3, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(19, 4), 0); // WanPort
        buffer[23] = (byte)(isTcp ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), 300); // Timeout
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(28, 8), DateTime.UtcNow.Ticks); // Timestamp
        buffer[36] = (byte)(reqPass ? 1 : 0); // ReqPass
        int offset = 37;
        offset += ProtocolHelper.WriteKcpConfig(buffer.AsSpan(offset), new KcpConfig());
        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), serviceName);
        offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), devName);

        buffer[offset++] = 0; // LocalEps count = 0

        if (username != null || password != null)
        {
            offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), username ?? "");
            offset += ProtocolHelper.WriteString(buffer.AsSpan(offset), password ?? "");
        }

        return buffer.AsSpan(0, offset).ToArray();
    }

    [Fact]
    public void Proxy_StrictMode_RejectsUnauthenticated()
    {
        string pwdFile = Path.Combine(_testDir, "strict.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Strict,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        var packet = BuildRegisterPacket("rdp", true, "client1", Guid.NewGuid(), null, null);
        var remoteEp = new IPEndPoint(IPAddress.Loopback, 50001);

        proxy.ProcessRegister(packet, remoteEp);

        // Should NOT be registered
        Assert.Null(proxy.DirectQuery("rdp/tcp"));
    }

    [Fact]
    public void Proxy_StrictMode_RejectsWrongPassword()
    {
        string pwdFile = Path.Combine(_testDir, "strict2.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Strict,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        var packet = BuildRegisterPacket("rdp", true, "client1", Guid.NewGuid(), "admin", "wrong_password");
        var remoteEp = new IPEndPoint(IPAddress.Loopback, 50001);

        proxy.ProcessRegister(packet, remoteEp);

        // Should NOT be registered
        Assert.Null(proxy.DirectQuery("rdp/tcp"));
    }

    [Fact]
    public void Proxy_StrictMode_AcceptsValidCredentials()
    {
        string pwdFile = Path.Combine(_testDir, "strict3.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Strict,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        var packet = BuildRegisterPacket("rdp", true, "client1", Guid.NewGuid(), "admin", "secret");
        var remoteEp = new IPEndPoint(IPAddress.Loopback, 50001);

        proxy.ProcessRegister(packet, remoteEp);

        // Should be registered
        var rec = proxy.DirectQuery("rdp/tcp");
        Assert.NotNull(rec);
        Assert.True(rec.IsAuthenticated);
        Assert.Equal("admin", rec.OwnerUser);
    }

    [Fact]
    public void Proxy_OptionalMode_AcceptsAnonymous_WhenNoCredentials()
    {
        string pwdFile = Path.Combine(_testDir, "opt1.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Optional,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        // Anonymous (no credentials)
        var packet = BuildRegisterPacket("web", true, "client1", Guid.NewGuid(), null, null);
        var remoteEp = new IPEndPoint(IPAddress.Loopback, 50002);

        proxy.ProcessRegister(packet, remoteEp);

        var rec = proxy.DirectQuery("web/tcp");
        Assert.NotNull(rec);
        Assert.False(rec.IsAuthenticated);
        Assert.Equal("anonymous", rec.OwnerUser);
    }

    [Fact]
    public void Proxy_OptionalMode_RejectsWrongCredentials()
    {
        string pwdFile = Path.Combine(_testDir, "opt2.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Optional,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        // Provided wrong credentials -> MUST BE REJECTED!
        var packet = BuildRegisterPacket("web", true, "client1", Guid.NewGuid(), "admin", "wrong_pass");
        var remoteEp = new IPEndPoint(IPAddress.Loopback, 50002);

        proxy.ProcessRegister(packet, remoteEp);

        Assert.Null(proxy.DirectQuery("web/tcp"));
    }

    [Fact]
    public void Proxy_OptionalMode_AntiOverwriteProtection()
    {
        string pwdFile = Path.Combine(_testDir, "opt3.pwd");
        File.WriteAllLines(pwdFile, new[]
        {
            "alice:alice_pass",
            "bob:bob_pass"
        });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Optional,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        var epAlice = new IPEndPoint(IPAddress.Loopback, 50010);
        var epAnon = new IPEndPoint(IPAddress.Loopback, 50011);
        var epBob = new IPEndPoint(IPAddress.Loopback, 50012);

        // 1. Alice registers "myservice" with valid auth
        var alicePacket = BuildRegisterPacket("myservice", true, "alice_pc", Guid.NewGuid(), "alice", "alice_pass");
        proxy.ProcessRegister(alicePacket, epAlice);

        var rec = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(rec);
        Assert.True(rec.IsAuthenticated);
        Assert.Equal("alice", rec.OwnerUser);
        Assert.Equal(epAlice, rec.PublicEp);

        // 2. Anonymous client tries to overwrite "myservice" -> MUST BE REJECTED
        var anonPacket = BuildRegisterPacket("myservice", true, "anon_pc", Guid.NewGuid(), null, null);
        proxy.ProcessRegister(anonPacket, epAnon);

        rec = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(rec);
        Assert.Equal("alice", rec.OwnerUser); // Still Alice!
        Assert.Equal(epAlice, rec.PublicEp);

        // 3. Bob (another valid user) tries to overwrite Alice's "myservice" -> MUST BE REJECTED
        var bobPacket = BuildRegisterPacket("myservice", true, "bob_pc", Guid.NewGuid(), "bob", "bob_pass");
        proxy.ProcessRegister(bobPacket, epBob);

        rec = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(rec);
        Assert.Equal("alice", rec.OwnerUser); // Still Alice!
        Assert.Equal(epAlice, rec.PublicEp);

        // 4. Alice reconnects from a new IP/port (e.g. dynamic IP change) -> ALLOWED (same user)
        var epAliceNew = new IPEndPoint(IPAddress.Loopback, 50013);
        var aliceNewPacket = BuildRegisterPacket("myservice", true, "alice_pc", Guid.NewGuid(), "alice", "alice_pass");
        proxy.ProcessRegister(aliceNewPacket, epAliceNew);

        rec = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(rec);
        Assert.Equal("alice", rec.OwnerUser);
        Assert.Equal(epAliceNew, rec.PublicEp); // Updated to new endpoint!
    }

    [Fact]
    public void Proxy_OptionalMode_AuthenticatedCanOverwriteAnonymous()
    {
        string pwdFile = Path.Combine(_testDir, "opt4.pwd");
        File.WriteAllLines(pwdFile, new[] { "alice:alice_pass" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Optional,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        var epAnon = new IPEndPoint(IPAddress.Loopback, 50020);
        var epAlice = new IPEndPoint(IPAddress.Loopback, 50021);

        // 1. Anonymous registers "tempservice"
        var anonPacket = BuildRegisterPacket("tempservice", true, "anon_pc", Guid.NewGuid(), null, null);
        proxy.ProcessRegister(anonPacket, epAnon);

        var rec = proxy.DirectQuery("tempservice/tcp");
        Assert.NotNull(rec);
        Assert.False(rec.IsAuthenticated);
        Assert.Equal("anonymous", rec.OwnerUser);

        // 2. Authenticated user (Alice) registers "tempservice" -> ALLOWED to claim resource
        var alicePacket = BuildRegisterPacket("tempservice", true, "alice_pc", Guid.NewGuid(), "alice", "alice_pass");
        proxy.ProcessRegister(alicePacket, epAlice);

        rec = proxy.DirectQuery("tempservice/tcp");
        Assert.NotNull(rec);
        Assert.True(rec.IsAuthenticated);
        Assert.Equal("alice", rec.OwnerUser);
        Assert.Equal(epAlice, rec.PublicEp);
    }

    [Fact]
    public void Proxy_Password_SeparatedFrom_AccessPassword_ReqPass()
    {
        string pwdFile = Path.Combine(_testDir, "sep_auth.pwd");
        File.WriteAllLines(pwdFile, new[] { "admin:secret" });

        var cfg = new AppConfig
        {
            ConfigPath = Path.Combine(_testDir, "test.ini"),
            AuthFile = pwdFile,
            AuthMode = AuthMode.Strict,
            Port = 0
        };

        using var udp = new ZeroCopyUdpSocket(0);
        var proxy = new ProxyMode(cfg, udp);

        // 1. S registers with valid P-Mode credentials (admin:secret), but WITHOUT AccessPassword (reqPass = false)
        var packetNoAccessPass = BuildRegisterPacket("rdp_public", true, "s_node", Guid.NewGuid(), "admin", "secret", reqPass: false);
        var ep1 = new IPEndPoint(IPAddress.Loopback, 50030);
        proxy.ProcessRegister(packetNoAccessPass, ep1);

        var rec1 = proxy.DirectQuery("rdp_public/tcp");
        Assert.NotNull(rec1);
        Assert.True(rec1.IsAuthenticated); // Authenticated on P!
        Assert.False(rec1.RequiresPassword); // Free access for C!

        // 2. S registers with valid P-Mode credentials, WITH AccessPassword (reqPass = true)
        var packetWithAccessPass = BuildRegisterPacket("rdp_private", true, "s_node", Guid.NewGuid(), "admin", "secret", reqPass: true);
        var ep2 = new IPEndPoint(IPAddress.Loopback, 50031);
        proxy.ProcessRegister(packetWithAccessPass, ep2);

        var rec2 = proxy.DirectQuery("rdp_private/tcp");
        Assert.NotNull(rec2);
        Assert.True(rec2.IsAuthenticated); // Authenticated on P!
        Assert.True(rec2.RequiresPassword); // C must provide AccessPassword!
    }

    [Fact]
    public async Task TcpForwarding_WithAccessPassword_BlocksUnauthenticatedClient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1. Setup backend Echo Server
        var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;
        int backendConnectionCount = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                    Interlocked.Increment(ref backendConnectionCount);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buf = new byte[1024];
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
        });

        // 2. Setup Proxy P
        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);

        await Task.Delay(100, cts.Token);

        // 3. Setup Server S with AccessPassword
        string plainAccessPass = "Secret123";
        byte[] accessPassHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(plainAccessPass));

        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "protected_echo",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            AccessPassword = accessPassHash
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token); // Wait for S to register to P

        // 4. Setup Client C WITHOUT password (empty / null Password)
        using var lC1 = new TcpListener(IPAddress.Loopback, 0);
        lC1.Start();
        int cPortNoPass = ((IPEndPoint)lC1.LocalEndpoint).Port;
        lC1.Stop();

        var cCfgNoPass = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        cCfgNoPass.ClientRecords.Add(new ClientRecord
        {
            Port = cPortNoPass,
            TargetName = "protected_echo",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            Password = null // Unauthenticated!
        });
        var cEngineNoPass = new RouteEngine(cCfgNoPass);
        _ = cEngineNoPass.StartAsync(cts.Token);

        await Task.Delay(500, cts.Token);

        // Attempt TCP connection through unauthenticated C
        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync("127.0.0.1", cPortNoPass, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] testMsg = Encoding.UTF8.GetBytes("hello unauthenticated");
            await stream.WriteAsync(testMsg, 0, testMsg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = 0;
            using var readTimeout = new CancellationTokenSource(3000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
            try
            {
                readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, linked.Token);
            }
            catch { }

            // MUST be 0 bytes read (connection terminated without echo)
            Assert.Equal(0, readBytes);
        }

        // Backend MUST NEVER have received a connection!
        Assert.Equal(0, backendConnectionCount);

        // 5. Setup Client C WITH correct password
        using var lC2 = new TcpListener(IPAddress.Loopback, 0);
        lC2.Start();
        int cPortWithPass = ((IPEndPoint)lC2.LocalEndpoint).Port;
        lC2.Stop();

        var cCfgWithPass = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        cCfgWithPass.ClientRecords.Add(new ClientRecord
        {
            Port = cPortWithPass,
            TargetName = "protected_echo",
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            Password = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(plainAccessPass))
        });
        var cEngineWithPass = new RouteEngine(cCfgWithPass);
        _ = cEngineWithPass.StartAsync(cts.Token);

        await Task.Delay(500, cts.Token);

        // Attempt TCP connection through authenticated C
        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync("127.0.0.1", cPortWithPass, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] testMsg = Encoding.UTF8.GetBytes("hello authenticated");
            await stream.WriteAsync(testMsg, 0, testMsg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, cts.Token);

            Assert.True(readBytes > 0);
            string response = Encoding.UTF8.GetString(respBuf, 0, readBytes);
            Assert.Equal("hello authenticated", response);
        }

        // Backend MUST have received exactly 1 connection from authenticated client!
        Assert.Equal(1, backendConnectionCount);

        echoListener.Stop();
    }

    [Fact]
    public void CommandLine_PlainTextAndHash256Password_ParsedCorrectly()
    {
        // 1. Plain text password via CLI
        var cfg1 = new AppConfig();
        ConfigParser.Parse(new[] { "8080=web:plainPass123@1.2.3.4" }, false);
        var rec1 = ConfigParser.Parse(new[] { "8080=web:plainPass123@1.2.3.4" }, false).ClientRecords[0];
        byte[] expectedHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes("plainPass123"));
        Assert.NotNull(rec1.Password);
        Assert.True(rec1.Password.SequenceEqual(expectedHash));

        // 2. Hash256 password via CLI (_HASH256_)
        string b64 = Convert.ToBase64String(expectedHash);
        var rec2 = ConfigParser.Parse(new[] { $"8080=web:_HASH256_{b64}@1.2.3.4" }, false).ClientRecords[0];
        Assert.NotNull(rec2.Password);
        Assert.True(rec2.Password.SequenceEqual(expectedHash));

        // 3. Lowercase _hash256_ via CLI
        var rec3 = ConfigParser.Parse(new[] { $"8080=web:_hash256_{b64}@1.2.3.4" }, false).ClientRecords[0];
        Assert.NotNull(rec3.Password);
        Assert.True(rec3.Password.SequenceEqual(expectedHash));
    }

    [Fact]
    public async Task Client_PromptPassword_WhenNotInConPipeMode_PromptsAndConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string secretPass = "SuperSecretPromptPass";

        // Setup echo backend
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
                            byte[] buf = new byte[1024];
                            int read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                            if (read > 0)
                            {
                                await stream.WriteAsync(buf, 0, read, cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // Setup P
        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);

        await Task.Delay(100, cts.Token);

        // Setup Server S requiring AccessPassword
        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "prompt_echo",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            AccessPassword = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(secretPass))
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // Setup Client C WITHOUT password (null), and IsConsolePipe = false
        using var lC = new TcpListener(IPAddress.Loopback, 0);
        lC.Start();
        int cPort = ((IPEndPoint)lC.LocalEndpoint).Port;
        lC.Stop();

        bool promptWritten = false;
        bool passwordRead = false;
        ClientMode.CustomPasswordPromptWriter = prompt => { promptWritten = true; };
        ClientMode.CustomPasswordReader = () =>
        {
            passwordRead = true;
            return secretPass;
        };

        try
        {
            var cCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "prompt_echo",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true,
                Password = null, // No password specified!
                IsConsolePipe = false // Normal mode
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);

            await Task.Delay(500, cts.Token);

            // Connect via TCP
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", cPort, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] msg = Encoding.UTF8.GetBytes("hello prompted");
            await stream.WriteAsync(msg, 0, msg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, cts.Token);

            Assert.True(readBytes > 0);
            string response = Encoding.UTF8.GetString(respBuf, 0, readBytes);
            Assert.Equal("hello prompted", response);

            // Verify prompt and read occurred!
            Assert.True(promptWritten, "Password prompt MUST be written when not in con: pipe mode");
            Assert.True(passwordRead, "Password MUST be read when not in con: pipe mode");
        }
        finally
        {
            ClientMode.CustomPasswordPromptWriter = null;
            ClientMode.CustomPasswordReader = null;
            echoListener.Stop();
        }
    }

    [Fact]
    public async Task Client_ConPipeMode_WhenPasswordRequired_DoesNotPromptAndFails()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string secretPass = "SuperSecretPromptPass";

        // Setup echo backend
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        // Setup P
        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);

        await Task.Delay(100, cts.Token);

        // Setup Server S requiring AccessPassword
        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "con_echo",
            TargetServer = $"127.0.0.1:{pPort}",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            IsTcp = true,
            AccessPassword = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(secretPass))
        });
        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);

        await Task.Delay(1500, cts.Token);

        // Setup Client C WITHOUT password (null), and IsConsolePipe = true (con: mode)
        using var lC = new TcpListener(IPAddress.Loopback, 0);
        lC.Start();
        int cPort = ((IPEndPoint)lC.LocalEndpoint).Port;
        lC.Stop();

        bool promptAttempted = false;
        ClientMode.CustomPasswordPromptWriter = prompt => { promptAttempted = true; };
        ClientMode.CustomPasswordReader = () =>
        {
            promptAttempted = true;
            return secretPass;
        };

        try
        {
            var cCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "con_echo",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true,
                Password = null, // No password specified!
                IsConsolePipe = true // con: pipe mode!
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);

            await Task.Delay(500, cts.Token);

            // Connect via TCP
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", cPort, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] msg = Encoding.UTF8.GetBytes("hello con pipe");
            await stream.WriteAsync(msg, 0, msg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = 0;
            using var readTimeout = new CancellationTokenSource(3000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
            try
            {
                readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, linked.Token);
            }
            catch { }

            // MUST be 0 bytes read (connection blocked)
            Assert.Equal(0, readBytes);

            // Console prompt MUST NOT have been called in con: pipe mode!
            Assert.False(promptAttempted, "In con: pipe mode, client MUST NOT prompt on console!");
        }
        finally
        {
            ClientMode.CustomPasswordPromptWriter = null;
            ClientMode.CustomPasswordReader = null;
            echoListener.Stop();
        }
    }

    [Fact]
    public async Task ServerMode_Auth_PlaintextToHWHash_ThenRestart_Works()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1. Setup P with strict auth and pwd file containing password for "holyping"
        string pwdFile = Path.Combine(_testDir, "p_auth.pwd");
        string passwordA = "MySuperSecretPassword999";
        byte[] bBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(passwordA));
        string passwordB = "_HASH256_" + Convert.ToBase64String(bBytes);
        File.WriteAllLines(pwdFile, new[] { $"holyping:{passwordB}" });

        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig
        {
            Port = pPort,
            EnableProxy = true,
            AuthMode = AuthMode.Strict,
            AuthFile = pwdFile,
            ConfigPath = Path.Combine(_testDir, "proxy.ini")
        };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 2. Setup S configuration with plaintext password in s.ini
        string sIniPath = Path.Combine(_testDir, "s.ini");
        File.WriteAllLines(sIniPath, new[]
        {
            $"DevId={Guid.NewGuid()}",
            "Username=holyping",
            $"Password={passwordA}",
            "[myservice]",
            "target=127.0.0.1:1234",
            $"server=127.0.0.1:{pPort}"
        });

        // First run: parse s.ini with plaintext password
        var sCfg1 = ConfigParser.Parse(new[] { "-c", sIniPath }, false);
        var sEngine1 = new RouteEngine(sCfg1);
        _ = sEngine1.StartAsync(cts.Token);

        // Wait for S to register with P
        await Task.Delay(1500, cts.Token);

        // Verify P registered the service and it is authenticated
        var proxy = pEngine.Proxy;
        Assert.NotNull(proxy);
        var reg1 = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(reg1);
        Assert.True(reg1.IsAuthenticated);
        Assert.Equal("holyping", reg1.OwnerUser);

        // Stop S1
        sEngine1.Dispose();

        // Verify s.ini was encrypted to _HWHash_ on disk
        string[] iniLines = File.ReadAllLines(sIniPath);
        Assert.Contains(iniLines, l => l.StartsWith("Password=_HWHash_"));
        Assert.DoesNotContain(iniLines, l => l.Contains(passwordA));

        // 3. Second run: parse s.ini with now-encrypted _HWHash_ password
        var sCfg2 = ConfigParser.Parse(new[] { "-c", sIniPath }, false);
        var sEngine2 = new RouteEngine(sCfg2);
        _ = sEngine2.StartAsync(cts.Token);

        // Wait for S to re-register with P
        await Task.Delay(1500, cts.Token);

        // Verify P accepted the re-registration using _HWHash_!
        var reg2 = proxy.DirectQuery("myservice/tcp");
        Assert.NotNull(reg2);
        Assert.True(reg2.IsAuthenticated);
        Assert.Equal("holyping", reg2.OwnerUser);

        sEngine2.Dispose();
        pEngine.Dispose();
    }

    [Fact]
    public async Task ClientMode_AuthToS_WithManualPasswordPromptAndDelay_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1. Setup Local Echo Server
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

        // 2. Setup P
        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S with accesspassword
        string secretPass = "userDelaySecretPassword";
        byte[] accessHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(secretPass));

        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini", ForceRelay = true };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "prompt_delay_echo",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            AccessPassword = accessHash
        });

        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        // 4. Setup C without password, simulating user typing delay
        using var lC = new TcpListener(IPAddress.Loopback, 0);
        lC.Start();
        int cPort = ((IPEndPoint)lC.LocalEndpoint).Port;
        lC.Stop();

        bool promptWritten = false;
        ClientMode.CustomPasswordPromptWriter = prompt => { promptWritten = true; };
        ClientMode.CustomPasswordReader = () =>
        {
            // 模拟用户在控制台手动输入密码耗时 2000 毫秒
            Thread.Sleep(2000);
            return secretPass;
        };

        try
        {
            var cCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "prompt_delay_echo",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true,
                Password = null,
                IsConsolePipe = false
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", cPort, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] msg = Encoding.UTF8.GetBytes("hello delayed prompt");
            await stream.WriteAsync(msg, 0, msg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, cts.Token);

            Assert.True(readBytes > 0);
            string response = Encoding.UTF8.GetString(respBuf, 0, readBytes);
            Assert.Equal("hello delayed prompt", response);
            Assert.True(promptWritten);
        }
        finally
        {
            ClientMode.CustomPasswordPromptWriter = null;
            ClientMode.CustomPasswordReader = null;
            echoListener.Stop();
        }
    }

    [Fact]
    public async Task ClientMode_AuthToS_WithManualHashedPasswordPrompt_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1. Setup Local Echo Server
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

        // 2. Setup P
        using var lP = new TcpListener(IPAddress.Loopback, 0);
        lP.Start();
        int pPort = ((IPEndPoint)lP.LocalEndpoint).Port;
        lP.Stop();

        var pCfg = new AppConfig { Port = pPort, EnableProxy = true, ConfigPath = "dummy.ini" };
        var pEngine = new RouteEngine(pCfg);
        _ = pEngine.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 3. Setup S with accesspassword
        string secretPass = "myHashedInputTest";
        byte[] accessHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(secretPass));

        var sCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini", ForceRelay = true };
        sCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "prompt_hash_echo",
            TargetIp = "127.0.0.1",
            TargetPort = echoPort,
            TargetServer = $"127.0.0.1:{pPort}",
            IsTcp = true,
            AccessPassword = accessHash
        });

        var sEngine = new RouteEngine(sCfg);
        _ = sEngine.StartAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        // 4. Setup C without password, user pastes _HASH256_... at prompt
        using var lC = new TcpListener(IPAddress.Loopback, 0);
        lC.Start();
        int cPort = ((IPEndPoint)lC.LocalEndpoint).Port;
        lC.Stop();

        string b64Hash = "_HASH256_" + Convert.ToBase64String(accessHash);
        ClientMode.CustomPasswordPromptWriter = prompt => { };
        ClientMode.CustomPasswordReader = () => b64Hash;

        try
        {
            var cCfg = new AppConfig { DevId = Guid.NewGuid(), Port = 0, ConfigPath = "dummy.ini" };
            cCfg.ClientRecords.Add(new ClientRecord
            {
                Port = cPort,
                TargetName = "prompt_hash_echo",
                TargetServer = $"127.0.0.1:{pPort}",
                IsTcp = true,
                Password = null,
                IsConsolePipe = false
            });

            var cEngine = new RouteEngine(cCfg);
            _ = cEngine.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", cPort, cts.Token);
            using var stream = tcpClient.GetStream();
            byte[] msg = Encoding.UTF8.GetBytes("hello hashed prompt");
            await stream.WriteAsync(msg, 0, msg.Length, cts.Token);

            byte[] respBuf = new byte[1024];
            int readBytes = await stream.ReadAsync(respBuf, 0, respBuf.Length, cts.Token);

            Assert.True(readBytes > 0);
            string response = Encoding.UTF8.GetString(respBuf, 0, readBytes);
            Assert.Equal("hello hashed prompt", response);
        }
        finally
        {
            ClientMode.CustomPasswordPromptWriter = null;
            ClientMode.CustomPasswordReader = null;
            echoListener.Stop();
        }
    }
}
