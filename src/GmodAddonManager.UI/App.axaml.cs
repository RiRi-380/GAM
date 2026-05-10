using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;
using GmodAddonManager.UI.Models;
using System.IO;
using System;
using System.Threading.Tasks;
using GmodAddonManager.Core.Utils;
using Newtonsoft.Json;

namespace GmodAddonManager.UI;

public sealed partial class App : Application, IDisposable
{
    private AddonManager? addonManager;
    private GmodProcessWatcher? processWatcher;
    private PendingChangeManager? pendingChangeManager;
    private HybridWorkshopService? hybridWorkshopService;
    private WorkshopIconResolver? workshopIconResolver;
    private ApplicationLock? applicationLock;
    private ExperimentIpcServer? experimentIpcServer;
    
    public AddonManager? AddonManager => addonManager;
    public WorkshopIconResolver? WorkshopIconResolver => workshopIconResolver;

    public override void Initialize()
    {
        try
        {
#if DEBUG
            // Very early error logging
            System.IO.File.WriteAllText("app_startup.log", $"App Initialize started at: {DateTime.Now}\n");
#endif
            
            AvaloniaXamlLoader.Load(this);
            
#if DEBUG
            System.IO.File.AppendAllText("app_startup.log", $"XAML loaded successfully at: {DateTime.Now}\n");
#endif
        }
        catch (Exception ex)
        {
#if DEBUG
            System.IO.File.WriteAllText("xaml_load_error.log", $"XAML Load Error at: {DateTime.Now}\n{ex.ToString()}");
#endif
            throw;
        }
    }

    public override async void OnFrameworkInitializationCompleted()
    {
#if DEBUG
        try
        {
            System.IO.File.AppendAllText("app_startup.log", $"OnFrameworkInitializationCompleted started at: {DateTime.Now}\n");
        }
        catch 
        { 
            // Ignore debug logging errors - non-critical
        }
#endif

        AppSettings? settings = null;
        ShutdownMode? originalShutdownMode = null;
        
        try
        {
            // 繧ｨ繝ｩ繝ｼ繝ｭ繧ｰ繝輔ぃ繧､繝ｫ縺ｮ繝代せ
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_init_error.log");
            
            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Loading AppSettings at: {DateTime.Now}\n");
#endif
                
                // 險隱櫁ｨｭ螳壹・蛻晄悄蛹悶ｒ譛蛻昴↓陦後≧
                settings = AppSettings.Load();
                
#if DEBUG
                File.AppendAllText("app_startup.log", $"AppSettings loaded, initializing LocalizationManager at: {DateTime.Now}\n");
#endif
                
                // Force LocalizationManager initialization
                var locManager = LocalizationManager.Instance;
#if DEBUG
                File.AppendAllText("app_startup.log", $"LocalizationManager instance created at: {DateTime.Now}\n");
#endif
                
                locManager.ChangeLanguage(settings.Language);
#if DEBUG
                File.AppendAllText("app_startup.log", $"Language changed to {settings.Language} at: {DateTime.Now}\n");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                File.WriteAllText(logPath, $"AppSettings/Localization Error at: {DateTime.Now}\n{ex.ToString()}");
                File.AppendAllText("app_startup.log", $"AppSettings/Localization Error at: {DateTime.Now}\n{ex.Message}\n");
#endif
                // 繝・ヵ繧ｩ繝ｫ繝郁ｨｭ螳壹ｒ菴ｿ逕ｨ
                settings = new AppSettings();
            }

#if DEBUG
            File.AppendAllText("app_startup.log", $"Creating DialogService at: {DateTime.Now}\n");
#endif
            
            // 繧ｵ繝ｼ繝薙せ縺ｮ蛻晄悄蛹・
            var dialogService = new DialogService();
            
#if DEBUG
            File.AppendAllText("app_startup.log", $"DialogService created, creating UIErrorHandler at: {DateTime.Now}\n");
#endif
            
            var errorHandler = new UIErrorHandler(dialogService);
            
#if DEBUG
            File.AppendAllText("app_startup.log", $"UIErrorHandler created at: {DateTime.Now}\n");
#endif
            
            // 繧｢繝励Μ繧ｱ繝ｼ繧ｷ繝ｧ繝ｳ繝ｭ繝・け縺ｮ蜿門ｾ・
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager"
            );

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime startupDesktop)
            {
                originalShutdownMode = startupDesktop.ShutdownMode;
                startupDesktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            var startupPathRecovery = await RunStartupPathRecoveryAsync(settings, appDataPath);
            
            applicationLock = new ApplicationLock(appDataPath);
            if (!applicationLock.TryAcquireLock())
            {
                var runningProcess = applicationLock.GetRunningProcessInfo();
                string alreadyRunningMessage;
                if (runningProcess != null)
                {
                    var runningStartTime = runningProcess.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
                    alreadyRunningMessage = L.Format(
                        "Error.AlreadyRunningWithDetails",
                        runningProcess.ProcessId,
                        runningStartTime
                    );
                }
                else
                {
                    alreadyRunningMessage = L.Get("Error.AlreadyRunning");
                }

                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    alreadyRunningMessage
                );
                
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                {
                    desktopLifetime.Shutdown();
                    return;
                }
            }
            
            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Creating AddonManager at: {DateTime.Now}\n");
