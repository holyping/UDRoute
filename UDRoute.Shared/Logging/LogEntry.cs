using System;

namespace UDRoute.Logging
{
    public class LogEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;

        public LogEntry() { }

        public LogEntry(DateTime timestamp, LogLevel level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public string TimeString => Timestamp.ToString("HH:mm:ss.fff");

        public string LevelString => Level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => Level.ToString().ToUpperInvariant()
        };

        public string LevelColorHex => Level switch
        {
            LogLevel.Error => "#EF4444",
            LogLevel.Warn => "#F59E0B",
            LogLevel.Info => "#3B82F6",
            LogLevel.Debug => "#8B5CF6",
            _ => "#6B7280"
        };

        public string BadgeBgColorHex => Level switch
        {
            LogLevel.Error => "#FEE2E2",
            LogLevel.Warn => "#FEF3C7",
            LogLevel.Info => "#DBEAFE",
            LogLevel.Debug => "#EDE9FE",
            _ => "#F3F4F6"
        };

        public override string ToString() => $"[{TimeString}] [{LevelString}] {Message}";
    }
}
