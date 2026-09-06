using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UDRoute;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;
using System.Linq;
using System.Security.Cryptography;

namespace UDRoute.Tests;

public class TestLogger : Logger
{
    private readonly ITestOutputHelper _output;
    public TestLogger(ITestOutputHelper output) { _output = output; }
    protected override void WriteCore(LogLevel level, string msg) {
        try { _output.WriteLine(msg); } catch { }
    }
}
