using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Reads Garry's Mod Workshop subscription data from Steam appworkshop_4000.acf files.
    /// </summary>
    public class SteamWorkshopCacheReader
    {
        private const string GMOD_APP_ID = "4000";
        private const string WORKSHOP_CACHE_FILE = $"appworkshop_{GMOD_APP_ID}.acf";

        public static string? GetSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var steamPath = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(steamPath))
                        {
                            return NormalizeSteamPath(steamPath);
                        }
                    }
                }

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
                System.Diagnostics.Debug.WriteLine($"Error while searching for Steam installation: {ex.Message}");
            }

            return null;
        }

        public static string? GetWorkshopCacheFilePath()
        {
            return GetWorkshopCacheFilePaths().FirstOrDefault();
        }

        public static IReadOnlyList<string> GetWorkshopCacheFilePaths()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                return Array.Empty<string>();
            }

            return GetWorkshopCacheFilePaths(steamPath);
        }

        internal static IReadOnlyList<string> GetWorkshopCacheFilePaths(string steamPath)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var libraryPath in GetSteamLibraryPaths(steamPath))
            {
                AddExistingPath(paths, seen, Path.Combine(libraryPath, "steamapps", "workshop", WORKSHOP_CACHE_FILE));
            }

            return paths;
        }

        internal static IReadOnlyList<string> GetSteamLibraryPaths(string steamPath)
        {
            var libraryPaths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddExistingDirectory(libraryPaths, seen, NormalizeSteamPath(steamPath));

            try
            {
                var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersPath))
                {
                    return libraryPaths;
                }

                var content = File.ReadAllText(libraryFoldersPath);
                var matches = Regex.Matches(content, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase);

                foreach (Match match in matches)
                {
                    AddExistingDirectory(libraryPaths, seen, NormalizeSteamPath(match.Groups[1].Value));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read Steam library folders: {ex.Message}");
            }

            return libraryPaths;
        }

        public static List<string> GetSubscribedAddonIds()
        {
            return GetSubscribedAddonIds(GetWorkshopCacheFilePaths());
        }

        internal static List<string> GetSubscribedAddonIds(IEnumerable<string> cacheFilePaths)
        {
            var addonIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var cacheFilePath in cacheFilePaths.Where(File.Exists))
            {
                try
                {
                    var content = File.ReadAllText(cacheFilePath);
                    foreach (var addonId in ParseSubscribedAddonIds(content))
                    {
                        if (seen.Add(addonId))
                        {
                            addonIds.Add(addonId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read Workshop cache {cacheFilePath}: {ex.Message}");
                }
            }

            return addonIds;
        }

        internal static List<string> ParseSubscribedAddonIds(string content)
        {
            var addonIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (TryGetSection(content, "WorkshopItemsInstalled", out var installedSection))
            {
                foreach (var item in EnumerateNumericChildSections(installedSection))
                {
                    AddAddonId(addonIds, seen, item.Key);
                }

                if (addonIds.Count == 0)
                {
                    var idMatches = Regex.Matches(installedSection, @"""(\d+)""\s*""[^""]*""");
                    foreach (Match match in idMatches)
                    {
                        AddAddonId(addonIds, seen, match.Groups[1].Value);
                    }
                }
            }

            return addonIds;
        }

        public static Dictionary<string, WorkshopItemInfo> GetAddonDetails()
        {
            return GetAddonDetails(GetWorkshopCacheFilePaths());
        }

        internal static Dictionary<string, WorkshopItemInfo> GetAddonDetails(IEnumerable<string> cacheFilePaths)
        {
            var addonInfo = new Dictionary<string, WorkshopItemInfo>(StringComparer.Ordinal);

            foreach (var cacheFilePath in cacheFilePaths.Where(File.Exists))
            {
                try
                {
                    var content = File.ReadAllText(cacheFilePath);
                    foreach (var kvp in ParseAddonDetails(content))
                    {
                        addonInfo[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read Workshop details {cacheFilePath}: {ex.Message}");
                }
            }

            return addonInfo;
        }

        internal static Dictionary<string, WorkshopItemInfo> ParseAddonDetails(string content)
        {
            var addonInfo = new Dictionary<string, WorkshopItemInfo>(StringComparer.Ordinal);

            if (!TryGetSection(content, "WorkshopItemDetails", out var detailsSection))
            {
                return addonInfo;
            }

            foreach (var item in EnumerateNumericChildSections(detailsSection))
            {
                var info = new WorkshopItemInfo { Id = item.Key };

                var title = ReadStringField(item.Body, "title");
                if (!string.IsNullOrEmpty(title))
                {
                    info.Title = title;
                }

                var tags = ReadStringField(item.Body, "tags");
                if (!string.IsNullOrEmpty(tags))
                {
                    info.Tags = tags;
                }

                var timeUpdated = ReadStringField(item.Body, "timeupdated");
                if (long.TryParse(timeUpdated, out var timestamp))
                {
                    info.TimeUpdated = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                }

                addonInfo[item.Key] = info;
            }

            return addonInfo;
        }

        private static void AddExistingPath(List<string> paths, HashSet<string> seen, string path)
        {
            if (File.Exists(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        private static void AddExistingDirectory(List<string> paths, HashSet<string> seen, string path)
        {
            if (Directory.Exists(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        private static void AddAddonId(List<string> addonIds, HashSet<string> seen, string addonId)
        {
            if (seen.Add(addonId))
            {
                addonIds.Add(addonId);
            }
        }

        private static string NormalizeSteamPath(string path)
        {
            return path.Replace('/', '\\').Replace(@"\\", @"\");
        }

        private static string? ReadStringField(string section, string fieldName)
        {
            var match = Regex.Match(
                section,
                $@"""{Regex.Escape(fieldName)}""\s*""([^""]*)""",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace(@"\\", @"\");
        }

        private static bool TryGetSection(string content, string sectionName, out string section)
        {
            section = string.Empty;
            var match = Regex.Match(
                content,
                $@"""{Regex.Escape(sectionName)}""\s*{{",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return false;
            }

            var openBraceIndex = content.IndexOf('{', match.Index + match.Length - 1);
            return TryExtractBraceBody(content, openBraceIndex, out section, out _);
        }

        private static IEnumerable<(string Key, string Body)> EnumerateNumericChildSections(string section)
        {
            var searchIndex = 0;
            while (searchIndex < section.Length)
            {
                var match = Regex.Match(
                    section.Substring(searchIndex),
                    @"""(\d+)""\s*{",
                    RegexOptions.CultureInvariant);

                if (!match.Success)
                {
                    yield break;
                }

                var absoluteMatchIndex = searchIndex + match.Index;
                var openBraceIndex = section.IndexOf('{', absoluteMatchIndex + match.Length - 1);
                if (!TryExtractBraceBody(section, openBraceIndex, out var body, out var closeBraceIndex))
                {
                    yield break;
                }

                yield return (match.Groups[1].Value, body);
                searchIndex = closeBraceIndex + 1;
            }
        }

        private static bool TryExtractBraceBody(string content, int openBraceIndex, out string body, out int closeBraceIndex)
        {
            body = string.Empty;
            closeBraceIndex = -1;

            if (openBraceIndex < 0 || openBraceIndex >= content.Length || content[openBraceIndex] != '{')
            {
                return false;
            }

            var depth = 0;
            var inString = false;
            var escaping = false;
            var bodyStart = openBraceIndex + 1;

            for (var i = openBraceIndex; i < content.Length; i++)
            {
                var c = content[i];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (c == '\\')
                    {
                        escaping = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBraceIndex = i;
                        body = content.Substring(bodyStart, i - bodyStart);
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public class WorkshopItemInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
        public DateTime? TimeUpdated { get; set; }
    }
}
