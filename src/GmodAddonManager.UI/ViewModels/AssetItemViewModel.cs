using GmodAddonManager.Core.Models;

using GmodAddonManager.Core.Services;

using GmodAddonManager.UI.Services;

using GmodAddonManager.UI.Views;

using GmodAddonManager.UI.Models;

using ReactiveUI;

using System;

using System.ComponentModel;

using System.Reactive;
using System.Reactive.Linq;

using System.Threading.Tasks;

using System.Collections.Generic;

using System.Linq;

using System.IO;

using Avalonia.Media.Imaging;
using Avalonia.Threading;



namespace GmodAddonManager.UI.ViewModels;



// 繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ陦ｨ縺吝・謖吝梛

public enum AssetState

{

    Enabled,   // 隴帷甥譟・

    Disabled,  // 辟｡蜉ｹ

    Excluded   // 髯､螟・

}



public class AssetItemViewModel : ViewModelBase, IDisposable

{

    private static bool memberHistoryExperimentalEnabled;

    public const string GmodDisabledSystemAssetId =

        GmodDisabledAddonReconciliationService.SystemAssetId;

    public const string GmodDisabledSystemAssetName =

        GmodDisabledAddonReconciliationService.SystemAssetName;

    private Asset asset;

    private readonly AddonManager addonManager;

    private bool isSelected;

    private bool isEnabled;

    private int addonCount;

    private bool isSystem;

    private AssetState assetState;

    private bool isFavorite;

    private bool isCurrent;

    private Bitmap? assetImageBitmap;



        public AssetItemViewModel(

            Asset asset, 

            AddonManager addonManager,

            PendingChangeManager pendingChangeManager,

            GmodProcessWatcher processWatcher)

