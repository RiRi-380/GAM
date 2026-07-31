using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI;

namespace GmodAddonManager.UI.Views;

public partial class SettingsDialog : Window
{
    private readonly IDialogService dialogService;
    private readonly AddonManager? addonManager;
    private AppSettings? currentSettings;
    
    public event EventHandler? ResetManagerRequested;
    public event EventHandler? PathHealthRequested;
    public event EventHandler? PathRecoveryRequested;

    public bool WasSaved { get; private set; }

    public bool RetainMissingAssetReferences { get; private set; }
    
    public SettingsDialog()
        : this(false)
    {
    }

    public SettingsDialog(bool retainMissingAssetReferences)
    {
        InitializeComponent();
        dialogService = new DialogService();
        RetainMissingAssetReferences = retainMissingAssetReferences;
        
        LoadCurrentSettings();
    }

    public SettingsDialog(AddonManager addonManager)
        : this(
            addonManager?.GetConfiguration().RetainMissingAssetReferences ??
            throw new ArgumentNullException(nameof(addonManager)))
    {
        this.addonManager = addonManager;
    }
    
    private void LoadCurrentSettings()
    {
        currentSettings = AppSettings.Load();
        // ログの場所を表示
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager", "logs"
        );
        LogLocationText.Text = logPath;
        VersionText.Text = L.Format("Settings.CurrentVersion", GetCurrentVersion());
        
        // 言語設定を反映
        LanguageComboBox.SelectedIndex = currentSettings.Language == "ja-JP" ? 0 : 1;
        
        // コンソール表示設定を反映
        ShowConsoleCheckBox.IsChecked = currentSettings.ShowConsoleOnStartup;

        BackgroundTitleUpdatesCheckBox.IsChecked = currentSettings.EnableBackgroundTitleUpdates;
        BackgroundAddonPreloadCheckBox.IsChecked = currentSettings.EnableBackgroundAddonPreload;
        RetainMissingAssetReferencesCheckBox.IsChecked = RetainMissingAssetReferences;
        ApplyResetManagerTexts();
        
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task<bool> TryRestartApplicationAsync()
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

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
        return true;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (currentSettings == null) return;
            
            var languageChanged = false;
            
            // 言語が変更されたかチェック
            var newLanguage = LanguageComboBox.SelectedIndex == 0 ? "ja-JP" : "en-US";
            if (newLanguage != currentSettings.Language)
            {
                languageChanged = true;
            }
            
            // 設定を更新
            currentSettings.Language = newLanguage;

            currentSettings.EnableBackgroundTitleUpdates = BackgroundTitleUpdatesCheckBox.IsChecked ?? false;
            currentSettings.EnableBackgroundAddonPreload = BackgroundAddonPreloadCheckBox.IsChecked ?? false;
            RetainMissingAssetReferences =
                RetainMissingAssetReferencesCheckBox.IsChecked ?? false;
            if (addonManager != null &&
                addonManager.GetConfiguration().RetainMissingAssetReferences !=
                RetainMissingAssetReferences)
            {
                addonManager.GetConfiguration().RetainMissingAssetReferences =
                    RetainMissingAssetReferences;
                await addonManager.SaveConfigurationImmediatelyAsync();
            }
            
            // コンソール表示設定を更新
            var newShowConsole = ShowConsoleCheckBox.IsChecked ?? false;
            var showConsoleChanged = newShowConsole != currentSettings.ShowConsoleOnStartup;
            currentSettings.ShowConsoleOnStartup = newShowConsole;
            
            // 保存
            currentSettings.Save();
            
            if (languageChanged)
            {
                var restartNow = await dialogService.ShowConfirmAsync(
                    L.Get("Settings.LanguageRestartTitle"),
                    L.Get("Settings.LanguageRestartMessage"));
                if (restartNow)
                {
                    var restarted = await TryRestartApplicationAsync();
                    if (restarted)
                    {
                        return;
                    }
                }
            }

            if (showConsoleChanged)
            {
                var consoleMessage = newShowConsole
                    ? L.Get("Settings.ShowConsoleEnabledMessage")
                    : L.Get("Settings.ShowConsoleDisabledMessage");
                await dialogService.ShowInfoAsync(L.Get("Success.Title"), consoleMessage);
            }
            
            WasSaved = true;
            Close();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("SettingsDialog.OnSave", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.SettingsDialogFailed"));
        }
    }
    private void ApplyResetManagerTexts()
    {
        var japanese = currentSettings?.Language == "ja-JP";
        ResetManagerTitleText.Text = japanese ? "GAMを初期化" : "Reset GAM";
        ResetManagerButtonText.Text = japanese ? "GAMを初期化" : "Reset GAM";
        ResetManagerDescriptionText.Text = japanese
            ? "Custom Asset・お気に入り・Version・共通除外を初期化します。Steamの購読とWorkshopのアドオン本体は削除しません。"
            : "Resets Custom Assets, favorites, Versions, and global exclusions. Steam subscriptions and Workshop addon files are not deleted.";
    }

    private async void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager", "logs"
            );
            
            if (!Directory.Exists(logPath))
            {
                Directory.CreateDirectory(logPath);
            }
            
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.OpenFolderFailed", ex.Message));
        }
    }
    
    private void OnResetManager(object? sender, RoutedEventArgs e)
    {
        var requested = ResetManagerRequested;
        Close();
        requested?.Invoke(this, EventArgs.Empty);
    }
    
    private void OnPathHealth(object? sender, RoutedEventArgs e)
    {
        var requested = PathHealthRequested;
        Close();
        requested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPathRecovery(object? sender, RoutedEventArgs e)
    {
        var requested = PathRecoveryRequested;
        Close();
        requested?.Invoke(this, EventArgs.Empty);
    }
    
    private async void OnCheckForUpdate(object? sender, RoutedEventArgs e)
    {
        try
        {
            var updateService = CreateUpdateServiceFromUi();
            var result = await updateService.CheckForUpdateAsync(forceCheck: true);

            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.UpdateInfo != null)
            {
                await UpdateDialogCoordinator.TryShowAsync(this, updateService, result.UpdateInfo);
                return;
            }

            if (result.Status == UpdateCheckStatus.Error)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Format("Settings.UpdateCheckFailed", result.ErrorMessage ?? L.Get("Error.Unknown"))
                );
                return;
            }

            await dialogService.ShowInfoAsync(
                L.Get("Settings.UpdateCheckTitle"),
                L.Get("Settings.NoUpdateAvailable")
            );
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Settings.UpdateCheckFailed", ex.Message)
            );
        }
    }

    private UpdateService CreateUpdateServiceFromUi()
    {
        return new UpdateService(GetCurrentVersion());
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";
    }

}
