using System;
using System.IO;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Models
{
    public class AppSettings
    {
        public string Language { get; set; } = "ja-JP";
        public bool ShowConsoleOnStartup { get; set; } = false;
        public bool EnableBackgroundTitleUpdates { get; set; } = false;
        public bool EnableBackgroundAddonPreload { get; set; } = false;
        public string? CustomGmodInstallPath { get; set; }
        public string? CustomWorkshopPath { get; set; }
        public string? ConfirmedGmodInstallPath { get; set; }
        public string? ConfirmedWorkshopPath { get; set; }
        public string? DismissedPathRecoverySignature { get; set; }
        
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager",
            "settings.json"
        );
        
        public static AppSettings Load()
        {
            try
            {
                return LoadFrom(SettingsPath);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AppSettings.Load", ex);
            }
            
            return new AppSettings();
        }

        public static AppSettings LoadFrom(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("The settings path is required.", nameof(path));
            }

            var backupPath = path + ".bak";
            if (!File.Exists(path) && !File.Exists(backupPath))
            {
                return new AppSettings();
            }

            try
            {
                return ReadValidated(path);
            }
            catch (Exception primaryException) when (File.Exists(backupPath))
            {
                AppSettings recovered;
                try
                {
                    recovered = ReadValidated(backupPath);
                }
                catch (Exception backupException)
                {
                    throw new InvalidOperationException(
                        "Both the primary application settings and their backup are unreadable.",
                        new AggregateException(primaryException, backupException));
                }

                return recovered;
            }
        }
        
        public void Save()
        {
            SaveTo(SettingsPath);
        }

        public void SaveTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("The settings path is required.", nameof(path));
            }

            var tempPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(tempPath, json);

                if (!File.Exists(path))
                {
                    File.Move(tempPath, path);
                    return;
                }

                if (IsValid(path))
                {
                    File.Replace(tempPath, path, path + ".bak");
                }
                else
                {
                    var corruptArchivePath =
                        path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
                    File.Replace(tempPath, path, corruptArchivePath);
                }
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AppSettings.Save", ex);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("AppSettings.Save.Cleanup", ex);
                }
            }
        }

        private static AppSettings ReadValidated(string path)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Application settings file is empty.");
            }

            return Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json)
                ?? throw new InvalidOperationException("Application settings deserialized to null.");
        }

        private static bool IsValid(string path)
        {
            try
            {
                _ = ReadValidated(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
