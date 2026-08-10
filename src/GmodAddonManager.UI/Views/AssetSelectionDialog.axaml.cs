using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetSelectionDialog : Window, IDisposable
{
    private const double BreadcrumbWheelPixels = 60;

    private string? selectedAssetId;
    private AssetTargetPickerViewModel? pickerViewModel;
    private readonly Func<string, string?, Task<AssetItemViewModel?>>? createAssetAsync;
    private readonly Func<string, string?>? nameValidator;
    private bool disposed;

    public AssetSelectionDialog()
    {
        InitializeComponent();
        AddHandler(
            KeyDownEvent,
            OnWindowNavigationKeyDown,
            RoutingStrategies.Tunnel);
        AssetListBox.AddHandler(
            KeyDownEvent,
            OnListKeyDown,
            RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public AssetSelectionDialog(
        AddonManager addonManager,
        IEnumerable<AssetItemViewModel> assets,
        Func<string, string?, Task<AssetItemViewModel?>>? createAssetAsync = null)
        : this()
    {
        pickerViewModel = new AssetTargetPickerViewModel(addonManager, assets);
        pickerViewModel.Navigated += OnPickerNavigated;
        DataContext = pickerViewModel;

        this.createAssetAsync = createAssetAsync;
        nameValidator = candidateName => addonManager.AssetNameExists(candidateName)
            ? L.Format("Error.AssetNameAlreadyExists", candidateName)
            : null;
        CreateAssetButton.IsVisible = createAssetAsync != null;
    }

    internal AssetTargetPickerViewModel? PickerViewModel => pickerViewModel;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var entry = AssetListBox.SelectedItem as AssetListEntryViewModel;
        selectedAssetId = entry?.Asset?.Id;
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedAssetId);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!disposed)
                {
                    AssetListBox.Focus();
                }
            },
            DispatcherPriority.Input);
        ScheduleBreadcrumbScrollToEnd();
    }

    private void OnWindowNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Back ||
             (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt))) &&
            pickerViewModel?.IsInsideGroup == true)
        {
            pickerViewModel.ReturnToParent();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var entry = AssetListBox.SelectedItem as AssetListEntryViewModel;
            if (entry?.IsGroup == true)
            {
                pickerViewModel?.OpenGroup(entry);
            }
            else if (entry?.Asset != null)
            {
                Close(entry.Asset.Id);
            }
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Up || e.Key == Key.Down) &&
            AssetListBox.SelectedItem == null)
        {
            var initialSelection = pickerViewModel?.Entries.FirstOrDefault();
            if (initialSelection != null)
            {
                AssetListBox.SelectedItem = initialSelection;
                AssetListBox.ScrollIntoView(initialSelection);
                AssetListBox.Focus();
            }
            e.Handled = true;
        }
    }

    private void OnEntryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left ||
            sender is not Control control ||
            control.DataContext is not AssetListEntryViewModel entry ||
            !entry.IsGroup)
        {
            return;
        }

        pickerViewModel?.OpenGroup(entry);
        e.Handled = true;
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control ||
            control.DataContext is not AssetListEntryViewModel entry ||
            entry.Asset == null)
        {
            return;
        }

        Close(entry.Asset.Id);
        e.Handled = true;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        pickerViewModel?.ReturnToParent();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(selectedAssetId);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void OnCreateAssetClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (createAssetAsync == null || pickerViewModel == null)
            {
                return;
            }

            CreateAssetErrorText.Text = string.Empty;
            CreateAssetErrorText.IsVisible = false;
            var dialog = new SimpleAssetCreateDialog(
                allowSmartAssets: false,
                allowAssetGroups: false,
                eligibleGroupAssets: null,
                eligibleChildGroups: null,
                nameValidator);
            var name = await dialog.ShowDialog<string?>(this);
            if (disposed ||
                pickerViewModel == null ||
                string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var newAsset = await createAssetAsync(
                name.Trim(),
                pickerViewModel.CurrentGroupId);
            if (disposed || pickerViewModel == null || newAsset == null)
            {
                return;
            }

            var entry = pickerViewModel.RegisterTargetAsset(newAsset, ownsAsset: true);
            if (entry == null)
            {
                return;
            }

            AssetListBox.SelectedItem = entry;
            AssetListBox.ScrollIntoView(entry);
            AssetListBox.Focus();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetSelectionDialog.OnCreateAssetClick", ex);
            if (!disposed)
            {
                CreateAssetErrorText.Text = L.Get("Error.AssetCreateFailedGeneric");
                CreateAssetErrorText.IsVisible = true;
            }
        }
    }

    private void OnPickerNavigated(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        selectedAssetId = null;
        AssetListBox.SelectedItem = null;
        OkButton.IsEnabled = false;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!disposed)
                {
                    AssetListBox.Focus();
                }
            },
            DispatcherPriority.Input);
        ScheduleBreadcrumbScrollToEnd();
    }

    private void OnBreadcrumbPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
            ? e.Delta.X
            : e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        var maxOffset = Math.Max(
            0,
            scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        var nextOffset = Math.Clamp(
            scrollViewer.Offset.X - delta * BreadcrumbWheelPixels,
            0,
            maxOffset);
        if (Math.Abs(nextOffset - scrollViewer.Offset.X) < double.Epsilon)
        {
            return;
        }

        scrollViewer.Offset = new Vector(nextOffset, scrollViewer.Offset.Y);
        e.Handled = true;
    }

    private void OnBreadcrumbSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ScheduleBreadcrumbScrollToEnd();
    }

    private void ScheduleBreadcrumbScrollToEnd()
    {
        if (disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (disposed)
                {
                    return;
                }

                var maxOffset = Math.Max(
                    0,
                    BreadcrumbScrollViewer.Extent.Width -
                    BreadcrumbScrollViewer.Viewport.Width);
                BreadcrumbScrollViewer.Offset = new Vector(
                    maxOffset,
                    BreadcrumbScrollViewer.Offset.Y);
            },
            DispatcherPriority.Render);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Opened -= OnOpened;
        Closed -= OnClosed;
        RemoveHandler(KeyDownEvent, OnWindowNavigationKeyDown);
        AssetListBox.RemoveHandler(KeyDownEvent, OnListKeyDown);
        if (pickerViewModel != null)
        {
            pickerViewModel.Navigated -= OnPickerNavigated;
            pickerViewModel.Dispose();
            pickerViewModel = null;
        }
        DataContext = null;
        GC.SuppressFinalize(this);
    }
}