#endif
                // Current release is soft-only. Ignore any hard-mode settings/env.
                var disableMode = DisableMode.Soft;
                addonManager = new AddonManager(new AddonManagerOptions
                {
                    ErrorHandler = errorHandler,
                    DisableMode = disableMode,
                    CustomGmodInstallPath = settings.CustomGmodInstallPath,
                    CustomWorkshopPath = settings.CustomWorkshopPath,
                    EnableLocalAddonsExperimental = settings.EnableLocalAddonsExperimental
                });
                addonManager.EnableLocalAddonManagement = settings.EnableLocalAddonsExperimental;
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager created, calling InitializeAsync at: {DateTime.Now}\n");
#endif
                await addonManager.InitializeAsync();
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager InitializeAsync completed at: {DateTime.Now}\n");
#endif

                // 辟｡蜉ｹ蛹悶Δ繝ｼ繝芽ｨｭ螳壹ｒ驕ｩ逕ｨ
                addonManager.StrictLinkMode = settings.StrictLinkMode || addonManager.StrictLinkMode;
                
                // WorkshopIconResolver setup
                workshopIconResolver = addonManager.GetWorkshopIconResolver() as WorkshopIconResolver;
            }
            catch (UnauthorizedAccessException ex)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager creation/init privilege error at: {DateTime.Now}\n{ex}\n");
#endif
                errorHandler.HandleError(ex, "AddonManager.InitializeAsync", ErrorSeverity.Critical);

                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("Error.JunctionCreationFailed")
                );

                applicationLock.Dispose();

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                {
                    desktopLifetime.Shutdown();
                }
                return;
            }
            catch (Exception ex)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager creation/init error at: {DateTime.Now}\n{ex}\n");
#endif
                errorHandler.HandleError(ex, "AddonManager.InitializeAsync", ErrorSeverity.Critical);

                var sanitizedMessage = PathSanitizer.SanitizeException(ex);
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Format("Error.InitializationFailed", sanitizedMessage)
                );

                applicationLock?.Dispose();

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                {
                    desktopLifetime.Shutdown();
                }
                return;
            }

            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Creating GmodProcessWatcher at: {DateTime.Now}\n");
#endif
                processWatcher = new GmodProcessWatcher();
#if DEBUG
                File.AppendAllText("app_startup.log", $"GmodProcessWatcher created, calling StartWatching at: {DateTime.Now}\n");
#endif
                processWatcher.StartWatching();
#if DEBUG
                File.AppendAllText("app_startup.log", $"GmodProcessWatcher StartWatching completed at: {DateTime.Now}\n");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"GmodProcessWatcher error at: {DateTime.Now}\n{ex.ToString()}\n");
#endif
                throw;
            }

            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Creating PendingChangeManager at: {DateTime.Now}\n");
#endif
                pendingChangeManager = new PendingChangeManager(
                    addonManager, 
                    addonManager.GetManagerPath(),
                    errorHandler
                );
#if DEBUG
                File.AppendAllText("app_startup.log", $"PendingChangeManager created at: {DateTime.Now}\n");
