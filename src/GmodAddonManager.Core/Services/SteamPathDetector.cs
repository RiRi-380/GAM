using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GmodAddonManager.Core.Services
{
    public class SteamPathDetector : ISteamPathDetector
    {
        private const string GMOD_APP_ID = "4000";
        private const string WORKSHOP_RELATIVE_PATH = @"steamapps\workshop\content\" + GMOD_APP_ID;
        private const string GMOD_INSTALL_MANIFEST = "appmanifest_4000.acf";
        private const string GMOD_WORKSHOP_MANIFEST = "appworkshop_4000.acf";
        private static readonly TimeSpan LibraryPathCacheTtl = TimeSpan.FromSeconds(30);
        private readonly object _libraryPathLock = new object();
        private readonly string? forcedSteamPath;
        private DateTime _libraryPathsCachedAtUtc = DateTime.MinValue;
        private string? _libraryPathsCachedSteamPath;
        private List<string>? _libraryPathsCache;

        private readonly List<string> commonSteamPaths = new List<string>
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\SteamLibrary",
            @"E:\Steam",
            @"E:\SteamLibrary"
        };

        public SteamPathDetector()
        {
        }

        public SteamPathDetector(string? forcedSteamPath)
        {
            this.forcedSteamPath = string.IsNullOrWhiteSpace(forcedSteamPath)
                ? null
                : forcedSteamPath;
        }

        public string DetectWorkshopPath()
        {
            var snapshot = DetectPathSnapshot();
            if (snapshot.ActiveWorkshopRoot != null &&
                !string.IsNullOrWhiteSpace(snapshot.ActiveWorkshopRoot.RootPath))
            {
                return snapshot.ActiveWorkshopRoot.RootPath;
            }

            throw new DirectoryNotFoundException(
                "Could not find Garry's Mod workshop folder. Please ensure Steam and Garry's Mod are installed.");
        }

        public PathSnapshot DetectPathSnapshot()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("This application currently only supports Windows.");
            }

            var issues = new List<string>();
            var steamPath = DetectSteamPath();
            var libraryPaths = GetSteamLibraryPaths(steamPath);
            var libraries = libraryPaths
                .Select((path, index) => BuildLibraryCandidate(path, index == 0 ? "SteamRootOrDefault" : "LibraryFoldersVdf"))
                .ToList();

            var gmodInstall = libraries
                .Select(BuildGmodInstallCandidate)
                .Where(candidate => candidate.Confidence != PathCandidateConfidence.Rejected)
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenByDescending(candidate => candidate.DirectoryExists)
                .ThenBy(candidate => candidate.LibraryPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (gmodInstall == null)
            {
                issues.Add("Garry's Mod appmanifest_4000.acf was not found in any Steam library.");
            }

            var workshopRoots = libraries.Select(BuildWorkshopRootCandidate).ToList();
            var activeWorkshopRoot = workshopRoots
                .Where(candidate => candidate.Confidence != PathCandidateConfidence.Rejected)
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenByDescending(candidate => candidate.ValidPayloadCount)
                .ThenByDescending(candidate => candidate.HasAppWorkshopManifest)
                .ThenBy(candidate => candidate.LibraryPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (activeWorkshopRoot == null)
            {
                issues.Add("Garry's Mod workshop content root was not found in any Steam library.");
            }

            var gmodCachePath = gmodInstall != null && gmodInstall.DirectoryExists
                ? Path.Combine(gmodInstall.InstallPath, @"garrysmod\cache\workshop")
                : null;
            var addonnomountPath = gmodInstall != null && gmodInstall.DirectoryExists
                ? Path.Combine(gmodInstall.InstallPath, @"garrysmod\cfg\addonnomount.txt")
                : null;

            return new PathSnapshot
            {
                DetectedAtUtc = DateTime.UtcNow,
                SteamRootPath = steamPath,
                SteamLibraries = libraries,
                GmodInstall = gmodInstall,
                WorkshopRoots = workshopRoots,
                ActiveWorkshopRoot = activeWorkshopRoot,
                GmodCacheWorkshopPath = gmodCachePath,
                AddonNoMountPath = addonnomountPath,
                HealthIssues = issues
            };
        }

        public string? DetectSteamPath()
        {
            if (!string.IsNullOrWhiteSpace(forcedSteamPath) && Directory.Exists(forcedSteamPath))
            {
                return forcedSteamPath;
            }

            string? registryPath = TryGetSteamPathFromRegistry();
            if (!string.IsNullOrEmpty(registryPath) && Directory.Exists(registryPath))
            {
                return registryPath;
            }

            foreach (var path in commonSteamPaths)
            {
                if (Directory.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private string? TryGetSteamPathFromRegistry()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    if (key != null)
                    {
                        object? steamPath = key.GetValue("SteamPath");
                        if (steamPath != null)
                        {
                            var rawPath = steamPath.ToString();
                            if (!string.IsNullOrWhiteSpace(rawPath))
                            {
                                return rawPath.Replace('/', '\\');
                            }

                            return null;
                        }
                    }
                }

                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (key != null)
                    {
                        object? installPath = key.GetValue("InstallPath");
                        if (installPath != null)
                        {
                            return installPath.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SteamPathDetector] Registry lookup failed: {ex.Message}");
            }

            return null;
        }

        public List<string> GetSteamLibraryPaths(string? steamPath)
        {
            if (string.IsNullOrEmpty(steamPath))
            {
                return new List<string>();
            }

            var now = DateTime.UtcNow;
            lock (_libraryPathLock)
            {
                if (_libraryPathsCache != null &&
                    string.Equals(_libraryPathsCachedSteamPath, steamPath, StringComparison.OrdinalIgnoreCase) &&
                    now - _libraryPathsCachedAtUtc <= LibraryPathCacheTtl)
                {
                    return new List<string>(_libraryPathsCache);
                }
            }

            var libraryPaths = new List<string>();
            AddLibraryPath(libraryPaths, steamPath);

            try
            {
                string libraryFoldersPath = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                if (File.Exists(libraryFoldersPath))
                {
                    foreach (var value in ReadValveKeyValuePairs(libraryFoldersPath)
                                 .Where(kvp => string.Equals(kvp.Key, "path", StringComparison.OrdinalIgnoreCase))
                                 .Select(kvp => kvp.Value))
                    {
                        AddLibraryPath(libraryPaths, value);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SteamPathDetector] Failed to parse libraryfolders.vdf: {ex.Message}");
            }

            lock (_libraryPathLock)
            {
                _libraryPathsCache = new List<string>(libraryPaths);
                _libraryPathsCachedSteamPath = steamPath;
                _libraryPathsCachedAtUtc = DateTime.UtcNow;
            }

            return libraryPaths;
        }

        public bool IsGmodInstalled(string? workshopPath)
        {
            var snapshot = DetectPathSnapshot();
            var gmodPath = snapshot.GmodInstall?.InstallPath;
            return !string.IsNullOrWhiteSpace(gmodPath) && Directory.Exists(gmodPath);
        }

        public string? DetectGmodCachePath()
        {
            try
            {
                var snapshot = DetectPathSnapshot();
                if (string.IsNullOrWhiteSpace(snapshot.GmodCacheWorkshopPath))
                {
                    return null;
                }

                string cachePath = snapshot.GmodCacheWorkshopPath;

                if (!Directory.Exists(cachePath))
                {
                    Directory.CreateDirectory(cachePath);
                }

                return cachePath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddLibraryPath(List<string> libraryPaths, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = path.Replace('/', '\\').TrimEnd('\\');
            if (Directory.Exists(normalized) &&
                !libraryPaths.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                libraryPaths.Add(normalized);
            }
        }

        private static SteamLibraryCandidate BuildLibraryCandidate(string libraryPath, string source)
        {
            var steamApps = Path.Combine(libraryPath, "steamapps");
            var workshopManifest = Path.Combine(steamApps, "workshop", GMOD_WORKSHOP_MANIFEST);
            var workshopContent = Path.Combine(libraryPath, WORKSHOP_RELATIVE_PATH);
            return new SteamLibraryCandidate
            {
                Path = libraryPath,
                Source = source,
                HasSteamApps = Directory.Exists(steamApps),
                HasGmodAppManifest = File.Exists(Path.Combine(steamApps, GMOD_INSTALL_MANIFEST)),
                HasWorkshopManifest = File.Exists(workshopManifest),
                HasWorkshopContentRoot = Directory.Exists(workshopContent)
            };
        }

        private static GmodInstallCandidate BuildGmodInstallCandidate(SteamLibraryCandidate library)
        {
            var reasons = new List<string>();
            var manifestPath = Path.Combine(library.Path, "steamapps", GMOD_INSTALL_MANIFEST);
            if (!File.Exists(manifestPath))
            {
                return new GmodInstallCandidate
                {
                    LibraryPath = library.Path,
                    AppManifestPath = manifestPath,
                    Confidence = PathCandidateConfidence.Rejected,
                    RejectReasons = new[] { "appmanifest_4000.acf is missing." }
                };
            }

            var manifest = ReadValveKeyValueFile(manifestPath);
            var appIdMatched = manifest.TryGetValue("appid", out var appId) &&
                               string.Equals(appId, GMOD_APP_ID, StringComparison.Ordinal);
            if (!appIdMatched)
            {
                reasons.Add("appmanifest appid is not 4000.");
            }

            manifest.TryGetValue("installdir", out var installDir);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = "GarrysMod";
                reasons.Add("installdir is missing; using GarrysMod fallback.");
            }

            var installPath = Path.Combine(library.Path, "steamapps", "common", installDir);
            var directoryExists = Directory.Exists(installPath);
            var garrysmodDirectoryExists = Directory.Exists(Path.Combine(installPath, "garrysmod"));

            var confidence = PathCandidateConfidence.Rejected;
            if (appIdMatched && directoryExists && garrysmodDirectoryExists)
            {
                confidence = PathCandidateConfidence.High;
            }
            else if (appIdMatched && directoryExists)
            {
                confidence = PathCandidateConfidence.Medium;
                reasons.Add("garrysmod subdirectory is missing.");
            }
            else if (appIdMatched)
            {
                confidence = PathCandidateConfidence.Low;
                reasons.Add("install directory is missing.");
            }

            return new GmodInstallCandidate
            {
                LibraryPath = library.Path,
                AppManifestPath = manifestPath,
                InstallDir = installDir,
                InstallPath = installPath,
                AppIdMatched = appIdMatched,
                DirectoryExists = directoryExists,
                GarrysmodDirectoryExists = garrysmodDirectoryExists,
                Confidence = confidence,
                RejectReasons = reasons
            };
        }

        private static WorkshopRootCandidate BuildWorkshopRootCandidate(SteamLibraryCandidate library)
        {
            var root = Path.Combine(library.Path, WORKSHOP_RELATIVE_PATH);
            var manifest = Path.Combine(library.Path, "steamapps", "workshop", GMOD_WORKSHOP_MANIFEST);
            var reasons = new List<string>();
            var contentRootExists = Directory.Exists(root);
            var contentRootReadable = false;
            var validPayloadCount = 0;
            var invalidFolderCount = 0;

            if (contentRootExists)
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var id = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(id) ||
                            id.StartsWith(".", StringComparison.Ordinal) ||
                            !long.TryParse(id, out _))
                        {
                            continue;
                        }

                        if (AddonPayloadValidator.HasValidAddonPayload(dir))
                        {
                            validPayloadCount++;
                        }
                        else
                        {
                            invalidFolderCount++;
                        }
                    }

                    contentRootReadable = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    reasons.Add($"Failed to inspect workshop content root: {ex.Message}");
                }
            }
            else
            {
                reasons.Add("workshop content root is missing.");
            }

            var hasManifest = File.Exists(manifest);
            var confidence = PathCandidateConfidence.Rejected;
            if (contentRootReadable && hasManifest && validPayloadCount > 0)
            {
                confidence = PathCandidateConfidence.High;
            }
            else if (contentRootReadable && validPayloadCount > 0)
            {
                confidence = PathCandidateConfidence.Medium;
                reasons.Add("appworkshop_4000.acf is missing.");
            }
            else if (contentRootReadable && hasManifest)
            {
                confidence = PathCandidateConfidence.Low;
                reasons.Add("workshop root exists but has no valid payload.");
            }
            else if (contentRootReadable)
            {
                confidence = PathCandidateConfidence.Low;
                reasons.Add("workshop root exists without appworkshop manifest or valid payload.");
            }

            return new WorkshopRootCandidate
            {
                LibraryPath = library.Path,
                RootPath = root,
                AppWorkshopManifestPath = manifest,
                HasAppWorkshopManifest = hasManifest,
                ContentRootExists = contentRootReadable,
                ValidPayloadCount = validPayloadCount,
                EmptyOrInvalidFolderCount = invalidFolderCount,
                Confidence = confidence,
                RejectReasons = reasons
            };
        }

        private static Dictionary<string, string> ReadValveKeyValueFile(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ReadValveKeyValuePairs(path))
            {
                values[pair.Key] = pair.Value;
            }

            return values;
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadValveKeyValuePairs(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = trimmed.Split('"')
                    .Where((part, index) => index % 2 == 1)
                    .ToArray();
                if (parts.Length >= 2)
                {
                    yield return new KeyValuePair<string, string>(parts[0], parts[1].Replace(@"\\", @"\"));
                }
            }
        }
    }
}
