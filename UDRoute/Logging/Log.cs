using System.Runtime.InteropServices;

namespace UDRoute.Logging
{
    public static class Log
    {
        private static Logger _instance = new ConsoleLogger();

        public static Logger Instance => _instance;

        public static void Init(AppConfig config, bool isServiceMode = false)
        {
            _instance.Dispose();

            Logger logger = config.LogType switch
            {
                LogType.Console => new ConsoleLogger(),
                LogType.EventLog => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new WindowsEventLogger()
                    : new LinuxSyslogLogger(),
                LogType.File => new FileLogger(config.LogFile),
                _ => isServiceMode
                    ? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsEventLogger() : new LinuxSyslogLogger())
                    : new ConsoleLogger()
            };

            logger.Level = config.LogLevel;
            _instance = logger;
        }

        public static void SetLogger(Logger logger)
        {
            _instance.Dispose();
            _instance = logger;
        }

        public static bool IsEnabled(LogLevel level) => _instance.IsEnabled(level);
        public static bool IsTraceEnabled => _instance.IsEnabled(LogLevel.Trace);
        public static bool IsDebugEnabled => _instance.IsEnabled(LogLevel.Debug);
        public static bool IsInfoEnabled => _instance.IsEnabled(LogLevel.Info);
        public static bool IsWarnEnabled => _instance.IsEnabled(LogLevel.Warn);
        public static bool IsErrorEnabled => _instance.IsEnabled(LogLevel.Error);

        // --- Interpolated String Handler Overloads (Zero allocation when level is disabled) ---
        public static void Trace(ref LogTraceInterpolatedStringHandler handler)
        {
            if (handler.IsEnabled) _instance.Trace(handler.ToStringAndClear());
        }

        public static void Debug(ref LogDebugInterpolatedStringHandler handler)
        {
            if (handler.IsEnabled) _instance.Debug(handler.ToStringAndClear());
        }

        public static void Info(ref LogInfoInterpolatedStringHandler handler)
        {
            if (handler.IsEnabled) _instance.Info(handler.ToStringAndClear());
        }

        public static void Warn(ref LogWarnInterpolatedStringHandler handler)
        {
            if (handler.IsEnabled) _instance.Warn(handler.ToStringAndClear());
        }

        public static void Error(ref LogErrorInterpolatedStringHandler handler)
        {
            if (handler.IsEnabled) _instance.Error(handler.ToStringAndClear());
        }

        public static void Error(ref LogErrorInterpolatedStringHandler handler, Exception ex)
        {
            if (handler.IsEnabled) _instance.Error(handler.ToStringAndClear(), ex);
        }

        // --- Standard String Overloads ---
        public static void Trace(string message) => _instance.Trace(message);
        public static void Debug(string message) => _instance.Debug(message);
        public static void Info(string message) => _instance.Info(message);
        public static void Warn(string message) => _instance.Warn(message);
        public static void Error(string message) => _instance.Error(message);
        public static void Error(string message, Exception ex) => _instance.Error(message, ex);

        // --- Format String Overloads ---
        public static void Trace(string format, params object[] args)
        {
            if (IsEnabled(LogLevel.Trace)) _instance.Trace(string.Format(format, args));
        }

        public static void Debug(string format, params object[] args)
        {
            if (IsEnabled(LogLevel.Debug)) _instance.Debug(string.Format(format, args));
        }

        public static void Info(string format, params object[] args)
        {
            if (IsEnabled(LogLevel.Info)) _instance.Info(string.Format(format, args));
        }

        public static void Warn(string format, params object[] args)
        {
            if (IsEnabled(LogLevel.Warn)) _instance.Warn(string.Format(format, args));
        }

        public static void Error(string format, params object[] args)
        {
            if (IsEnabled(LogLevel.Error)) _instance.Error(string.Format(format, args));
        }
    }
}

