using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GmodAddonManager.Core.Services
{
    public enum AddonPayloadKind
    {
        None,
        GmaArchive,
        CacheArchive,
        FolderAddon
    }

    public sealed class AddonPayloadValidationResult
    {
        public bool IsValid { get; private set; }
        public AddonPayloadKind Kind { get; private set; }
        public string? EvidencePath { get; private set; }
        public IReadOnlyList<string> Reasons { get; private set; } = Array.Empty<string>();

        public static AddonPayloadValidationResult Valid(AddonPayloadKind kind, string evidencePath)
        {
            return new AddonPayloadValidationResult
            {
                IsValid = true,
                Kind = kind,
                EvidencePath = evidencePath,
                Reasons = Array.Empty<string>()
            };
        }

        public static AddonPayloadValidationResult Invalid(params string[] reasons)
        {
            return new AddonPayloadValidationResult
            {
                IsValid = false,
                Kind = AddonPayloadKind.None,
                Reasons = reasons.Length == 0 ? new[] { "No valid addon payload found." } : reasons
            };
        }
    }

    public static class AddonPayloadValidator
    {
        private static readonly HashSet<string> IgnoredMarkerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".gam_disabled",
            ".gam_stub",
            ".gam_owner.json",
            "desktop.ini",
            "thumbs.db"
        };

        private static readonly HashSet<string> ContentDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lua",
            "materials",
            "models",
            "maps",
            "gamemodes",
            "sound",
            "scripts",
            "particles",
            "resource",
            "cfg",
            "data"
        };

        public static bool HasValidAddonPayload(string directoryPath)
        {
            return Validate(directoryPath).IsValid;
        }

        public static AddonPayloadValidationResult Validate(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return AddonPayloadValidationResult.Invalid("Path is empty.");
            }

            if (!Directory.Exists(directoryPath))
            {
                return AddonPayloadValidationResult.Invalid("Directory does not exist.");
            }

            try
            {
                var hasInvalidGma = false;
                foreach (var archive in Directory.EnumerateFiles(directoryPath, "*.gma", SearchOption.TopDirectoryOnly))
                {
                    if (HasGmaHeader(archive))
                    {
                        return AddonPayloadValidationResult.Valid(AddonPayloadKind.GmaArchive, archive);
                    }
                    hasInvalidGma = true;
                }

                foreach (var cache in Directory.EnumerateFiles(directoryPath, "*.cache", SearchOption.TopDirectoryOnly))
                {
                    if (IsNonZeroFile(cache))
                    {
                        return AddonPayloadValidationResult.Valid(AddonPayloadKind.CacheArchive, cache);
                    }
                }

                var addonJsonPath = Path.Combine(directoryPath, "addon.json");
                var hasAddonJson = IsNonZeroFile(addonJsonPath);
                foreach (var contentDir in ContentDirectoryNames)
                {
                    var candidate = Path.Combine(directoryPath, contentDir);
                    if (!Directory.Exists(candidate))
                    {
                        continue;
                    }

                    var payloadFile = FindFirstPayloadFile(candidate);
                    if (payloadFile != null && (hasAddonJson || ContentDirectoryNames.Contains(Path.GetFileName(candidate))))
                    {
                        return AddonPayloadValidationResult.Valid(AddonPayloadKind.FolderAddon, payloadFile);
                    }
                }

                return AddonPayloadValidationResult.Invalid(
                    hasInvalidGma
                        ? "A .gma archive is unreadable or has an invalid header. No other addon payload was found."
                        : "Directory has no .gma/.cache archive and no recognized addon content directory with files.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return AddonPayloadValidationResult.Invalid($"Failed to inspect payload: {ex.Message}");
            }
        }

        public static bool IsIgnoredMarker(string filePath)
        {
            return IgnoredMarkerNames.Contains(Path.GetFileName(filePath));
        }

        private static string? FindFirstPayloadFile(string directoryPath)
        {
            try
            {
                return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(file => !IsIgnoredMarker(file) && IsNonZeroFile(file));
            }
            catch
            {
                return null;
            }
        }

        // Fixed prefix, three empty metadata strings, addon version, and file-table terminator.
        private const int MinimumGmaHeaderLength = 32;

        internal static bool HasGmaHeader(string filePath)
        {
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < MinimumGmaHeaderLength)
                {
                    return false;
                }

                using var reader = new BinaryReader(stream);
                return reader.ReadByte() == 'G' &&
                       reader.ReadByte() == 'M' &&
                       reader.ReadByte() == 'A' &&
                       reader.ReadByte() == 'D';
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsNonZeroFile(string filePath)
        {
            try
            {
                return File.Exists(filePath) && new FileInfo(filePath).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
