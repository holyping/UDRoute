using System.Text;

namespace UDRoute.Logging
{
    public class FileLogger : Logger
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private StreamWriter? _writer;

        public FileLogger(string filePath)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, false);
            _writer = new StreamWriter(stream, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        protected override void WriteCore(LogLevel level, string message)
        {
            lock (_lock)
            {
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpper()}] {message}");
            }
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}

