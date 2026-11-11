using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using Xunit;

namespace UDRoute.Tests;

public class ConfigParserTests : IDisposable
{
    private readonly string _testDir;

    public ConfigParserTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "udroute_config_test_" + Guid.NewGuid().ToString("N"));
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
    public void ParseIni_FileTarget_ParsedCorrectly()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[kpxt]
target=D:\BOSKPXT;/file
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("kpxt/file", sRec.Name);
        Assert.True(sRec.IsFile);
        Assert.Equal(@"D:\BOSKPXT", sRec.BaseDir);
    }

    [Fact]
    public void ParseIni_FileTarget_WithInlineComment_ParsedCorrectly()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[kpxt]
target=D:\BOSKPXT;/file ; 文件共享服务目录
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("kpxt/file", sRec.Name);
        Assert.True(sRec.IsFile);
        Assert.Equal(@"D:\BOSKPXT", sRec.BaseDir);
    }

    [Fact]
    public void ParseIni_FileTarget_WithQuotes_ParsedCorrectly()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[kpxt]
target=""D:\BOSKPXT;/file"" ; 引号包裹
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("kpxt/file", sRec.Name);
        Assert.True(sRec.IsFile);
        Assert.Equal(@"D:\BOSKPXT", sRec.BaseDir);
    }

    [Fact]
    public void ParseIni_TcpTarget_WithComment_ParsedCorrectly()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[web]
target=127.0.0.1:8080/tcp ; 内部网页
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("web", sRec.Name);
        Assert.False(sRec.IsFile);
        Assert.Equal("127.0.0.1", sRec.TargetIp);
        Assert.Equal(8080, sRec.TargetPort);
        Assert.True(sRec.IsTcp);
    }

    [Fact]
    public void ParseIni_InvalidTarget_DoesNotThrowFormatException()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[invalid]
target=D:\BOSKPXT
");

        // Should not throw System.FormatException
        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("invalid", sRec.Name);
        // Default TargetPort is 0 because parsing failed gracefully
        Assert.Equal(0, sRec.TargetPort);
    }

    [Fact]
    public void ParseCommandLine_FileTarget_ParsedCorrectly()
    {
        var cfg = ConfigParser.Parse(new[] { "-s", @"kpxt=D:\BOSKPXT;/file@1.2.3.4:1399" });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("kpxt/file", sRec.Name);
        Assert.True(sRec.IsFile);
        Assert.Equal(@"D:\BOSKPXT", sRec.BaseDir);
    }

    [Fact]
    public void ParseIni_TcpTarget_WithSemicolonFileComment_PreservedAsTcp()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[web]
target=127.0.0.1:8080/tcp ;/file this comment should be stripped
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("web", sRec.Name);
        Assert.False(sRec.IsFile);
        Assert.Equal("127.0.0.1", sRec.TargetIp);
        Assert.Equal(8080, sRec.TargetPort);
        Assert.True(sRec.IsTcp);
    }

    [Fact]
    public void ParseIni_FileTarget_WithRepeatedSemicolonFile_SecondStrippedAsComment()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399

[kpxt]
target=D:\BOSKPXT;/file ;/file extra comment
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.Equal("kpxt/file", sRec.Name);
        Assert.True(sRec.IsFile);
        Assert.Equal(@"D:\BOSKPXT", sRec.BaseDir);
    }

    [Fact]
    public void ParseIni_PasswordAndAccessPassword_SeparatelyParsedAndHashed()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399
username = zhangsan
password = p_secret_123
accesspassword = c_access_456

[rdp]
target=127.0.0.1:3389/tcp
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Single(cfg.ServerRecords);
        var sRec = cfg.ServerRecords[0];
        Assert.NotNull(cfg.Password);
        Assert.NotNull(cfg.AccessPassword);
        Assert.NotNull(sRec.AccessPassword);
        Assert.False(cfg.Password.SequenceEqual(cfg.AccessPassword));
        Assert.True(sRec.AccessPassword.SequenceEqual(cfg.AccessPassword));

        // Verify the ini file was modified: password protected with _HWHash_, accesspassword protected with _HASH256_
        string content = File.ReadAllText(iniPath);
        Assert.DoesNotContain("p_secret_123", content);
        Assert.DoesNotContain("c_access_456", content);
        Assert.Contains("_HWHash_", content);
        Assert.Contains("_HASH256_", content);
        byte[] expectedAccessBytes = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes("c_access_456"));
        Assert.True(sRec.AccessPassword.SequenceEqual(expectedAccessBytes));
    }

    [Fact]
    public void ParseIni_SectionLevelAccessPassword_OverridesGlobal()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399
accesspassword = global_pass

[rdp]
target=127.0.0.1:3389/tcp
accesspassword = rdp_pass