#endif

                // 襍ｷ蜍墓凾縺ｫ菫晉蕗螟画峩縺後≠繧後・蜿ｯ閭ｽ縺ｪ髯舌ｊ驕ｩ逕ｨ
                if (pendingChangeManager.HasPendingChanges())
                {
                    try
                    {
                        await pendingChangeManager.ApplyPendingChangesAsync();
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        File.AppendAllText("app_startup.log", $"ApplyPendingChangesAsync at startup error at: {DateTime.Now}\n{ex}\n");
#endif
                    }
                }

                // GMod邨ゆｺ・ｾ後↓菫晉蕗螟画峩繧定・蜍暮←逕ｨ
                if (processWatcher != null)
                {
                    processWatcher.GmodStopped += (_, __) => _ = ApplyPendingChangesAfterGmodStoppedAsync();
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"PendingChangeManager error at: {DateTime.Now}\n{ex.ToString()}\n");
#endif
                throw;
            }

            if (addonManager != null)
            {
                addonManager.GmodRunningProvider = () => processWatcher?.IsGmodRunning;
                addonManager.PendingChangeCountProvider = () => pendingChangeManager?.GetPendingChangeCount();
            }

            if (addonManager != null && ShouldEnableExperimentIpc(addonManager))
            {
                var pipeName = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_PIPE_NAME");
                experimentIpcServer = new ExperimentIpcServer(addonManager, pipeName ?? "GAMExperiment");
                experimentIpcServer.Start();

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime ipcLifetime)
                {
                    ipcLifetime.Exit += (_, __) => experimentIpcServer.Dispose();
                }
            }

            if (addonManager == null || processWatcher == null || pendingChangeManager == null)
            {
                throw new InvalidOperationException("App initialization incomplete.");
            }

            var addonManagerLocal = addonManager;
            var processWatcherLocal = processWatcher;
            var pendingChangeManagerLocal = pendingChangeManager;

            if (startupPathRecovery.ApplyRepairs)
            {
                await ApplyStartupPathRecoveryRepairsAsync(
                    addonManagerLocal,
                    pendingChangeManagerLocal,
                    processWatcherLocal,
                    errorHandler);
            }

            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Setting up ViewModelLocator at: {DateTime.Now}\n");
#endif
                // Release build logging to track execution
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmodAddonManager", "logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "viewmodel_locator_setup.log"), $"[App] ViewModelLocator setup started at: {DateTime.Now}\n");
                
                // ViewModelLocator縺ｮ險ｭ螳・
                ViewModelLocator.AddonManager = addonManagerLocal;
                ViewModelLocator.ProcessWatcher = processWatcherLocal;
                ViewModelLocator.PendingChangeManager = pendingChangeManagerLocal;
                ViewModelLocator.SteamWorkshopService = addonManagerLocal.GetSteamWorkshopService();
                ViewModelLocator.ErrorHandler = errorHandler;
                ViewModelLocator.DialogService = dialogService;
                
                var logDir2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmodAddonManager", "logs");
                File.AppendAllText(Path.Combine(logDir2, "viewmodel_locator_setup.log"), $"[App] Using Web API only at: {DateTime.Now}\n");

                hybridWorkshopService = new HybridWorkshopService(
                    addonManagerLocal.GetSteamWorkshopService()
                );
                ViewModelLocator.HybridWorkshopService = hybridWorkshopService;
#if DEBUG
                File.AppendAllText("app_startup.log", $"ViewModelLocator setup completed at: {DateTime.Now}\n");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"ViewModelLocator setup error at: {DateTime.Now}\n{ex.ToString()}\n");
#endif
                // ViewModelLocator setup error
                throw;
            }


#if DEBUG
            File.AppendAllText("app_startup.log", $"Checking ApplicationLifetime at: {DateTime.Now}\n");
