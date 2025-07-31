using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GmodAddonManager.UI.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? new SolidColorBrush(Color.Parse("#4A90E2")) : new SolidColorBrush(Colors.Red);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}