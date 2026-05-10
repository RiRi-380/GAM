using System;
using System.IO;
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
            string? configuredWorkshopRootPath = null)
        {
            var previous = configuration?.PathState?.LastDetectedSnapshot ??
                           configuration?.PathState?.LastKnownGoodSnapshot;
            var previousGmod = previous?.GmodInstall?.InstallPath;
            var previousWorkshop = previous?.ActiveWorkshopRoot?.RootPath;
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
            var detectedDifferent =
                !PathsEqual(previousGmod, detectedGmod) ||
                !PathsEqual(previousWorkshop, detectedWorkshop);
            var noDetectedCandidate =
                string.IsNullOrWhiteSpace(detectedGmod) ||
                string.IsNullOrWhiteSpace(detectedWorkshop);

            var shouldPrompt = configuredPathsInvalid ||
                               noDetectedCandidate ||
                               (!string.IsNullOrWhiteSpace(previousGmod) && previousMissing && detectedDifferent);

            return new StartupPathRecoveryDecision
            {
                ShouldPrompt = shouldPrompt,
                Reason = BuildReason(configuredPathsInvalid, previousMissing, noDetectedCandidate),
                PreviousGmodInstallPath = previousGmod,
                PreviousWorkshopRootPath = previousWorkshop,
                DetectedGmodInstallPath = detectedGmod,
                DetectedWorkshopRootPath = detectedWorkshop
            };
        }

        private static string BuildReason(bool configuredPathsInvalid, bool previousMissing, bool noDetectedCandidate)
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

            return "Path recovery is recommended.";
        }

        private static bool IsMissing(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) && !Directory.Exists(path);
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
            }

            try
            {
                var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
