using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UDRoute
{
    public static class I18n
    {
        public static bool IsZh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 纯静态字符串中英文切换
        /// </summary>
        public static string Text(string zh, string en) => IsZh ? zh : en;

        /// <summary>
        /// 双插值字符串：利用 C# 10+ InterpolatedStringHandler 的 short-circuit 机制，
        /// 仅当前语言对应的插值表达式与字符串格式化会被实际执行与求值，非当前语言分支完全跳过。
        /// </summary>
        public static string Text(I18nZhHandler zh, I18nEnHandler en)
        {
            return IsZh ? zh.ToStringAndClear() : en.ToStringAndClear();
        }

        public static string Text(I18nZhHandler zh, string en)
        {
            return IsZh ? zh.ToStringAndClear() : en;
        }

        public static string Text(string zh, I18nEnHandler en)
        {
            return IsZh ? zh : en.ToStringAndClear();
        }

        /// <summary>
        /// 模板格式化：仅对当前系统语言选中的模板进行 string.Format
        /// </summary>
        public static string Format(string zhFormat, string enFormat, params object?[] args)
        {
            return string.Format(IsZh ? zhFormat : enFormat, args);
        }
    }

    [InterpolatedStringHandler]
    public ref struct I18nZhHandler
    {
        private DefaultInterpolatedStringHandler _inner;
        private readonly bool _isEnabled;

        public I18nZhHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = I18n.IsZh;
            _isEnabled = isEnabled;
            _inner = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value)
        {
            if (_isEnabled) _inner.AppendLiteral(value);
        }

        public void AppendFormatted<T>(T value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted<T>(T value, string? format)
        {
            if (_isEnabled) _inner.AppendFormatted(value, format);
        }

        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_isEnabled) _inner.AppendFormatted(value, alignment);
        }

        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (_isEnabled) _inner.AppendFormatted(value, alignment, format);
        }

        public void AppendFormatted(ReadOnlySpan<char> value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted(string? value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public string ToStringAndClear() => _isEnabled ? _inner.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct I18nEnHandler
    {
        private DefaultInterpolatedStringHandler _inner;
        private readonly bool _isEnabled;

        public I18nEnHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = !I18n.IsZh;
            _isEnabled = isEnabled;
            _inner = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value)
        {
            if (_isEnabled) _inner.AppendLiteral(value);
        }

        public void AppendFormatted<T>(T value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted<T>(T value, string? format)
        {
            if (_isEnabled) _inner.AppendFormatted(value, format);
        }

        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_isEnabled) _inner.AppendFormatted(value, alignment);
        }

        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (_isEnabled) _inner.AppendFormatted(value, alignment, format);
        }

        public void AppendFormatted(ReadOnlySpan<char> value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public void AppendFormatted(string? value)
        {
            if (_isEnabled) _inner.AppendFormatted(value);
        }

        public string ToStringAndClear() => _isEnabled ? _inner.ToStringAndClear() : string.Empty;
    }
}
