using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace GmodAddonManager.UI.Views;

public partial class SimpleAssetCreateDialog : Window
{
    public SimpleAssetCreateDialog()
    {
        InitializeComponent();
        UpdateCreateButtonState();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AssetNameTextBox.Focus();
            AssetNameTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCreateButtonState();
    }

    private void OnNameKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CreateButton.IsEnabled)
        {
            TryCloseWithName();
        }
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        TryCloseWithName();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void TryCloseWithName()
    {
        var name = AssetNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Close(name);
    }

    private void UpdateCreateButtonState()
    {
        CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(AssetNameTextBox.Text);
    }
}
