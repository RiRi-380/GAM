using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
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
    private bool startupUpdateCheckStarted;
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
        AllOffCommand = ReactiveCommand.CreateFromTask(AllOffAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        ResetManagerCommand = ReactiveCommand.CreateFromTask(ResetManagerAsync);

        // 讀懃ｴ｢讖溯・縺ｮ螳溯｣・
        this.WhenAnyValue(x => x.SearchText)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
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
    
    public void StartStartupUpdateCheck()
    {
        if (startupUpdateCheckStarted)
        {
            return;
        }

        startupUpdateCheckStarted = true;
        _ = RunStartupUpdateCheckSafelyAsync();
    }

    private async Task RunStartupUpdateCheckSafelyAsync()
    {
        try
        {
            await CheckForUpdatesAfterStartupAsync();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindowViewModel.CheckForUpdatesAfterStartupAsync", ex);
        }
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        // 襍ｷ蜍・遘貞ｾ後↓繧｢繝・・繝・・繝医メ繧ｧ繝・け
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckForUpdatesAsync();
    }
    
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            var updateService = CreateUpdateService(currentVersion);
            
            var updateResult = await updateService.CheckForUpdateAsync(forceCheck: false);
            if (updateResult.Status == UpdateCheckStatus.UpdateAvailable && updateResult.UpdateInfo != null)
            {
                // 繧｢繝・・繝・・繝医ム繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                    
                if (mainWindow != null)
                {
                    await UpdateDialogCoordinator.TryShowAsync(mainWindow, updateService, updateResult.UpdateInfo);
                }
            }
        }
        catch (Exception ex)
        {
            // 繧｢繝・・繝・・繝医メ繧ｧ繝・け縺ｮ繧ｨ繝ｩ繝ｼ縺ｯ辟｡隕・
            SafeFileLogger.TryLogException("MainWindowViewModel.CheckForUpdatesAsync", ex);
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
    public ReactiveCommand<Unit, Unit> AllOffCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetManagerCommand { get; }
    
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

    public async Task InitializeAsync()
    {
        try
        {
#if DEBUG
            // MainWindowViewModel.InitializeAsync started
#endif

            // AddonManager is initialized once by App. The grid load owns the single
            // startup workshop scan and updates the configuration before assets render.
            await AddonGridViewModel.LoadAddonsAsync();

            // ViewModel繧貞・譛溷喧
            AssetListViewModel.LoadAssets();
            
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
        
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Format("Error.InitializationFailed", ex.Message));
        }
    }

    public void RefreshActualStateFromRuntime()
    {
        if (!isInitialized)
        {
            return;
        }

        // ApplyFilter refreshes each card from CaptureState(). This is deliberately
        // read-only: focus recovery must accept GMod-side changes without reconciling.
        AddonGridViewModel.ApplyFilter();
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

            // UI縺ｨ繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ蜀崎ｪｭ縺ｿ霎ｼ縺ｿ
            AssetListViewModel.LoadAssets();
            
            // 繧｢繧ｻ繝・ヨ縺悟・隱ｭ縺ｿ霎ｼ縺ｿ縺輔ｌ縺溷ｾ後・∈謚槭ｒ蠕ｩ蜈・＠縲，urrentAsset繧呈峩譁ｰ
            var appliedSelection = false;
            if (!string.IsNullOrEmpty(currentAssetId))
            {
                var asset = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == currentAssetId);
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
                addonManager.InvalidateWorkshopScanCache();
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
    
    private void UpdateAddonStatistics()
    {
        try
        {
            var config = addonManager.GetConfiguration();
            var finalStates = addonManager.GetFinalAddonStates();
            var totalAddons = finalStates.Count;
            var enabledAddons = finalStates.Count(kvp => kvp.Value);
            var disabledAddons = finalStates.Count(kvp => !kvp.Value);
            
            // 繝輔ぃ繧､繝ｫ繧ｵ繧､繧ｺ繧定ｨ育ｮ・
            long totalSize = 0;
            foreach (var addon in config.AddonMetadata.Values)
            {
                totalSize += addon.Size;
            }
            
            // 繧ｵ繧､繧ｺ繧剃ｺｺ髢薙′隱ｭ縺ｿ繧・☆縺・ｽ｢蠑上↓螟画鋤
            string sizeText = FormatFileSize(totalSize);
            
            var statistics =
                $"{L.Get("Status.TotalAddons")}: {totalAddons} | " +
                $"{L.Get("Status.Enabled")}: {enabledAddons} | " +
                $"{L.Get("Status.Disabled")}: {disabledAddons} | " +
                $"{L.Get("Status.TotalSize")}: {sizeText}";
            AddonStatistics = addonManager.PendingDownloadCount > 0
                ? $"{L.Format("Status.SteamDownloadPending", addonManager.PendingDownloadCount)} | {statistics}"
                : statistics;
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

    private async Task AllOffAsync()
    {
        try
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            using var progressDialog = ProgressDialogService.Show(
                mainWindow,
                L.Get("MainWindow.AllOff"),
                L.Get("Busy.UpdatingAddonStates"));
            progressDialog?.SetIndeterminate();

            await addonManager.SetAllOffAsync();
            AssetListViewModel.LoadAssets();
            await AddonGridViewModel.LoadAddonsAsync();
            UpdateUndoState();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindowViewModel.AllOffAsync", ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.ResetFailed"));
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            var dialog = new SettingsDialog(addonManager);
            EventHandler resetRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(ResetManagerAsync, "ResetManagerRequested");
            EventHandler pathHealthRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(OpenPathHealthAsync, "PathHealthRequested");
            EventHandler pathRecoveryRequestedHandler = (_, _) =>
                _ = RunSettingsActionSafeAsync(RunManualPathRecoveryAsync, "PathRecoveryRequested");

            dialog.ResetManagerRequested += resetRequestedHandler;
            dialog.PathHealthRequested += pathHealthRequestedHandler;
            dialog.PathRecoveryRequested += pathRecoveryRequestedHandler;
            
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
                dialog.PathHealthRequested -= pathHealthRequestedHandler;
                dialog.PathRecoveryRequested -= pathRecoveryRequestedHandler;
            }

            if (dialog.WasSaved)
            {
                await RefreshAddonsAsync(showProgress: false);
            }

            // 險ｭ螳壼､画峩繧貞渚譏
            var updatedSettings = AppSettings.Load();
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

    private async Task OpenPathHealthAsync()
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

        var dialog = new PathHealthDialog(new PathHealthViewModel(addonManager));
        await dialog.ShowDialog(mainWindow);
        await RefreshAddonsAsync(rescanWorkshop: false, showProgress: false);
    }

    private async Task RunManualPathRecoveryAsync()
    {
        var settings = AppSettings.Load();
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager");

        var result = await StartupPathRecoveryCoordinator.RunManualAsync(settings, appDataPath);
        if (!result.Accepted)
        {
            return;
        }

        var dialogService = new DialogService();
        var errorHandler = new UIErrorHandler(dialogService);
        using (BeginBusy(L.Get("Busy.RepairingPaths")))
        using (var repairManager = new AddonManager(new AddonManagerOptions
        {
            ErrorHandler = errorHandler,
            DisableMode = DisableMode.Soft,
            CustomGmodInstallPath = settings.CustomGmodInstallPath,
            CustomWorkshopPath = settings.CustomWorkshopPath
        }))
        {
            await repairManager.InitializeAsync();
            var repairPendingChangeManager = new PendingChangeManager(
                repairManager,
                repairManager.GetManagerPath(),
                errorHandler);
            await StartupPathRecoveryCoordinator.ApplyRepairsAsync(
                repairManager,
                repairPendingChangeManager,
                processWatcher,
                errorHandler);
        }

        await dialogService.ShowInfoAsync(
            L.Get("Settings.PathRecoveryAppliedTitle"),
            L.Get("Settings.PathRecoveryAppliedMessage"));
        await TryRestartApplicationAsync(dialogService);
    }

    private static async Task<bool> TryRestartApplicationAsync(IDialogService dialogService)
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.RestartFailed", "Executable path not found."));
            return false;
        }

        try
        {
            var startInfo = RestartHandoff.CreateRestartStartInfo(
                processPath,
                Environment.GetCommandLineArgs().Skip(1),
                Environment.ProcessId);

            var process = Process.Start(startInfo);
            if (process == null)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Format("Error.RestartFailed", L.Get("Error.Unknown")));
                return false;
            }
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.RestartFailed", ex.Message));
            return false;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }

        return true;
    }

    private async Task ResetManagerAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 荳谺｡遒ｺ隱・
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("Warning.Title"),
                L.Get("Warning.ResetManagerSoft"));
            
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
}


