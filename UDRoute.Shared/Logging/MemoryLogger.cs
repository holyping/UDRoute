using System;
using System.Collections.Generic;

namespace UDRoute.Logging
{
    public class MemoryLogger : Logger
    {
        private readonly object _lock = new();
        private readonly Queue<LogEntry> _queue;
        private int _maxCapacity;

        public event Action<LogEntry>? LogAppended;
        public event Action? LogsCleared;

        public int MaxCapacity
        {
            get { lock (_lock) return _maxCapacity; }
            set
            {
                lock (_lock)
                {
                    _maxCapacity = Math.Max(1, value);
                    while (_queue.Count > _maxCapacity)
                    {
                        _queue.Dequeue();
                    }
                }
            }
        }

        public int Count
        {
            get { lock (_lock) return _queue.Count; }
        }

        public MemoryLogger(int maxCapacity = 2000, LogLevel defaultLevel = LogLevel.Info)
        {
            _maxCapacity = Math.Max(1, maxCapacity);
            _queue = new Queue<LogEntry>(_maxCapacity);
            Level = defaultLevel;
        }

        protected override void WriteCore(LogLevel level, string message)
        {
            var entry = new LogEntry(DateTime.Now, level, message);
            lock (_lock)
            {
                _queue.Enqueue(entry);
                while (_queue.Count > _maxCapacity)
                {
                    _queue.Dequeue();
                }
            }
            LogAppended?.Invoke(entry);
        }

        public IReadOnlyList<LogEntry> GetSnapshot()
        {
            lock (_lock)
            {
                return _queue.ToArray();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();
            }
            LogsCleared?.Invoke();
        }
    }
}
