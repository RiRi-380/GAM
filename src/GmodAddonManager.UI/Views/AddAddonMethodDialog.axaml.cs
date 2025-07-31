using Avalonia.Controls;
using Avalonia.Interactivity;

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