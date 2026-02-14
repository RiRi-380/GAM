using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Steam Workshop のキャッシュファイル (appworkshop_4000.acf) から
    /// サブスクライブ済みアドオンの情報を高速に読み取る
    /// </summary>
    public class SteamWorkshopCacheReader
    {
        private const string GMOD_APP_ID = "4000";
        private const string WORKSHOP_CACHE_FILE = $"appworkshop_{GMOD_APP_ID}.acf";
        private static readonly object CacheLock = new object();
        private static readonly TimeSpan PathCacheTtl = TimeSpan.FromSeconds(30);
        private static bool _steamPathCacheInitialized;
        private static string? _cachedSteamPath;
        private static DateTime _cachedSteamPathAtUtc = DateTime.MinValue;
        private static bool _cachePathInitialized;
        private static string? _cachedWorkshopCachePath;
        private static DateTime _cachedWorkshopCachePathAtUtc = DateTime.MinValue;
        private static string? _cachedParsedPath;
        private static DateTime _cachedParsedWriteUtc = DateTime.MinValue;
        private static List<string>? _cachedSubscribedAddonIds;
        private static Dictionary<string, WorkshopItemInfo>? _cachedAddonDetails;
        
        /// <summary>
        /// Steam のインストールパスを取得
        /// </summary>
        public static string? GetSteamPath()
        {
            lock (CacheLock)
            {
                if (_steamPathCacheInitialized &&
                    DateTime.UtcNow - _cachedSteamPathAtUtc <= PathCacheTtl)
                {
                    return _cachedSteamPath;
                }
            }

            string? detectedPath = null;
            try
            {
                // Windowsレジストリから取得
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var steamPath = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(steamPath))
                        {
                            detectedPath = steamPath.Replace('/', '\\');
                            return detectedPath;
                        }
                    }
                }
                
                // 一般的なパスを試す
                var commonPaths = new[]
                {
                    @"C:\Program Files (x86)\Steam",
                    @"C:\Program Files\Steam",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam")
                };
                
                foreach (var path in commonPaths)
                {
                    if (Directory.Exists(path))
                    {
                        detectedPath = path;
                        return detectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                // Failed to find Steam installation - log but return null
                System.Diagnostics.Debug.WriteLine($"Error while searching for Steam installation: {ex.Message}");
            }
            finally
            {
                lock (CacheLock)
                {
                    _steamPathCacheInitialized = true;
                    _cachedSteamPath = detectedPath;
                    _cachedSteamPathAtUtc = DateTime.UtcNow;
                }
            }

            return detectedPath;
        }
        
        /// <summary>
        /// appworkshop_4000.acf ファイルのパスを取得
        /// </summary>
        public static string? GetWorkshopCacheFilePath()
        {
            lock (CacheLock)
            {
                if (_cachePathInitialized &&
                    DateTime.UtcNow - _cachedWorkshopCachePathAtUtc <= PathCacheTtl)
                {
                    return _cachedWorkshopCachePath;
                }
            }

            string? detectedPath = null;
            var steamPath = GetSteamPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                var workshopCachePath = Path.Combine(steamPath, "steamapps", "workshop", WORKSHOP_CACHE_FILE);
                if (File.Exists(workshopCachePath))
                {
                    detectedPath = workshopCachePath;
                }
                else
                {
                    // 別の場所も試す
                    var altPath = Path.Combine(steamPath, "userdata");
                    if (Directory.Exists(altPath))
                    {
                        foreach (var userDir in Directory.GetDirectories(altPath))
                        {
                            var userWorkshopPath = Path.Combine(userDir, "ugc", WORKSHOP_CACHE_FILE);
                            if (File.Exists(userWorkshopPath))
                            {
                                detectedPath = userWorkshopPath;
                                break;
                            }
                        }
                    }
                }
            }

            lock (CacheLock)
            {
                _cachePathInitialized = true;
                _cachedWorkshopCachePath = detectedPath;
                _cachedWorkshopCachePathAtUtc = DateTime.UtcNow;
            }

            return detectedPath;
        }

        private static string? TryGetSection(string content, string sectionName)
        {
            if (string.IsNullOrEmpty(content))
                return null;

            var token = "\"" + sectionName + "\"";
            var idx = content.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            var braceStart = content.IndexOf('{', idx);
            if (braceStart < 0)
                return null;

            var braceEnd = FindMatchingBrace(content, braceStart);
            if (braceEnd < 0)
                return null;

            return content.Substring(braceStart + 1, braceEnd - braceStart - 1);
        }

        private static int FindMatchingBrace(string text, int startIndex)
        {
            var depth = 0;
            for (var i = startIndex; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static IEnumerable<(string Id, string Block)> EnumerateItemBlocks(string sectionText)
        {
            if (string.IsNullOrEmpty(sectionText))
                yield break;

            var i = 0;
            while (i < sectionText.Length)
            {
                var keyStart = sectionText.IndexOf('"', i);
                if (keyStart < 0)
                    yield break;
                var keyEnd = sectionText.IndexOf('"', keyStart + 1);
                if (keyEnd < 0)
                    yield break;
                var key = sectionText.Substring(keyStart + 1, keyEnd - keyStart - 1);

                var braceStart = sectionText.IndexOf('{', keyEnd + 1);
                if (braceStart < 0)
                    yield break;

                var braceEnd = FindMatchingBrace(sectionText, braceStart);
                if (braceEnd < 0)
                    yield break;

                var block = sectionText.Substring(braceStart + 1, braceEnd - braceStart - 1);
                yield return (key, block);
                i = braceEnd + 1;
            }
        }
        
        /// <summary>
        /// サブスクライブ済みアドオンのIDリストを取得
        /// </summary>
        public static List<string> GetSubscribedAddonIds()
        {
            var addonIds = new List<string>();
            
            try
            {
                var cacheFilePath = GetWorkshopCacheFilePath();
                if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath))
                    return addonIds;

                var lastWriteUtc = File.GetLastWriteTimeUtc(cacheFilePath);
                lock (CacheLock)
                {
                    if (string.Equals(_cachedParsedPath, cacheFilePath, StringComparison.OrdinalIgnoreCase) &&
                        _cachedParsedWriteUtc == lastWriteUtc &&
                        _cachedSubscribedAddonIds != null)
                    {
                        return new List<string>(_cachedSubscribedAddonIds);
                    }
                }

                var content = File.ReadAllText(cacheFilePath);
                
                var installedSection = TryGetSection(content, "WorkshopItemsInstalled");
                if (!string.IsNullOrEmpty(installedSection))
                {
                    foreach (var (id, _) in EnumerateItemBlocks(installedSection))
                    {
                        if (!id.All(char.IsDigit))
                            continue;
                        addonIds.Add(id);
                    }
                }
                
                var detailsSection = TryGetSection(content, "WorkshopItemDetails");
                if (!string.IsNullOrEmpty(detailsSection))
                {
                    foreach (var (id, _) in EnumerateItemBlocks(detailsSection))
                    {
                        if (!id.All(char.IsDigit))
                            continue;
                        if (!addonIds.Contains(id))
                        {
                            addonIds.Add(id);
                        }
                    }
                }
                lock (CacheLock)
                {
                    _cachedParsedPath = cacheFilePath;
                    _cachedParsedWriteUtc = lastWriteUtc;
                    _cachedSubscribedAddonIds = new List<string>(addonIds);
                    _cachedAddonDetails = null;
                }
            }
            catch (Exception)
            {
                // エラーログ
            }
            
            return addonIds;
        }
        
        /// <summary>
        /// アドオンの詳細情報を取得（タイトル、タグなど）
        /// </summary>
        public static Dictionary<string, WorkshopItemInfo> GetAddonDetails()
        {
            var addonInfo = new Dictionary<string, WorkshopItemInfo>();
            
            try
            {
                var cacheFilePath = GetWorkshopCacheFilePath();
                if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath))
                    return addonInfo;

                var lastWriteUtc = File.GetLastWriteTimeUtc(cacheFilePath);
                lock (CacheLock)
                {
                    if (string.Equals(_cachedParsedPath, cacheFilePath, StringComparison.OrdinalIgnoreCase) &&
                        _cachedParsedWriteUtc == lastWriteUtc &&
                        _cachedAddonDetails != null)
                    {
                        return new Dictionary<string, WorkshopItemInfo>(_cachedAddonDetails);
                    }
                }

                var content = File.ReadAllText(cacheFilePath);
                
                var detailsSection = TryGetSection(content, "WorkshopItemDetails");
                if (!string.IsNullOrEmpty(detailsSection))
                {
                    foreach (var (id, itemContent) in EnumerateItemBlocks(detailsSection))
                    {
                        if (!id.All(char.IsDigit))
                            continue;
                        
                        var info = new WorkshopItemInfo { Id = id };
                        
                        // タイトルを抽出
                        var titleMatch = Regex.Match(itemContent, @"""title""\s*""([^""]+)""");
                        if (titleMatch.Success)
                            info.Title = titleMatch.Groups[1].Value;
                        
                        // タグを抽出
                        var tagsMatch = Regex.Match(itemContent, @"""tags""\s*""([^""]+)""");
                        if (tagsMatch.Success)
                            info.Tags = tagsMatch.Groups[1].Value;
                        
                        // 更新時刻を抽出
                        var timeMatch = Regex.Match(itemContent, @"""timeupdated""\s*""(\d+)""");
                        if (timeMatch.Success && long.TryParse(timeMatch.Groups[1].Value, out var timestamp))
                        {
                            info.TimeUpdated = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                        }
                        
                        addonInfo[id] = info;
                    }
                }
                lock (CacheLock)
                {
                    _cachedParsedPath = cacheFilePath;
                    _cachedParsedWriteUtc = lastWriteUtc;
                    _cachedAddonDetails = new Dictionary<string, WorkshopItemInfo>(addonInfo);
                    _cachedSubscribedAddonIds = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SteamWorkshopCacheReader] Failed to parse workshop cache: {ex.Message}");
            }
            
            return addonInfo;
        }
    }
    
    public class WorkshopItemInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Tags { get; set; } = "";
        public DateTime? TimeUpdated { get; set; }
    }
}
