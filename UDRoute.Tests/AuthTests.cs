using System.Buffers.Binary;
using System.Net;
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
        Assert.False(auth.Authenticate("any", "any"));
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
            Assert.True(auth.Authenticate("alice", "mySecretPass123"));
            Assert.True(auth.Authenticate("bob", "hunter2"));

            // Case-insensitive username check
            Assert.True(auth.Authenticate("ALICE", "mySecretPass123"));

            // Wrong credentials
            Assert.False(auth.Authenticate("alice", "wrongpwd"));
            Assert.False(auth.Authenticate("bob", "wrongpwd"));
            Assert.False(auth.Authenticate("charlie", "hunter2"));
        }

        // Verify that the file was automatically converted to $SHA256$ on disk
        string[] content = File.ReadAllLines(pwdFile);
        Assert.Contains(content, l => l.StartsWith("alice:$SHA256$"));
        Assert.Contains(content, l => l.StartsWith("bob:$SHA256$"));
        Assert.DoesNotContain(content, l => l.Contains("mySecretPass123"));
    }

    [Fact]
    public void HashedFile_LoadedDirectlyWithoutRehashing()
    {
        string pwdFile = Path.Combine(_testDir, "hashed.pwd");
        // Pre-compute sha256 for "test1234"
        // echo -n "test1234" | sha256 = 937e8d5fbb48bd4949536cd65b8d35c426b80d2f830c5c308e2cdec422ae2244
        File.WriteAllText(pwdFile, "admin:$SHA256$937e8d5fbb48bd4949536cd65b8d35c426b80d2f830c5c308e2cdec422ae2244\n");

        using (var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile))
        {
            auth.Start();
            Assert.True(auth.Authenticate("admin", "test1234"));
            Assert.False(auth.Authenticate("admin", "wrong"));
        }

        string text = File.ReadAllText(pwdFile);
        Assert.Contains("admin:$SHA256$937e8d5fbb48bd4949536cd65b8d35c426b80d2f830c5c308e2cdec422ae2244", text);
    }

    [Fact]
    public async Task Watcher_DynamicReload_UpdatesWithoutRestart()
    {
        string pwdFile = Path.Combine(_testDir, "dynamic.pwd");
        File.WriteAllLines(pwdFile, new[] { "user1:pass1" });

        using var auth = new AuthManager(Path.Combine(_testDir, "dummy.ini"), pwdFile);
        auth.Start();

        Assert.True(auth.Authenticate("user1", "pass1"));
        Assert.False(auth.Authenticate("user2", "pass2"));

        // Dynamically add user2 to file while running
        await Task.Delay(200);
        File.AppendAllText(pwdFile, "user2:pass2\n");

        // Wait for FileSystemWatcher debounce (500ms in AuthManager + small buffer)
        await Task.Delay(1000);

        Assert.True(auth.Authenticate("user2", "pass2"), "Dynamic user2 should be authenticated after file change");
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
            Assert.True(auth.Authenticate("alice", "secret_plaintext"));
        }
        finally
        {
            File.SetAttributes(pwdFile, FileAttributes.Normal);
        }
    }

    // =========================================================================
    // ProxyMode Registration & Auth Level Tests
    // =========================================================================

    private byte[] BuildRegisterPacket(string serviceName, bool isTcp, string devName, Guid devId, string? username, string? password)
    {
        byte[] buffer = new byte[1024];
        buffer[0] = (byte)MsgType.Register;
        devId.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(17, 4), 0); // WanPort
        buffer[21] = (byte)(isTcp ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22, 4), 300); // Timeout
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(26, 8), DateTime.UtcNow.Ticks); // Timestamp
        buffer[34] = 0; // ReqPass
        int offset = 35;
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
}
