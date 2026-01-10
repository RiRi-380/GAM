using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

namespace GmodAddonManager.UI.Views;

public partial class SettingsDialog : Window
{
    private readonly IDialogService dialogService;
    private AppSettings? currentSettings;
    private readonly UpdateService updateService;
    
    public event EventHandler? ResetManagerRequested;
    public event EventHandler? RestoreOriginalRequested;
    
    public SettingsDialog()
    {
        InitializeComponent();
        dialogService = new DialogService();
        
        // UpdateServiceの初期化
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('-')[0] ?? "1.0.0";
        updateService = new UpdateService(version);
        
        LoadCurrentSettings();
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
        
        // 言語設定を反映
        LanguageComboBox.SelectedIndex = currentSettings.Language == "ja-JP" ? 0 : 1;
        
        // コンソール表示設定を反映
        ShowConsoleCheckBox.IsChecked = currentSettings.ShowConsoleOnStartup;

        // 無効化モード設定を反映
        SoftDisableRadio.IsChecked = currentSettings.DisableMode == DisableMode.Soft;
        HardDisableRadio.IsChecked = currentSettings.DisableMode == DisableMode.Hard;
        UnsubscribeCheckBox.IsChecked = currentSettings.UnsubscribeOnHardDisable;
        
        // ComboBoxの選択変更イベントを設定
        LanguageComboBox.SelectionChanged += OnLanguageSelectionChanged;
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (currentSettings == null) return;
        
        var needsRestart = false;
        var languageChanged = false;
        
        // 言語が変更されたかチェック
        var newLanguage = LanguageComboBox.SelectedIndex == 0 ? "ja-JP" : "en-US";
        if (newLanguage != currentSettings.Language)
        {
            languageChanged = true;
            // 言語を即座に変更
            LocalizationManager.Instance.ChangeLanguage(newLanguage);
        }
        
        // 設定を更新
        currentSettings.Language = newLanguage;

        // 無効化モード設定を更新
        currentSettings.DisableMode = SoftDisableRadio.IsChecked == true ? DisableMode.Soft : DisableMode.Hard;
        currentSettings.UnsubscribeOnHardDisable = UnsubscribeCheckBox.IsChecked ?? false;
        
        // コンソール表示設定を更新
        var newShowConsole = ShowConsoleCheckBox.IsChecked ?? false;
        if (newShowConsole != currentSettings.ShowConsoleOnStartup)
        {
            needsRestart = true;
        }
        currentSettings.ShowConsoleOnStartup = newShowConsole;
        
        // 保存
        currentSettings.Save();
        
        // 成功メッセージを表示
        if (needsRestart)
        {
            var consoleMessage = newShowConsole 
                ? "設定が保存されました。\nコンソール表示設定は次回起動時から有効になります。"
                : "設定が保存されました。\nコンソール表示設定は次回起動時から無効になります。";
            await dialogService.ShowInfoAsync(L.Get("Success.Title"), consoleMessage);
        }
        else if (languageChanged)
        {
            // 言語変更済みのメッセージ
            await dialogService.ShowInfoAsync(L.Get("Success.Title"), L.Get("Settings.LanguageChanged"));
        }
        
        Close();
    }
    
    
    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (currentSettings != null && LanguageComboBox.SelectedIndex >= 0)
        {
            var newLanguage = LanguageComboBox.SelectedIndex == 0 ? "ja-JP" : "en-US";
            if (newLanguage != LocalizationManager.Instance.CurrentLanguage)
            {
                // 即座に言語を変更
                LocalizationManager.Instance.ChangeLanguage(newLanguage);
            }
        }
    }
    
    private void OnOpenLogFolder(object? sender, RoutedEventArgs e)
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
            dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Format("Error.OpenFolderFailed", ex.Message)).Wait();
        }
    }
    
    private void OnResetManager(object? sender, RoutedEventArgs e)
    {
        Close();
        ResetManagerRequested?.Invoke(this, EventArgs.Empty);
    }
    
    private void OnRestoreOriginal(object? sender, RoutedEventArgs e)
    {
        Close();
        RestoreOriginalRequested?.Invoke(this, EventArgs.Empty);
    }
    
    private async void OnCheckForUpdate(object? sender, RoutedEventArgs e)
    {
        try
        {
            var updateInfo = await updateService.CheckForUpdateAsync();
            
            if (updateInfo != null)
            {
                // アップデートダイアログを表示
                var dialog = new UpdateDialog()
                {
                    DataContext = new UpdateDialogViewModel(updateService, updateInfo)
                };
                await dialog.ShowDialog(this);
            }
            else
            {
                await dialogService.ShowInfoAsync(
                    L.Get("Settings.UpdateCheckTitle"),
                    L.Get("Settings.NoUpdateAvailable")
                );
            }
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Settings.UpdateCheckFailed", ex.Message)
            );
        }
    }
}
