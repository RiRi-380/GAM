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
            if (value is string assetId)
            {
                // ジャンクションアセットは赤色
                if (assetId == "junction-system-asset")
                {
                    return Colors.Red;
                }
                // それ以外はデフォルトのアクセントカラー
                else
                {
                    return Color.Parse("#4A90E2");
                }
            }
            
            return Color.Parse("#0078D4");
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}