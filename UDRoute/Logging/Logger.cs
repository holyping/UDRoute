namespace UDRoute.Logging
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        None = 5,
    }

    public enum LogType
    {
        Default,
        Console,
        EventLog,
        File
    }

    public abstract class Logger : IDisposable
    {
        public LogLevel Level { get; set; } = LogLevel.Warn;

        public virtual void Trace(string message) => Write(LogLevel.Trace, message);
        public virtual void Debug(string message) => Write(LogLevel.Debug, message);
        public virtual void Info(string message) => Write(LogLevel.Info, message);
        public virtual void Warn(string message) => Write(LogLevel.Warn, message);
        public virtual void Error(string message) => Write(LogLevel.Error, message);
        public virtual void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message} -> {ex.Message}");

        public void Write(LogLevel level, string message)
        {
            if (level < Level || Level == LogLevel.None) return;
            WriteCore(level, message);
        }

        protected abstract void WriteCore(LogLevel level, string message);

        public virtual void Dispose() { }
    }

    public class NullLogger : Logger
    {
        public override void Trace(string message) { }
        public override void Debug(string message) { }
        public override void Info(string message) { }
        public override void Warn(string message) { }
        public override void Error(string message) { }
        public override void Error(string message, Exception ex) { }
        
        protected override void WriteCore(LogLevel level, string message) { }
    }
}