[web]
target=127.0.0.1:8080/tcp
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });

        Assert.Equal(2, cfg.ServerRecords.Count);
        var rdpRec = cfg.ServerRecords[0];
        var webRec = cfg.ServerRecords[1];

        Assert.NotNull(rdpRec.AccessPassword);
        Assert.NotNull(webRec.AccessPassword);
        Assert.False(rdpRec.AccessPassword.SequenceEqual(webRec.AccessPassword));
        Assert.True(webRec.AccessPassword.SequenceEqual(cfg.AccessPassword!));
    }

    [Fact]
    public void ParseIni_PreHashedAccessPassword_NotReHashed()
    {
        string iniPath = Path.Combine(_testDir, "udroute.ini");
        byte[] originalHash = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes("mysecret"));
        string preHashed = "_HASH256_" + Convert.ToBase64String(originalHash);

        File.WriteAllText(iniPath, $@"
server = 1.2.3.4:1399
[rdp]
target = 127.0.0.1:3389/tcp
accesspassword = {preHashed}
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });
        var sRec = cfg.ServerRecords[0];

        Assert.NotNull(sRec.AccessPassword);
        Assert.True(sRec.AccessPassword.SequenceEqual(originalHash));

        string content = File.ReadAllText(iniPath);
        Assert.Contains(preHashed, content);
        Assert.DoesNotContain("_HWHash_", content);
    }

    [Fact]
    public void ParseIni_RelativeLogFileAndAuthFile_ResolvedRelativeToIniDir()
    {
        string subDir = Path.Combine(_testDir, "service_dir");
        Directory.CreateDirectory(subDir);
        string iniPath = Path.Combine(subDir, "udroute.ini");

        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399
logfile = app.log
authfile = secret.pwd
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });
        Assert.NotNull(cfg);
        Assert.Equal(Path.Combine(subDir, "app.log"), cfg.LogFile);
        Assert.Equal(Path.Combine(subDir, "secret.pwd"), cfg.AuthFile);
    }

    [Fact]
    public void ParseIni_DefaultLogFile_ResolvedRelativeToIniDir()
    {
        string subDir = Path.Combine(_testDir, "default_log_dir");
        Directory.CreateDirectory(subDir);
        string iniPath = Path.Combine(subDir, "custom.ini");

        File.WriteAllText(iniPath, @"
server = 1.2.3.4:1399
");

        var cfg = ConfigParser.Parse(new[] { "-c", iniPath });
        Assert.NotNull(cfg);
        Assert.Equal(Path.Combine(subDir, "custom.log"), cfg.LogFile);
    }

    [Fact]
    public void ParseCommandLine_ClientOnly_UsesRandomPort_NotDefault9400()
    {
        var cfg = ConfigParser.Parse(new[] { "11433=sql@www.qzsoft.top" });
        Assert.NotNull(cfg);
        Assert.False(cfg.EnableProxy);
        Assert.Equal(0, cfg.Port);
        Assert.Single(cfg.ClientRecords);
    }

    [Fact]
    public void ParseCommandLine_ServerOnly_UsesRandomPort_NotDefault9400()
    {
        var cfg = ConfigParser.Parse(new[] { "sql=127.0.0.1:1433@www.qzsoft.top" });
        Assert.NotNull(cfg);
        Assert.False(cfg.EnableProxy);
        Assert.Equal(0, cfg.Port);
        Assert.Single(cfg.ServerRecords);
    }

    [Fact]
    public void ParseIni_ClientOrServerOnly_WithoutProxyConfig_UsesRandomPort()
    {
        string cIni = Path.Combine(_testDir, "c_only.ini");
        File.WriteAllText(cIni, "11433/tcp = sql@www.qzsoft.top\n");
        var cCfg = ConfigParser.Parse(new[] { "-c", cIni });
        Assert.NotNull(cCfg);
        Assert.False(cCfg.EnableProxy);
        Assert.Equal(0, cCfg.Port);

        string sIni = Path.Combine(_testDir, "s_only.ini");
        File.WriteAllText(sIni, "server = www.qzsoft.top\n[sql]\ntarget = 127.0.0.1:1433/tcp\n");
        var sCfg = ConfigParser.Parse(new[] { "-c", sIni });
        Assert.NotNull(sCfg);
        Assert.False(sCfg.EnableProxy);
        Assert.Equal(0, sCfg.Port);

        string scIni = Path.Combine(_testDir, "sc_only.ini");
        File.WriteAllText(scIni, "server = www.qzsoft.top\n11433/tcp = sql\n[sql]\ntarget = 127.0.0.1:1433/tcp\n");
        var scCfg = ConfigParser.Parse(new[] { "-c", scIni });
        Assert.NotNull(scCfg);
        Assert.False(scCfg.EnableProxy);
        Assert.Equal(0, scCfg.Port);
    }

    [Fact]
    public void Parse_ExplicitProxyMode_UsesDefault9400OrConfiguredPort()
    {
        var cThis = ConfigParser.Parse(new[] { "11433=sql@this" });
        Assert.NotNull(cThis);
        Assert.True(cThis.EnableProxy);
        Assert.Equal(Constants.DefaultProxyPort, cThis.Port);

        var sThis = ConfigParser.Parse(new[] { "sql=127.0.0.1:1433@this" });
        Assert.NotNull(sThis);
        Assert.True(sThis.EnableProxy);
        Assert.Equal(Constants.DefaultProxyPort, sThis.Port);

        string pOnlyIni = Path.Combine(_testDir, "p_only.ini");
        File.WriteAllText(pOnlyIni, "authmode=none\n");
        var pCfg = ConfigParser.Parse(new[] { "-c", pOnlyIni });
        Assert.NotNull(pCfg);
        Assert.True(pCfg.EnableProxy);
        Assert.Equal(Constants.DefaultProxyPort, pCfg.Port);

        string sWithPortIni = Path.Combine(_testDir, "s_port.ini");
        File.WriteAllText(sWithPortIni, "port=9501\n[sql]\ntarget=127.0.0.1:1433\nserver=www.qzsoft.top\n");
        var sWithPortCfg = ConfigParser.Parse(new[] { "-c", sWithPortIni });
        Assert.NotNull(sWithPortCfg);
        Assert.True(sWithPortCfg.EnableProxy);
        Assert.Equal(9501, sWithPortCfg.Port);

        string cWithRegTimeoutIni = Path.Combine(_testDir, "c_regtimeout.ini");
        File.WriteAllText(cWithRegTimeoutIni, "regtimeout=600\n11433=sql@www.qzsoft.top\n");
        var cWithRegTimeoutCfg = ConfigParser.Parse(new[] { "-c", cWithRegTimeoutIni });
        Assert.NotNull(cWithRegTimeoutCfg);
        Assert.True(cWithRegTimeoutCfg.EnableProxy);
        Assert.Equal(Constants.DefaultProxyPort, cWithRegTimeoutCfg.Port);

        string sThisIni = Path.Combine(_testDir, "s_this.ini");
        File.WriteAllText(sThisIni, "[sql]\ntarget=127.0.0.1:1433\nserver=this\n");
        var sThisIniCfg = ConfigParser.Parse(new[] { "-c", sThisIni });
        Assert.NotNull(sThisIniCfg);
        Assert.True(sThisIniCfg.EnableProxy);
        Assert.Equal(Constants.DefaultProxyPort, sThisIniCfg.Port);
    }

    [Fact]
    public async Task RouteEngine_ClientOrServerWithoutProxy_BindsRandomPortNot9400()
    {
        using var probeTcp = new TcpListener(IPAddress.Loopback, 0);
        probeTcp.Start();
        int freeTcpPort = ((IPEndPoint)probeTcp.LocalEndpoint).Port;
        probeTcp.Stop();

        var cliCfg = ConfigParser.Parse(new[] { $"{freeTcpPort}=sql@127.0.0.1", "-log", "none" });
        Assert.NotNull(cliCfg);
        Assert.False(cliCfg.EnableProxy);
        Assert.Equal(0, cliCfg.Port);

        using (var cts = new CancellationTokenSource())
        using (var engine = new RouteEngine(cliCfg))
        {
            var runTask = engine.StartAsync(cts.Token);
            await Task.Delay(150);
            Assert.Null(engine.Proxy);
            Assert.True(engine.BoundPort > 0);
            Assert.NotEqual(Constants.DefaultProxyPort, engine.BoundPort);
            cts.Cancel();
            try { await runTask; } catch { }
        }

        // Defense-in-depth: even if AppConfig has Port=9400 while EnableProxy=false, RouteEngine binds random port
        var legacyCfg = new AppConfig
        {
            EnableProxy = false,
            Port = Constants.DefaultProxyPort,
            ConfigPath = "dummy.ini"
        };
        legacyCfg.ServerRecords.Add(new ServerRecord
        {
            Name = "sql",
            TargetIp = "127.0.0.1",
            TargetPort = 1433,
            TargetServer = "127.0.0.1:9400",
            IsTcp = true
        });

        using (var cts = new CancellationTokenSource())
        using (var engine = new RouteEngine(legacyCfg))
        {
            var runTask = engine.StartAsync(cts.Token);
            await Task.Delay(150);
            Assert.Null(engine.Proxy);
            Assert.True(engine.BoundPort > 0);
            Assert.NotEqual(Constants.DefaultProxyPort, engine.BoundPort);
            cts.Cancel();
            try { await runTask; } catch { }
        }
    }
}



