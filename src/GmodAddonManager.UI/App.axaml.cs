using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;
using GmodAddonManager.UI.Models;
using System.IO;
using System;
using System.Threading.Tasks;
using GmodAddonManager.Core.Utils;

namespace GmodAddonManager.UI;

public partial class App : Application
{
    private AddonManager? addonManager;
    private GmodProcessWatcher? processWatcher;
    private PendingChangeManager? pendingChangeManager;
    private SteamworksManager? steamworksManager;
    private HybridWorkshopService? hybridWorkshopService;
    private WorkshopIconResolver? workshopIconResolver;
    private ApplicationLock? applicationLock;
    private ExperimentIpcServer? experimentIpcServer;
    private Window? startupWindow;
    
    public AddonManager? AddonManager => addonManager;
    public SteamworksManager? SteamworksManager => steamworksManager;
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
        WriteStartupTrace("OnFrameworkInitializationCompleted started");
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
        
        try
        {
            // エラーログファイルのパス
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_init_error.log");
            
            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Loading AppSettings at: {DateTime.Now}\n");
#endif
                
                // 言語設定の初期化を最初に行う
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
                // デフォルト設定を使用
                settings = new AppSettings();
            }
            
#if DEBUG
            File.AppendAllText("app_startup.log", $"Creating DialogService at: {DateTime.Now}\n");
#endif
            
            // サービスの初期化
            var dialogService = new DialogService();
            
#if DEBUG
            File.AppendAllText("app_startup.log", $"DialogService created, creating UIErrorHandler at: {DateTime.Now}\n");
#endif
            
            var errorHandler = new UIErrorHandler(dialogService);
            
#if DEBUG
            File.AppendAllText("app_startup.log", $"UIErrorHandler created at: {DateTime.Now}\n");
#endif

            var startupDesktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            ShowStartupWindow(startupDesktop);
            await YieldForStartupWindowAsync();

