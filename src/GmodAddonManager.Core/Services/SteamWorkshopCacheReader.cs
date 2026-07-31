using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using GmodAddonManager.Core.Models;
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
        private const int StableReadAttempts = 5;
        private const int StableReadDelayMs = 50;

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
            return GetWorkshopSnapshot().SubscribedIds.ToList();
        }

        internal static List<string> GetSubscribedAddonIds(IEnumerable<string> cacheFilePaths)
        {
            return GetWorkshopSnapshot(cacheFilePaths).SubscribedIds.ToList();
        }

        public static SteamWorkshopSnapshot GetWorkshopSnapshot()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                return new SteamWorkshopSnapshot(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isAuthoritative: false,
                    DateTime.UtcNow);
            }

            var manifestPaths = GetWorkshopCacheFilePaths(steamPath);

            return GetWorkshopSnapshot(manifestPaths);
        }

        internal static SteamWorkshopSnapshot GetWorkshopSnapshot(IEnumerable<string>? cacheFilePaths)
        {
            var observedAtUtc = DateTime.UtcNow;
            var subscribedIds = new HashSet<string>(StringComparer.Ordinal);
            var installedIds = new HashSet<string>(StringComparer.Ordinal);
            var isAuthoritative = true;

            string[] paths;
            try
            {
                paths = cacheFilePaths?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to enumerate Workshop cache paths: {ex.Message}");
                paths = Array.Empty<string>();
                isAuthoritative = false;
            }

            if (paths.Length == 0)
            {
                isAuthoritative = false;
            }

            foreach (var cacheFilePath in paths)
            {
                if (!File.Exists(cacheFilePath))
                {
                    isAuthoritative = false;
                    continue;
                }

                try
                {
                    if (!TryReadStableText(cacheFilePath, out var content))
                    {
                        isAuthoritative = false;
                        continue;
                    }

                    var parsed = ParseWorkshopSnapshot(content, observedAtUtc);
                    subscribedIds.UnionWith(parsed.SubscribedIds);
                    installedIds.UnionWith(parsed.InstalledIds);
                    isAuthoritative &= parsed.IsAuthoritative;
                }
                catch (Exception ex)
                {
                    isAuthoritative = false;
                    System.Diagnostics.Debug.WriteLine($"Failed to read Workshop cache {cacheFilePath}: {ex.Message}");
                }
            }

            return new SteamWorkshopSnapshot(
                subscribedIds,
                installedIds,
                isAuthoritative,
                observedAtUtc);
        }

        internal static List<string> ParseSubscribedAddonIds(string content)
        {
            return ParseWorkshopSnapshot(content, DateTime.UtcNow).SubscribedIds.ToList();
        }

        internal static SteamWorkshopSnapshot ParseWorkshopSnapshot(
            string? content,
            DateTime observedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(content) ||
                !ValveKeyValueDocumentParser.TryParse(content, out var documentEntries))
            {
                return new SteamWorkshopSnapshot(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isAuthoritative: false,
                    observedAtUtc);
            }

            var appWorkshopEntries = documentEntries
                .Where(entry =>
                    string.Equals(
                        entry.Key,
                        "AppWorkshop",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (appWorkshopEntries.Count != 1 ||
                appWorkshopEntries[0].Children is not { } appWorkshopChildren ||
                !TryGetUniqueSection(
                    appWorkshopChildren,
                    "WorkshopItemDetails",
                    out var detailsEntries) ||
                !TryGetUniqueSection(
                    appWorkshopChildren,
                    "WorkshopItemsInstalled",
                    out var installedEntries) ||
                !TryParseSubscribedIdsFromDetails(
                    detailsEntries,
                    out var subscribedIds) ||
                !TryParseInstalledIds(
                    installedEntries,
                    out var installedIds))
            {
                return new SteamWorkshopSnapshot(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isAuthoritative: false,
                    observedAtUtc);
            }

            return new SteamWorkshopSnapshot(
                subscribedIds,
                installedIds,
                isAuthoritative: true,
                observedAtUtc);
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
                    if (!TryReadStableText(cacheFilePath, out var content))
                    {
                        continue;
                    }

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
                    info.TimeUpdated = DateTimeOffset
                        .FromUnixTimeSeconds(timestamp)
                        .UtcDateTime;
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

        private static bool TryGetUniqueSection(
            IReadOnlyList<ValveKeyValueEntry> entries,
            string sectionName,
            out IReadOnlyList<ValveKeyValueEntry> sectionEntries)
        {
            sectionEntries = Array.Empty<ValveKeyValueEntry>();
            var matches = entries
                .Where(entry =>
                    string.Equals(
                        entry.Key,
                        sectionName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1 ||
                matches[0].Children is not { } matchedChildren)
            {
                return false;
            }

            sectionEntries = matchedChildren;
            return true;
        }

        private static bool TryParseSubscribedIdsFromDetails(
            IReadOnlyList<ValveKeyValueEntry> entries,
            out IReadOnlyList<string> subscribedIds)
        {
            subscribedIds = Array.Empty<string>();
            var normalizedEntries = new List<(string Id, IReadOnlyList<ValveKeyValueEntry> Fields)>();
            foreach (var entry in entries)
            {
                if (!TryNormalizeWorkshopId(entry.Key, out var addonId) ||
                    entry.Children == null)
                {
                    return false;
                }

                normalizedEntries.Add((addonId, entry.Children));
            }

            var explicitlySubscribed = normalizedEntries
                .Where(entry => HasSubscribedByValue(entry.Fields))
                .Select(entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            // Older Steam manifests may not emit subscribedby. In that case the numeric
            // WorkshopItemDetails children remain the best available subscription source.
            subscribedIds = explicitlySubscribed.Length > 0
                ? explicitlySubscribed
                : normalizedEntries
                    .Select(entry => entry.Id)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
            return true;
        }

        private static bool TryParseInstalledIds(
            IReadOnlyList<ValveKeyValueEntry> entries,
            out IReadOnlyList<string> installedIds)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!TryNormalizeWorkshopId(entry.Key, out var addonId))
                {
                    installedIds = Array.Empty<string>();
                    return false;
                }

                ids.Add(addonId);
            }

            installedIds = ids
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            return true;
        }

        private static bool TryNormalizeWorkshopId(string value, out string normalized)
        {
            normalized = string.Empty;
            if (!ulong.TryParse(value, out var numeric))
            {
                return false;
            }

            normalized = numeric.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        private static bool HasSubscribedByValue(
            IReadOnlyList<ValveKeyValueEntry> fields)
        {
            var subscribedBy = fields
                .FirstOrDefault(field =>
                    string.Equals(
                        field.Key,
                        "subscribedby",
                        StringComparison.OrdinalIgnoreCase) &&
                    field.Value != null)
                ?.Value;
            return !string.IsNullOrWhiteSpace(subscribedBy) &&
                   !string.Equals(subscribedBy, "0", StringComparison.Ordinal);
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

        private static bool TryReadStableText(string path, out string content)
        {
            content = string.Empty;
            for (var attempt = 0; attempt < StableReadAttempts; attempt++)
            {
                try
                {
                    var before = WorkshopManifestFingerprint.Read(path);
                    if (!before.Exists)
                    {
                        return false;
                    }

                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(
                        stream,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true);
                    var candidate = reader.ReadToEnd();
                    var after = WorkshopManifestFingerprint.Read(path);
                    if (before.Equals(after))
                    {
                        content = candidate;
                        return true;
                    }
                }
                catch (IOException)
                {
                    // Steam may be replacing the manifest. Retry a bounded number of times.
                }
                catch (UnauthorizedAccessException)
                {
                    // Treat an unreadable manifest as non-authoritative.
                }

                Thread.Sleep(StableReadDelayMs);
            }

            return false;
        }

        private sealed class ValveKeyValueEntry
        {
            public ValveKeyValueEntry(string key, string value)
            {
                Key = key;
                Value = value;
            }

            public ValveKeyValueEntry(
                string key,
                IReadOnlyList<ValveKeyValueEntry> children)
            {
                Key = key;
                Children = children;
            }

            public string Key { get; }
            public string? Value { get; }
            public IReadOnlyList<ValveKeyValueEntry>? Children { get; }
        }

        private sealed class ValveKeyValueDocumentParser
        {
            private const int MaximumDepth = 64;
            private readonly string text;
            private int position;

            private ValveKeyValueDocumentParser(string text)
            {
                this.text = text;
            }

            public static bool TryParse(
                string text,
                out IReadOnlyList<ValveKeyValueEntry> entries)
            {
                entries = Array.Empty<ValveKeyValueEntry>();
                var parser = new ValveKeyValueDocumentParser(text);
                if (!parser.TryReadEntries(
                        stopAtClosingBrace: false,
                        depth: 0,
                        out var parsedEntries))
                {
                    return false;
                }

                entries = parsedEntries;
                return true;
            }

            private bool TryReadEntries(
                bool stopAtClosingBrace,
                int depth,
                out IReadOnlyList<ValveKeyValueEntry> entries)
            {
                entries = Array.Empty<ValveKeyValueEntry>();
                if (depth > MaximumDepth)
                {
                    return false;
                }

                var parsedEntries = new List<ValveKeyValueEntry>();
                while (true)
                {
                    if (!TrySkipTrivia())
                    {
                        return false;
                    }

                    if (IsAtEnd)
                    {
                        if (stopAtClosingBrace)
                        {
                            return false;
                        }

                        entries = parsedEntries;
                        return true;
                    }

                    if (text[position] == '}')
                    {
                        if (!stopAtClosingBrace)
                        {
                            return false;
                        }

                        position++;
                        entries = parsedEntries;
                        return true;
                    }

                    if (!TryReadQuotedToken(out var key) ||
                        !TrySkipTrivia())
                    {
                        return false;
                    }

                    if (TryConsume('{'))
                    {
                        if (!TryReadEntries(
                                stopAtClosingBrace: true,
                                depth: depth + 1,
                                out var children))
                        {
                            return false;
                        }

                        parsedEntries.Add(new ValveKeyValueEntry(key, children));
                        continue;
                    }

                    if (!TryReadQuotedToken(out var value))
                    {
                        return false;
                    }

                    parsedEntries.Add(new ValveKeyValueEntry(key, value));
                }
            }

            private bool IsAtEnd => position >= text.Length;

            private bool TryConsume(char expected)
            {
                if (IsAtEnd || text[position] != expected)
                {
                    return false;
                }

                position++;
                return true;
            }

            private bool TryReadQuotedToken(out string value)
            {
                value = string.Empty;
                if (!TryConsume('"'))
                {
                    return false;
                }

                var builder = new StringBuilder();
                while (!IsAtEnd)
                {
                    var current = text[position++];
                    if (current == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }

                    if (current == '\r' || current == '\n')
                    {
                        return false;
                    }

                    if (current == '\\' && !IsAtEnd)
                    {
                        var escaped = text[position++];
                        if (escaped == '"' || escaped == '\\')
                        {
                            builder.Append(escaped);
                        }
                        else
                        {
                            builder.Append('\\').Append(escaped);
                        }
                        continue;
                    }

                    builder.Append(current);
                }

                return false;
            }

            private bool TrySkipTrivia()
            {
                while (!IsAtEnd)
                {
                    if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF')
                    {
                        position++;
                        continue;
                    }

                    if (position + 1 >= text.Length || text[position] != '/')
                    {
                        return true;
                    }

                    if (text[position + 1] == '/')
                    {
                        position += 2;
                        while (!IsAtEnd && text[position] != '\r' && text[position] != '\n')
                        {
                            position++;
                        }
                        continue;
                    }

                    if (text[position + 1] != '*')
                    {
                        return true;
                    }

                    position += 2;
                    var commentClosed = false;
                    while (position + 1 < text.Length)
                    {
                        if (text[position] == '*' && text[position + 1] == '/')
                        {
                            position += 2;
                            commentClosed = true;
                            break;
                        }
                        position++;
                    }

                    if (!commentClosed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private readonly struct WorkshopManifestFingerprint :
            IEquatable<WorkshopManifestFingerprint>
        {
            private WorkshopManifestFingerprint(
                bool exists,
                DateTime? lastWriteUtc,
                long? fileSize)
            {
                Exists = exists;
                LastWriteUtc = lastWriteUtc;
                FileSize = fileSize;
            }

            public bool Exists { get; }
            private DateTime? LastWriteUtc { get; }
            private long? FileSize { get; }

            public static WorkshopManifestFingerprint Read(string path)
            {
                if (!File.Exists(path))
                {
                    return new WorkshopManifestFingerprint(false, null, null);
                }

                var info = new FileInfo(path);
                return new WorkshopManifestFingerprint(
                    true,
                    info.LastWriteTimeUtc,
                    info.Length);
            }

            public bool Equals(WorkshopManifestFingerprint other)
            {
                return Exists == other.Exists &&
                       LastWriteUtc == other.LastWriteUtc &&
                       FileSize == other.FileSize;
            }
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
