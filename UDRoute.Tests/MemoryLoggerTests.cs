using System;
using System.Linq;
using System.Threading.Tasks;
using UDRoute.Logging;
using Xunit;

namespace UDRoute.Tests;

public class MemoryLoggerTests
{
    [Fact]
    public void MemoryLogger_RespectsMaxCapacity_AndPerformsRollingEviction()
    {
        var logger = new MemoryLogger(maxCapacity: 5, defaultLevel: LogLevel.Trace);
        Log.SetLogger(logger);

        for (int i = 1; i <= 10; i++)
        {
            Log.Info($"Message {i}");
        }

        Assert.Equal(5, logger.Count);
        var snapshot = logger.GetSnapshot();
        Assert.Equal(5, snapshot.Count);

        // First 5 (1..5) should have been rolled out; 6..10 should remain
        Assert.Equal("Message 6", snapshot[0].Message);
        Assert.Equal("Message 7", snapshot[1].Message);
        Assert.Equal("Message 8", snapshot[2].Message);
        Assert.Equal("Message 9", snapshot[3].Message);
        Assert.Equal("Message 10", snapshot[4].Message);
    }

    [Fact]
    public void MemoryLogger_LogLevelFiltering_WorksDynamically()
    {
        var logger = new MemoryLogger(maxCapacity: 100, defaultLevel: LogLevel.Warn);
        Log.SetLogger(logger);

        Log.Trace("Trace message");
        Log.Debug("Debug message");
        Log.Info("Info message");
        Log.Warn("Warn message");
        Log.Error("Error message");

        var snapshot = logger.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(LogLevel.Warn, snapshot[0].Level);
        Assert.Equal(LogLevel.Error, snapshot[1].Level);

        // Dynamically lower level to Debug
        logger.Level = LogLevel.Debug;
        Log.Level = LogLevel.Debug;

        Log.Debug("New debug message");
        Log.Info("New info message");

        snapshot = logger.GetSnapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Equal("New debug message", snapshot[2].Message);
        Assert.Equal("New info message", snapshot[3].Message);
    }

    [Fact]
    public void MemoryLogger_Clear_EmptiesAllLogs()
    {
        var logger = new MemoryLogger(maxCapacity: 100, defaultLevel: LogLevel.Info);
        bool clearedEventFired = false;
        logger.LogsCleared += () => clearedEventFired = true;

        logger.Info("Hello 1");
        logger.Info("Hello 2");
        Assert.Equal(2, logger.Count);

        logger.Clear();
        Assert.Equal(0, logger.Count);
        Assert.Empty(logger.GetSnapshot());
        Assert.True(clearedEventFired);
    }

    [Fact]
    public async Task MemoryLogger_ConcurrentWrites_AreThreadSafe()
    {
        var logger = new MemoryLogger(maxCapacity: 200, defaultLevel: LogLevel.Trace);

        var tasks = Enumerable.Range(0, 10).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                logger.Info($"Task {taskId} - Msg {i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(200, logger.Count);
        var snapshot = logger.GetSnapshot();
        Assert.Equal(200, snapshot.Count);
    }

    [Fact]
    public void LogEntry_PropertiesAndColors_FormatCorrectly()
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Error, "Fatal error occurred");
        Assert.Equal("ERROR", entry.LevelString);
        Assert.Equal("#EF4444", entry.LevelColorHex);
        Assert.Equal("#FEE2E2", entry.BadgeBgColorHex);
        Assert.Contains("[ERROR] Fatal error occurred", entry.ToString());
    }
}