#endif
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"ApplicationLifetime is desktop, creating MainWindowViewModel at: {DateTime.Now}\n");
#endif
                try
                {
                    var mainViewModel = new MainWindowViewModel(
                        addonManagerLocal, 
                        processWatcherLocal, 
                        pendingChangeManagerLocal
                    );
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindowViewModel created at: {DateTime.Now}\n");
#endif
                    
                    // ViewModelLocator縺ｫ險ｭ螳・
                    ViewModelLocator.MainWindowViewModel = mainViewModel;
                    
#if DEBUG
                    File.AppendAllText("app_startup.log", $"Creating MainWindow at: {DateTime.Now}\n");
#endif
                    desktop.MainWindow = new MainWindow
                    {
                        DataContext = mainViewModel
                    };
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindow created, calling Show() at: {DateTime.Now}\n");
#endif
                    
                    // 繧ｦ繧｣繝ｳ繝峨え繧呈・遉ｺ逧・↓陦ｨ遉ｺ
                    desktop.MainWindow.Show();
                    if (originalShutdownMode.HasValue)
                    {
                        desktop.ShutdownMode = originalShutdownMode.Value;
                    }
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindow.Show() called at: {DateTime.Now}\n");
#endif
                    
                    // 繧｢繝励Μ繧ｱ繝ｼ繧ｷ繝ｧ繝ｳ邨ゆｺ・凾縺ｮ繧ｯ繝ｪ繝ｼ繝ｳ繧｢繝・・
                    desktop.Exit += (s, e) =>
                    {
                        mainViewModel.Dispose();
                        Cleanup();
                    };
#if DEBUG
                    File.AppendAllText("app_startup.log", $"Exit handler registered at: {DateTime.Now}\n");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    File.AppendAllText("app_startup.log", $"Window creation error at: {DateTime.Now}\n{ex.ToString()}\n");
#endif
                    throw;
                }
            }
            else
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"ApplicationLifetime is NOT desktop at: {DateTime.Now}\n");
#endif
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 邂｡逅・・ｨｩ髯舌お繝ｩ繝ｼ繝繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.AdminRequiredTitle"), 
                L.Get("Error.AdminRequiredMessage")
            );
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            // 縺昴・莉悶・繧ｨ繝ｩ繝ｼ
            var dialogService = new DialogService();
            var errorHandler = new UIErrorHandler(dialogService);
            errorHandler.HandleError(ex, "Application initialization failed", ErrorSeverity.Critical);
            await dialogService.ShowErrorAsync(
                L.Get("Error.StartupTitle"), 
                L.Format("Error.StartupFailed", ex.Message)
            );
            Environment.Exit(1);
        }

#if DEBUG
        File.AppendAllText("app_startup.log", $"Calling base.OnFrameworkInitializationCompleted at: {DateTime.Now}\n");
#endif
        base.OnFrameworkInitializationCompleted();
#if DEBUG
        File.AppendAllText("app_startup.log", $"base.OnFrameworkInitializationCompleted completed at: {DateTime.Now}\n");
