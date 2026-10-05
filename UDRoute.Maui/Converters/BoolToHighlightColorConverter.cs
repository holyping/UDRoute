using System.Globalization;

namespace UDRoute.Maui.Converters;

public class BoolToHighlightColorConverter : IValueConverter
{
    public Color TrueColor { get; set; } = Color.FromArgb("#10B981"); // 明亮绿色高亮
    public Color FalseColor { get; set; } = Colors.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolVal && boolVal)
        {
            return TrueColor;
        }
        return FalseColor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