            // アプリケーションロックの取得
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager"
            );
            
            applicationLock = new ApplicationLock(appDataPath);
            if (!applicationLock.TryAcquireLock())
            {
                var runningProcess = applicationLock.GetRunningProcessInfo();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    runningProcess != null 
                        ? $"GAMは既に実行中です。\nプロセスID: {runningProcess.ProcessId}\n開始時刻: {runningProcess.StartTime:yyyy-MM-dd HH:mm:ss}"
                        : "GAMは既に実行中です。"
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
                var disableMode = settings.DisableMode;
                if (IsEnvTrue(Environment.GetEnvironmentVariable("GAM_EXPERIMENT_FORCE_HARD_DISABLE")))
                {
                    disableMode = DisableMode.Hard;
                }

                string? previewWorkshopPath = null;
                string? previewAppDataPath = null;
                if (IsPreviewMode())
                {
                    var previewRoot = GetPreviewRoot();
                    previewWorkshopPath = Path.Combine(previewRoot, "steamapps", "workshop", "content", "4000");
                    previewAppDataPath = Path.Combine(previewRoot, "appdata");
                    Directory.CreateDirectory(previewWorkshopPath);
                    Directory.CreateDirectory(previewAppDataPath);

                    // Keep preview mode in soft disable to avoid file ops.
                    disableMode = DisableMode.Soft;
                    errorHandler.HandleInfo($"Preview mode enabled. WorkshopPath={previewWorkshopPath}", "App");
                }

                addonManager = new AddonManager(new AddonManagerOptions
                {
                    ErrorHandler = errorHandler,
                    DisableMode = disableMode,
                    CustomWorkshopPath = previewWorkshopPath,
                    CustomAppDataPath = previewAppDataPath,
                    DisableCacheScan = IsPreviewMode()
                });
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager created, calling InitializeAsync at: {DateTime.Now}\n");
#endif
                await addonManager.InitializeAsync();
#if DEBUG
                File.AppendAllText("app_startup.log", $"AddonManager InitializeAsync completed at: {DateTime.Now}\n");
#endif

                // 無効化モード設定を適用
                addonManager.UnsubscribeOnHardDisable = settings.UnsubscribeOnHardDisable;
                addonManager.StrictLinkMode = settings.StrictLinkMode || addonManager.StrictLinkMode;
                
                // WorkshopIconResolverの取得
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
                    "GAMはジャンクションを作成できませんでした。\n\n" +
                    "対処方法:\n" +
                    "1. Gmod Addon Manager を右クリックして \"管理者として実行\" を選択する\n" +
                    "   または Windows 設定 > 更新とセキュリティ > 開発者向け機能 で「開発者モード」を有効にする\n" +
                    "2. その後アプリを再起動してください。"
                );

                applicationLock?.Dispose();

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
                    $"初期化に失敗しました。\n詳細: {sanitizedMessage}"
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

                // 起動時に保留変更があれば可能な限り適用
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

                // GMod終了後に保留変更を自動適用
                if (processWatcher != null)
                {
                    processWatcher.GmodStopped += async (_, __) =>
                    {
                        try
                        {
                            await pendingChangeManager.ApplyPendingChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            // Best-effort; avoid crashing shutdown
                            System.IO.File.AppendAllText("app_startup.log", $"ApplyPendingChangesAsync error at: {DateTime.Now}\n{ex}\n");
                        }
                    };
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

            try
            {
#if DEBUG
                File.AppendAllText("app_startup.log", $"Setting up ViewModelLocator at: {DateTime.Now}\n");
#endif
                // Release build logging to track execution
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmodAddonManager", "logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "viewmodel_locator_setup.log"), $"[App] ViewModelLocator setup started at: {DateTime.Now}\n");
                
                // ViewModelLocatorの設定
                ViewModelLocator.AddonManager = addonManager;
                ViewModelLocator.ProcessWatcher = processWatcher;
                ViewModelLocator.PendingChangeManager = pendingChangeManager;
                ViewModelLocator.SteamWorkshopService = addonManager.GetSteamWorkshopService();
                ViewModelLocator.ErrorHandler = errorHandler;
                ViewModelLocator.DialogService = dialogService;
                
                // Log that we reached Steamworks initialization point
                var logDir2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmodAddonManager", "logs");
                File.AppendAllText(Path.Combine(logDir2, "viewmodel_locator_setup.log"), $"[App] ViewModelLocator setup completed, starting Steamworks init at: {DateTime.Now}\n");
                
                // Steamworks SDKの初期化（高速化のため）
                var steamworksLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmodAddonManager", "logs", "steamworks_init.log");
                File.WriteAllText(steamworksLogPath, $"[App] Starting Steamworks initialization at: {DateTime.Now}\n");
                // gmpublisherと同じように別スレッドで初期化を試みる
                var steamworksTask = Task.Run(() =>
                {
                    try
                    {
                        File.AppendAllText(steamworksLogPath, $"[App] Creating SteamworksManager instance at: {DateTime.Now}\n");
                        var sw = new SteamworksManager();
                        File.AppendAllText(steamworksLogPath, $"[App] Calling SteamworksManager.Initialize() at: {DateTime.Now}\n");
                        bool initialized = sw.Initialize();
                        File.AppendAllText(steamworksLogPath, $"[App] SteamworksManager.Initialize() returned: {initialized} at: {DateTime.Now}\n");
                        return initialized ? sw : null;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(steamworksLogPath, $"[App] Steamworks initialization exception: {ex.GetType().Name} at: {DateTime.Now}\n");
                        File.AppendAllText(steamworksLogPath, $"[App] Exception message: {ex.Message}\n");
                        File.AppendAllText(steamworksLogPath, $"[App] Stack trace: {ex.StackTrace}\n");
                        return null;
                    }
                });
                
                // 初期化を待つ（最大5秒）
                if (steamworksTask.Wait(5000))
                {
                    steamworksManager = steamworksTask.Result;
                    bool steamworksInitialized = steamworksManager != null && steamworksManager.IsInitialized;
                    
#if DEBUG
                    File.AppendAllText("app_startup.log", $"Steamworks initialized: {steamworksInitialized} at: {DateTime.Now}\n");
#endif
                    
                    // 初期化成功時、App IDを確認
                    if (steamworksInitialized)
                    {
                    }
                    else
                    {
                    }
                    
                    // ハイブリッドサービスの作成（Steamworks優先、Web APIフォールバック）
                    hybridWorkshopService = new HybridWorkshopService(
                        steamworksManager,
                        addonManager.GetSteamWorkshopService()
                    );
                    ViewModelLocator.HybridWorkshopService = hybridWorkshopService;
                }
                else
                {
                    // タイムアウトした場合
                    // Web APIモードのみで動作
                    hybridWorkshopService = new HybridWorkshopService(
                        null,
                        addonManager.GetSteamWorkshopService()
                    );
                    ViewModelLocator.HybridWorkshopService = hybridWorkshopService;
                }
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
                        addonManager, 
                        processWatcher, 
                        pendingChangeManager
                    );
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindowViewModel created at: {DateTime.Now}\n");
#endif
                    
                    // ViewModelLocatorに設定
                    ViewModelLocator.MainWindowViewModel = mainViewModel;
                    