#endif
    }

    private async Task ApplyPendingChangesAfterGmodStoppedAsync()
    {
        if (pendingChangeManager == null)
        {
            return;
        }

        try
        {
            await pendingChangeManager.ApplyPendingChangesAsync();
        }
        catch (Exception ex)
        {
            // Best-effort; avoid crashing shutdown.
            SafeFileLogger.TryLogException("App.PendingChangeManager.ApplyPendingChangesAsync", ex);
        }
    }

    private static async Task<StartupPathRecoveryState> RunStartupPathRecoveryAsync(AppSettings settings, string appDataPath)
    {
        var configuration = TryLoadExistingConfiguration(appDataPath);
        var snapshot = DetectStartupPathSnapshot(settings);
        var pathSignature = BuildPathRecoverySignature(snapshot);
        var promptForUnconfirmedPaths =
            !string.IsNullOrWhiteSpace(pathSignature) &&
            !string.Equals(settings.DismissedPathRecoverySignature, pathSignature, StringComparison.OrdinalIgnoreCase);
        var decision = StartupPathRecoveryEvaluator.Evaluate(
            configuration,
            snapshot,
            settings.CustomGmodInstallPath,
            settings.CustomWorkshopPath,
            promptForUnconfirmedPaths,
            settings.ConfirmedGmodInstallPath,
            settings.ConfirmedWorkshopPath);

        if (!decision.ShouldPrompt)
        {
            return new StartupPathRecoveryState();
        }

        var result = await StartupPathRecoveryDialog.ShowStandaloneAsync(decision);
        if (!result.Accepted)
        {
            if (!string.IsNullOrWhiteSpace(pathSignature))
            {
                settings.DismissedPathRecoverySignature = pathSignature;
                settings.Save();
            }

            return new StartupPathRecoveryState();
        }

        settings.CustomGmodInstallPath = result.GmodInstallPath;
        settings.CustomWorkshopPath = result.WorkshopRootPath;
        settings.ConfirmedGmodInstallPath = result.GmodInstallPath;
        settings.ConfirmedWorkshopPath = result.WorkshopRootPath;
        settings.DismissedPathRecoverySignature = null;
        settings.Save();
        return new StartupPathRecoveryState { ApplyRepairs = true };
    }

    private static string? BuildPathRecoverySignature(PathSnapshot snapshot)
    {
        var gmod = snapshot.GmodInstall?.InstallPath;
        var workshop = snapshot.ActiveWorkshopRoot?.RootPath;
        if (string.IsNullOrWhiteSpace(gmod) || string.IsNullOrWhiteSpace(workshop))
        {
            return null;
        }

        return $"{NormalizePathForSignature(gmod)}|{NormalizePathForSignature(workshop)}";
    }

    private static string NormalizePathForSignature(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
    }

    private static PathSnapshot DetectStartupPathSnapshot(AppSettings settings)
    {
        if (PathOverrideResolver.TryCreateSnapshot(
                settings.CustomGmodInstallPath,
                settings.CustomWorkshopPath,
                out var overrideSnapshot,
                out _))
        {
            return overrideSnapshot;
        }

        try
        {
            return new SteamPathDetector().DetectPathSnapshot();
        }
        catch (Exception ex)
        {
            return new PathSnapshot
            {
                HealthIssues = new[] { $"Startup path detection failed: {ex.Message}" }
            };
        }
    }

    private static Configuration? TryLoadExistingConfiguration(string appDataPath)
    {
        try
        {
            var configPath = Path.Combine(appDataPath, "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            return JsonConvert.DeserializeObject<Configuration>(json);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("App.TryLoadExistingConfiguration", ex);
            return null;
        }
    }

    private static async Task ApplyStartupPathRecoveryRepairsAsync(
        AddonManager manager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher,
        IErrorHandler errorHandler)
    {
        try
        {
            var metadataResult = await manager.RepairStalePathMetadataAsync();
            var addonNoMountResult = await manager.MigrateAddonNoMountEntriesAsync();
            var stateApplyResult = "applied";
            if (processWatcher.IsGmodRunning)
            {
                pendingChangeManager.QueueApplyStates();
                stateApplyResult = "queued";
            }
            else
            {
                await manager.UpdateAddonStatesAsync();
                await manager.SaveConfigurationAsync();
            }

            errorHandler.HandleInfo(
                $"Startup path recovery applied: metadata={metadataResult.ChangedCount}, addonnomount={addonNoMountResult.ChangedCount}, stateApply={stateApplyResult}",
                "StartupPathRecovery");
        }
        catch (Exception ex)
        {
            errorHandler.HandleWarning($"Startup path recovery repair failed: {ex.Message}", "StartupPathRecovery");
        }
    }

    private sealed class StartupPathRecoveryState
    {
        public bool ApplyRepairs { get; set; }
    }

        private void Cleanup()
    {
        experimentIpcServer?.Dispose();
        processWatcher?.Dispose();
        addonManager?.Dispose();
        applicationLock?.Dispose();

        experimentIpcServer = null;
        processWatcher = null;
        addonManager = null;
        applicationLock = null;
    }

    public void Dispose()
    {
        Cleanup();
    }

    public void ReleaseApplicationLockForRestart()
    {
        try
        {
            applicationLock?.Dispose();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("App.ReleaseApplicationLockForRestart", ex);
        }
        applicationLock = null;
    }

    private static bool ShouldEnableExperimentIpc(AddonManager addonManager)
    {
        var enable = Environment.GetEnvironmentVariable("GAM_ENABLE_IPC");
        if (IsEnvTrue(enable))
        {
            return true;
        }

        var logPath = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            return true;
        }

        return addonManager.IsExperimentContextActive;
    }

    private static bool IsEnvTrue(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}



