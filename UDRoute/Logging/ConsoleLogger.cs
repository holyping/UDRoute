using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace UDRoute.Logging
{
    public class ConsoleLogger : Logger
    {
        private readonly struct LogItem
        {
            public readonly LogLevel Level;
            public readonly string Message;
            public readonly DateTime Timestamp;

            public LogItem(LogLevel level, string message, DateTime timestamp)
            {
                Level = level;
                Message = message;
                Timestamp = timestamp;
            }
        }

        private static readonly BlockingCollection<LogItem> _queue = new(new ConcurrentQueue<LogItem>());
        private static Thread? _workerThread;
        private static readonly object _initLock = new();
        private static volatile bool _isShutdown;

        private static void EnsureStarted()
        {
            if (_workerThread != null || _isShutdown) return;
            lock (_initLock)
            {
                if (_workerThread != null || _isShutdown) return;
                _workerThread = new Thread(ProcessQueue)
                {
                    IsBackground = true,
                    Name = "UDRoute-ConsoleLogger"
                };
                _workerThread.Start();
            }
        }

        protected override void WriteCore(LogLevel level, string message)
        {
            if (_isShutdown)
            {
                WriteDirect(level, message);
                return;
            }

            EnsureStarted();
            try
            {
                if (!_queue.TryAdd(new LogItem(level, message, DateTime.Now)))
                {
                    WriteDirect(level, message);
                }
            }
            catch
            {
                WriteDirect(level, message);
            }
        }

        private static void ProcessQueue()
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    WriteDirect(item.Level, item.Message, item.Timestamp);
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding was called and queue exhausted
            }
            catch
            {
                // Prevent unhandled exception from terminating process
            }
        }

        private static void WriteDirect(LogLevel level, string message, DateTime? timestamp = null)
        {
            try
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

                DateTime ts = timestamp ?? DateTime.Now;
                Console.WriteLine($"[{ts:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpper()}] {message}");
                Console.ForegroundColor = origColor;
            }
            catch { }
        }

        public static void Shutdown()
        {
            if (_isShutdown) return;
            _isShutdown = true;

            try
            {
                _queue.CompleteAdding();
                _workerThread?.Join(1000);
            }
            catch { }
            finally
            {
                _workerThread = null;
            }
        }

        public static void Flush(int timeoutMs = 500)
        {
            var sw = Stopwatch.StartNew();
            while (_queue.Count > 0 && sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(10);
            }
        }

        public override void Dispose()
        {
            Flush(200);
        }
    }
}

