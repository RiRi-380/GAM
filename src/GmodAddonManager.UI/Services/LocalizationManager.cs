using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
#if DEBUG
                        SafeFileLogger.TryLogInfo(
                            "LocalizationManager.Instance",
                            $"Creating new instance at: {DateTime.Now:O}");
#endif
                        _instance = new LocalizationManager();
                    }

                    return _instance;
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("LocalizationManager.Instance", ex);
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
                    OnPropertyChanged("");
                }
            }
        }

        private LocalizationManager()
        {
#if DEBUG
            SafeFileLogger.TryLogInfo(
                "LocalizationManager.Constructor",
                $"Constructor started at: {DateTime.Now:O}");
#endif

            _resources = new Dictionary<string, Dictionary<string, string>>();
            _currentLanguage = "ja-JP";

            try
            {
                LoadResources();
#if DEBUG
                SafeFileLogger.TryLogInfo(
                    "LocalizationManager.LoadResources",
                    $"Resources loaded at: {DateTime.Now:O}");
#endif
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("LocalizationManager.LoadResources", ex);
#if DEBUG
                SafeFileLogger.TryLogInfo(
                    "LocalizationManager.LoadResources",
                    $"LoadResources failed at: {DateTime.Now:O}; using empty fallback dictionaries.");
#endif

                _resources["ja-JP"] = new Dictionary<string, string>();
                _resources["en-US"] = new Dictionary<string, string>();
            }
        }

        private void LoadResources()
        {
            try
            {
                var resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");

                _resources["ja-JP"] = LoadResourceFile(Path.Combine(resourcesPath, "ja-JP.json"));
                _resources["en-US"] = LoadResourceFile(Path.Combine(resourcesPath, "en-US.json"));

                if (!_resources.ContainsKey("ja-JP"))
                {
                    _resources["ja-JP"] = new Dictionary<string, string>();
                }

                if (!_resources.ContainsKey("en-US"))
                {
                    _resources["en-US"] = new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("LocalizationManager.LoadResources", ex);
                _resources["ja-JP"] = new Dictionary<string, string>();
                _resources["en-US"] = new Dictionary<string, string>();
            }
        }

        private static Dictionary<string, string> LoadResourceFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new Dictionary<string, string>();
                }

                var bytes = File.ReadAllBytes(path);
                if (TryLoadResource(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), out var resources))
                {
                    return resources;
                }
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException($"LocalizationManager.LoadResourceFile({path})", ex);
            }

            return new Dictionary<string, string>();
        }

        private static bool TryLoadResource(byte[] bytes, Encoding encoding, out Dictionary<string, string> resources)
        {
            resources = new Dictionary<string, string>();
            try
            {
                var text = encoding.GetString(bytes);
                if (!string.IsNullOrEmpty(text) && text[0] == '\uFEFF')
                {
                    text = text.Substring(1);
                }

                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);
                if (parsed == null)
                {
                    return false;
                }

                resources = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string GetString(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (_resources.TryGetValue(_currentLanguage, out var langResources) &&
                langResources != null &&
                langResources.TryGetValue(key, out var value))
            {
                return value;
            }

            return key;
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

    public static class Loc
    {
        public static string Get(string key) => LocalizationManager.Instance.GetString(key);
    }
}