#if DEBUG
                    File.AppendAllText("app_startup.log", $"Creating MainWindow at: {DateTime.Now}\n");
#endif
                    var mainWindow = new MainWindow
                    {
                        DataContext = mainViewModel
                    };
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindow created, calling Show() at: {DateTime.Now}\n");
#endif
                    
                    // ウィンドウを明示的に表示
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    CloseStartupWindow();
#if DEBUG
                    File.AppendAllText("app_startup.log", $"MainWindow.Show() called at: {DateTime.Now}\n");
#endif
                    
                    // アプリケーション終了時のクリーンアップ
                    desktop.Exit += (s, e) =>
                    {
                        addonManager?.Dispose(); // 未保存データの処理
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
            // 管理者権限エラーダイアログを表示
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                "管理者権限が必要です", 
                "このアプリケーションを管理者として実行してください。"
            );
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            // その他のエラー
            var dialogService = new DialogService();
            var errorHandler = new UIErrorHandler(dialogService);
            errorHandler.HandleError(ex, "Application initialization failed", ErrorSeverity.Critical);
            await dialogService.ShowErrorAsync(
                "起動エラー", 
                $"アプリケーションの起動に失敗しました: {ex.Message}"
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

    private void ShowStartupWindow(IClassicDesktopStyleApplicationLifetime? desktop)
    {
        WriteStartupTrace("ShowStartupWindow requested");
        if (desktop == null || startupWindow != null)
        {
            WriteStartupTrace(desktop == null
                ? "ShowStartupWindow skipped because desktop lifetime is unavailable"
                : "ShowStartupWindow skipped because startup window already exists");
            return;
        }

        startupWindow = new Window
        {
            Title = L.Get("InitialLoading.Title"),
            Width = 460,
            Height = 180,
            MinWidth = 420,
            MinHeight = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
            ShowInTaskbar = true,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = L.Get("InitialLoading.MainTitle"),
                        FontSize = 22,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = L.Get("InitialLoading.Initializing"),
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Opacity = 0.85
                    },
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 8,
                        Margin = new Thickness(0, 6, 0, 0)
                    },
                    new TextBlock
                    {
                        Text = L.Get("InitialLoading.PleaseWait"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.65,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };

        desktop.MainWindow = startupWindow;
        startupWindow.Show();
        WriteStartupTrace("ShowStartupWindow Show returned");
    }

    private static async Task YieldForStartupWindowAsync()
    {
        WriteStartupTrace("YieldForStartupWindowAsync started");

        var firstDispatcherTurn = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(
            () => firstDispatcherTurn.TrySetResult(null),
            DispatcherPriority.Background);

        await firstDispatcherTurn.Task;

        // Give the platform backend a short turn to create and paint the native window
        // before startup continues into filesystem-heavy initialization.
        await Task.Delay(100);

        WriteStartupTrace("YieldForStartupWindowAsync completed");
    }

    private static void WriteStartupTrace(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "startup_trace.log"),
                $"[App] {message} at: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n");
        }
        catch
        {
            // Startup tracing must never affect application startup.
        }
    }

    private void CloseStartupWindow()
    {
        var window = startupWindow;
        startupWindow = null;
        window?.Close();
    }

    private void Cleanup()
    {
        processWatcher?.Dispose();
        steamworksManager?.Dispose();
        applicationLock?.Dispose();
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

    private static bool IsPreviewMode()
    {
        if (IsEnvTrue(Environment.GetEnvironmentVariable("GAM_UI_PREVIEW")))
        {
            return true;
        }

        var args = Environment.GetCommandLineArgs();
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--preview", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetPreviewRoot()
    {
        var env = Environment.GetEnvironmentVariable("GAM_UI_PREVIEW_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        // Use a unique temp folder per run to avoid reusing cached preview config.
        var runId = Guid.NewGuid().ToString("N");
        return Path.Combine(Path.GetTempPath(), "GAM_UI_PREVIEW", runId);
    }
}
