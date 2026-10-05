using System.Globalization;

namespace UDRoute.Maui.Converters;

public class TabIndexToTextColorConverter : IValueConverter
{
    public Color SelectedColor { get; set; } = Colors.White;
    public Color UnselectedColor { get; set; } = Color.FromArgb("#888888");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intVal && parameter != null)
        {
            if (int.TryParse(parameter.ToString(), out int targetVal))
            {
                return intVal == targetVal ? SelectedColor : UnselectedColor;
            }
        }
        return UnselectedColor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
