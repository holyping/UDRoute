namespace UDRoute.Logging
{
    public class ConsoleLogger : Logger
    {
        private static readonly object _lock = new();

        protected override void WriteCore(LogLevel level, string message)
        {
            lock (_lock)
            {
                var origColor = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Trace => ConsoleColor.Gray,
                    _ => ConsoleColor.Gray
                };

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpper()}] {message}");
                Console.ForegroundColor = origColor;
            }
        }
    }
}

