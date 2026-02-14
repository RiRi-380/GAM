using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public enum AddAddonMethod
{
    Url,
    Select
}

public class AddAddonMethodResult
{
    public AddAddonMethod Method { get; set; }
}

public partial class AddAddonMethodDialog : Window
{
    public AddAddonMethodDialog()
    {
        InitializeComponent();

        var showSubscribeActions = ViewModelLocator.AddonManager?.DisableMode == DisableMode.Hard;
        SelectRadioButton.IsVisible = showSubscribeActions;
        if (!showSubscribeActions)
        {
            UrlRadioButton.IsChecked = true;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var result = new AddAddonMethodResult
        {
            Method = UrlRadioButton.IsChecked == true ? AddAddonMethod.Url : AddAddonMethod.Select
        };
        
        Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
