using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetCleanupDialog : Window, INotifyPropertyChanged
{
    private readonly Asset _asset = null!;
    private readonly AddonManager _addonManager = null!;
    private ObservableCollection<CleanupPreviewItem> _previewItems;
    private bool _hasPreviewData;
    private int _totalAddonsCount;
    private int _toUnsubscribeCount;
    private int _toKeepCount;
    private List<string> _addonsToUnsubscribe;
    private bool _hasChanges;

    public AssetCleanupDialog()
    {
        InitializeComponent();
        DataContext = this;
        _previewItems = new ObservableCollection<CleanupPreviewItem>();
        _addonsToUnsubscribe = new List<string>();
    }

    public AssetCleanupDialog(Asset asset, AddonManager addonManager) : this()
    {
        _asset = asset;
        _addonManager = addonManager;
        AssetName = asset.Name;
    }

    public string AssetName { get; private set; } = string.Empty;
    
    public bool HasChanges => _hasChanges;

    public ObservableCollection<CleanupPreviewItem> PreviewItems => _previewItems;

    public bool HasPreviewData
    {
        get => _hasPreviewData;
        set
        {
            _hasPreviewData = value;
            OnPropertyChanged();
        }
    }

    public int TotalAddonsCount
    {
        get => _totalAddonsCount;
        set
        {
            _totalAddonsCount = value;
            OnPropertyChanged();
        }
    }

    public int ToUnsubscribeCount
    {
        get => _toUnsubscribeCount;
        set
        {
            _toUnsubscribeCount = value;
            OnPropertyChanged();
        }
    }

    public int ToKeepCount
    {
        get => _toKeepCount;
        set
        {
            _toKeepCount = value;
            OnPropertyChanged();
        }
    }

    private async void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await GeneratePreviewAsync();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetCleanupDialog.OnPreviewClick", ex);
        }
    }

    private async Task GeneratePreviewAsync()
    {
        try
        {
            PreviewItems.Clear();
            _addonsToUnsubscribe.Clear();

            var includeDisabled = DisabledAndExcludedRadio?.IsChecked ?? false;
            var allAddons = _addonManager.GetAllAddons();
            
            // アセット内のアドオンを取得（すべてのアドオンを対象とする）
            var assetAddons = new List<string>();
            
            if (_asset.ContainsAllAddons())
            {
                // Subscribeアセットの場合、すべてのアドオンを対象
                assetAddons = allAddons.Keys.ToList();
            }
            else
            {
                // 通常のアセットの場合、アセットに含まれるアドオンを対象
                assetAddons = _asset.Addons.Where(id => id != "*").ToList();
            }
            
            // デバッグ情報
            // System.Diagnostics.Debug.WriteLine($"Asset: {_asset.Name}");
            // System.Diagnostics.Debug.WriteLine($"ContainsAllAddons: {_asset.ContainsAllAddons()}");
            // System.Diagnostics.Debug.WriteLine($"Total addons in asset: {assetAddons.Count}");
            // System.Diagnostics.Debug.WriteLine($"AddonStates count: {_asset.AddonStates.Count}");

            // すべてのアドオンが対象
            TotalAddonsCount = assetAddons.Count;
            ToUnsubscribeCount = 0;
            ToKeepCount = 0;

            foreach (var addonId in assetAddons)
            {
                var shouldUnsubscribe = false;
                var reason = "";
                var statusIcon = "OK";
                var action = L.Get("AssetCleanup.Keep");
                var actionColor = "#4CAF50";
                var backgroundColor = "Transparent";

                if (!includeDisabled)
                {
                    // 「除外のみ」モード：問答無用で解除対象
                    shouldUnsubscribe = true;
                    reason = L.Get("AssetCleanup.WillUnsubscribe");
                    statusIcon = "X";
                    action = L.Get("AssetCleanup.Unsubscribe");
                    actionColor = "#F44336";
                    backgroundColor = "#1AF44336";
                }
                else
                {
                    // 「除外＋無効」モード：他のアセットで使用されていないもののみ解除
                    var isInOtherAsset = IsAddonInOtherAsset(addonId);
                    
                    if (!isInOtherAsset)
                    {
                        shouldUnsubscribe = true;
                        reason = L.Get("AssetCleanup.NotUsedInOtherAssets");
                        statusIcon = "!";
                        action = L.Get("AssetCleanup.Unsubscribe");
                        actionColor = "#FF9800";
                        backgroundColor = "#1AFF9800";
                    }
                    else
                    {
                        reason = L.Get("AssetCleanup.UsedInOtherAssets");
                        backgroundColor = "#1A4CAF50";
                    }
                }

                if (shouldUnsubscribe)
                {
                    _addonsToUnsubscribe.Add(addonId);
                    ToUnsubscribeCount++;
                }
                else
                {
                    ToKeepCount++;
                }

                // アドオン情報を取得
                string title = addonId;
                if (allAddons.TryGetValue(addonId, out var addon))
                {
                    title = !string.IsNullOrEmpty(addon.Title) ? addon.Title : addonId;
                }

                PreviewItems.Add(new CleanupPreviewItem
                {
                    AddonId = addonId,
                    Title = title,
                    Reason = reason,
                    StatusIcon = statusIcon,
                    Action = action,
                    ActionColor = actionColor,
                    BackgroundColor = backgroundColor
                });
            }

            HasPreviewData = true;
            CleanupButton.IsEnabled = _addonsToUnsubscribe.Count > 0;
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Format("Error.CleanupPreviewFailed", ex.Message));
        }
    }

    private bool IsAddonInOtherAsset(string addonId)
    {
        var config = _addonManager.GetConfiguration();
        foreach (var asset in config.Assets)
        {
            if (asset.Id == _asset.Id)
                continue;

            // Skip Subscribe asset (contains all addons) from the check
            if (asset.ContainsAllAddons())
                continue;

            if (asset.Addons.Contains(addonId))
            {
                // 他のアセットでの状態も確認
                var state = asset.GetAddonState(addonId);
                if (state != AddonState.Excluded) // 除外以外なら使用中とみなす
                {
                    return true;
                }
            }
        }
        return false;
    }

    private async void OnCleanupClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_addonsToUnsubscribe.Count == 0)
                return;

            var dialogService = new DialogService();

            var result = await dialogService.ShowConfirmAsync(
                L.Get("AssetCleanup.ConfirmTitle"),
                L.Format("AssetCleanup.ConfirmMessage", _addonsToUnsubscribe.Count));

            if (!result)
                return;

            IsEnabled = false;
            using var progressDialog = ProgressDialogService.Show(
                this,
                L.Get("Unsubscribe.ProgressTitle"),
                L.Format("Busy.Detail.AddonCount", _addonsToUnsubscribe.Count));
            progressDialog?.UpdateProgress(0, _addonsToUnsubscribe.Count);

            var total = _addonsToUnsubscribe.Count;
            var current = 0;

            foreach (var addonId in _addonsToUnsubscribe)
            {
                _asset.RemoveAddon(addonId);
                current++;
                progressDialog?.UpdateProgress(current, total);
            }

            IsEnabled = true;
            progressDialog?.Close();

            await _addonManager.SaveConfigurationAsync();
            _hasChanges = true;

            await dialogService.ShowInfoAsync(
                L.Get("AssetCleanup.ResultTitle"),
                L.Format("AssetCleanup.Success", total));

            Close();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.CleanupFailed", ex.Message));
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class CleanupPreviewItem
{
    public string AddonId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string StatusIcon { get; set; } = "";
    public string Action { get; set; } = "";
    public string ActionColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
}
