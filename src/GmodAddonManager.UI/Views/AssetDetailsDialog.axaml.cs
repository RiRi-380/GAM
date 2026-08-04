using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Views;

public partial class AssetDetailsDialog : Window, INotifyPropertyChanged
{
    private AssetItemViewModel? assetViewModel;
    private AddonManager? addonManager;

    private string assetName = string.Empty;
    private string assetTypeText = string.Empty;
    private string assetStateText = string.Empty;
    private string memberCountText = "0";
    private string availableCountText = "0";
    private string missingCountText = "0";
    private string totalSizeText = "0 B";
    private string assetPath = string.Empty;
    private string memoText = string.Empty;
    private bool canEditName;
    private bool canEditMemo;
    private bool canManageMemberHistory;
    private bool isSmartAsset;
    private string smartRuleText = string.Empty;
    private string smartAutomationStatusText = string.Empty;
    private string smartAutomationDescription = string.Empty;

    public AssetDetailsDialog()
    {
        InitializeComponent();
        DataContext = this;
        Closed += OnClosed;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public string AssetName
    {
        get => assetName;
        set => SetField(ref assetName, value);
    }

    public bool CanEditName
    {
        get => canEditName;
        private set => SetField(ref canEditName, value);
    }

    public string AssetTypeText
    {
        get => assetTypeText;
        private set => SetField(ref assetTypeText, value);
    }

    public string AssetStateText
    {
        get => assetStateText;
        private set => SetField(ref assetStateText, value);
    }

    public string MemberCountText
    {
        get => memberCountText;
        private set => SetField(ref memberCountText, value);
    }

    public string AvailableCountText
    {
        get => availableCountText;
        private set => SetField(ref availableCountText, value);
    }

    public string MissingCountText
    {
        get => missingCountText;
        private set => SetField(ref missingCountText, value);
    }

    public string TotalSizeText
    {
        get => totalSizeText;
        private set => SetField(ref totalSizeText, value);
    }

    public string AssetPath
    {
        get => assetPath;
        private set => SetField(ref assetPath, value);
    }

    public string MemoText
    {
        get => memoText;
        set => SetField(ref memoText, value);
    }

    public bool CanEditMemo
    {
        get => canEditMemo;
        private set
        {
            if (SetField(ref canEditMemo, value))
            {
                OnPropertyChanged(nameof(IsMemoReadOnly));
            }
        }
    }

    public bool IsMemoReadOnly => !CanEditMemo;

    public bool CanManageMemberHistory
    {
        get => canManageMemberHistory;
        private set => SetField(ref canManageMemberHistory, value);
    }

    public bool IsSmartAsset
    {
        get => isSmartAsset;
        private set => SetField(ref isSmartAsset, value);
    }

    public string SmartRuleText
    {
        get => smartRuleText;
        private set => SetField(ref smartRuleText, value);
    }

    public string SmartAutomationStatusText
    {
        get => smartAutomationStatusText;
        private set => SetField(ref smartAutomationStatusText, value);
    }

    public string SmartAutomationDescription
    {
        get => smartAutomationDescription;
        private set => SetField(ref smartAutomationDescription, value);
    }

    public void SetAsset(
        AssetItemViewModel asset,
        AddonManager manager,
        IReadOnlySet<string>? availableAddonIds = null)
    {
        assetViewModel = asset ?? throw new ArgumentNullException(nameof(asset));
        addonManager = manager ?? throw new ArgumentNullException(nameof(manager));
        LoadOverview(availableAddonIds);
    }

    private void LoadOverview(IReadOnlySet<string>? availableAddonIds = null)
    {
        if (assetViewModel == null || addonManager == null)
        {
            return;
        }

        var configuration = addonManager.GetConfiguration();
        var model = configuration.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, assetViewModel.Id, StringComparison.Ordinal));
        if (model == null)
        {
            return;
        }

        AssetName = assetViewModel.Name;
        AssetTypeText = model.IsSystem
            ? L.Get("AssetDetails.TypeSystem")
            : model.IsSmart
                ? L.Get("AssetDetails.TypeSmart")
                : L.Get("AssetDetails.TypeFixed");
        AssetStateText = model.State switch
        {
            AddonState.Enabled => L.Get("AssetList.Enabled"),
            AddonState.Disabled => L.Get("AssetList.Disabled"),
            AddonState.Excluded => L.Get("AssetList.Excluded"),
            _ => model.State.ToString()
        };

