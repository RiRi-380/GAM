using System;
using System.IO;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public enum StartupPathRecoveryReason
    {
        None = 0,
        ConfiguredPathInvalid,
        GmodPathUnavailable,
        WorkshopPathUnavailable,
        RequiredPathsUnavailable,
        RecordedPathMissing,
        RecordedPathChanged,
        ManualRequest
    }

    public sealed class StartupPathRecoveryDecision
    {
        public bool ShouldPrompt { get; set; }
        public StartupPathRecoveryReason Reason { get; set; }
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
            string? confirmedGmodInstallPath = null,
            string? confirmedWorkshopRootPath = null)
        {
            var previous = configuration?.PathState?.LastDetectedSnapshot ??
                           configuration?.PathState?.LastKnownGoodSnapshot;
            var pathStateGmod = previous?.GmodInstall?.InstallPath;
            var pathStateWorkshop = previous?.ActiveWorkshopRoot?.RootPath;
            var previousGmod = FirstRecordedPath(
                pathStateGmod,
                confirmedGmodInstallPath,
                configuredGmodInstallPath);
            var previousWorkshop = FirstRecordedPath(
                pathStateWorkshop,
                confirmedWorkshopRootPath,
                configuredWorkshopRootPath);
            var detectedGmodCandidate = currentSnapshot.GmodInstall;
            var detectedGmod = detectedGmodCandidate != null &&
                               detectedGmodCandidate.Confidence != PathCandidateConfidence.Rejected &&
                               PathOverrideResolver.IsDirectoryUsable(
                                   Path.Combine(detectedGmodCandidate.InstallPath, "garrysmod"))
                ? detectedGmodCandidate.InstallPath
                : null;
            var detectedWorkshopCandidate = currentSnapshot.ActiveWorkshopRoot;
            var detectedWorkshop = detectedWorkshopCandidate != null &&
                                   detectedWorkshopCandidate.Confidence != PathCandidateConfidence.Rejected &&
                                   PathOverrideResolver.IsDirectoryUsable(detectedWorkshopCandidate.RootPath)
                ? detectedWorkshopCandidate.RootPath
                : null;

            var configuredPathsInvalid =
                !string.IsNullOrWhiteSpace(configuredGmodInstallPath) &&
                !PathOverrideResolver.IsDirectoryUsable(
                    Path.Combine(configuredGmodInstallPath, "garrysmod"));
            configuredPathsInvalid |=
                !string.IsNullOrWhiteSpace(configuredWorkshopRootPath) &&
                !PathOverrideResolver.IsDirectoryUsable(configuredWorkshopRootPath);

            var previousMissing =
                AnyGmodPathMissing(pathStateGmod, confirmedGmodInstallPath, configuredGmodInstallPath) ||
                AnyDirectoryMissing(pathStateWorkshop, confirmedWorkshopRootPath, configuredWorkshopRootPath);
            var hasPreviousPath = HasAnyPath(
                pathStateGmod,
                confirmedGmodInstallPath,
                configuredGmodInstallPath,
                pathStateWorkshop,
                confirmedWorkshopRootPath,
                configuredWorkshopRootPath);
            var detectedDifferent =
                AnyPathDiffers(detectedGmod, pathStateGmod, confirmedGmodInstallPath, configuredGmodInstallPath) ||
                AnyPathDiffers(detectedWorkshop, pathStateWorkshop, confirmedWorkshopRootPath, configuredWorkshopRootPath);
            var noDetectedCandidate =
                string.IsNullOrWhiteSpace(detectedGmod) ||
                string.IsNullOrWhiteSpace(detectedWorkshop);
            var existingInventoryNeedsUsablePaths =
                HasExistingInventory(configuration) && noDetectedCandidate;

            var shouldPrompt = configuredPathsInvalid ||
                               (hasPreviousPath &&
                                (previousMissing || noDetectedCandidate || detectedDifferent)) ||
                               existingInventoryNeedsUsablePaths;

            return new StartupPathRecoveryDecision
            {
                ShouldPrompt = shouldPrompt,
                Reason = BuildReason(
                    configuredPathsInvalid,
                    previousMissing,
                    detectedGmod,
                    detectedWorkshop,
                    detectedDifferent),
                PreviousGmodInstallPath = previousGmod,
                PreviousWorkshopRootPath = previousWorkshop,
                DetectedGmodInstallPath = detectedGmod,
                DetectedWorkshopRootPath = detectedWorkshop
            };
        }

        private static StartupPathRecoveryReason BuildReason(
            bool configuredPathsInvalid,
            bool previousMissing,
            string? detectedGmod,
            string? detectedWorkshop,
            bool detectedDifferent)
        {
            if (string.IsNullOrWhiteSpace(detectedGmod) &&
                string.IsNullOrWhiteSpace(detectedWorkshop))
            {
                return StartupPathRecoveryReason.RequiredPathsUnavailable;
            }

            if (string.IsNullOrWhiteSpace(detectedGmod))
            {
                return StartupPathRecoveryReason.GmodPathUnavailable;
            }

            if (string.IsNullOrWhiteSpace(detectedWorkshop))
            {
                return StartupPathRecoveryReason.WorkshopPathUnavailable;
            }

            // Prefer the concrete missing component when discovery could only
            // recover part of a configured pair. A generic invalid-path reason
            // hides the actionable distinction between GMod and Workshop.
            if (configuredPathsInvalid)
            {
                return StartupPathRecoveryReason.ConfiguredPathInvalid;
            }

            if (previousMissing)
            {
                return StartupPathRecoveryReason.RecordedPathMissing;
            }

            if (detectedDifferent)
            {
                return StartupPathRecoveryReason.RecordedPathChanged;
            }

            return StartupPathRecoveryReason.None;
        }

        private static bool AnyGmodPathMissing(params string?[] paths)
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) &&
                    !PathOverrideResolver.IsDirectoryUsable(Path.Combine(path, "garrysmod")))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AnyDirectoryMissing(params string?[] paths)
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && !PathOverrideResolver.IsDirectoryUsable(path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyPath(params string?[] paths)
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AnyPathDiffers(string? detectedPath, params string?[] recordedPaths)
        {
            foreach (var recordedPath in recordedPaths)
            {
                if (!string.IsNullOrWhiteSpace(recordedPath) && !PathsEqual(recordedPath, detectedPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasExistingInventory(Configuration? configuration)
        {
            if (configuration?.AddonMetadata?.Count > 0)
            {
                return true;
            }

            if (configuration?.KnownSubscribedAddonIds?.Count > 0)
            {
                return true;
            }

            if (configuration?.Assets == null)
            {
                return false;
            }

            foreach (var asset in configuration.Assets)
            {
                if (asset.AddonStates.Count > 0)
                {
                    return true;
                }

                foreach (var addonId in asset.Addons)
                {
                    if (!string.Equals(addonId, "*", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? FirstRecordedPath(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return null;
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