        {

        this.asset = asset;

        this.addonManager = addonManager;

        // 蛻晄悄蛟､險ｭ螳・

        Id = asset.Id;

        name = asset.Name;

        var displayState = asset.State;

        IsEnabled = displayState == AddonState.Enabled;

        IsSystem = asset.IsSystem || IsGmodDisabledAsset;

        UpdateAddonCount();

        

        // 繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ險ｭ螳夲ｼ・efaultAddonState縺九ｉ・・

        assetState = (AssetState)displayState;
        isFavorite = asset.IsFavorite;



        // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・

        // Commands

        ToggleEnabledCommand = ReactiveCommand.CreateFromTask(

            ToggleEnabledAsync,

            this.WhenAnyValue(x => x.CanToggleAssetActive));

        DeleteCommand = ReactiveCommand.CreateFromTask(

            DeleteAsync,

            this.WhenAnyValue(x => x.IsSystem, isSystem => !isSystem));

        ShowDetailsCommand = ReactiveCommand.CreateFromTask(ShowDetailsDialogAsync);
        SetEnabledCommand = ReactiveCommand.CreateFromTask(

            SetEnabledAsync,

            this.WhenAnyValue(x => x.CanEditAddonDefaultState));

        SetDisabledCommand = ReactiveCommand.CreateFromTask(

            SetDisabledAsync,

            this.WhenAnyValue(x => x.CanEditAddonDefaultState));

        SetExcludedCommand = ReactiveCommand.CreateFromTask(

            SetExcludedAsync,

            this.WhenAnyValue(x => x.CanSetExcluded));

        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(

            ToggleFavoriteAsync,

            this.WhenAnyValue(x => x.CanFavorite));

        VersionManageCommand = ReactiveCommand.CreateFromTask(
            VersionManageAsync,
            this.WhenAnyValue(x => x.CanManageVersions));

        EditImageCommand = ReactiveCommand.CreateFromTask(

            EditAsync,

            System.Reactive.Linq.Observable.Select(
                 this.WhenAnyValue(x => x.IsSystem),
                 _ => CanEditImage));
        EditCommand = EditImageCommand;

        // Localization

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;



        _ = LoadAssetImageAsync();

    }



    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)

    {

        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))

        {

            // 繧ｷ繧ｹ繝・Β繧｢繧ｻ繝・ヨ縺ｮ蜷榊燕繧呈峩譁ｰ

            if (IsSystem)

            {

                this.RaisePropertyChanged(nameof(Name));

            }



            this.RaisePropertyChanged(nameof(AddonCountDisplay));

            this.RaisePropertyChanged(nameof(DisabledAddonCountDisplay));

            this.RaisePropertyChanged(nameof(VersionDisplay));

            this.RaisePropertyChanged(nameof(EnabledStateLabel));
            this.RaisePropertyChanged(nameof(DisabledStateLabel));
            this.RaisePropertyChanged(nameof(ExcludedStateLabel));
            this.RaisePropertyChanged(nameof(EnabledStateTooltip));
            this.RaisePropertyChanged(nameof(DisabledStateTooltip));
            this.RaisePropertyChanged(nameof(ExcludedStateTooltip));
            this.RaisePropertyChanged(nameof(FavoriteButtonText));
            this.RaisePropertyChanged(nameof(SmartBadgeText));
            this.RaisePropertyChanged(nameof(SmartRuleText));
            this.RaisePropertyChanged(nameof(SmartAutomationStatusText));
            this.RaisePropertyChanged(nameof(SmartAutomationDescription));
        }

    }



    public string Id { get; }

    

    private string name;

    public string Name 

    { 

        get

        {

            // 繧ｷ繧ｹ繝・Β繧｢繧ｻ繝・ヨ縺ｮ蜷榊燕繧偵Ο繝ｼ繧ｫ繝ｩ繧､繧ｺ

            if (IsSystem)

            {

                if (Id == SystemAssetDefinitions.SubscribeId)

                    return L.Get("Asset.SubscribeAsset");

                if (Id == GmodDisabledSystemAssetId)

                    return GmodDisabledSystemAssetName;

            }

            return name;

        }

    }



    public bool IsSelected

    {

        get => isSelected;

        set => SetAndRaise(ref isSelected, value);

    }



    public bool IsEnabled

    {

        get => isEnabled;

        set
        {
            SetAndRaise(ref isEnabled, value);
            this.RaisePropertyChanged(nameof(AssetActiveLabel));
            this.RaisePropertyChanged(nameof(AssetActiveTooltip));
        }

    }



    public int AddonCount

    {

        get => addonCount;

        private set

        {

            SetAndRaise(ref addonCount, value);

            this.RaisePropertyChanged(nameof(AddonCountDisplay));

            this.RaisePropertyChanged(nameof(DisabledAddonCountDisplay));

        }

    }



    public string AddonCountDisplay => IsGmodDisabledAsset

        ? DisabledAddonCountDisplay

        : L.Format("AssetList.AddonCount", AddonCount);



    public string DisabledAddonCountDisplay => L.Format("AssetList.DisabledCount", AddonCount);



    public bool IsSystem

    {

        get => isSystem;

        private set

        {

            SetAndRaise(ref isSystem, value);

            this.RaisePropertyChanged(nameof(CanEditImage));

            this.RaisePropertyChanged(nameof(CanEditName));

            this.RaisePropertyChanged(nameof(CanSetExcluded));

        }

    }

    

    public bool CanEditImage => !IsSystem || IsSubscribeAsset;

    public bool CanEditName => !IsSystem;

    

    // 蜑企勁繝懊ち繝ｳ繧定｡ｨ遉ｺ縺吶ｋ縺九←縺・

    public bool CanDelete => !IsSystem;
    public bool CanManageVersions =>
        memberHistoryExperimentalEnabled && !IsSystem && !IsSmart;
    public bool CanToggleAssetActive => IsSubscribeAsset || !IsSystem;
    public bool CanEditAddonDefaultState =>
        IsSubscribeAsset || IsGmodDisabledAsset || !IsSystem;
    public bool IsSubscribeAsset => Id == SystemAssetDefinitions.SubscribeId;
    public bool IsGmodDisabledAsset => Id == GmodDisabledSystemAssetId;
    public bool IncludesUnavailableMembership =>
        IsSubscribeAsset || IsGmodDisabledAsset || asset.RetainMissingReferences;
    public int StateColumnSpan => IsSubscribeAsset || IsGmodDisabledAsset ? 2 : 1;
    public bool CanSetExcluded =>
        !IsSystem || IsSubscribeAsset || IsGmodDisabledAsset;
    public bool CanFavorite => !IsSystem;
    public bool IsSmart => asset.IsSmart;
    public bool IsSmartAutomationFrozen =>
        asset.SmartAutomationState?.Status ==
        SmartAssetAutomationStatus.FrozenInvalidRule;
    public string SmartBadgeText => L.Get("SmartAsset.Badge");
    public string SmartRuleText
    {
        get
        {
            var rule = asset.MembershipRule;
            if (rule == null)
            {
                return string.Empty;
            }

            var kindLabel = rule.Kind == AssetMembershipRuleKind.Type
                ? L.Get("SmartAsset.TypeLabel")
                : L.Get("SmartAsset.TagLabel");
            var valueKey = (rule.Kind == AssetMembershipRuleKind.Type
                ? "AddonType."
                : "AddonTag.") + rule.Value;
            var localizedValue = L.Get(valueKey);
            if (string.Equals(localizedValue, valueKey, StringComparison.Ordinal))
            {
                localizedValue = rule.Value;
            }

            return L.Format("SmartAsset.RuleFormat", kindLabel, localizedValue);
        }
    }
    public string SmartAutomationStatusText => IsSmartAutomationFrozen
        ? L.Get("SmartAsset.Status.Frozen")
        : L.Get("SmartAsset.Status.Active");
    public string SmartAutomationDescription => IsSmartAutomationFrozen
        ? L.Get("SmartAsset.Status.FrozenDescription")
        : L.Get("SmartAsset.Status.ActiveDescription");
    public string EnabledStateLabel => IsSubscribeAsset ? "ON" : L.Get("AssetList.Enabled");
    public string DisabledStateLabel => IsSubscribeAsset ? "OFF" : L.Get("AssetList.Disabled");
    public string ExcludedStateLabel => IsSubscribeAsset
        ? L.Get("AssetList.ExcludeAll")
        : L.Get("AssetList.Excluded");
    public string EnabledStateTooltip => IsSubscribeAsset
        ? L.Get("AssetList.SubscribeEnabledTooltip")
        : L.Get("AssetList.EnabledTooltip");
    public string DisabledStateTooltip => IsSubscribeAsset
        ? L.Get("AssetList.SubscribeDisabledTooltip")
        : L.Get("AssetList.DisabledTooltip");
    public string ExcludedStateTooltip => IsSubscribeAsset
        ? L.Get("AssetList.SubscribeExcludedTooltip")
        : L.Get("AssetList.ExcludedTooltip");
    public string AssetActiveLabel => IsEnabled
        ? L.Get("AssetList.AssetActiveOn")
        : L.Get("AssetList.AssetActiveOff");
    public string AssetActiveTooltip => IsEnabled
        ? L.Get("AssetList.AssetActiveOnTooltip")
        : L.Get("AssetList.AssetActiveOffTooltip");

    public ReactiveCommand<Unit, Unit> ToggleEnabledCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public ReactiveCommand<Unit, Unit> ShowDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> SetEnabledCommand { get; }

    public ReactiveCommand<Unit, Unit> SetDisabledCommand { get; }

    public ReactiveCommand<Unit, Unit> SetExcludedCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }

    public ReactiveCommand<Unit, Unit> VersionManageCommand { get; }

    public ReactiveCommand<Unit, Unit> EditImageCommand { get; }

    public ReactiveCommand<Unit, Unit> EditCommand { get; }    

    public static void ApplyGlobalSettings(AppSettings settings)

    {

        ArgumentNullException.ThrowIfNull(settings);

        memberHistoryExperimentalEnabled = settings.EnableMemberHistoryExperimental;

    }

    public void NotifySettingsChanged()

    {

        this.RaisePropertyChanged(nameof(CanManageVersions));

    }

    // 繝舌・繧ｸ繝ｧ繝ｳ陦ｨ遉ｺ

    public string VersionDisplay 

    {

        get

        {

            if (asset.CurrentVersion <= 0)

            {

                return L.Get("Version.NotSaved");

            }

            return asset.CurrentVersion > 0 &&
                   addonManager.AssetVersionHasMembershipChanges(
                       Id,
                       asset.CurrentVersion)
                ? L.Format("Version.ChangedFormat", asset.CurrentVersion)
                : $"v{asset.CurrentVersion}";

        }

    }

    

    // 迥ｶ諷九・繝ｭ繝代ユ繧｣

    public bool IsEnabledState => assetState == AssetState.Enabled;

    public bool IsDisabledState => assetState == AssetState.Disabled;

    public bool IsExcludedState => assetState == AssetState.Excluded;



    public bool IsFavorite
    {
        get => isFavorite;
        private set
        {
            SetAndRaise(ref isFavorite, value);
            this.RaisePropertyChanged(nameof(FavoriteButtonText));
        }
    }

    public string FavoriteButtonText => IsFavorite
        ? L.Get("AddonDetails.RemovedFromFavorites")
        : L.Get("AddonDetails.AddedToFavorites");



    public Bitmap? AssetImageBitmap

    {

        get => assetImageBitmap;

        private set

        {

            if (assetImageBitmap == value)

            {

                return;

            }



            assetImageBitmap?.Dispose();

            assetImageBitmap = value;

            this.RaisePropertyChanged(nameof(AssetImageBitmap));

            this.RaisePropertyChanged(nameof(HasCustomImage));
            this.RaisePropertyChanged(nameof(HasNoCustomImage));

        }

    }



    public bool HasCustomImage => AssetImageBitmap != null;

    public bool HasNoCustomImage => AssetImageBitmap == null;



    public bool IsCurrent

    {

        get => isCurrent;

        set

        {

            SetAndRaise(ref isCurrent, value);

            this.RaisePropertyChanged(nameof(BorderColor));

        }

    }

    

    // 譫縺ｮ濶ｲ・育樟蝨ｨ縺ｮ繧｢繧ｻ繝・ヨ: 髱偵∝・髢狗憾諷・ 邱・襍､・・

    public string BorderColor

    {

        get

        {

            if (IsCurrent) return "#4A90E2"; // Blue for current asset

            return "Transparent";

        }

    }

    

    // 迥ｶ諷九↓蠢懊§縺溯牡

    public string AssetStateColor

    {

        get

        {

            return assetState switch

            {

                AssetState.Enabled => "#4CAF50",   // 驍ｱ繝ｻ

                AssetState.Disabled => "#FF9800",  // 繧ｪ繝ｬ繝ｳ繧ｸ

                AssetState.Excluded => "#F44336",  // 襍､

                _ => "#9E9E9E"  // 繧ｰ繝ｬ繝ｼ

            };

        }

    }



    private async Task ToggleEnabledAsync()

    {

        if (!CanToggleAssetActive)
        {
            return;
        }

        await SetAssetStateAsync(IsEnabledState ? AddonState.Disabled : AddonState.Enabled);

    }



    private async Task EditAsync()

    {

        try

        {

            if (!CanEditImage)

            {

                return;

            }



            var mainWindow = await GetMainWindow();

            if (mainWindow == null)

            {

                return;

            }



            var dialog = new AssetEditDialog(addonManager.ResolveAssetImagePath(asset));

            var result = await dialog.ShowDialog<AssetEditResult?>(mainWindow);

            if (result == null || !result.IsSaved)

            {

                return;

            }



            await addonManager.ApplyAssetEditAsync(
                Id,
                asset.Name,
                result.SourceImagePath,
                result.Crop,
                result.RemoveImage);

            var updated = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == Id);

            if (updated != null)

            {

                RefreshFromModel(updated);

            }

            ViewModelLocator.AssetListViewModel?.LoadAssets();

        }

        catch (Exception)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(

                L.Get("Error.Title"),

                L.Get("Error.AssetEditFailed"));

        }

    }



    private async Task DeleteAsync()

    {

        if (!CanDelete)

        {

            return;

        }



        try

        {

            var dialogService = new DialogService();

            var confirmed = await dialogService.ShowConfirmAsync(

                L.Get("Confirm.Title"),

                L.Format("Confirm.DeleteAsset", Name));

            if (!confirmed)

            {

                return;

            }



            await addonManager.DeleteAssetAsync(Id);



            ViewModelLocator.AssetListViewModel?.LoadAssets();

            await UpdateAddonGridAsync();

        }

        catch (Exception ex)

        {

            SafeFileLogger.TryLogException("AssetItemViewModel.DeleteAsync", ex);

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.AssetDeleteFailed", ex.Message));

        }

    }





    public List<string> GetAddonIds()

    {

        // ContainsAllAddons 縺ｮ蝣ｴ蜷医・螳滄圀縺ｮ蜈ｨ繧｢繝峨が繝ｳID繧定ｿ斐☆

        if (asset.ContainsAllAddons())

        {

            // Subscribe represents the current Steam subscription set, not
            // metadata retained only for missing Custom Asset references.
            return addonManager.GetResolvedAddonStates().Keys.ToList();

        }

        else

        {

            // *繧帝勁螟悶＠縺ｦ霑斐☆・亥ｿｵ縺ｮ縺溘ａ・・

            return asset.Addons.Where(id => id != "*").ToList();

        }

    }



    private void UpdateAddonCount()

    {

        if (asset.ContainsAllAddons())

        {

            // 蜈ｨ繧｢繝峨が繝ｳ繧貞性繧蝣ｴ蜷・縲∝ｮ滄圀縺ｮ蜈ｨ繧｢繝峨が繝ｳ謨ｰ繧定｡ｨ遉ｺ

            AddonCount = addonManager.GetResolvedAddonStates().Count;

        }

        else

        {

            AddonCount = asset.Addons.Count;

        }

    }



    private async Task LoadAssetImageAsync()
    {
        Bitmap? loadedBitmap = null;
        try
        {
            if (disposedValue)
            {
                return;
            }
            var resolvedPath = addonManager.ResolveAssetImagePath(asset);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                await SetAssetImageBitmapAsync(null);
                return;
            }
            loadedBitmap = await Task.Run(() => new Bitmap(resolvedPath));
            await SetAssetImageBitmapAsync(loadedBitmap);
            loadedBitmap = null;
        }
        catch (Exception ex)
        {
            loadedBitmap?.Dispose();
            if (disposedValue)
            {
                return;
            }
            SafeFileLogger.TryLogException("AssetItemViewModel.LoadAssetImageAsync", ex);
            await SetAssetImageBitmapAsync(null);
        }
    }
    private async Task SetAssetImageBitmapAsync(Bitmap? bitmap)
    {
        if (disposedValue)
        {
            bitmap?.Dispose();
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (disposedValue)
            {
                bitmap?.Dispose();
                return;
            }
            AssetImageBitmap = bitmap;
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (disposedValue)
            {
                bitmap?.Dispose();
                return;
            }
            AssetImageBitmap = bitmap;
        });
    }



    public void RefreshFromModel(Asset updatedAsset)

    {

        // Update model snapshot

        this.asset = updatedAsset;



        name = updatedAsset.Name;

        var displayState = updatedAsset.State;

        IsEnabled = displayState == AddonState.Enabled;

        assetState = (AssetState)displayState;
        IsFavorite = updatedAsset.IsFavorite;

        UpdateAddonCount();

        this.RaisePropertyChanged(nameof(Name));



        this.RaisePropertyChanged(nameof(IsEnabledState));

        this.RaisePropertyChanged(nameof(IsDisabledState));

        this.RaisePropertyChanged(nameof(IsExcludedState));

        this.RaisePropertyChanged(nameof(AssetStateColor));
        this.RaisePropertyChanged(nameof(AssetActiveLabel));
        this.RaisePropertyChanged(nameof(AssetActiveTooltip));
        this.RaisePropertyChanged(nameof(VersionDisplay));
        this.RaisePropertyChanged(nameof(IsSmart));
        this.RaisePropertyChanged(nameof(CanManageVersions));
        this.RaisePropertyChanged(nameof(IncludesUnavailableMembership));
        this.RaisePropertyChanged(nameof(IsSmartAutomationFrozen));
        this.RaisePropertyChanged(nameof(SmartBadgeText));
        this.RaisePropertyChanged(nameof(SmartRuleText));
        this.RaisePropertyChanged(nameof(SmartAutomationStatusText));
        this.RaisePropertyChanged(nameof(SmartAutomationDescription));



        _ = LoadAssetImageAsync();

    }



    private IDisposable? BeginBusy(string title, string? detail = null)

    {

        return ViewModelLocator.MainWindowViewModel?.BeginBusy(title, detail);

    }



    private void UpdateBusyProgress(int current, int total)

    {

        ViewModelLocator.MainWindowViewModel?.UpdateBusyProgress(current, total);

    }



    private async Task<Avalonia.Controls.Window?> GetMainWindow()

    {

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)

        {

            return desktop.MainWindow;

        }

        return null;

    }

    private async Task HandleAssetOperationErrorAsync(string context, Exception ex, bool showDialog)

    {

        SafeFileLogger.TryLogException(context, ex);

        if (!showDialog)

        {

            return;

        }

        try

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(

                L.Get("Error.Title"),

                L.Format("Error.StateChangeFailed", ex.Message));

        }

        catch (Exception dialogEx)

        {

            SafeFileLogger.TryLogException($"{context}.ShowError", dialogEx);

        }

    }



    private Task SetEnabledAsync()

    {

        return SetAssetStateAsync(AddonState.Enabled);

    }



    private Task SetDisabledAsync()

    {

        return SetAssetStateAsync(AddonState.Disabled);

    }



    private Task SetExcludedAsync()

    {

        return CanSetExcluded

            ? SetAssetStateAsync(AddonState.Excluded)

            : Task.CompletedTask;

    }



    private async Task SetAssetStateAsync(AddonState targetState)

    {

        if (!CanEditAddonDefaultState)

        {

            return;

        }

        if ((AddonState)assetState == targetState)

        {

            return;

        }



        try

        {

            await addonManager.ApplyAssetDefaultStateAsync(Id, targetState);



            var updated = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == Id);

            if (updated != null)

            {

                RefreshFromModel(updated);

            }



            await UpdateAddonGridAsync();

        }

        catch (Exception ex)

        {

            await HandleAssetOperationErrorAsync("AssetItemViewModel.SetAssetStateAsync", ex, showDialog: true);

        }

    }





    private async Task ToggleFavoriteAsync()

    {

        if (!CanFavorite)

        {

            return;

        }



        var targetFavorite = !IsFavorite;

        try

        {

            await addonManager.SetAssetFavoriteAsync(Id, targetFavorite);

            IsFavorite = targetFavorite;

            ViewModelLocator.AssetListViewModel?.LoadAssets();

        }

        catch (Exception ex)

        {

            SafeFileLogger.TryLogException("AssetItemViewModel.ToggleFavoriteAsync", ex);

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetEditFailed"));

        }

    }



    private async Task ReloadAddons()

    {

        try

        {

            // MainWindowViewModel繧貞叙蠕励＠縺ｦ繝ｪ繝ｭ繝ｼ繝・

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)

            {

                if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)

                {

                    await mainVm.RefreshAddonsAsync(showProgress: false);

                }

            }

        }

        catch (Exception ex)

        {
            await HandleAssetOperationErrorAsync("AssetItemViewModel.ReloadAddons", ex, showDialog: false);
        }

    }



    private async Task UpdateAddonGridAsync()

    {

        try

        {

            // AddonGridViewModel縺ｮ繝輔ぅ繝ｫ繧ｿ繧貞・驕ｩ逕ｨ

            var addonGridVm = ViewModelLocator.AddonGridViewModel;

            if (addonGridVm != null)

            {

                addonGridVm.ApplyFilter();

            }

        }

        catch (Exception ex)

        {
            await HandleAssetOperationErrorAsync("AssetItemViewModel.UpdateAddonGridAsync", ex, showDialog: false);
        }

    }



    private async Task ShowDetailsDialogAsync()

    {

        try

        {

            var detailsDialog = new AssetDetailsDialog();

            var availableAddonIds = ViewModelLocator.AddonGridViewModel?.AllAddons
                .Where(addon => addon.IsAvailable)
                .Select(addon => addon.AddonId)
                .ToHashSet(StringComparer.Ordinal);

            detailsDialog.SetAsset(this, addonManager, availableAddonIds);

            

            var mainWindow = await GetMainWindow();

            if (mainWindow != null)

            {

                await detailsDialog.ShowDialog(mainWindow);



                // 繝繧､繧｢繝ｭ繧ｰ繧帝哩縺倥◆蠕後∝､画峩繧貞渚譏

                RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == Id));

            }

        }

        catch (Exception ex)

        {

            SafeFileLogger.TryLogException("AssetItemViewModel.ShowDetailsAsync", ex);

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.DetailsDialogFailed", ex.Message));

        }

    }



    private async Task VersionManageAsync()

    {

        try

        {

            await ShowVersionManagementWindowAsync();

        }

        catch (Exception ex)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(

                L.Get("Error.Title"),

                L.Get("Error.VersionManagementFailed"));

        }

    }



    private async Task ShowVersionManagementWindowAsync()

    {

        var mainWindow = await GetMainWindow();

        if (mainWindow != null)

        {

            // 譛譁ｰ縺ｮ繧｢繧ｻ繝・ヨ迥ｶ諷九ｒ蜿門ｾ暦ｼ医い繝峨が繝ｳ霑ｽ蜉繝ｻ蜑企勁蠕後・迥ｶ諷九ｒ蜿肴丐・・

            var config = addonManager.GetConfiguration();

            var latestAsset = config.Assets.FirstOrDefault(a => a.Id == asset.Id);

            if (latestAsset != null)

            {

                asset = latestAsset; // 譛譁ｰ縺ｮ繧｢繧ｻ繝・ヨ諠・ｱ縺ｫ譖ｴ譁ｰ

            }

            

            var dialog = new VersionManagementDialog(asset, addonManager);

            await dialog.ShowDialog(mainWindow);

            var latestAfterDialog = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == Id);
            if (latestAfterDialog != null)
            {
                RefreshFromModel(latestAfterDialog);
            }

            ViewModelLocator.AssetListViewModel?.RefreshAssetStates();

            

            // 繝繧､繧｢繝ｭ繧ｰ縺碁哩縺倥ｉ繧後◆繧蔚I繧呈峩譁ｰ

            this.RaisePropertyChanged(nameof(VersionDisplay));

        }

    }



    #region IDisposable Support

    private bool disposedValue = false;



    protected virtual void Dispose(bool disposing)
    {
        if (disposedValue)
        {
            return;
        }
        disposedValue = true;
        if (disposing)
        {
            LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
            assetImageBitmap?.Dispose();
            assetImageBitmap = null;
        }
    }



    public void Dispose()

    {

        Dispose(true);

        GC.SuppressFinalize(this);

    }

    #endregion

}

























