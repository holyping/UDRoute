using System;
using System.Collections.Generic;
using UDRoute.Logging;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UDRoute.Tests;

public class LoggerPerformanceTests
{
    private readonly ITestOutputHelper _out;

    public LoggerPerformanceTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private class MemoryLogger : Logger
    {
        public List<string> Messages { get; } = new();

        protected override void WriteCore(LogLevel level, string message)
        {
            Messages.Add($"[{level}] {message}");
        }
    }

    [Fact]
    public void InterpolatedStringHandler_SkipsArgumentEvaluation_WhenLevelDisabled()
    {
        var memLogger = new MemoryLogger { Level = LogLevel.Warn };
        Log.SetLogger(memLogger);

        int sideEffectCount = 0;
        string SideEffectFunc()
        {
            sideEffectCount++;
            return "computed_value";
        }

        // 1. Info is disabled (Level is Warn)
        Log.Info($"Test message with side effect: {SideEffectFunc()}");
        Log.Debug($"Test debug side effect: {SideEffectFunc()}");
        Log.Trace($"Test trace side effect: {SideEffectFunc()}");

        // Assert: SideEffectFunc was NEVER called!
        Assert.Equal(0, sideEffectCount);
        Assert.Empty(memLogger.Messages);

        // 2. Warn is enabled
        Log.Warn($"Warning message with side effect: {SideEffectFunc()}");
        Assert.Equal(1, sideEffectCount);
        Assert.Single(memLogger.Messages);
        Assert.Contains("computed_value", memLogger.Messages[0]);

        // 3. Lower level to Debug
        memLogger.Level = LogLevel.Debug;
        Log.Debug($"Debug message with side effect: {SideEffectFunc()}");
        Assert.Equal(2, sideEffectCount);
        Assert.Equal(2, memLogger.Messages.Count);
        Assert.Contains("Debug message with side effect: computed_value", memLogger.Messages[1]);
    }

    [Fact]
    public void FormatStringOverload_WorksCorrectly()
    {
        var memLogger = new MemoryLogger { Level = LogLevel.Info };
        Log.SetLogger(memLogger);

        Log.Info("User {0} logged in from {1}:{2}", "alice", "127.0.0.1", 8080);
        Assert.Single(memLogger.Messages);
        Assert.Equal("[Info] User alice logged in from 127.0.0.1:8080", memLogger.Messages[0]);
    }
}

