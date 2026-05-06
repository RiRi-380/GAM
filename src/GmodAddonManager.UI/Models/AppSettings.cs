using System;
using System.IO;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Models
{
    public class AppSettings
    {
        public string Language { get; set; } = "ja-JP";
        public bool ShowConsoleOnStartup { get; set; } = false;
        public DisableMode DisableMode { get; set; } = DisableMode.Soft;
        public bool StrictLinkMode { get; set; } = false;
        public bool EnableBackgroundTitleUpdates { get; set; } = false;
        public bool EnableBackgroundAddonPreload { get; set; } = false;
        public bool EnableLocalAddonsExperimental { get; set; } = false;
        public bool EnableDisableManifestImport { get; set; } = false;
        public string DeveloperModePhrase { get; set; } = "";
        
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
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AppSettings.Load", ex);
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
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AppSettings.Save", ex);
            }
        }
    }
}
