using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using System.Reflection;
using GmodAddonManager.UI.Models;
using Avalonia.Threading;

namespace GmodAddonManager.UI.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly PendingChangeManager pendingChangeManager;

    private string searchText = "";
    private AssetListViewModel assetListViewModel;
    private AddonGridViewModel addonGridViewModel;
    private StatusBarViewModel statusBarViewModel;
    private bool canUndo;
    private string addonStatistics = "";
    private bool isInitialized = false;
    private int busyCounter = 0;
    private bool isBusy = false;
    private string busyTitle = "";
    private string busyDetail = "";
    private int busyProgressCurrent = 0;
    private int busyProgressTotal = 0;
    private bool isBusyProgressIndeterminate = true;
    private bool isDisableManifestImportEnabled;
    private readonly CompositeDisposable subscriptions = new();

    public MainWindowViewModel(
        AddonManager addonManager, 
        GmodProcessWatcher processWatcher,
        PendingChangeManager pendingChangeManager)
    {
        this.addonManager = addonManager;
        this.processWatcher = processWatcher;
        this.pendingChangeManager = pendingChangeManager;

        // ViewModel縺ｮ蛻晄悄蛹・
        assetListViewModel = new AssetListViewModel(
            addonManager, pendingChangeManager, processWatcher);
        addonGridViewModel = new AddonGridViewModel(addonManager, pendingChangeManager, processWatcher);
        statusBarViewModel = new StatusBarViewModel(
            processWatcher, pendingChangeManager);
        
        // ViewModelLocator縺ｫ險ｭ螳・
        ViewModelLocator.AssetListViewModel = assetListViewModel;
        ViewModelLocator.AddonGridViewModel = addonGridViewModel;

        // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAddonsAsync(showProgress: true));
        UndoCommand = ReactiveCommand.CreateFromTask(UndoLastActionAsync);
        MigrateAddonsCommand = ReactiveCommand.CreateFromTask(MigrateAddonsAsync);
        ResetAllStatesCommand = ReactiveCommand.CreateFromTask(ResetAllStatesAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        ImportDisableManifestCommand = ReactiveCommand.CreateFromTask(ImportDisableManifestAsync);
        ResetManagerCommand = ReactiveCommand.CreateFromTask(ResetManagerAsync);
        RestoreOriginalCommand = ReactiveCommand.CreateFromTask(RestoreOriginalAsync);
        IsDisableManifestImportEnabled = AppSettings.Load().EnableDisableManifestImport;

        // 讀懃ｴ｢讖溯・縺ｮ螳溯｣・
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .DistinctUntilChanged()
            .Subscribe(text => 
            {
                AddonGridViewModel.FilterText = text;
            })
            .DisposeWith(subscriptions);

        // 繧｢繧ｻ繝・ヨ驕ｸ謚槭・逶｣隕・
        AssetListViewModel.WhenAnyValue(x => x.SelectedAsset)
            .Subscribe(asset =>
            {
                // 蛻晄悄蛹紋ｸｭ縺ｯ菴輔ｂ縺励↑縺・
                if (!isInitialized) return;
                
                AddonGridViewModel.SetCurrentAsset(asset);
            })
            .DisposeWith(subscriptions);

        // 蛻晄悄繝・・繧ｿ繝ｭ繝ｼ繝峨・MainWindow縺ｮOnOpened繧､繝吶Φ繝医〒陦後ｏ繧後ｋ
        
        // Undo迥ｶ諷九・逶｣隕・
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateUndoState())
            .DisposeWith(subscriptions);
            
        // 菫晉蕗荳ｭ縺ｮ螟画峩縺碁←逕ｨ縺輔ｌ縺滓凾縺ｮ繝ｪ繝輔Ξ繝・す繝･
        statusBarViewModel.PendingChangesApplied += OnPendingChangesApplied;
    }

    private void OnPendingChangesApplied(object? sender, EventArgs e)
    {
        _ = RefreshAfterPendingChangesAppliedAsync();
    }

    private async Task RefreshAfterPendingChangesAppliedAsync()
    {
        try
        {
            await RefreshAddonsAsync(showProgress: true);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "MainWindowViewModel.RefreshAfterPendingChangesAppliedAsync",
                ex);
        }
    }
    
    private async Task CheckForUpdatesAfterStartupAsync()
    {
        // 襍ｷ蜍・遘貞ｾ後↓繧｢繝・・繝・・繝医メ繧ｧ繝・け
        await Task.Delay(5000);
        await CheckForUpdatesAsync();
    }
    
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            var updateService = CreateUpdateService(currentVersion);
            
            var updateResult = await updateService.CheckForUpdateAsync(forceCheck: true);
            if (updateResult.Status == UpdateCheckStatus.UpdateAvailable && updateResult.UpdateInfo != null)
            {
                // 繧｢繝・・繝・・繝医ム繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ
                var dialog = new UpdateDialog
                {
                    DataContext = new UpdateDialogViewModel(updateService, updateResult.UpdateInfo)
                };
                
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                    
                if (mainWindow != null)
                {
                    await dialog.ShowDialog(mainWindow);
                }
            }
        }
        catch (Exception ex)
        {
            // 繧｢繝・・繝・・繝医メ繧ｧ繝・け縺ｮ繧ｨ繝ｩ繝ｼ縺ｯ辟｡隕・
            // Update check failed: {ex.Message}
        }
    }

    private static UpdateService CreateUpdateService(string currentVersion)
    {
        return new UpdateService(currentVersion);
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";
    }

    public string SearchText
    {
        get => searchText;
        set => SetAndRaise(ref searchText, value);
    }

    public AssetListViewModel AssetListViewModel
    {
        get => assetListViewModel;
        private set => SetAndRaise(ref assetListViewModel, value);
    }

    public AddonGridViewModel AddonGridViewModel
    {
        get => addonGridViewModel;
        private set => SetAndRaise(ref addonGridViewModel, value);
    }

    public StatusBarViewModel StatusBarViewModel
    {
        get => statusBarViewModel;
        private set => SetAndRaise(ref statusBarViewModel, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetAndRaise(ref isBusy, value);
    }

    public string BusyTitle
    {
        get => busyTitle;
        private set => SetAndRaise(ref busyTitle, value);
    }

    public string BusyDetail
    {
        get => busyDetail;
        private set
        {
            SetAndRaise(ref busyDetail, value);
            this.RaisePropertyChanged(nameof(HasBusyDetail));
        }
    }

    public bool HasBusyDetail => !string.IsNullOrWhiteSpace(BusyDetail);

    public int BusyProgressValue
    {
        get => busyProgressCurrent;
        private set => SetAndRaise(ref busyProgressCurrent, value);
    }

    public int BusyProgressMax
    {
        get => Math.Max(busyProgressTotal, 1);
        private set
        {
            busyProgressTotal = value;
            this.RaisePropertyChanged(nameof(BusyProgressMax));
            this.RaisePropertyChanged(nameof(BusyProgressText));
            this.RaisePropertyChanged(nameof(HasBusyProgress));
        }
    }

    public bool IsBusyProgressIndeterminate
    {
        get => isBusyProgressIndeterminate;
        private set
        {
            SetAndRaise(ref isBusyProgressIndeterminate, value);
            this.RaisePropertyChanged(nameof(HasBusyProgress));
            this.RaisePropertyChanged(nameof(BusyProgressText));
        }
    }

    public bool HasBusyProgress => !IsBusyProgressIndeterminate && busyProgressTotal > 0;

    public string BusyProgressText
    {
        get
        {
            if (!HasBusyProgress)
            {
                return string.Empty;
            }

            var total = Math.Max(busyProgressTotal, 1);
            var current = Math.Clamp(busyProgressCurrent, 0, total);
            var percent = (int)Math.Round((double)current / total * 100.0);
            return L.Format("Busy.ProgressText", current, total, percent);
        }
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> MigrateAddonsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetAllStatesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportDisableManifestCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetManagerCommand { get; }
    public ReactiveCommand<Unit, Unit> RestoreOriginalCommand { get; }
    
    public bool CanUndo
    {
        get => canUndo;
        private set => SetAndRaise(ref canUndo, value);
    }
    
    public string AddonStatistics
    {
        get => addonStatistics;
        private set => SetAndRaise(ref addonStatistics, value);
    }

    public bool IsDisableManifestImportEnabled
    {
        get => isDisableManifestImportEnabled;
        private set => SetAndRaise(ref isDisableManifestImportEnabled, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
#if DEBUG
            // MainWindowViewModel.InitializeAsync started
#endif
            
            // Check if this is first run (no config exists)
            var config = addonManager.GetConfiguration();
            bool isFirstRun = config.AddonMetadata.Count == 0;
            
            if (isFirstRun)
            {
                // Show initial loading window
                await ShowInitialLoadingWindow();
            }
            else
            {
                // Normal initialization
#if DEBUG
                // Calling ScanWorkshopFolderAsync from MainWindowViewModel
#endif
                await addonManager.ScanWorkshopFolderAsync();
                
                // Check for new addons after normal initialization
                await CheckForNewAddons();
            }
            
            // ViewModel繧貞・譛溷喧
            AssetListViewModel.LoadAssets();
            
            // 繧｢繝峨が繝ｳ繧偵Ο繝ｼ繝会ｼ医い繧ｻ繝・ヨ驕ｸ謚槫燕縺ｫ繝・・繧ｿ繧呈ｺ門ｙ・・
            await AddonGridViewModel.LoadAddonsAsync();
            
            // 繝・ヵ繧ｩ繝ｫ繝医い繧ｻ繝・ヨ繧帝∈謚橸ｼ・ubscribe Asset繧帝∈謚橸ｼ・
            var subscribeAsset = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            if (subscribeAsset != null)
            {
                // 蛻晄悄蛹悶ヵ繝ｩ繧ｰ繧剃ｸ譎ら噪縺ｫtrue縺ｫ縺励※縲√い繧ｻ繝・ヨ驕ｸ謚槭′蜿肴丐縺輔ｌ繧九ｈ縺・↓縺吶ｋ
                isInitialized = true;
                AssetListViewModel.SelectedAsset = subscribeAsset;
                isInitialized = false;
                
                // 蠢ｵ縺ｮ縺溘ａ謇句虚縺ｧ繧りｨｭ螳・
                AddonGridViewModel.SetCurrentAsset(subscribeAsset);
            }
            
            // 邨ｱ險域ュ蝣ｱ繧呈峩譁ｰ
            UpdateAddonStatistics();
            
            // 蛻晄悄蛹門ｮ御ｺ・ヵ繝ｩ繧ｰ繧定ｨｭ螳・
            isInitialized = true;
            
            // 繧｢繝・・繝・・繝医メ繧ｧ繝・け繧帝幕蟋・
            _ = CheckForUpdatesAfterStartupAsync();
        
            // 襍ｷ蜍墓凾縺ｫ繧ｳ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ縺ｮ蟄伜惠遒ｺ隱・
            _ = CheckCollectionExistenceAsync();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Format("Error.InitializationFailed", ex.Message));
        }
    }
    
    private async Task ShowInitialLoadingWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow == null)
            {
                return;
            }
            var loadingWindow = new InitialLoadingWindow(addonManager);
            await loadingWindow.ShowDialog(desktop.MainWindow);
        }
    }
    
    private async Task CheckForNewAddons()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow == null)
            {
                return;
            }
            var checkWindow = new NewAddonCheckWindow(addonManager);
            await checkWindow.ShowDialog(desktop.MainWindow);
        }
    }

    public async Task RefreshAddonsAsync(bool rescanWorkshop = true, bool showProgress = false)
    {
        ProgressDialogHandle? progressDialog = null;
        try
        {
            // 迴ｾ蝨ｨ驕ｸ謚槭＆繧後※縺・ｋ繧｢繧ｻ繝・ヨ縺ｮID繧剃ｿ晏ｭ・
            var currentAssetId = AssetListViewModel.SelectedAsset?.Id;

            if (showProgress)
            {
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                var detail = rescanWorkshop ? L.Get("Busy.ScanningWorkshop") : null;
                progressDialog = ProgressDialogService.Show(mainWindow, L.Get("Busy.RefreshingAddons"), detail);
                progressDialog?.SetIndeterminate();
            }

            if (rescanWorkshop)
            {
                // 繧ｳ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ縺ｮ蟄伜惠遒ｺ隱・
                await CheckCollectionExistenceAsync();
            }
            
            // UI縺ｨ繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ蜀崎ｪｭ縺ｿ霎ｼ縺ｿ
            AssetListViewModel.LoadAssets();
            
            // 繧｢繧ｻ繝・ヨ縺悟・隱ｭ縺ｿ霎ｼ縺ｿ縺輔ｌ縺溷ｾ後・∈謚槭ｒ蠕ｩ蜈・＠縲，urrentAsset繧呈峩譁ｰ
            var appliedSelection = false;
            if (!string.IsNullOrEmpty(currentAssetId))
            {
                var asset = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == currentAssetId) 
                          ?? AssetListViewModel.JunctionAsset.FirstOrDefault(a => a.Id == currentAssetId);
                if (asset != null)
                {
                    AssetListViewModel.SelectedAsset = asset;
                    AddonGridViewModel.SetCurrentAsset(asset);
                    appliedSelection = true;
                }
            }

            if (!appliedSelection && AssetListViewModel.SelectedAsset != null)
            {
                AddonGridViewModel.SetCurrentAsset(AssetListViewModel.SelectedAsset);
                appliedSelection = true;
            }
            
            if (rescanWorkshop)
            {
                await AddonGridViewModel.LoadAddonsAsync();
            }
            else if (!appliedSelection)
            {
                AddonGridViewModel.ApplyFilter();
            }
            
            // 繧｢繝峨が繝ｳ邨ｱ險域ュ蝣ｱ繧呈峩譁ｰ
            UpdateAddonStatistics();
        }
        catch (Exception ex)
        {
            progressDialog?.Close();
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UpdateFailed"));
        }
        finally
        {
            progressDialog?.Close();
        }
    }
    
    private async Task CheckCollectionExistenceAsync()
    {
        try
        {
            var workshopService = addonManager.GetSteamWorkshopService();
            if (workshopService == null)
            {
                return;
            }
                
            var config = addonManager.GetConfiguration();
            bool hasChanges = false;
            
            foreach (var asset in config.Assets)
            {
                if (!string.IsNullOrEmpty(asset.WorkshopCollectionId))
                {
                    var lookupResult = await workshopService.GetCollectionDetailsWithStatusAsync(asset.WorkshopCollectionId);
                    if (lookupResult.Status == WorkshopCollectionLookupStatus.NotFound)
                    {
                        // 繧ｳ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ縺悟ｭ伜惠縺励↑縺・ｴ蜷医・蜈ｬ髢狗憾諷九ｒ隗｣髯､
                        asset.WorkshopCollectionId = null;
                        asset.AutoUpdateCollection = false;
                        hasChanges = true;
                    }
                }
            }
            
            if (hasChanges)
            {
                await addonManager.SaveConfigurationAsync();
                
                // UI縺ｮ譖ｴ譁ｰ繧偵Γ繧､繝ｳ繧ｹ繝ｬ繝・ラ縺ｧ螳溯｡・
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AssetListViewModel.LoadAssets();
                });
            }
        }
        catch (Exception ex)
        {
            // Collection existence check failed: {ex.Message}
        }
    }
    
    private void UpdateAddonStatistics()
    {
        try
        {
            var config = addonManager.GetConfiguration();
            int totalAddons = config.AddonMetadata.Count;
            int enabledAddons = 0;
            int disabledAddons = 0;
            
            // 蜷・い繧ｻ繝・ヨ縺ｮ繧｢繝峨が繝ｳ迥ｶ諷九ｒ髮・ｨ・
            foreach (var asset in config.Assets)
            {
                if (addonManager.DisableMode == DisableMode.Hard && asset.Id == "junction-system-asset")
                {
                    disabledAddons += asset.Addons.Count;
                }
                else if (asset.Enabled)
                {
                    // 譛牙柑縺ｪ繧｢繧ｻ繝・ヨ蜀・・繧｢繝峨が繝ｳ縺ｧ縲・xcluded縺ｧ縺ｪ縺・ｂ縺ｮ繧偵き繧ｦ繝ｳ繝・
                    foreach (var addonId in asset.Addons)
                    {
                        var state = asset.AddonStates.ContainsKey(addonId) 
                            ? asset.AddonStates[addonId] 
                            : asset.DefaultAddonState;
                        
                        if (state != AddonState.Excluded)
                        {
                            enabledAddons++;
                        }
                    }
                }
            }
            
            // 驥崎､・ｒ髯､蜴ｻ・郁､・焚繧｢繧ｻ繝・ヨ縺ｫ蜷ｫ縺ｾ繧後ｋ繧｢繝峨が繝ｳ繧定・・・・
            enabledAddons = Math.Min(enabledAddons, totalAddons - disabledAddons);
            
            // 繝輔ぃ繧､繝ｫ繧ｵ繧､繧ｺ繧定ｨ育ｮ・
            long totalSize = 0;
            foreach (var addon in config.AddonMetadata.Values)
            {
                totalSize += addon.Size;
            }
            
            // 繧ｵ繧､繧ｺ繧剃ｺｺ髢薙′隱ｭ縺ｿ繧・☆縺・ｽ｢蠑上↓螟画鋤
            string sizeText = FormatFileSize(totalSize);
            
            AddonStatistics = $"{L.Get("Status.TotalAddons")}: {totalAddons} | {L.Get("Status.Enabled")}: {enabledAddons} | {L.Get("Status.Disabled")}: {disabledAddons} | {L.Get("Status.TotalSize")}: {sizeText}";
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindowViewModel.UpdateAddonStatistics", ex);
            AddonStatistics = "";
        }
    }
    
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public void Dispose()
    {
        statusBarViewModel.PendingChangesApplied -= OnPendingChangesApplied;
        subscriptions.Dispose();
        assetListViewModel?.Dispose();
        statusBarViewModel?.Dispose();
        addonGridViewModel?.Dispose();
    }
    
    private void UpdateUndoState()
    {
        CanUndo = addonManager.GetUndoManager().CanUndo;
    }

    public IDisposable BeginBusy(string title, string? detail = null)
    {
        System.Threading.Interlocked.Increment(ref busyCounter);
        ResetBusyProgress();
        SetBusyState(true, title, detail ?? "");
        return new BusyScope(this);
    }

    private void EndBusy()
    {
        var remaining = System.Threading.Interlocked.Decrement(ref busyCounter);
        if (remaining <= 0)
        {
            System.Threading.Interlocked.Exchange(ref busyCounter, 0);
            SetBusyState(false, "", "");
        }
    }

    private void SetBusyState(bool busy, string title, string detail)
    {
        void Apply()
        {
            IsBusy = busy;
            BusyTitle = title;
            BusyDetail = detail;
            if (!busy)
            {
                ResetBusyProgress();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    public void UpdateBusyProgress(int current, int total)
    {
        if (total <= 0)
        {
            SetBusyProgress(0, 0, indeterminate: true);
            return;
        }

        var safeCurrent = Math.Clamp(current, 0, total);
        SetBusyProgress(safeCurrent, total, indeterminate: false);
    }

    private void SetBusyProgress(int current, int total, bool indeterminate)
    {
        void Apply()
        {
            IsBusyProgressIndeterminate = indeterminate;
            BusyProgressMax = total;
            BusyProgressValue = current;
            this.RaisePropertyChanged(nameof(BusyProgressText));
            this.RaisePropertyChanged(nameof(HasBusyProgress));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    private void ResetBusyProgress()
    {
        SetBusyProgress(0, 0, indeterminate: true);
    }

    private sealed class BusyScope : IDisposable
    {
        private MainWindowViewModel? owner;

        public BusyScope(MainWindowViewModel owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            owner?.EndBusy();
            owner = null;
        }
    }
    
    private async Task UndoLastActionAsync()
    {
        try
        {
            var undoManager = addonManager.GetUndoManager();
            var lastAction = undoManager.PeekLastAction();
            
            if (lastAction != null)
            {
                var dialogService = new DialogService();
                var confirmMessage = BuildUndoConfirmMessage(lastAction);
                var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), confirmMessage);
                
                if (confirmed)
                {
                    var success = await addonManager.UndoLastActionAsync();
                    if (success)
                    {
                        await RefreshAddonsAsync(showProgress: false);
                        await dialogService.ShowInfoAsync(L.Get("Success.Title"), L.Get("Success.UndoComplete"));
                    }
                    else
                    {
                        await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UndoFailed"));
                    }
                }
            }
            
            UpdateUndoState();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UndoOperationFailed"));
        }
    }

    private string BuildUndoConfirmMessage(UndoAction action)
    {
        var unknown = L.Get("Common.Unknown");
        var assetName = action.AssetName ?? action.AssetId ?? unknown;

        switch (action.Type)
        {
            case UndoActionType.AssetCreated:
                return L.Format("Confirm.Undo.AssetCreated", assetName);
            case UndoActionType.AssetDeleted:
                return L.Format("Confirm.Undo.AssetDeleted", assetName);
            case UndoActionType.AssetEnabled:
            case UndoActionType.AssetDisabled:
            case UndoActionType.AssetExcluded:
            {
                AddonState? previousState = null;
                if (action.IsAssetToggle == true && action.PreviousEnabledState.HasValue)
                {
                    previousState = action.PreviousEnabledState.Value ? AddonState.Enabled : AddonState.Disabled;
                }
                else if (action.PreviousDefaultAddonState.HasValue)
                {
                    previousState = action.PreviousDefaultAddonState.Value;
                }
                else if (action.PreviousEnabledState.HasValue)
                {
                    previousState = action.PreviousEnabledState.Value ? AddonState.Enabled : AddonState.Disabled;
                }

                if (previousState.HasValue)
                {
                    return L.Format("Confirm.Undo.AssetState", assetName, GetStateLabel(previousState.Value));
                }

                return L.Format("Confirm.Undo.AssetStateUnknown", assetName);
            }
            case UndoActionType.AddonStateChanged:
            {
                var affectedCount = GetAffectedAddonCount(action);
                if (affectedCount > 1)
                {
                    return L.Format("Confirm.Undo.AddonStateBatch", affectedCount);
                }

                var addonName = action.AddonName
                                ?? action.AffectedAddonIds?.FirstOrDefault()
                                ?? action.AddonId
                                ?? unknown;

                var previousState = action.PreviousAddonState;
                if (!previousState.HasValue && action.PreviousAddonStates != null && action.AffectedAddonIds?.Count == 1)
                {
                    var addonId = action.AffectedAddonIds[0];
                    if (action.PreviousAddonStates.TryGetValue(addonId, out var state))
                    {
                        previousState = state;
                    }
                }

                if (previousState.HasValue)
                {
                    return L.Format("Confirm.Undo.AddonStateSingleWithState", addonName, GetStateLabel(previousState.Value));
                }

                return L.Format("Confirm.Undo.AddonStateSingle", addonName);
            }
            case UndoActionType.AddonAddedToAsset:
            {
                var count = Math.Max(GetAffectedAddonCount(action), 1);
                return L.Format("Confirm.Undo.AddonAddedToAsset", assetName, count);
            }
            case UndoActionType.AddonRemovedFromAsset:
            {
                var count = Math.Max(GetAffectedAddonCount(action), 1);
                return L.Format("Confirm.Undo.AddonRemovedFromAsset", assetName, count);
            }
            default:
                return L.Format("Confirm.Undo", action.Description);
        }
    }

    private static int GetAffectedAddonCount(UndoAction action)
    {
        if (action.AffectedAddonIds != null && action.AffectedAddonIds.Count > 0)
        {
            return action.AffectedAddonIds.Count;
        }

        if (!string.IsNullOrWhiteSpace(action.AddonId))
        {
            return action.AddonId
                .Split(',')
                .Select(id => id.Trim())
                .Count(id => id.Length > 0);
        }

        return 0;
    }

    private static string GetStateLabel(AddonState state)
    {
        return state switch
        {
            AddonState.Enabled => L.Get("AssetList.Enabled") ?? "Enabled",
            AddonState.Disabled => L.Get("AssetList.Disabled") ?? "Disabled",
            AddonState.Excluded => L.Get("AssetList.Excluded") ?? "Excluded",
            _ => state.ToString()
        };
    }
    
    private async Task MigrateAddonsAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 荳谺｡遒ｺ隱・
            var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
                L.Get("Warning.ManualMigration"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 莠梧ｬ｡遒ｺ隱・
            var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
                L.Get("Confirm.ManualMigrationFinal"));
            
            if (!confirmed2)
            {
                return;
            }
            
            // logger.LogInformation("Starting manual migration process"); // Removed logging
            
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            using var progressDialog = ProgressDialogService.Show(
                mainWindow,
                L.Get("Busy.MigratingAddons"));
            progressDialog?.SetIndeterminate();

            // 遘ｻ陦悟・逅・ｒ螳溯｡・
            await addonManager.MigrateExistingAddonsAsync();
            
            // 繧｢繝峨が繝ｳ諠・ｱ繧貞・隱ｭ縺ｿ霎ｼ縺ｿ
            progressDialog?.UpdateStatus(L.Get("Busy.RefreshingAddons"));
            progressDialog?.UpdateDetail(L.Get("Busy.ScanningWorkshop"));
            await RefreshAddonsAsync(showProgress: false);
            
            progressDialog?.Close();
            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                L.Get("Success.MigrationComplete"));
                
            // logger.LogInformation("Manual migration process completed"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to run manual migration", ex); // Removed logging
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.MigrationFailed"));
        }
    }
    
    private async Task ResetAllStatesAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 驕ｸ謚槭ム繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ
            var choiceDialog = new ResetChoiceDialog();
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
                
            if (mainWindow == null)
            {
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }
            
            await choiceDialog.ShowDialog(mainWindow);
            
            if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.Cancel)
            {
                return;
            }
            
            // 驕ｸ謚槭↓蠢懊§縺ｦ蜃ｦ逅・ｒ蛻・ｲ・
            if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.ResetAll)
            {
                await ResetAllAddonsStatesAsync();
            }
            else if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.ResetCurrentOnly)
            {
                await ResetCurrentAssetStatesAsync();
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.ResetFailed"));
        }
    }
    
    private async Task ResetAllAddonsStatesAsync()
    {
        var dialogService = new DialogService();
        
        // 荳谺｡遒ｺ隱・
        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
            addonManager.DisableMode == DisableMode.Hard
                ? L.Get("Warning.ResetAllStates")
                : L.Get("Warning.ResetAllStatesSoft"));
        
        if (!confirmed)
        {
            return;
        }
        
        // 莠梧ｬ｡遒ｺ隱・
        var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
            L.Get("Confirm.ResetAllStatesFinal"));
        
        if (!confirmed2)
        {
            return;
        }
        
        // 繝ｪ繧ｻ繝・ヨ蜃ｦ逅・ｒ螳溯｡・
        var config = addonManager.GetConfiguration();
        int resetCount = 0;
        
        // 蜈ｨ繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ繝ｪ繧ｻ繝・ヨ
        foreach (var asset in config.Assets)
        {
            resetCount += asset.AddonStates.Count;
            asset.AddonStates.Clear();
            asset.DefaultAddonState = AddonState.Enabled;

            if (asset.Id != "junction-system-asset")
            {
                asset.Enabled = true;
            }
        }

        // 迥ｶ諷九ｒ譖ｴ譁ｰ・医ず繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺ｮ菴懈・/蜑企勁繧貞ｮ溯｡鯉ｼ・
        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        using var progressDialog = ProgressDialogService.Show(
            mainWindow,
            L.Get("Busy.UpdatingAddonStates"),
            L.Format("Busy.Detail.AddonCount", resetCount));
        await addonManager.UpdateAddonStatesAsync(progressDialog?.CreateProgress());
        
        // 險ｭ螳壹ｒ菫晏ｭ・
        await addonManager.SaveConfigurationAsync();
        
        // 繝ｪ繝ｭ繝ｼ繝・
        await RefreshAddonsAsync(showProgress: false);
        
        progressDialog?.Close();
        await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
            L.Format("Success.ResetStatesComplete", resetCount));
    }
    
    private async Task ResetCurrentAssetStatesAsync()
    {
        var dialogService = new DialogService();
        var currentAsset = AssetListViewModel.SelectedAsset;
        
        if (currentAsset == null)
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.NoAssetSelected"));
            return;
        }
        
        // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・讖溯・繧貞宛髯・
        if (currentAsset.Id == "junction-system-asset")
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.JunctionAssetNotSupported"));
            return;
        }
        
        // 遒ｺ隱・
        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
            L.Format("Confirm.ResetAssetStates", currentAsset.Name));
        
        if (!confirmed)
        {
            return;
        }
        
        // 螳滄圀縺ｮAsset繧ｪ繝悶ず繧ｧ繧ｯ繝医ｒ蜿門ｾ・
        var config = addonManager.GetConfiguration();
        var asset = config.Assets.FirstOrDefault(a => a.Id == currentAsset.Id);
        
        if (asset == null)
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetNotFound"));
            return;
        }
        
        // 迴ｾ蝨ｨ縺ｮ繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九・縺ｿ繝ｪ繧ｻ繝・ヨ
        var statesToReset = asset.AddonStates.ToList();
        int resetCount = statesToReset.Count;
        
        foreach (var kvp in statesToReset)
        {
            asset.AddonStates.Remove(kvp.Key);
        }
        
        // 迥ｶ諷九ｒ譖ｴ譁ｰ
        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        using var progressDialog = ProgressDialogService.Show(
            mainWindow,
            L.Get("Busy.UpdatingAddonStates"),
            L.Format("Busy.Detail.AddonCount", resetCount));
        await addonManager.UpdateAddonStatesAsync(progressDialog?.CreateProgress());
        
        // 險ｭ螳壹ｒ菫晏ｭ・
        await addonManager.SaveConfigurationAsync();
        
        // 繝ｪ繝ｭ繝ｼ繝・
        await RefreshAddonsAsync(showProgress: false);
        
        progressDialog?.Close();
        await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
            L.Format("Success.ResetAssetStates", currentAsset.Name, resetCount));
    }
    
    private async Task ImportDisableManifestAsync()
    {
        try
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }

            var viewModel = new DisableManifestImportViewModel(
                new DisableManifestImportService(addonManager));
            var dialog = new DisableManifestImportDialog(viewModel);
            var imported = await dialog.ShowDialog<bool?>(mainWindow);
            if (imported == true)
            {
                await RefreshAddonsAsync(rescanWorkshop: false, showProgress: false);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindowViewModel.ImportDisableManifestAsync", ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("DisableManifest.ImportFailed", ex.Message));
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            var dialog = new SettingsDialog();
            EventHandler resetRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(ResetManagerAsync, "ResetManagerRequested");
            EventHandler restoreRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(RestoreOriginalAsync, "RestoreOriginalRequested");
            EventHandler manualMigrationRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(MigrateAddonsAsync, "ManualMigrationRequested");

            dialog.ResetManagerRequested += resetRequestedHandler;
            dialog.RestoreOriginalRequested += restoreRequestedHandler;
            dialog.ManualMigrationRequested += manualMigrationRequestedHandler;
            
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
                
            if (mainWindow == null)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }
            
            try
            {
                await dialog.ShowDialog(mainWindow);
            }
            finally
            {
                dialog.ResetManagerRequested -= resetRequestedHandler;
                dialog.RestoreOriginalRequested -= restoreRequestedHandler;
                dialog.ManualMigrationRequested -= manualMigrationRequestedHandler;
            }

            // 險ｭ螳壼､画峩繧貞渚譏
            var updatedSettings = AppSettings.Load();
            IsDisableManifestImportEnabled = updatedSettings.EnableDisableManifestImport;
            var localSettingChanged = updatedSettings.EnableLocalAddonsExperimental != addonManager.EnableLocalAddonManagement;
            if (updatedSettings.DisableMode != addonManager.DisableMode)
            {
                var dialogService = new DialogService();
                await dialogService.ShowInfoAsync(
                    L.Get("Success.Title"),
                    L.Get("Settings.DisableModeRestart"));
            }

            if (localSettingChanged)
            {
                addonManager.EnableLocalAddonManagement = updatedSettings.EnableLocalAddonsExperimental;
                await RefreshAddonsAsync(showProgress: false);

                if (updatedSettings.EnableLocalAddonsExperimental &&
                    processWatcher.IsGmodRunning &&
                    processWatcher.IsNoAddonsActive())
                {
                    var dialogService = new DialogService();
                    await dialogService.ShowInfoAsync(
                        L.Get("Warning.Title"),
                        L.Get("Warning.LocalAddonsNoAddons"));
                }
            }
            AddonGridViewModel.ReloadSettings(updatedSettings);
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.SettingsDialogFailed"));
        }
    }

    private async Task RunSettingsActionSafeAsync(Func<Task> action, string actionName)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                $"MainWindowViewModel.{actionName}",
                ex);
        }
    }
    
    private async Task ResetManagerAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 荳谺｡遒ｺ隱・
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("Warning.Title"), 
                addonManager.DisableMode == DisableMode.Hard
                    ? L.Get("Warning.ResetManager")
                    : L.Get("Warning.ResetManagerSoft"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 莠梧ｬ｡遒ｺ隱搾ｼ医ｈ繧雁ｼｷ縺・ｭｦ蜻奇ｼ・
            var confirmed2 = await dialogService.ShowConfirmAsync(
                L.Get("Confirm.FinalConfirmation"), 
                L.Get("Confirm.ResetManagerFinal"));
            
            if (!confirmed2)
            {
                return;
            }
            
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            using var progressDialog = ProgressDialogService.Show(
                mainWindow,
                L.Get("Busy.ResettingManager"));
            progressDialog?.SetIndeterminate();

            // Reset蜃ｦ逅・ｒ螳溯｡・
            await addonManager.ResetManagerAsync();
            progressDialog?.Close();
            
            // 蛻晄悄隱ｭ縺ｿ霎ｼ縺ｿ逕ｻ髱｢繧定｡ｨ遉ｺ
            await ShowInitialLoadingWindow();
            
            // UI繧貞・隱ｭ縺ｿ霎ｼ縺ｿ
            AssetListViewModel.LoadAssets();
            await AddonGridViewModel.LoadAddonsAsync();
            
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"), 
                L.Get("Success.ResetManagerComplete"));
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"), 
                L.Get("Error.ResetFailed"));
        }
    }
    
    private async Task RestoreOriginalAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 荳谺｡遒ｺ隱・
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("Warning.Title"), 
                addonManager.DisableMode == DisableMode.Hard
                    ? L.Get("Confirm.RestoreOriginal")
                    : L.Get("Confirm.RestoreOriginalSoft"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 莠梧ｬ｡遒ｺ隱搾ｼ医ｈ繧雁ｼｷ縺・ｭｦ蜻奇ｼ・
            var confirmed2 = await dialogService.ShowConfirmAsync(
                L.Get("Confirm.FinalConfirmation"), 
                addonManager.DisableMode == DisableMode.Hard
                    ? L.Get("Confirm.RestoreOriginalFinal")
                    : L.Get("Confirm.RestoreOriginalFinalSoft"));
            
            if (!confirmed2)
            {
                return;
            }
            
            var desktopLifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var mainWindow = desktopLifetime?.MainWindow;
            using var progressDialog = ProgressDialogService.Show(
                mainWindow,
                L.Get("Busy.RestoringOriginal"));
            progressDialog?.SetIndeterminate();

            // Restore蜃ｦ逅・ｒ螳溯｡・
            await addonManager.RestoreOriginalStateAsync();
            
            progressDialog?.Close();
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"), 
                L.Get("Success.RestoreComplete"));
                
            // 繧｢繝励Μ繧ｱ繝ｼ繧ｷ繝ｧ繝ｳ繧堤ｵゆｺ・
            desktopLifetime?.Shutdown();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"), 
                L.Get("Error.RestoreFailed"));
        }
    }
}


