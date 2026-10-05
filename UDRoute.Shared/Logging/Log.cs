using System.Runtime.InteropServices;

namespace UDRoute.Logging
{
    public static class Log
    {
        private static Logger? _instance;

        public static void DisposeInstance() => _instance?.Dispose();

        public static Func<AppConfig, bool, Logger>? LoggerFactory { get; set; }

        public static LogLevel Level
        {
            get => _instance?.Level ?? LogLevel.Warn;
            set
            {
                if (_instance != null) _instance.Level = value;
            }
        }

        public static void Init(AppConfig config, bool isServiceMode = false)
        {
            if (LoggerFactory != null)
            {
                SetLogger(LoggerFactory(config, isServiceMode));
                if (_instance != null)
                {
                    _instance.Level = config.LogLevel;
                }
            }
        }

        public static void SetLogger(Logger logger)
        {
            _instance?.Dispose();
            _instance = logger;
        }

        public static bool IsEnabled(LogLevel level) => _instance?.IsEnabled(level) ?? false;
        public static bool IsTraceEnabled => _instance?.IsEnabled(LogLevel.Trace) ?? false;
        public static bool IsDebugEnabled => _instance?.IsEnabled(LogLevel.Debug) ?? false;
        public static bool IsInfoEnabled => _instance?.IsEnabled(LogLevel.Info) ?? false;
        public static bool IsWarnEnabled => _instance?.IsEnabled(LogLevel.Warn) ?? false;
        public static bool IsErrorEnabled => _instance?.IsEnabled(LogLevel.Error) ?? false;

        // --- Interpolated String Handler Overloads (Zero allocation when level is disabled) ---
        public static void Trace(ref LogTraceInterpolatedStringHandler handler)
        {
            if (_instance != null && handler.IsEnabled) _instance.Trace(handler.ToStringAndClear());
        }

        public static void Debug(ref LogDebugInterpolatedStringHandler handler)
        {
            if (_instance != null && handler.IsEnabled) _instance.Debug(handler.ToStringAndClear());
        }

        public static void Info(ref LogInfoInterpolatedStringHandler handler)
        {
            if (_instance != null && handler.IsEnabled) _instance.Info(handler.ToStringAndClear());
        }

        public static void Warn(ref LogWarnInterpolatedStringHandler handler)
        {
            if (_instance != null && handler.IsEnabled) _instance.Warn(handler.ToStringAndClear());
        }

        public static void Error(ref LogErrorInterpolatedStringHandler handler)
        {
            if (_instance != null && handler.IsEnabled) _instance.Error(handler.ToStringAndClear());
        }

        public static void Error(ref LogErrorInterpolatedStringHandler handler, Exception ex)
        {
            if (_instance != null && handler.IsEnabled) _instance.Error(handler.ToStringAndClear(), ex);
        }

        // --- Standard String Overloads ---
        public static void Trace(string message) => _instance?.Trace(message);
        public static void Debug(string message) => _instance?.Debug(message);
        public static void Info(string message) => _instance?.Info(message);
        public static void Warn(string message) => _instance?.Warn(message);
        public static void Error(string message) => _instance?.Error(message);
        public static void Error(string message, Exception ex) => _instance?.Error(message, ex);

        // --- Format String Overloads ---
        public static void Trace(string format, params object[] args)
        {
            if (_instance != null && _instance.IsEnabled(LogLevel.Trace)) _instance.Trace(string.Format(format, args));
        }

        public static void Debug(string format, params object[] args)
        {
            if (_instance != null && _instance.IsEnabled(LogLevel.Debug)) _instance.Debug(string.Format(format, args));
        }

        public static void Info(string format, params object[] args)
        {
            if (_instance != null && _instance.IsEnabled(LogLevel.Info)) _instance.Info(string.Format(format, args));
        }

        public static void Warn(string format, params object[] args)
        {
            if (_instance != null && _instance.IsEnabled(LogLevel.Warn)) _instance.Warn(string.Format(format, args));
        }

        public static void Error(string format, params object[] args)
        {
            if (_instance != null && _instance.IsEnabled(LogLevel.Error)) _instance.Error(string.Format(format, args));
        }
    }
}

