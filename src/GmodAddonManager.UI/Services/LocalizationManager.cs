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
        private const string DefaultLanguage = "en-US";
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
            : this(
                "ja-JP",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources"))
        {
        }

        internal LocalizationManager(string currentLanguage, string resourcesPath)
        {
#if DEBUG
            SafeFileLogger.TryLogInfo(
                "LocalizationManager.Constructor",
                $"Constructor started at: {DateTime.Now:O}");
#endif

            _resources = new Dictionary<string, Dictionary<string, string>>();
            _currentLanguage = currentLanguage;

            try
            {
                LoadResources(resourcesPath);
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

                EnsureLanguageDictionaries();
            }
        }

        internal LocalizationManager(
            string currentLanguage,
            Dictionary<string, Dictionary<string, string>> resources)
        {
            _currentLanguage = currentLanguage;
            _resources = resources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value != null
                    ? new Dictionary<string, string>(pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
            EnsureLanguageDictionaries();
        }

        private void LoadResources(string resourcesPath)
        {
            // Load each language independently. A malformed or unreadable current-language
            // file must not discard a usable default-language dictionary.
            _resources[DefaultLanguage] = LoadResourceFile(
                Path.Combine(resourcesPath, $"{DefaultLanguage}.json"));
            _resources["ja-JP"] = LoadResourceFile(
                Path.Combine(resourcesPath, "ja-JP.json"));
            EnsureLanguageDictionaries();
        }

        private void EnsureLanguageDictionaries()
        {
            foreach (var language in new[] { DefaultLanguage, "ja-JP" })
            {
                if (!_resources.TryGetValue(language, out var dictionary) || dictionary == null)
                {
                    _resources[language] = new Dictionary<string, string>(StringComparer.Ordinal);
                }
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

            if (TryGetNonEmptyString(_currentLanguage, key, out var value))
            {
                return value;
            }

            if (!string.Equals(_currentLanguage, DefaultLanguage, StringComparison.OrdinalIgnoreCase) &&
                TryGetNonEmptyString(DefaultLanguage, key, out value))
            {
                return value;
            }

            return key;
        }

        private bool TryGetNonEmptyString(string language, string key, out string value)
        {
            if (_resources.TryGetValue(language, out var languageResources) &&
                languageResources != null &&
                languageResources.TryGetValue(key, out var candidate) &&
                !string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate;
                return true;
            }

            value = string.Empty;
            return false;
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
