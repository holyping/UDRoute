using System;
using System.Runtime.CompilerServices;

namespace UDRoute.Logging
{
    [InterpolatedStringHandler]
    public ref struct LogTraceInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;
        public bool IsEnabled { get; }

        public LogTraceInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = Log.IsEnabled(LogLevel.Trace);
            IsEnabled = isEnabled;
            _innerHandler = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (IsEnabled) _innerHandler.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(ReadOnlySpan<char> value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }

        public string ToStringAndClear() => IsEnabled ? _innerHandler.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct LogDebugInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;
        public bool IsEnabled { get; }

        public LogDebugInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = Log.IsEnabled(LogLevel.Debug);
            IsEnabled = isEnabled;
            _innerHandler = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (IsEnabled) _innerHandler.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(ReadOnlySpan<char> value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }

        public string ToStringAndClear() => IsEnabled ? _innerHandler.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct LogInfoInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;
        public bool IsEnabled { get; }

        public LogInfoInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = Log.IsEnabled(LogLevel.Info);
            IsEnabled = isEnabled;
            _innerHandler = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (IsEnabled) _innerHandler.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(ReadOnlySpan<char> value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }

        public string ToStringAndClear() => IsEnabled ? _innerHandler.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct LogWarnInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;
        public bool IsEnabled { get; }

        public LogWarnInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = Log.IsEnabled(LogLevel.Warn);
            IsEnabled = isEnabled;
            _innerHandler = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (IsEnabled) _innerHandler.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(ReadOnlySpan<char> value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }

        public string ToStringAndClear() => IsEnabled ? _innerHandler.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct LogErrorInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _innerHandler;
        public bool IsEnabled { get; }

        public LogErrorInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = Log.IsEnabled(LogLevel.Error);
            IsEnabled = isEnabled;
            _innerHandler = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (IsEnabled) _innerHandler.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(ReadOnlySpan<char> value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (IsEnabled) _innerHandler.AppendFormatted(value); }
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) { if (IsEnabled) _innerHandler.AppendFormatted(value, alignment, format); }

        public string ToStringAndClear() => IsEnabled ? _innerHandler.ToStringAndClear() : string.Empty;
    }
}

