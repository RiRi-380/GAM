using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GmodAddonManager.UI.ViewModels
{
    public class BoolToPathDataConverter : IValueConverter
    {
        public Geometry? TrueValue { get; set; }
        public Geometry? FalseValue { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? TrueValue : FalseValue;
            }
            return FalseValue;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}