using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace GmodAddonManager.UI.Services
{
    public class LocalizationManager : INotifyPropertyChanged
    {
        private static LocalizationManager? _instance;
        private Dictionary<string, Dictionary<string, string>> _resources;
        private string _currentLanguage;
        
        public static LocalizationManager Instance
        {
            get
            {
                try
                {
                    if (_instance == null)
                    {
                        System.IO.File.AppendAllText("app_startup.log", $"LocalizationManager.Instance creating new instance at: {DateTime.Now}\n");
                        _instance = new LocalizationManager();
                    }
                    return _instance;
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText("localization_instance_error.log", $"LocalizationManager.Instance Error at: {DateTime.Now}\n{ex.ToString()}");
                    throw;
                }
            }
        }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(""); // すべてのバインディングを更新
                }
            }
        }
        
        private LocalizationManager()
        {
            try
            {
                System.IO.File.AppendAllText("app_startup.log", $"LocalizationManager constructor started at: {DateTime.Now}\n");
            }
            catch 
            {
                // Ignore debug logging errors - non-critical
            }
            
            _resources = new Dictionary<string, Dictionary<string, string>>();
            _currentLanguage = "ja-JP";
            
            try
            {
                LoadResources();
                System.IO.File.AppendAllText("app_startup.log", $"LocalizationManager resources loaded at: {DateTime.Now}\n");
            }
            catch (Exception ex)
            {
                // エラーログを出力
                try
                {
                    System.IO.File.WriteAllText("localization_init_error.log", $"Localization Init Error at: {DateTime.Now}\n{ex.ToString()}");
                    System.IO.File.AppendAllText("app_startup.log", $"LocalizationManager LoadResources failed at: {DateTime.Now}\n{ex.Message}\n");
                }
                catch 
                {
                    // Ignore error logging failures - non-critical
                }
                
                // リソースの読み込みに失敗してもアプリケーションを起動させる
                _resources["ja-JP"] = new Dictionary<string, string>();
                _resources["en-US"] = new Dictionary<string, string>();
            }
        }
        
        private void LoadResources()
        {
            try
            {
                var resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                
                // 日本語リソース
                var jaPath = Path.Combine(resourcesPath, "ja-JP.json");
                if (File.Exists(jaPath))
                {
                    var jaJson = File.ReadAllText(jaPath);
                    _resources["ja-JP"] = JsonConvert.DeserializeObject<Dictionary<string, string>>(jaJson) ?? new Dictionary<string, string>();
                }
                
                // 英語リソース
                var enPath = Path.Combine(resourcesPath, "en-US.json");
                if (File.Exists(enPath))
                {
                    var enJson = File.ReadAllText(enPath);
                    _resources["en-US"] = JsonConvert.DeserializeObject<Dictionary<string, string>>(enJson) ?? new Dictionary<string, string>();
                }
                
                // リソースファイルが見つからない場合は空の辞書を作成
                if (!_resources.ContainsKey("ja-JP"))
                    _resources["ja-JP"] = new Dictionary<string, string>();
                if (!_resources.ContainsKey("en-US"))
                    _resources["en-US"] = new Dictionary<string, string>();
            }
            catch
            {
                // エラー時は空の辞書を使用
                _resources["ja-JP"] = new Dictionary<string, string>();
                _resources["en-US"] = new Dictionary<string, string>();
            }
        }
        
        public string GetString(string key)
        {
            // keyがnullの場合は空文字を返す
            if (string.IsNullOrEmpty(key))
                return string.Empty;
                
            // リソースがロードされていない場合のフォールバック
            if (_resources == null || _resources.Count == 0)
            {
                // 日本語のデフォルト値を返す
                return GetDefaultJapaneseValue(key);
            }
            
            if (_resources.TryGetValue(_currentLanguage, out var langResources))
            {
                if (langResources != null && langResources.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            
            // フォールバック: 日本語のデフォルト値を返す
            return GetDefaultJapaneseValue(key);
        }
        
        private string GetDefaultJapaneseValue(string key)
        {
            // 最低限必要なキーに対してデフォルト値を返す
            switch (key)
            {
                case "MainWindow.Title": return "Gmod Addon Manager";
                case "Asset.Subscribe": return "サブスクライブ";
                case "Error.Title": return "エラー";
                case "Success.Title": return "完了";
                case "Warning.Title": return "警告";
                case "Confirm.Title": return "確認";
                case "Info.Title": return "情報";
                case "Dialog.OK": return "OK";
                case "Dialog.Cancel": return "キャンセル";
                case "Dialog.Save": return "保存";
                case "Dialog.Yes": return "はい";
                case "Dialog.No": return "いいえ";
                case "Dialog.Create": return "作成";
                case "Dialog.CreateAsset": return "アセット作成";
                case "Dialog.AssetNamePrompt": return "アセット名を入力してください:";
                case "Status.Ready": return "準備完了";
                default: return key;
            }
        }
        
        public string this[string key] => GetString(key);
        
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public void ChangeLanguage(string language)
        {
            CurrentLanguage = language;
        }
    }
    
    // XAMLで使用するための静的クラス
    public static class Loc
    {
        public static string Get(string key) => LocalizationManager.Instance.GetString(key);
    }
}