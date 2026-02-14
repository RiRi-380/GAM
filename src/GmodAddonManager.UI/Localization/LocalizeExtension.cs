using Avalonia.Data;
using Avalonia.Markup.Xaml;
using GmodAddonManager.UI.Services;
using System;

namespace GmodAddonManager.UI.Localization
{
    public class LocalizeExtension : MarkupExtension
    {
        public string Key { get; set; } = "";
        
        public LocalizeExtension() { }
        
        public LocalizeExtension(string key)
        {
            Key = key;
        }
        
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            // 動的更新のためにバインディングを返す
            var binding = new Binding
            {
                Source = LocalizationManager.Instance,
                Path = $"[{Key}]",
                Mode = BindingMode.OneWay
            };
            
            return binding;
        }
    }
}