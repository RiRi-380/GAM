using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GmodAddonManager.Core.Services
{
    public sealed class PathOverrideResolution
    {
        public string? SteamLibraryPath { get; set; }
        public string GmodInstallPath { get; set; } = string.Empty;
        public string WorkshopRootPath { get; set; } = string.Empty;
        public PathSnapshot Snapshot { get; set; } = new PathSnapshot();
    }

    public static class PathOverrideResolver
    {
        private const string GmodAppId = "4000";
        private const string AppManifestFileName = "appmanifest_4000.acf";

        public static bool TryResolveSelectedFolder(
            string? selectedPath,
            out PathOverrideResolution resolution,
            out string error)
        {
            resolution = new PathOverrideResolution();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                error = "No folder was selected.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = NormalizePath(selectedPath);
            }
            catch (Exception ex)
            {
                error = $"The selected folder path is invalid: {ex.Message}";
                return false;
            }

            if (!IsDirectoryUsable(fullPath))
            {
                error = "The selected folder does not exist or cannot be read.";
                return false;
            }

            if (TryResolveGmodInstallFolder(fullPath, out resolution))
            {
                return true;
            }

            if (TryResolveSteamLibraryFolder(fullPath, out resolution))
            {
                return true;
            }

            if (TryResolveWorkshopRootFolder(fullPath, out resolution))
            {
                return true;
            }

            error = "Select the Garry's Mod install folder, a Steam library folder, or steamapps\\workshop\\content\\4000.";
            return false;
        }

        public static bool TryCreateSnapshot(
            string? gmodInstallPath,
            string? workshopRootPath,
            out PathSnapshot snapshot,
            out string error)
        {
            snapshot = new PathSnapshot();
            error = string.Empty;

            var normalizedGmod = NormalizeNullablePath(gmodInstallPath);
            var normalizedWorkshop = NormalizeNullablePath(workshopRootPath);
            if (string.IsNullOrWhiteSpace(normalizedGmod) && string.IsNullOrWhiteSpace(normalizedWorkshop))
            {
                error = "No custom GMod or Workshop path is configured.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedGmod) &&
                !string.IsNullOrWhiteSpace(normalizedWorkshop))
            {
                normalizedGmod = TryInferGmodInstallFromWorkshopRoot(normalizedWorkshop);
            }

            if (string.IsNullOrWhiteSpace(normalizedWorkshop) &&
                !string.IsNullOrWhiteSpace(normalizedGmod))
            {
                normalizedWorkshop = TryInferWorkshopRootFromGmodInstall(normalizedGmod);
            }

            if (string.IsNullOrWhiteSpace(normalizedGmod) ||
                !IsDirectoryUsable(Path.Combine(normalizedGmod, "garrysmod")))
            {
                error = "The configured Garry's Mod folder is missing or invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedWorkshop) || !IsDirectoryUsable(normalizedWorkshop))
            {
                error = "The configured Workshop content folder is missing, invalid, or unreadable.";
                return false;
            }

            snapshot = CreateSnapshot(normalizedGmod, normalizedWorkshop);
            return true;
        }

        private static bool TryResolveGmodInstallFolder(string folder, out PathOverrideResolution resolution)
        {
            resolution = new PathOverrideResolution();
            if (!IsDirectoryUsable(Path.Combine(folder, "garrysmod")))
            {
                return false;
            }

            var workshopRoot = TryInferWorkshopRootFromGmodInstall(folder);
            if (string.IsNullOrWhiteSpace(workshopRoot) || !IsDirectoryUsable(workshopRoot))
            {
                return false;
            }

            resolution = CreateResolution(folder, workshopRoot);
            return true;
        }

        private static bool TryResolveSteamLibraryFolder(string folder, out PathOverrideResolution resolution)
        {
            resolution = new PathOverrideResolution();
            var manifestPath = Path.Combine(folder, "steamapps", AppManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifest = ReadValveKeyValueFile(manifestPath);
            if (!manifest.TryGetValue("appid", out var appId) ||
                !string.Equals(appId, GmodAppId, StringComparison.Ordinal))
            {
                return false;
            }

            var installDir = manifest.TryGetValue("installdir", out var value) &&
                             !string.IsNullOrWhiteSpace(value)
                ? value
                : "GarrysMod";
            var gmodInstall = Path.Combine(folder, "steamapps", "common", installDir);
            var workshopRoot = Path.Combine(folder, "steamapps", "workshop", "content", GmodAppId);
            if (!IsDirectoryUsable(Path.Combine(gmodInstall, "garrysmod")) ||
                !IsDirectoryUsable(workshopRoot))
            {
                return false;
            }

            resolution = CreateResolution(gmodInstall, workshopRoot, folder);
            return true;
        }

        private static bool TryResolveWorkshopRootFolder(string folder, out PathOverrideResolution resolution)
        {
            resolution = new PathOverrideResolution();
            if (!string.Equals(Path.GetFileName(folder), GmodAppId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var gmodInstall = TryInferGmodInstallFromWorkshopRoot(folder);
            if (string.IsNullOrWhiteSpace(gmodInstall) ||
                !IsDirectoryUsable(Path.Combine(gmodInstall, "garrysmod")) ||
                !IsDirectoryUsable(folder))
            {
                return false;
            }

            resolution = CreateResolution(gmodInstall, folder);
            return true;
        }

        private static PathOverrideResolution CreateResolution(
            string gmodInstallPath,
            string workshopRootPath,
            string? steamLibraryPath = null)
        {
            var normalizedGmod = NormalizePath(gmodInstallPath);
            var normalizedWorkshop = NormalizePath(workshopRootPath);
            var library = string.IsNullOrWhiteSpace(steamLibraryPath)
                ? TryInferSteamLibraryFromGmodInstall(normalizedGmod) ?? TryInferSteamLibraryFromWorkshopRoot(normalizedWorkshop)
                : NormalizePath(steamLibraryPath);

            return new PathOverrideResolution
            {
                SteamLibraryPath = library,
                GmodInstallPath = normalizedGmod,
                WorkshopRootPath = normalizedWorkshop,
                Snapshot = CreateSnapshot(normalizedGmod, normalizedWorkshop, library)
            };
        }

        private static PathSnapshot CreateSnapshot(
            string gmodInstallPath,
            string workshopRootPath,
            string? steamLibraryPath = null)
        {
            var library = string.IsNullOrWhiteSpace(steamLibraryPath)
                ? TryInferSteamLibraryFromGmodInstall(gmodInstallPath) ?? TryInferSteamLibraryFromWorkshopRoot(workshopRootPath)
                : NormalizePath(steamLibraryPath);

            var gmodCandidate = new GmodInstallCandidate
            {
                LibraryPath = library ?? string.Empty,
                AppManifestPath = string.IsNullOrWhiteSpace(library)
                    ? string.Empty
                    : Path.Combine(library, "steamapps", AppManifestFileName),
                InstallDir = Path.GetFileName(gmodInstallPath),
                InstallPath = gmodInstallPath,
                AppIdMatched = true,
                DirectoryExists = IsDirectoryUsable(gmodInstallPath),
                GarrysmodDirectoryExists = IsDirectoryUsable(Path.Combine(gmodInstallPath, "garrysmod")),
                Confidence = IsDirectoryUsable(Path.Combine(gmodInstallPath, "garrysmod"))
                    ? PathCandidateConfidence.High
                    : PathCandidateConfidence.Rejected
            };

            var workshopCandidate = BuildWorkshopCandidate(workshopRootPath, library);
            var libraries = string.IsNullOrWhiteSpace(library)
                ? Array.Empty<SteamLibraryCandidate>()
                : new[]
                {
                    new SteamLibraryCandidate
                    {
                        Path = library,
                        Source = "UserSelected",
                        HasSteamApps = Directory.Exists(Path.Combine(library, "steamapps")),
                        HasGmodAppManifest = File.Exists(Path.Combine(library, "steamapps", AppManifestFileName)),
                        HasWorkshopManifest = File.Exists(Path.Combine(library, "steamapps", "workshop", "appworkshop_4000.acf")),
                        HasWorkshopContentRoot = IsDirectoryUsable(workshopRootPath)
                    }
                };

            return new PathSnapshot
            {
                DetectedAtUtc = DateTime.UtcNow,
                SteamRootPath = library,
                SteamLibraries = libraries,
                GmodInstall = gmodCandidate,
                WorkshopRoots = new[] { workshopCandidate },
                ActiveWorkshopRoot = workshopCandidate,
                GmodCacheWorkshopPath = Path.Combine(gmodInstallPath, "garrysmod", "cache", "workshop"),
                AddonNoMountPath = Path.Combine(gmodInstallPath, "garrysmod", "cfg", "addonnomount.txt"),
                HealthIssues = Array.Empty<string>()
            };
        }

        private static WorkshopRootCandidate BuildWorkshopCandidate(string workshopRootPath, string? library)
        {
            var expectedWorkshopRoot = string.IsNullOrWhiteSpace(library)
                ? null
                : Path.Combine(library, "steamapps", "workshop", "content", GmodAppId);
            var rootMatchesLibrary = !string.IsNullOrWhiteSpace(expectedWorkshopRoot) &&
                                     string.Equals(
                                         NormalizePath(workshopRootPath),
                                         NormalizePath(expectedWorkshopRoot),
                                         StringComparison.OrdinalIgnoreCase);
            var manifestPath = rootMatchesLibrary
                ? Path.Combine(library!, "steamapps", "workshop", "appworkshop_4000.acf")
                : string.Empty;
            var hasManifest = !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath);
            var rootIsUsable = WorkshopRootInspector.TryCheckDirectoryReadable(workshopRootPath, out _);
            var manifestIsAuthoritative = false;
            var manifestMatchedFolderCount = 0;
            if (rootIsUsable && hasManifest)
            {
                manifestMatchedFolderCount = WorkshopRootInspector.CountManifestMatchedFolders(
                    workshopRootPath,
                    manifestPath,
                    out manifestIsAuthoritative);
            }

            var hasValidPayload = false;
            if (rootIsUsable && !manifestIsAuthoritative)
            {
                hasValidPayload = WorkshopRootInspector.HasValidPayloadFallback(workshopRootPath);
            }

            var confidence = PathCandidateConfidence.Rejected;
            if (rootIsUsable && manifestIsAuthoritative)
            {
                confidence = manifestMatchedFolderCount > 0
                    ? PathCandidateConfidence.High
                    : PathCandidateConfidence.Low;
            }
            else if (rootIsUsable)
            {
                confidence = hasValidPayload
                    ? PathCandidateConfidence.Medium
                    : PathCandidateConfidence.Low;
            }

            return new WorkshopRootCandidate
            {
                LibraryPath = library ?? string.Empty,
                RootPath = workshopRootPath,
                AppWorkshopManifestPath = manifestPath,
                HasAppWorkshopManifest = hasManifest,
                ContentRootExists = rootIsUsable,
                // Path discovery deliberately does not perform exhaustive payload validation.
                ValidPayloadCount = 0,
                EmptyOrInvalidFolderCount = 0,
                Confidence = confidence,
                RejectReasons = rootIsUsable
                    ? Array.Empty<string>()
                    : new[] { "Workshop content root is missing or unreadable." }
            };
        }

        public static bool IsDirectoryUsable(string? path)
        {
            return WorkshopRootInspector.TryCheckDirectoryReadable(path, out _);
        }

        private static string? TryInferWorkshopRootFromGmodInstall(string gmodInstallPath)
        {
            var library = TryInferSteamLibraryFromGmodInstall(gmodInstallPath);
            return string.IsNullOrWhiteSpace(library)
                ? null
                : Path.Combine(library, "steamapps", "workshop", "content", GmodAppId);
        }

        private static string? TryInferGmodInstallFromWorkshopRoot(string workshopRootPath)
        {
            var library = TryInferSteamLibraryFromWorkshopRoot(workshopRootPath);
            if (string.IsNullOrWhiteSpace(library))
            {
                return null;
            }

            var manifestPath = Path.Combine(library, "steamapps", AppManifestFileName);
            var installDir = "GarrysMod";
            if (File.Exists(manifestPath))
            {
                var values = ReadValveKeyValueFile(manifestPath);
                if (values.TryGetValue("installdir", out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    installDir = value;
                }
            }

            return Path.Combine(library, "steamapps", "common", installDir);
        }

        private static string? TryInferSteamLibraryFromGmodInstall(string gmodInstallPath)
        {
            var install = new DirectoryInfo(gmodInstallPath);
            var common = install.Parent;
            var steamApps = common?.Parent;
            var library = steamApps?.Parent;
            if (common == null ||
                steamApps == null ||
                library == null ||
                !string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return library.FullName;
        }

        private static string? TryInferSteamLibraryFromWorkshopRoot(string workshopRootPath)
        {
            var appId = new DirectoryInfo(workshopRootPath);
            var content = appId.Parent;
            var workshop = content?.Parent;
            var steamApps = workshop?.Parent;
            var library = steamApps?.Parent;
            if (content == null ||
                workshop == null ||
                steamApps == null ||
                library == null ||
                !string.Equals(appId.Name, GmodAppId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(content.Name, "content", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(workshop.Name, "workshop", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return library.FullName;
        }

        private static string? NormalizeNullablePath(string? path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static Dictionary<string, string> ReadValveKeyValueFile(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                return result;
            }

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
                    result[parts[0]] = parts[1].Replace(@"\\", @"\");
                }
            }

            return result;
        }
    }
}
