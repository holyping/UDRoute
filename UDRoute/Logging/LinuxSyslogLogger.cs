using System.Runtime.InteropServices;

namespace UDRoute.Logging
{
    public class LinuxSyslogLogger : Logger
    {
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern void openlog(string ident, int option, int facility);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern void syslog(int priority, string format, string message);

        [DllImport("libc", SetLastError = true)]
        private static extern void closelog();

        private const int LOG_PID = 0x01;
        private const int LOG_DAEMON = 3 << 3;

        private const int LOG_ERR = 3;
        private const int LOG_WARNING = 4;
        private const int LOG_INFO = 6;
        private const int LOG_DEBUG = 7;

        private bool _opened = false;

        public LinuxSyslogLogger()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    openlog("udroute", LOG_PID, LOG_DAEMON);
                    _opened = true;
                }
                catch { }
            }
        }

        protected override void WriteCore(LogLevel level, string message)
        {
            if (!_opened) return;

            int priority = level switch
            {
                LogLevel.Error => LOG_ERR,
                LogLevel.Warn => LOG_WARNING,
                LogLevel.Debug => LOG_DEBUG,
                LogLevel.Trace => LOG_DEBUG,
                _ => LOG_INFO
            };

            syslog(priority, "%s", $"[{level.ToString().ToUpper()}] {message}");
        }

        public override void Dispose()
        {
            if (_opened)
            {
                closelog();
                _opened = false;
            }
        }
    }
}

