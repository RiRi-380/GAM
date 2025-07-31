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
        
        /// <summary>
        /// Steam のインストールパスを取得
        /// </summary>
        public static string? GetSteamPath()
        {
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
                            return steamPath.Replace('/', '\\');
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
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                // Failed to find Steam installation - log but return null
                System.Diagnostics.Debug.WriteLine($"Error while searching for Steam installation: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// appworkshop_4000.acf ファイルのパスを取得
        /// </summary>
        public static string? GetWorkshopCacheFilePath()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;
            
            var workshopCachePath = Path.Combine(steamPath, "steamapps", "workshop", WORKSHOP_CACHE_FILE);
            if (File.Exists(workshopCachePath))
                return workshopCachePath;
            
            // 別の場所も試す
            var altPath = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(altPath))
            {
                foreach (var userDir in Directory.GetDirectories(altPath))
                {
                    var userWorkshopPath = Path.Combine(userDir, "ugc", WORKSHOP_CACHE_FILE);
                    if (File.Exists(userWorkshopPath))
                        return userWorkshopPath;
                }
            }
            
            return null;
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
                
                var content = File.ReadAllText(cacheFilePath);
                
                // ACFファイルから WorkshopItemsInstalled セクションを探す
                var installedMatch = Regex.Match(content, @"""WorkshopItemsInstalled""\s*{([^}]+)}", RegexOptions.Singleline);
                if (installedMatch.Success)
                {
                    var installedSection = installedMatch.Groups[1].Value;
                    
                    // IDを抽出 (形式: "1234567890" "0" など)
                    var idMatches = Regex.Matches(installedSection, @"""(\d+)""\s*""(\d+)""");
                    foreach (Match match in idMatches)
                    {
                        if (match.Groups.Count >= 2)
                        {
                            addonIds.Add(match.Groups[1].Value);
                        }
                    }
                }
                
                // WorkshopItemDetails セクションも確認
                var detailsMatch = Regex.Match(content, @"""WorkshopItemDetails""\s*{([^}]+)}", RegexOptions.Singleline);
                if (detailsMatch.Success)
                {
                    var detailsSection = detailsMatch.Groups[1].Value;
                    
                    // IDを抽出
                    var idMatches = Regex.Matches(detailsSection, @"""(\d+)""\s*{");
                    foreach (Match match in idMatches)
                    {
                        if (match.Groups.Count >= 2)
                        {
                            var id = match.Groups[1].Value;
                            if (!addonIds.Contains(id))
                            {
                                addonIds.Add(id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
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
                
                var content = File.ReadAllText(cacheFilePath);
                
                // WorkshopItemDetails セクションを解析
                var detailsMatch = Regex.Match(content, @"""WorkshopItemDetails""\s*{(.+?)^\s*}", 
                    RegexOptions.Singleline | RegexOptions.Multiline);
                    
                if (detailsMatch.Success)
                {
                    var detailsSection = detailsMatch.Groups[1].Value;
                    
                    // 各アイテムの詳細を抽出
                    var itemMatches = Regex.Matches(detailsSection, 
                        @"""(\d+)""\s*{([^}]+?)^\s*}", 
                        RegexOptions.Singleline | RegexOptions.Multiline);
                        
                    foreach (Match itemMatch in itemMatches)
                    {
                        if (itemMatch.Groups.Count >= 3)
                        {
                            var id = itemMatch.Groups[1].Value;
                            var itemContent = itemMatch.Groups[2].Value;
                            
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
                }
            }
            catch (Exception ex)
            {
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