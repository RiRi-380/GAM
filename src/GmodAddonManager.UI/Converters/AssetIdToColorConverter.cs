using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GmodAddonManager.UI.Converters
{
    public class AssetIdToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return Color.Parse("#4A90E2");
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
