using System;
using System.IO;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.Models
{
    public class AppSettings
    {
        public string Language { get; set; } = "ja-JP";
        public bool ShowConsoleOnStartup { get; set; } = false;
        public DisableMode DisableMode { get; set; } = DisableMode.Soft;
        public bool UnsubscribeOnHardDisable { get; set; } = false;
        public bool StrictLinkMode { get; set; } = false;
        public bool EnableDisableManifestImport { get; set; } = false;
        
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager",
            "settings.json"
        );
        
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch
            {
                // エラー時はデフォルト設定を返す
            }
            
            return new AppSettings();
        }
        
        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // エラーは無視（ログに記録するなど）
            }
        }
    }
}
