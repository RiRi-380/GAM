using GmodAddonManager.Core.Models;

using GmodAddonManager.Core.Services;

using GmodAddonManager.UI.Services;

using GmodAddonManager.UI.Views;

using ReactiveUI;

using System;

using System.ComponentModel;

using System.Diagnostics;

using System.Reactive;
using System.Reactive.Linq;

using System.Threading;

using System.Threading.Tasks;

using System.Collections.Generic;

using System.Linq;

using System.IO;

using Avalonia.Controls;

using Avalonia.Layout;

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

    private Asset asset;

    private readonly AddonManager addonManager;

    private readonly PendingChangeManager pendingChangeManager;

    private readonly GmodProcessWatcher processWatcher;

    private readonly bool showExclusiveApply;



    private bool isSelected;

    private bool isEnabled;

    private int addonCount;

    private bool isSystem;

    private AssetState assetState;

    private bool isPublished;

    private bool isCurrent;

    private Bitmap? assetImageBitmap;



        public AssetItemViewModel(

            Asset asset, 

            AddonManager addonManager,

            PendingChangeManager pendingChangeManager,

            GmodProcessWatcher processWatcher,

            bool showExclusiveApply)

        {

        this.asset = asset;

        this.addonManager = addonManager;

        this.pendingChangeManager = pendingChangeManager;

        this.processWatcher = processWatcher;

        this.showExclusiveApply = showExclusiveApply;



        // 蛻晄悄蛟､險ｭ螳・

        Id = asset.Id;

        name = asset.Name;

        IsEnabled = asset.Enabled;

        IsSystem = asset.IsSystem;

        UpdateAddonCount();

        

        // 繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ險ｭ螳夲ｼ・efaultAddonState縺九ｉ・・

        assetState = (AssetState)asset.DefaultAddonState;

        isPublished = !string.IsNullOrEmpty(asset.WorkshopCollectionId);



        // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・

        // Commands

        ToggleEnabledCommand = ReactiveCommand.CreateFromTask(ToggleEnabledAsync);

        DeleteCommand = ReactiveCommand.CreateFromTask(

            DeleteAsync,

            this.WhenAnyValue(x => x.IsSystem, x => x.Id, (isSystem, id) => !isSystem && id != "subscribe-system-asset" && id != "junction-system-asset"));

        ShowDetailsCommand = ReactiveCommand.CreateFromTask(ShowDetailsDialogAsync);
        SetEnabledCommand = ReactiveCommand.CreateFromTask(SetEnabledAsync);

        SetDisabledCommand = ReactiveCommand.CreateFromTask(SetDisabledAsync);

        SetExcludedCommand = ReactiveCommand.CreateFromTask(SetExcludedAsync);

        ShareCommand = ReactiveCommand.CreateFromTask(ShareAsync);

        VersionManageCommand = ReactiveCommand.CreateFromTask(
            VersionManageAsync,
            this.WhenAnyValue(x => x.CanManageVersions));

        ApplyExclusiveCommand = ReactiveCommand.CreateFromTask(ApplyExclusiveAsync);

        var canCleanup = addonManager.DisableMode == DisableMode.Hard;

        CleanupCommand = ReactiveCommand.CreateFromTask(

            ShowCleanupDialogAsync,

            this.WhenAnyValue(x => x.IsSystem, isSystem => !isSystem && canCleanup));

        EditCommand = ReactiveCommand.CreateFromTask(

            EditAsync,

            System.Reactive.Linq.Observable.Select(
                this.WhenAnyValue(x => x.IsSystem),
                _ => CanEditImage));

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

            this.RaisePropertyChanged(nameof(ShareButtonText));

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

                if (Id == "subscribe-system-asset")

                    return L.Get("Asset.SubscribeAsset");

                else if (Id == "junction-system-asset")

                    return L.Get("Asset.Junction");

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

        set => SetAndRaise(ref isEnabled, value);

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



    public string AddonCountDisplay => L.Format("AssetList.AddonCount", AddonCount);



    public string DisabledAddonCountDisplay => L.Format("AssetList.DisabledCount", AddonCount);



    public bool IsSystem

    {

        get => isSystem;

        private set

        {

            SetAndRaise(ref isSystem, value);

            this.RaisePropertyChanged(nameof(CanEditImage));

            this.RaisePropertyChanged(nameof(CanEditName));

        }

    }

    

    public bool CanEditImage => !IsSystem || Id == "subscribe-system-asset";

    public bool CanEditName => !IsSystem;

    

    // 蜑企勁繝懊ち繝ｳ繧定｡ｨ遉ｺ縺吶ｋ縺九←縺・

    public bool CanDelete => !IsSystem && Id != "subscribe-system-asset" && Id != "junction-system-asset";
    public bool CanManageVersions => Id != "subscribe-system-asset";

    public ReactiveCommand<Unit, Unit> ToggleEnabledCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public ReactiveCommand<Unit, Unit> ShowDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> SetEnabledCommand { get; }

    public ReactiveCommand<Unit, Unit> SetDisabledCommand { get; }

    public ReactiveCommand<Unit, Unit> SetExcludedCommand { get; }

    public ReactiveCommand<Unit, Unit> ShareCommand { get; }

    public ReactiveCommand<Unit, Unit> VersionManageCommand { get; }

    public ReactiveCommand<Unit, Unit> ApplyExclusiveCommand { get; }

    public ReactiveCommand<Unit, Unit> CleanupCommand { get; }

    public ReactiveCommand<Unit, Unit> EditCommand { get; }    

    // 繝舌・繧ｸ繝ｧ繝ｳ陦ｨ遉ｺ

    public string VersionDisplay 

    {

        get

        {

            // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｧ迴ｾ蝨ｨ繝舌・繧ｸ繝ｧ繝ｳ縺・縺ｮ蝣ｴ蜷医ｂ迚ｹ蛻･縺ｪ陦ｨ遉ｺ

            if (asset.CurrentVersion == 0 && asset.HasImportBaseline)

            {

                return L.Get("Version.ImportBaseline");

            }

            if (asset.CurrentVersion == 0)

            {

                return L.Get("Version.NotSaved");

            }

            // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｧ迴ｾ蝨ｨ繝舌・繧ｸ繝ｧ繝ｳ縺・1縺ｮ蝣ｴ蜷医・迚ｹ蛻･縺ｪ陦ｨ遉ｺ・井ｺ呈鋤諤ｧ縺ｮ縺溘ａ・・

            if (asset.CurrentVersion == -1 && asset.HasImportBaseline)

            {

                return L.Get("Version.ImportBaseline");

            }

            return $"v{asset.CurrentVersion}";

        }

    }

    

    // 迥ｶ諷九・繝ｭ繝代ユ繧｣

    public bool IsEnabledState => assetState == AssetState.Enabled;

    public bool IsDisabledState => assetState == AssetState.Disabled;

    public bool IsExcludedState => assetState == AssetState.Excluded;



    public bool IsPublished

    {

        get => isPublished;

        set => SetAndRaise(ref isPublished, value);

    }



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

            if (!IsPublished) return "Transparent";

            return "#4CAF50";

        }

    }

    

    public string ShareButtonText => IsPublished ? L.Get("Asset.Share") : L.Get("Asset.Share");

    

    // 蜈ｱ譛牙庄閭ｽ縺九←縺・°・・unction繧｢繧ｻ繝・ヨ莉･螟厄ｼ・

    public bool CanShare => Id != "junction-system-asset";

    public bool CanApplyExclusive => showExclusiveApply &&
        Id != "junction-system-asset" &&
        Id != DisableManifestImportServiceConstants.AssetId &&
        !Id.StartsWith(DisableManifestImportServiceConstants.NewAssetIdPrefix, StringComparison.Ordinal);

    

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

        if (!CanManageVersions)
        {
            return;
        }

        try

        {

            if (processWatcher.IsGmodRunning)

            {

                // Gmod縺悟ｮ溯｡御ｸｭ縺ｮ蝣ｴ蜷医・螟画峩繧剃ｿ晉蕗

                pendingChangeManager.AddPendingChange(

                    IsEnabled ? "disable" : "enable",

                    Id

                );

                return;

            }



            // Steam襍ｷ蜍穂ｸｭ縺ｮHard辟｡蜉ｹ蛹悶・蜀好L縺ｮ蜿ｯ閭ｽ諤ｧ縺後≠繧九◆繧∫｢ｺ隱搾ｼ域怏蜉ｹ蛹・Soft辟｡蜉ｹ蛹悶・縺昴・縺ｾ縺ｾ邯夊｡鯉ｼ・

            if (SteamProcessChecker.IsSteamRunning() && IsEnabled && addonManager.DisableMode == DisableMode.Hard)

            {

                var dialogService = new DialogService();

                var result = await dialogService.ShowConfirmAsync(

                    L.Get("Warning.SteamRunningTitle"),

                    L.Get("Warning.SteamRunningDisable"));

                if (!result)

                {

                    return;

                }

            }



            var mainWindow = await GetMainWindow();

            using var progressDialog = ProgressDialogService.Show(

                mainWindow,

                L.Get("Busy.SwitchingAsset"),

                L.Format("Busy.Detail.AssetNameWithCount", Name, AddonCount));

            var progress = progressDialog?.CreateProgress();



            // 蜊ｳ蠎ｧ縺ｫ蛻・ｊ譖ｿ縺・

            if (IsEnabled)

            {

                await addonManager.DisableAssetAsync(Id, progress);

            }

            else

            {

                await addonManager.EnableAssetAsync(Id, progress);

            }



            await addonManager.SaveConfigurationAsync();

            IsEnabled = !IsEnabled;

            asset.Enabled = IsEnabled;

        }

        catch (Exception)

        {

            throw;

        }

    }



    private async Task ApplyExclusiveAsync()

    {

        try

        {

            var dialogService = new DialogService();



            if (processWatcher.IsGmodRunning)

            {

                await dialogService.ShowErrorAsync(

                    L.Get("Warning.Title"),

                    L.Get("Warning.ApplyExclusiveWhileGmodRunning"));

                return;

            }



            if (SteamProcessChecker.IsSteamRunning() && addonManager.DisableMode == DisableMode.Hard)

            {

                var confirmed = await dialogService.ShowConfirmAsync(

                    L.Get("Warning.SteamRunningTitle"),

                    L.Get("Warning.SteamRunningDisable"));

                if (!confirmed)

                {

                    return;

                }

            }



            var mainWindow = await GetMainWindow();

            using var progressDialog = ProgressDialogService.Show(

                mainWindow,

                L.Get("Busy.ApplyingExclusive"),

                L.Format("Busy.Detail.AssetNameWithCount", Name, AddonCount));

            var progress = progressDialog?.CreateProgress();



            var applyResult = await addonManager.ApplyAssetExclusiveAsync(Id, progress);

            if (!applyResult.Success)

            {

                progressDialog?.Close();

                await dialogService.ShowErrorAsync(

                    L.Get("Error.Title"),

                    L.Get("Error.ApplyExclusiveFailed"));

            }



            ViewModelLocator.AssetListViewModel?.LoadAssets();

            await ReloadAddons();

        }

        catch (Exception)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(

                L.Get("Error.Title"),

                L.Get("Error.ApplyExclusiveFailed"));

        }

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



            var dialog = new AssetEditDialog(asset, addonManager, allowRename: CanEditName);

            var result = await dialog.ShowDialog<AssetEditResult?>(mainWindow);

            if (result == null || !result.IsSaved)

            {

                return;

            }



            if (CanEditName)

            {

                var trimmedName = result.Name.Trim();

                if (!string.Equals(asset.Name, trimmedName, StringComparison.OrdinalIgnoreCase))

                {

                    if (addonManager.AssetNameExists(trimmedName))

                    {

                        var dialogService = new DialogService();

                        await dialogService.ShowErrorAsync(

                            L.Get("Error.Title"),

                            L.Format("Error.AssetNameAlreadyExists", trimmedName));

                        return;

                    }



                    addonManager.RenameAsset(Id, trimmedName);

                }

            }



            if (result.RemoveImage)

            {

                addonManager.RemoveAssetImage(Id);

            }

            else if (!string.IsNullOrWhiteSpace(result.SourceImagePath) && result.Crop != null)

            {

                addonManager.SetAssetImageFromFile(Id, result.SourceImagePath, result.Crop);

            }



            await addonManager.SaveConfigurationAsync();



            var updated = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == Id);

            if (updated != null)

            {

                RefreshFromModel(updated);

            }

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

        try

        {

            var dialogService = new DialogService();

            

            // 遨ｺ縺ｮ繧｢繧ｻ繝・ヨ縺ｯ遒ｺ隱阪↑縺励〒蜑企勁

            if (GetAddonIds().Count == 0)

            {

                addonManager.DeleteAsset(Id);

                await addonManager.SaveConfigurationAsync();

                await ReloadAddons();

                return;

            }

            

            var showJunctionAsset = addonManager.DisableMode == DisableMode.Hard;

            var deleteDialog = new AssetDeleteDialog(showJunctionAsset, showJunctionAsset);

            var mainWindow = await GetMainWindow();

            

            if (mainWindow != null)

            {

                await deleteDialog.ShowDialog<AssetDeleteDialog.DeleteOption>(mainWindow);

                

                switch (deleteDialog.Result)

                {

                    case AssetDeleteDialog.DeleteOption.DeleteAssetOnly:

                        // 繧｢繧ｻ繝・ヨ縺ｮ縺ｿ繧貞炎髯､・井ｸｭ霄ｫ縺ｯ辟｡隕厄ｼ・

                        addonManager.DeleteAsset(Id);

                        await addonManager.SaveConfigurationAsync();

                        await ReloadAddons();

                        break;

                        

                    case AssetDeleteDialog.DeleteOption.MoveToOther:

                        // 荳谺｡遒ｺ隱・

                        var moveConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 

                            L.Format("Confirm.MoveAssetsToOther", Name));

                        

                        if (moveConfirmed)

                        {

                            // 遘ｻ蜍募・縺ｮ繧｢繧ｻ繝・ヨ繧帝∈謚橸ｼ磯壼ｸｸ縺ｮ驕ｸ謚槭→蜷後§繝ｭ繧ｸ繝・け・・

                            var assetListVm = ViewModelLocator.AssetListViewModel;

                            

                            if (assetListVm == null)

                            {

                                return;

                            }

                            

                            // 蜈ｨ繧｢繧ｻ繝・ヨ繝ｪ繧ｹ繝医ｒ菴懈・・医し繝悶せ繧ｯ繝ｩ繧､繝悶→繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧貞性繧・・

                            var allAssets = new List<AssetItemViewModel>();

                            allAssets.AddRange(assetListVm.Assets);

                            if (showJunctionAsset)

                            {

                                allAssets.AddRange(assetListVm.JunctionAsset);

                            }

                            

                            // 迴ｾ蝨ｨ縺ｮ繧｢繧ｻ繝・ヨ莉･螟悶ｒ繝輔ぅ繝ｫ繧ｿ

                            allAssets = allAssets.Where(a => a.Id != Id).ToList();

                            

                            // 繧｢繧ｻ繝・ヨ繧偵た繝ｼ繝茨ｼ医し繝悶せ繧ｯ繝ｩ繧､繝悶→繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧呈怙荳贋ｽ阪↓・・

                            var sortedAssets = new List<AssetItemViewModel>();

                            

                            // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶ｒ譛蛻昴↓

                            var subscribeAsset = allAssets.FirstOrDefault(a => a.Id == "subscribe-system-asset");

                            if (subscribeAsset != null) sortedAssets.Add(subscribeAsset);

                            

                            // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧・逡ｪ逶ｮ縺ｫ

                            var junctionAsset = allAssets.FirstOrDefault(a => a.Id == "junction-system-asset");

                            if (showJunctionAsset && junctionAsset != null) sortedAssets.Add(junctionAsset);

                            

                            // 縺昴・莉悶・繧｢繧ｻ繝・ヨ

                            sortedAssets.AddRange(allAssets.Where(a => a != subscribeAsset && a != junctionAsset));

                            

                            if (!sortedAssets.Any())

                            {

                                await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoDestinationAssets"));

                                return;

                            }

                            

                            var assetSelectionDialog = new AssetSelectionDialog(sortedAssets);

                            var selectedAsset = await assetSelectionDialog.ShowDialog<AssetItemViewModel>(mainWindow);

                            

                            if (selectedAsset != null)

                            {

                                // 莠梧ｬ｡遒ｺ隱・

                                var addonIds = GetAddonIds();

                                var sourceSet = new HashSet<string>(addonIds, StringComparer.Ordinal);

                                var destinationContainsAll = selectedAsset.Id == "subscribe-system-asset" ||

                                    selectedAsset.asset.ContainsAllAddons();

                                List<string> addonsToMove;

                                int existingCount;



                                if (destinationContainsAll)

                                {

                                    addonsToMove = new List<string>();

                                    existingCount = sourceSet.Count;

                                }

                                else

                                {

                                    var existingAddons = new HashSet<string>(selectedAsset.GetAddonIds(), StringComparer.Ordinal);

                                    addonsToMove = addonIds.Where(id => !existingAddons.Contains(id))

                                        .Distinct(StringComparer.Ordinal)

                                        .ToList();

                                    existingCount = sourceSet.Count - addonsToMove.Count;

                                }



                                if (addonsToMove.Count == 0)

                                {

                                    var deleteOnly = await dialogService.ShowConfirmAsync(

                                        L.Get("Confirm.Title"),

                                        L.Format("Confirm.MoveAddonsAllExists", existingCount, selectedAsset.Name));

                                    if (deleteOnly)

                                    {

                                        addonManager.DeleteAsset(Id);

                                        await addonManager.SaveConfigurationAsync();

                                        await dialogService.ShowInfoAsync(L.Get("Success.Title"),

                                            L.Get("Success.DeletedAssetOnlyAfterMove"));

                                        await ReloadAddons();

                                    }

                                    return;

                                }



                                var confirmMessage = existingCount > 0

                                    ? L.Format("Confirm.MoveAddonsPartial", existingCount, addonsToMove.Count, selectedAsset.Name)

                                    : L.Format("Confirm.MoveAddonsFinal", addonsToMove.Count, selectedAsset.Name);

                                var moveConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),

                                    confirmMessage);

                                

                                if (moveConfirmed2)

                                {

                                    // 繧｢繝峨が繝ｳ繧貞挨縺ｮ繧｢繧ｻ繝・ヨ縺ｫ遘ｻ蜍包ｼ育憾諷九・菫晄戟縺励↑縺・ｼ・

                                    if (addonsToMove.Count > 0)

                                    {

                                        using var progressDialog = ProgressDialogService.Show(

                                            mainWindow,

                                            L.Get("Busy.AddingAddonsToAsset"),

                                            L.Format("Busy.Detail.AssetNameWithCount", selectedAsset.Name, addonsToMove.Count));

                                        progressDialog?.UpdateProgress(0, addonsToMove.Count);



                                        var current = 0;

                                        foreach (var addonId in addonsToMove)

                                        {

                                            var state = selectedAsset.GetAddonState(addonId);

                                            selectedAsset.AddAddon(addonId, state); // 遘ｻ陦悟・繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ蜆ｪ蜈・

                                            current++;

                                            progressDialog?.UpdateProgress(current, addonsToMove.Count);

                                        }

                                    }

                                    

                                    // 蜈・・繧｢繧ｻ繝・ヨ繧貞炎髯､

                                    addonManager.DeleteAsset(Id);

                                    await addonManager.SaveConfigurationAsync();



                                    var successMessage = existingCount > 0

                                        ? L.Format("Success.MovedAddonsToAssetPartial", addonsToMove.Count, selectedAsset.Name)

                                        : L.Format("Success.MovedAddonsToAsset", addonsToMove.Count, selectedAsset.Name);

                                    await dialogService.ShowInfoAsync(L.Get("Success.Title"), successMessage);

                                    

                                    

                                    // 繝ｪ繝ｭ繝ｼ繝牙・逅・

                                    await ReloadAddons();

                                }

                            }

                        }

                        break;

                        

                    case AssetDeleteDialog.DeleteOption.DeleteWithContents:

                        if (!showJunctionAsset)

                        {

                            await dialogService.ShowErrorAsync(L.Get("Warning.Title"), L.Get("Warning.AssetUnavailableInMode"));

                            break;

                        }

                        // 荳谺｡遒ｺ隱・

                        var deleteConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 

                            L.Format("Confirm.DeleteAssetWithContents", Name));

                        

                        if (deleteConfirmed)

                        {

                            // 莠梧ｬ｡遒ｺ隱・

                            var deleteConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 

                                L.Format("Confirm.DeleteAssetFinal", Name));

                            

                            if (deleteConfirmed2)

                            {

                                addonManager.DeleteAsset(Id);

                                await addonManager.SaveConfigurationAsync();

                                

                                // 繝ｪ繝ｭ繝ｼ繝牙・逅・

                                await ReloadAddons();

                            }

                        }

                        break;

                        

                    case AssetDeleteDialog.DeleteOption.DisableAddons:

                        if (!showJunctionAsset)

                        {

                            await dialogService.ShowErrorAsync(L.Get("Warning.Title"), L.Get("Warning.AssetUnavailableInMode"));

                            break;

                        }

                        // 荳谺｡遒ｺ隱・

                        var disableConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 

                            L.Format("Confirm.DisableAllAddons", Name));

                        

                        if (disableConfirmed)

                        {

                            // 莠梧ｬ｡遒ｺ隱・

                            var addonCount = GetAddonIds().Count;

                            var disableConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 

                                L.Format("Confirm.DisableAddonsFinal", addonCount));

                            

                            if (disableConfirmed2)

                            {

                                // 繧｢繧ｻ繝・ヨ繧堤┌蜉ｹ蛹厄ｼ医ず繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧貞炎髯､・・

                                if (processWatcher.IsGmodRunning)

                                {

                                    pendingChangeManager.AddPendingChange("disable", Id);

                                    await dialogService.ShowInfoAsync(L.Get("Info.Title"), 

                                        L.Get("Info.DisableAfterGmodExit"));

                                }

                                else

                                {

                                    using var progressDialog = ProgressDialogService.Show(

                                        mainWindow,

                                        L.Get("Busy.SwitchingAsset"),

                                        L.Format("Busy.Detail.AssetNameWithCount", Name, addonCount));

                                    var progress = progressDialog?.CreateProgress();

                                    await addonManager.DisableAssetAsync(Id, progress);

                                    await addonManager.SaveConfigurationAsync();

                                    

                                    // 繧｢繧ｻ繝・ヨ繧貞炎髯､

                                    addonManager.DeleteAsset(Id);

                                    await addonManager.SaveConfigurationAsync();

                                    

                                    progressDialog?.Close();

                                    await dialogService.ShowInfoAsync(L.Get("Success.Title"), 

                                        L.Format("Success.DisabledAddons", addonCount));

                                    

                                    // 繝ｪ繝ｭ繝ｼ繝牙・逅・

                                    await ReloadAddons();

                                }

                                

                            }

                        }

                        break;

                        

                    case AssetDeleteDialog.DeleteOption.Cancel:

                    default:

                        // 繧ｭ繝｣繝ｳ繧ｻ繝ｫ

                        break;

                }

            }

        }

        catch (Exception ex)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetDeleteFailed"));

        }

    }



    public void AddAddon(string addonId)

    {

        AddAddon(addonId, asset.DefaultAddonState);

    }



    public void AddAddon(string addonId, AddonState state)

    {

        try

        {

            addonManager.AddAddonToAsset(Id, addonId, state);

            UpdateAddonCount();

            

        }

        catch (Exception)

        {

            throw;

        }

    }



    public void RemoveAddon(string addonId)

    {

        try

        {

            addonManager.RemoveAddonFromAsset(Id, addonId);

            UpdateAddonCount();

            

        }

        catch (Exception)

        {

            throw;

        }

    }



    public List<string> GetAddonIds()

    {

        // ContainsAllAddons 縺ｮ蝣ｴ蜷医・螳滄圀縺ｮ蜈ｨ繧｢繝峨が繝ｳID繧定ｿ斐☆

        if (asset.ContainsAllAddons())

        {

            var localAllAddons = addonManager.GetAllAddons();

            if (localAllAddons != null)

            {

                return localAllAddons.Keys.ToList();

            }

            else

            {

                return new List<string>();

            }

        }

        else

        {

            // *繧帝勁螟悶＠縺ｦ霑斐☆・亥ｿｵ縺ｮ縺溘ａ・・

            return asset.Addons.Where(id => id != "*").ToList();

        }

    }

    

    public IReadOnlyDictionary<string, AddonState> AddonStates => new Dictionary<string, AddonState>(asset.AddonStates);

    

    public AddonState GetAddonState(string addonId)

    {

        return asset.GetAddonState(addonId);

    }

    

    public void SetAddonState(string addonId, AddonState state)

    {

        try

        {

            addonManager.SetAddonState(Id, addonId, state);

        }

        catch (Exception)

        {

            throw;

        }

    }



    private void UpdateAddonCount()

    {

        if (asset.ContainsAllAddons())

        {

            // 蜈ｨ繧｢繝峨が繝ｳ繧貞性繧蝣ｴ蜷・縲∝ｮ滄圀縺ｮ蜈ｨ繧｢繝峨が繝ｳ謨ｰ繧定｡ｨ遉ｺ

            var localAllAddons = addonManager.GetAllAddons();

            if (localAllAddons != null)

            {

                AddonCount = localAllAddons.Count;

            }

            else

            {

                AddonCount = 0;

            }

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

        IsEnabled = updatedAsset.Enabled;

        assetState = (AssetState)updatedAsset.DefaultAddonState;

        UpdateAddonCount();

        this.RaisePropertyChanged(nameof(Name));



        this.RaisePropertyChanged(nameof(IsEnabledState));

        this.RaisePropertyChanged(nameof(IsDisabledState));

        this.RaisePropertyChanged(nameof(IsExcludedState));

        this.RaisePropertyChanged(nameof(AssetStateColor));



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



    private async Task SetEnabledAsync()

    {

        try

        {

            if (assetState != AssetState.Enabled)

            {

                // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・2谿ｵ繝√ぉ繝・け

                if (Id == "subscribe-system-asset")

                {

                    var dialogService = new DialogService();

                    

                    // 1谿ｵ逶ｮ遒ｺ隱・

                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),

                        L.Format("Confirm.EnableSystemAsset", Name));

                    

                    if (!confirmed1)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                    

                    // 2谿ｵ逶ｮ遒ｺ隱・

                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),

                        L.Format("Confirm.EnableSystemAssetFinal", Name));

                    

                    if (!confirmed2)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                }

                

                assetState = AssetState.Enabled;



                var mainWindow = await GetMainWindow();

                using var progressDialog = ProgressDialogService.Show(

                    mainWindow,

                    L.Get("Busy.SwitchingAsset"),

                    L.Format("Busy.Detail.AssetNameWithCount", Name, AddonCount));

                var progress = progressDialog?.CreateProgress();



                await addonManager.ApplyAssetDefaultStateAsync(Id, AddonState.Enabled, progress);

                

                // 險ｭ螳壹ｒ菫晏ｭ・

                await addonManager.SaveConfigurationAsync();

                

                // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧呈峩譁ｰ

                this.RaisePropertyChanged(nameof(IsEnabledState));

                this.RaisePropertyChanged(nameof(IsDisabledState));

                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));

                

                

                // 繧｢繝峨が繝ｳ荳隕ｧ繧呈峩譁ｰ

                await UpdateAddonGridAsync();

            }

        }

        catch (Exception ex)

        {
            await HandleAssetOperationErrorAsync("AssetItemViewModel.SetEnabledAsync", ex, showDialog: true);
        }

    }



    private async Task SetDisabledAsync()

    {

        try

        {

            if (assetState != AssetState.Disabled)

            {

                // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・2谿ｵ繝√ぉ繝・け

                if (Id == "subscribe-system-asset")

                {

                    var dialogService = new DialogService();

                    

                    // 1谿ｵ逶ｮ遒ｺ隱・

                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),

                        L.Format("Confirm.DisableSystemAsset", Name));

                    

                    if (!confirmed1)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                    

                    // 2谿ｵ逶ｮ遒ｺ隱・

                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),

                        L.Format("Confirm.DisableSystemAssetFinal", Name));

                    

                    if (!confirmed2)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                }

                

                assetState = AssetState.Disabled;



                var mainWindow = await GetMainWindow();

                using var progressDialog = ProgressDialogService.Show(

                    mainWindow,

                    L.Get("Busy.SwitchingAsset"),

                    L.Format("Busy.Detail.AssetNameWithCount", Name, AddonCount));

                var progress = progressDialog?.CreateProgress();



                await addonManager.ApplyAssetDefaultStateAsync(Id, AddonState.Disabled, progress);

                

                // 險ｭ螳壹ｒ菫晏ｭ・

                await addonManager.SaveConfigurationAsync();

                

                // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧呈峩譁ｰ

                this.RaisePropertyChanged(nameof(IsEnabledState));

                this.RaisePropertyChanged(nameof(IsDisabledState));

                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));

                

                

                // 繧｢繝峨が繝ｳ荳隕ｧ繧呈峩譁ｰ

                await UpdateAddonGridAsync();

            }

        }

        catch (Exception ex)

        {
            await HandleAssetOperationErrorAsync("AssetItemViewModel.SetDisabledAsync", ex, showDialog: true);
        }

    }



    private async Task SetExcludedAsync()

    {

        try

        {

            if (assetState != AssetState.Excluded)

            {

                // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・2谿ｵ繝√ぉ繝・け

                if (Id == "subscribe-system-asset")

                {

                    var dialogService = new DialogService();

                    

                    // 1谿ｵ逶ｮ遒ｺ隱・

                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),

                        L.Format("Confirm.ExcludeSystemAsset", Name));

                    

                    if (!confirmed1)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                    

                    // 2谿ｵ逶ｮ遒ｺ隱・

                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),

                        L.Format("Confirm.ExcludeSystemAssetFinal", Name));

                    

                    if (!confirmed2)

                    {

                        // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ縺励※蜈・↓謌ｻ縺・

                        this.RaisePropertyChanged(nameof(IsEnabledState));

                        this.RaisePropertyChanged(nameof(IsDisabledState));

                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        this.RaisePropertyChanged(nameof(AssetStateColor));

                        return;

                    }

                }

                

                assetState = AssetState.Excluded;



                var mainWindow = await GetMainWindow();

                using var progressDialog = ProgressDialogService.Show(

                    mainWindow,

                    L.Get("Busy.SwitchingAsset"),

                    L.Format("Busy.Detail.AssetNameWithCount", Name, AddonCount));

                var progress = progressDialog?.CreateProgress();



                await addonManager.ApplyAssetDefaultStateAsync(Id, AddonState.Excluded, progress);

                

                // 險ｭ螳壹ｒ菫晏ｭ・

                await addonManager.SaveConfigurationAsync();

                

                // 迥ｶ諷九・繝ｭ繝代ユ繧｣繧呈峩譁ｰ

                this.RaisePropertyChanged(nameof(IsEnabledState));

                this.RaisePropertyChanged(nameof(IsDisabledState));

                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));

                

                

                // 繧｢繝峨が繝ｳ荳隕ｧ繧呈峩譁ｰ

                await UpdateAddonGridAsync();

            }

        }

        catch (Exception ex)

        {
            await HandleAssetOperationErrorAsync("AssetItemViewModel.SetExcludedAsync", ex, showDialog: true);
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

            detailsDialog.SetAsset(this, addonManager);

            

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

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.DetailsDialogFailed"));

        }

    }



    private async Task ShareAsync()

    {

        try

        {

            var dialogService = new DialogService();



            if (!IsPublished)

            {

                var addonIds = GetAddonIds();

                if (addonIds.Count == 0)

                {

                    await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoAddonsToShare"));

                    return;

                }



                await ShowGamExportDialogAsync(addonIds);

                return;

            }



            var confirmed = await dialogService.ShowConfirmAsync(

                L.Get("Asset.OpenWorkshopPage.Title"),

                L.Get("Asset.OpenWorkshopPage.Message"));



            if (confirmed && asset.WorkshopCollectionId != null)

            {

                var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={asset.WorkshopCollectionId}";

                try

                {

                    Process.Start(new ProcessStartInfo

                    {

                        FileName = url,

                        UseShellExecute = true

                    });

                }

                catch

                {

                    await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.OpenWorkshopFailed"));

                }

            }

        }

        catch (Exception)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.ShareFailed"));

        }

    }



    // GAM繧ｨ繧ｯ繧ｹ繝昴・繝医ム繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ

    private async Task ShowGamExportDialogAsync(List<string> addonIds)

    {

        var gamExportDialog = new GamExportDialog();

        gamExportDialog.SetAddonIds(addonIds);

        

        var mainWindow = await GetMainWindow();

        if (mainWindow != null)

        {

            await gamExportDialog.ShowDialog(mainWindow);

            

            if (gamExportDialog.DialogResult)

            {

                var dialogService = new DialogService();

                await dialogService.ShowInfoAsync(

                    L.Get("Success.Title"),

                    L.Format("GamExport.SuccessMessage", gamExportDialog.SavePath));

            }

        }

    }

    

    // GAM蠖｢蠑上〒繧ｨ繧ｯ繧ｹ繝昴・繝茨ｼ・orkshop菴懈・蠕後・霑ｽ蜉菫晏ｭ倡畑・・

    private async Task ExportToGamFormatAsync(string title, string description, List<string> addonIds)

    {

        try

        {

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            var fileName = $"collection_{DateTime.Now:yyyyMMdd_HHmmss}.gam";

            var savePath = Path.Combine(desktopPath, fileName);

            

            var lines = new List<string>

            {

                "# GAM Collection Export v1",

                $"# Title: {title}",

                $"# Description: {description}",

                $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",

                $"# Count: {addonIds.Count}",

                ""

            };

            

            lines.AddRange(addonIds);

            

            await File.WriteAllLinesAsync(savePath, lines);

        }

        catch (Exception ex)

        {

            // 繧ｨ繝ｩ繝ｼ縺檎匱逕溘＠縺ｦ繧８orkshop菴懈・縺ｯ謌仙粥縺励※縺・ｋ縺ｮ縺ｧ繝ｭ繧ｰ縺ｮ縺ｿ險倬鹸

        }

    }

    

    // 繝舌・繧ｸ繝ｧ繝ｳ邂｡逅・

    private List<string> ResolveAddonIdsForVersion()

    {

        var resolved = new HashSet<string>();



        // Subscribe繧｢繧ｻ繝・ヨ縺ｯ螳溘し繝悶せ繧ｯ荳隕ｧ繧貞━蜈・

        if (Id == "subscribe-system-asset")

        {

            foreach (var addonId in SteamWorkshopCacheReader.GetSubscribedAddonIds())

            {

                if (addonId != "*")

                {

                    resolved.Add(addonId);

                }

            }

        }



        if (resolved.Count == 0)

        {

            if (asset.ContainsAllAddons())

            {

                var localAllAddons = addonManager.GetAllAddons();

                if (localAllAddons != null)

                {

                    foreach (var addonId in localAllAddons.Keys)

                    {

                        if (addonId != "*")

                        {

                            resolved.Add(addonId);

                        }

                    }

                }

            }

            else

            {

                foreach (var addonId in asset.Addons)

                {

                    if (addonId != "*")

                    {

                        resolved.Add(addonId);

                    }

                }

            }

        }



        return resolved.OrderBy(id => id).ToList();

    }



    private async Task VersionManageAsync()

    {

        try

        {

            var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

            if (window?.MainWindow != null)

            {

                // v0縺ｮ蝣ｴ蜷医・菫晏ｭ倥ム繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ・医う繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺後≠繧句ｴ蜷医ｒ髯､縺擾ｼ・

                if (asset.CurrentVersion == 0 && !asset.HasImportBaseline)

                {

                    var saveDialog = new SaveVersionDialog

                    {

                        AssetName = asset.Name

                    };

                    

                    await saveDialog.ShowDialog(window.MainWindow);

                    

                    if (saveDialog.IsSaved)

                    {

                        var resolvedAddonIds = ResolveAddonIdsForVersion();



                        // v1繧剃ｽ廢

                        var newVersion = new AssetVersion

                        {

                            Version = 1,

                            CreatedAt = DateTime.Now,

                            AddonIds = new List<string>(resolvedAddonIds),

                            IncludeAddonStates = saveDialog.IncludeAddonStates

                        };

                        

                        // GAM蠖｢蠑上・繧ｳ繝ｳ繝・Φ繝・ｒ逕滓・

                        var gamLines = new List<string>

                        {

                            "# GAM Collection Export v1",

                            $"# Title: {asset.Name} v1",

                            $"# Description: Version 1 of {asset.Name}",

                            $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",

                            $"# Count: {resolvedAddonIds.Count}",

                            ""

                        };

                        gamLines.AddRange(resolvedAddonIds);

                        newVersion.GamContent = string.Join("\n", gamLines);

                        

                        // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ菫晏ｭ倥☆繧句ｴ蜷・

                        if (saveDialog.IncludeAddonStates)

                        {

                            var filter = new HashSet<string>(resolvedAddonIds);

                            newVersion.AddonStates = asset.AddonStates

                                .Where(kvp => filter.Contains(kvp.Key))

                                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                        }

                        

                        // 繝舌・繧ｸ繝ｧ繝ｳ螻･豁ｴ縺ｫ霑ｽ蜉

                        asset.VersionHistory.Add(newVersion);

                        asset.CurrentVersion = 1;

                        

                        // 險ｭ螳壹ｒ菫晏ｭ・

                        await addonManager.SaveConfigurationAsync();

                        

                        // UI繧呈峩譁ｰ

                        RefreshFromModel(asset);

                        

                        var dialogService = new DialogService();

                        await dialogService.ShowInfoAsync(

                            L.Get("Success.Title"),

                            L.Format("VersionManagement.CreateCompleteMessage", 1));

                        

                        // 繝｡繧､繝ｳ繧ｦ繧｣繝ｳ繝峨え繧貞・隱ｭ縺ｿ霎ｼ縺ｿ

                        await ReloadAddons();

                    }

                }

                else

                {

                    // v1莉･髯阪・蝣ｴ蜷医・騾壼ｸｸ騾壹ｊ繝舌・繧ｸ繝ｧ繝ｳ邂｡逅・判髱｢繧帝幕縺・

                    await ShowVersionManagementWindowAsync();

                }

            }

        }

        catch (Exception ex)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(

                L.Get("Error.Title"),

                L.Get("Error.VersionManagementFailed"));

        }

    }



    private async Task CreateNewVersionAsync(bool includeAddonStates)

    {

        try

        {

            var resolvedAddonIds = ResolveAddonIdsForVersion();



            // 譁ｰ縺励＞繝舌・繧ｸ繝ｧ繝ｳ繧剃ｽ懈・

            var newVersionNumber = asset.CurrentVersion + 1;

            var newVersion = new AssetVersion

            {

                Version = newVersionNumber,

                CreatedAt = DateTime.Now,

                AddonIds = new List<string>(resolvedAddonIds),

                IncludeAddonStates = includeAddonStates

            };

            

            // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ菫晏ｭ倥☆繧句ｴ蜷・

            if (includeAddonStates)

            {

                var filter = new HashSet<string>(resolvedAddonIds);

                newVersion.AddonStates = asset.AddonStates

                    .Where(kvp => filter.Contains(kvp.Key))

                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            }

            

            // 繝舌・繧ｸ繝ｧ繝ｳ螻･豁ｴ縺ｫ霑ｽ蜉

            asset.VersionHistory.Add(newVersion);

            asset.CurrentVersion = newVersionNumber;

            

            // 險ｭ螳壹ｒ菫晏ｭ・

            await addonManager.SaveConfigurationAsync();

            

            // UI繧呈峩譁ｰ

            this.RaisePropertyChanged(nameof(VersionDisplay));

            

            var dialogService = new DialogService();

            await dialogService.ShowInfoAsync(

                L.Get("Success.Title"),

                L.Format("VersionManagement.CreateCompleteMessage", newVersionNumber)

            );

        }

        catch (Exception ex)

        {

            throw;

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



    private async Task ShowCleanupDialogAsync()

    {

                    if (addonManager.DisableMode == DisableMode.Soft)

        {

            return;

        }



        try

        {

            var mainWindow = await GetMainWindow();

            if (mainWindow != null)

            {

                var dialog = new AssetCleanupDialog(asset, addonManager);

                await dialog.ShowDialog(mainWindow);

                

                // 繧ｯ繝ｪ繝ｼ繝ｳ繧｢繝・・蠕後・繧｢繝峨が繝ｳ繝ｪ繧ｹ繝医ｒ蜀崎ｪｭ縺ｿ霎ｼ縺ｿ

                if (dialog.HasChanges)

                {

                    await ReloadAddons();

                }

            }

        }

        catch (Exception ex)

        {

            var dialogService = new DialogService();

            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 

                L.Get("Error.CleanupFailed"));

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

























