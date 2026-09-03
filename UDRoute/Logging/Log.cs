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

        public static void Trace(string message) => _instance.Trace(message);
        public static void Debug(string message) => _instance.Debug(message);
        public static void Info(string message) => _instance.Info(message);
        public static void Warn(string message) => _instance.Warn(message);
        public static void Error(string message) => _instance.Error(message);
        public static void Error(string message, Exception ex) => _instance.Error(message, ex);
    }
}