        var ids = assetViewModel.GetAddonIds()
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "*")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var metadata = addonManager.GetAllAddons();
        var available = availableAddonIds ?? metadata
            .Where(entry => entry.Value.IsAvailable && !entry.Value.IsLocal)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var availableCount = ids.Count(available.Contains);
        long totalBytes = 0;
        foreach (var id in ids)
        {
            if (metadata.TryGetValue(id, out var addon) && addon.Size > 0)
            {
                totalBytes = totalBytes > long.MaxValue - addon.Size
                    ? long.MaxValue
                    : totalBytes + addon.Size;
            }
        }

        MemberCountText = ids.Length.ToString("N0", CultureInfo.CurrentCulture);
        AvailableCountText = availableCount.ToString("N0", CultureInfo.CurrentCulture);
        MissingCountText = Math.Max(0, ids.Length - availableCount)
            .ToString("N0", CultureInfo.CurrentCulture);
        TotalSizeText = FormatFileSize(totalBytes);
        AssetPath = BuildAssetPath(model, configuration);
        MemoText = model.Memo ?? string.Empty;
        CanEditName = assetViewModel.CanEditName;
        CanEditMemo = !model.IsSystem;
        CanManageMemberHistory = assetViewModel.CanManageVersions;

        IsSmartAsset = model.IsSmart;
        SmartRuleText = assetViewModel.SmartRuleText;
        SmartAutomationStatusText = assetViewModel.SmartAutomationStatusText;
        SmartAutomationDescription = assetViewModel.SmartAutomationDescription;
    }

    private async void OnSaveDetails(object? sender, RoutedEventArgs e)
    {
        if (!CanEditMemo || assetViewModel == null || addonManager == null)
        {
            return;
        }

        var candidateName = (AssetName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            SetSaveStatus(L.Get("AssetDetails.NameRequired"), isError: true);
            return;
        }

        SaveDetailsButton.IsEnabled = false;
        ClearSaveStatus();
        try
        {
            var current = addonManager.GetConfiguration().Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, assetViewModel.Id, StringComparison.Ordinal));
            if (current == null)
            {
                SetSaveStatus(L.Get("AssetDetails.MemoAssetNotFound"), isError: true);
                return;
            }

            if (!string.Equals(current.Name, candidateName, StringComparison.OrdinalIgnoreCase) &&
                addonManager.AssetNameExists(candidateName))
            {
                SetSaveStatus(
                    L.Format("Error.AssetNameAlreadyExists", candidateName),
                    isError: true);
                return;
            }

            await addonManager.ApplyAssetEditAsync(
                assetViewModel.Id,
                candidateName,
                sourceImagePath: null,
                crop: null,
                removeImage: false);
            await addonManager.UpdateAssetMemoAsync(
                assetViewModel.Id,
                MemoText);
            var latest = addonManager.GetConfiguration().Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, assetViewModel.Id, StringComparison.Ordinal));
            if (latest != null)
            {
                assetViewModel.RefreshFromModel(latest);
                LoadOverview();
            }
            SetSaveStatus(L.Get("AssetDetails.Saved"), isError: false);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetDetailsDialog.SaveDetails", ex);
            SetSaveStatus(
                L.Format("AssetDetails.SaveFailed", ex.Message),
                isError: true);
        }
        finally
        {
            SaveDetailsButton.IsEnabled = CanEditMemo;
        }
    }

    private async void OnMemberHistory(object? sender, RoutedEventArgs e)
    {
        if (!CanManageMemberHistory || assetViewModel == null || addonManager == null)
        {
            return;
        }

        try
        {
            var model = addonManager.GetConfiguration().Assets.FirstOrDefault(
                asset => asset.Id == assetViewModel.Id);
            if (model == null || model.IsSystem || model.IsSmart)
            {
                CanManageMemberHistory = false;
                return;
            }

            MemberHistoryButton.IsEnabled = false;
            var dialog = new VersionManagementDialog(model, addonManager);
            await dialog.ShowDialog(this);

            var latest = addonManager.GetConfiguration().Assets.FirstOrDefault(
                asset => asset.Id == assetViewModel.Id);
            if (latest != null)
            {
                assetViewModel.RefreshFromModel(latest);
                LoadOverview();
                ViewModelLocator.AssetListViewModel?.RefreshAssetStates();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetDetailsDialog.History", ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.VersionManagementFailed"));
        }
        finally
        {
            MemberHistoryButton.IsEnabled = CanManageMemberHistory;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) ||
            string.IsNullOrEmpty(e.PropertyName))
        {
            var memo = MemoText;
            var name = AssetName;
            LoadOverview();
            MemoText = memo;
            if (CanEditName)
            {
                AssetName = name;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
    }

    private void ClearSaveStatus()
    {
        SaveStatusTextBlock.Text = string.Empty;
        SaveStatusTextBlock.IsVisible = false;
        SaveStatusTextBlock.Classes.Remove("error");
    }

    private void SetSaveStatus(string message, bool isError)
    {
        SaveStatusTextBlock.Text = message;
        SaveStatusTextBlock.IsVisible = true;
        SaveStatusTextBlock.Classes.Remove("error");
        if (isError)
        {
            SaveStatusTextBlock.Classes.Add("error");
        }
    }

    private static string BuildAssetPath(Asset asset, Configuration configuration)
    {
        var segments = new List<string> { asset.Name };
        var groupsById = configuration.AssetGroups.ToDictionary(
            group => group.Id,
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parentId = asset.ParentGroupId;
        while (!string.IsNullOrWhiteSpace(parentId) &&
               visited.Add(parentId) &&
               groupsById.TryGetValue(parentId, out var group))
        {
            segments.Add(group.Name);
            parentId = group.ParentGroupId;
        }

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{value:N2} {units[unitIndex]}";
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
