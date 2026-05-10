using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public sealed class StartupPathRecoveryDecision
    {
        public bool ShouldPrompt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? PreviousGmodInstallPath { get; set; }
        public string? PreviousWorkshopRootPath { get; set; }
        public string? DetectedGmodInstallPath { get; set; }
        public string? DetectedWorkshopRootPath { get; set; }
        public bool HasDetectedCandidate =>
            !string.IsNullOrWhiteSpace(DetectedGmodInstallPath) &&
            !string.IsNullOrWhiteSpace(DetectedWorkshopRootPath);
    }

    public static class StartupPathRecoveryEvaluator
    {
        public static StartupPathRecoveryDecision Evaluate(
            Configuration? configuration,
            PathSnapshot currentSnapshot,
            string? configuredGmodInstallPath = null,
            string? configuredWorkshopRootPath = null,
            bool promptForUnconfirmedExistingConfig = false,
            string? confirmedGmodInstallPath = null,
            string? confirmedWorkshopRootPath = null)
        {
            var previous = configuration?.PathState?.LastDetectedSnapshot ??
                           configuration?.PathState?.LastKnownGoodSnapshot;
            var inferredWorkshop = InferWorkshopRootFromAddonMetadata(configuration);
            var previousGmod = previous?.GmodInstall?.InstallPath;
            var previousWorkshop = previous?.ActiveWorkshopRoot?.RootPath ?? inferredWorkshop;
            var detectedGmod = currentSnapshot.GmodInstall?.Confidence == PathCandidateConfidence.Rejected
                ? null
                : currentSnapshot.GmodInstall?.InstallPath;
            var detectedWorkshop = currentSnapshot.ActiveWorkshopRoot?.Confidence == PathCandidateConfidence.Rejected
                ? null
                : currentSnapshot.ActiveWorkshopRoot?.RootPath;

            var configuredPathsInvalid =
                !string.IsNullOrWhiteSpace(configuredGmodInstallPath) &&
                !Directory.Exists(Path.Combine(configuredGmodInstallPath, "garrysmod"));
            configuredPathsInvalid |=
                !string.IsNullOrWhiteSpace(configuredWorkshopRootPath) &&
                !Directory.Exists(configuredWorkshopRootPath);

            var previousMissing =
                IsMissing(previousGmod) ||
                IsMissing(previousWorkshop);
            var hasPreviousPath =
                !string.IsNullOrWhiteSpace(previousGmod) ||
                !string.IsNullOrWhiteSpace(previousWorkshop);
            var detectedDifferent =
                (!string.IsNullOrWhiteSpace(previousGmod) && !PathsEqual(previousGmod, detectedGmod)) ||
                (!string.IsNullOrWhiteSpace(previousWorkshop) && !PathsEqual(previousWorkshop, detectedWorkshop));
            var metadataWorkshopDifferent =
                !string.IsNullOrWhiteSpace(inferredWorkshop) &&
                !PathsEqual(inferredWorkshop, detectedWorkshop);
            var noDetectedCandidate =
                string.IsNullOrWhiteSpace(detectedGmod) ||
                string.IsNullOrWhiteSpace(detectedWorkshop);
            var shouldConfirmExistingConfig =
                promptForUnconfirmedExistingConfig &&
                HasExistingInventory(configuration) &&
                !noDetectedCandidate &&
                (!PathsEqual(confirmedGmodInstallPath, detectedGmod) ||
                 !PathsEqual(confirmedWorkshopRootPath, detectedWorkshop));

            var shouldPrompt = configuredPathsInvalid ||
                               noDetectedCandidate ||
                               (hasPreviousPath && detectedDifferent) ||
                               metadataWorkshopDifferent ||
                               shouldConfirmExistingConfig;

            return new StartupPathRecoveryDecision
            {
                ShouldPrompt = shouldPrompt,
                Reason = BuildReason(
                    configuredPathsInvalid,
                    previousMissing,
                    noDetectedCandidate,
                    shouldConfirmExistingConfig),
                PreviousGmodInstallPath = previousGmod,
                PreviousWorkshopRootPath = previousWorkshop,
                DetectedGmodInstallPath = detectedGmod,
                DetectedWorkshopRootPath = detectedWorkshop
            };
        }

        private static string BuildReason(
            bool configuredPathsInvalid,
            bool previousMissing,
            bool noDetectedCandidate,
            bool shouldConfirmExistingConfig)
        {
            if (configuredPathsInvalid)
            {
                return "Configured path override is no longer valid.";
            }

            if (noDetectedCandidate)
            {
                return "Garry's Mod install or Workshop content path could not be detected.";
            }

            if (previousMissing)
            {
                return "Previous Garry's Mod or Workshop path is missing.";
            }

            if (shouldConfirmExistingConfig)
            {
                return "Confirm the Garry's Mod and Workshop paths GAM should use.";
            }

            return "Path recovery is recommended.";
        }

        private static bool IsMissing(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) && !Directory.Exists(path);
        }

        private static string? InferWorkshopRootFromAddonMetadata(Configuration? configuration)
        {
            if (configuration?.AddonMetadata == null || configuration.AddonMetadata.Count == 0)
            {
                return null;
            }

            var roots = new List<string>();
            foreach (var kvp in configuration.AddonMetadata)
            {
                if (kvp.Value == null || kvp.Value.IsLocal || !long.TryParse(kvp.Key, out _))
                {
                    continue;
                }

                var root = TryExtractWorkshopRoot(kvp.Value.FolderPath, kvp.Key);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    roots.Add(root);
                }
            }

            return roots
                .GroupBy(path => NormalizeForCompare(path), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
        }

        private static bool HasExistingInventory(Configuration? configuration)
        {
            if (configuration == null)
            {
                return false;
            }

            if (configuration.AddonMetadata?.Count > 0)
            {
                return true;
            }

            return configuration.Assets?.Any(asset =>
                asset.Addons.Any(addonId => !string.Equals(addonId, "*", StringComparison.Ordinal)) ||
                asset.AddonStates.Count > 0) == true;
        }

        private static string? TryExtractWorkshopRoot(string? metadataPath, string addonId)
        {
            if (string.IsNullOrWhiteSpace(metadataPath))
            {
                return null;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(metadataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }

            var leaf = Path.GetFileName(normalized);
            if (!string.Equals(leaf, addonId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileNameWithoutExtension(leaf), addonId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var root = Directory.GetParent(normalized)?.FullName;
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            if (!LooksLikeGmodWorkshopRoot(root))
            {
                return null;
            }

            return NormalizeForCompare(root);
        }

        private static bool LooksLikeGmodWorkshopRoot(string root)
        {
            try
            {
                var appId = new DirectoryInfo(root);
                var content = appId.Parent;
                var workshop = content?.Parent;
                var steamApps = workshop?.Parent;
                return content != null &&
                       workshop != null &&
                       steamApps != null &&
                       string.Equals(appId.Name, "4000", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(content.Name, "content", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(workshop.Name, "workshop", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeForCompare(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
            }

            try
            {
                var normalizedLeft = NormalizeForCompare(left);
                var normalizedRight = NormalizeForCompare(right);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
