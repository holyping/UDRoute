using System.IO;
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
}


