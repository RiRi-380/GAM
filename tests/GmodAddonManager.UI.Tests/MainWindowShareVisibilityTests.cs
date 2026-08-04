using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class MainWindowShareVisibilityTests
{
    [AvaloniaFact]
    public void ShareModeKeepsProductionMainWindowCenterContentMutuallyExclusive()
    {
        var shareState = new ShareVisibilityState();
        var shell = new MainWindowBindingShell(shareState);
        using var window = new MainWindow
        {
            DataContext = shell,
            WindowState = WindowState.Normal,
            Width = 1400,
            Height = 800
        };

        window.Show();
        try
        {
            var addonGridHost = Assert.IsType<Grid>(
                window.FindControl<Grid>("AddonGridHost"));
            var shareWorkspaceHost = Assert.IsType<Grid>(
                window.FindControl<Grid>("ShareWorkspaceHost"));
            var detailsPanel = Assert.IsType<GmodAddonManager.UI.Controls.AddonDetailsFloatingPanel>(
                window.FindControl<GmodAddonManager.UI.Controls.AddonDetailsFloatingPanel>(
                    "AddonDetailsPanel"));

            AssertVisibility(
                addonGridHost,
                shareWorkspaceHost,
                detailsPanel,
                addonGridExpected: true);

            shareState.IsShareMode = true;
            AssertVisibility(
                addonGridHost,
                shareWorkspaceHost,
                detailsPanel,
                addonGridExpected: false);

            shareState.IsShareMode = false;
            AssertVisibility(
                addonGridHost,
                shareWorkspaceHost,
                detailsPanel,
                addonGridExpected: true);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertVisibility(
        Grid addonGridHost,
        Grid shareWorkspaceHost,
        GmodAddonManager.UI.Controls.AddonDetailsFloatingPanel detailsPanel,
        bool addonGridExpected)
    {
        Assert.Equal(addonGridExpected, addonGridHost.IsEffectivelyVisible);
        Assert.Equal(!addonGridExpected, shareWorkspaceHost.IsEffectivelyVisible);
        Assert.Equal(addonGridExpected, detailsPanel.IsEffectivelyVisible);
        Assert.Equal(
            1,
            new[]
            {
                addonGridHost.IsEffectivelyVisible,
                shareWorkspaceHost.IsEffectivelyVisible
            }.Count(isVisible => isVisible));
    }

    private sealed class MainWindowBindingShell
    {
        public MainWindowBindingShell(ShareVisibilityState shareState)
        {
            AssetListViewModel = shareState;
        }

        public ShareVisibilityState AssetListViewModel { get; }

        public object AddonGridViewModel { get; } = new();
    }

    private sealed class ShareVisibilityState : INotifyPropertyChanged
    {
        private bool isShareMode;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsShareMode
        {
            get => isShareMode;
            set
            {
                if (value == isShareMode)
                {
                    return;
                }

                isShareMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAddonGridVisible));
            }
        }

        public bool IsAddonGridVisible => !IsShareMode;

        public bool IsCurrentGroupEmptyVisible { get; }

        public bool IsAssetMutationEnabled => !IsShareMode;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
