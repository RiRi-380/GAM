using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Services
{
    public enum PathCandidateConfidence
    {
        Rejected,
        Low,
        Medium,
        High
    }

    public sealed class SteamLibraryCandidate
    {
        public string Path { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public bool HasSteamApps { get; set; }
        public bool HasGmodAppManifest { get; set; }
        public bool HasWorkshopManifest { get; set; }
        public bool HasWorkshopContentRoot { get; set; }
    }

    public sealed class GmodInstallCandidate
    {
        public string LibraryPath { get; set; } = string.Empty;
        public string AppManifestPath { get; set; } = string.Empty;
        public string InstallDir { get; set; } = string.Empty;
        public string InstallPath { get; set; } = string.Empty;
        public bool AppIdMatched { get; set; }
        public bool DirectoryExists { get; set; }
        public bool GarrysmodDirectoryExists { get; set; }
        public PathCandidateConfidence Confidence { get; set; }
        public IReadOnlyList<string> RejectReasons { get; set; } = Array.Empty<string>();
    }

    public sealed class WorkshopRootCandidate
    {
        public string LibraryPath { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public string AppWorkshopManifestPath { get; set; } = string.Empty;
        public bool HasAppWorkshopManifest { get; set; }
        public bool ContentRootExists { get; set; }
        public int ValidPayloadCount { get; set; }
        public int EmptyOrInvalidFolderCount { get; set; }
        public PathCandidateConfidence Confidence { get; set; }
        public IReadOnlyList<string> RejectReasons { get; set; } = Array.Empty<string>();
    }

    public sealed class PathSnapshot
    {
        public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
        public string? SteamRootPath { get; set; }
        public IReadOnlyList<SteamLibraryCandidate> SteamLibraries { get; set; } = Array.Empty<SteamLibraryCandidate>();
        public GmodInstallCandidate? GmodInstall { get; set; }
        public IReadOnlyList<WorkshopRootCandidate> WorkshopRoots { get; set; } = Array.Empty<WorkshopRootCandidate>();
        public WorkshopRootCandidate? ActiveWorkshopRoot { get; set; }
        public string? GmodCacheWorkshopPath { get; set; }
        public string? AddonNoMountPath { get; set; }
        public IReadOnlyList<string> HealthIssues { get; set; } = Array.Empty<string>();
    }
}
