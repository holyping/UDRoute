using System.Globalization;

namespace UDRoute.Maui.Converters;

public class TabIndexToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intVal && parameter != null)
        {
            if (int.TryParse(parameter.ToString(), out int targetVal))
            {
                return intVal == targetVal;
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
