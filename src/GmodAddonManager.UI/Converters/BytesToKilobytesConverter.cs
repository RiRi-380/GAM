using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GmodAddonManager.UI.Converters
{
    public class BytesToKilobytesConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                return bytes / 1024.0;
            }
            return 0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}