using System.Globalization;

namespace UDRoute.Maui.Converters;

public class TabIndexToColorConverter : IValueConverter
{
    public Color SelectedColor { get; set; } = Color.FromArgb("#512BD4");
    public Color UnselectedColor { get; set; } = Colors.Transparent;

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
