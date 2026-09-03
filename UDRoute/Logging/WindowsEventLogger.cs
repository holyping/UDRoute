using System.Runtime.InteropServices;

namespace UDRoute.Logging
{
    public class WindowsEventLogger : Logger
    {
        private const string SourceName = "UDRoute";
        private IntPtr _eventSource = IntPtr.Zero;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterEventSourceW(string? lpUNCServerName, string lpSourceName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ReportEventW(
            IntPtr hEventLog,
            ushort wType,
            ushort wCategory,
            uint dwEventID,
            IntPtr lpUserSid,
            ushort wNumStrings,
            uint dwDataSize,
            string[] lpStrings,
            byte[]? lpRawData);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeregisterEventSource(IntPtr hEventLog);

        private const ushort EVENTLOG_SUCCESS = 0x0000;
        private const ushort EVENTLOG_ERROR_TYPE = 0x0001;
        private const ushort EVENTLOG_WARNING_TYPE = 0x0002;
        private const ushort EVENTLOG_INFORMATION_TYPE = 0x0004;

        public WindowsEventLogger()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    _eventSource = RegisterEventSourceW(null, SourceName);
                }
                catch { }
            }
        }

        protected override void WriteCore(LogLevel level, string message)
        {
            if (_eventSource == IntPtr.Zero) return;

            ushort type = level switch
            {
                LogLevel.Error => EVENTLOG_ERROR_TYPE,
                LogLevel.Warn => EVENTLOG_WARNING_TYPE,
                _ => EVENTLOG_INFORMATION_TYPE
            };

            string fullMsg = $"[{level.ToString().ToUpper()}] {message}";
            ReportEventW(_eventSource, type, 0, 1000, IntPtr.Zero, 1, 0, new[] { fullMsg }, null);
        }

        public override void Dispose()
        {
            if (_eventSource != IntPtr.Zero)
            {
                DeregisterEventSource(_eventSource);
                _eventSource = IntPtr.Zero;
            }
        }
    }
}

