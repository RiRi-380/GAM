using System;

namespace GmodAddonManager.Core.Models
{
    // Deliberately contains no identifiers, names, paths or free-form messages.
    public sealed class AddonDiagnosticSnapshot
    {
        public DateTime CapturedAtUtc { get; internal set; }
        public bool Initialized { get; internal set; }
        public int SchemaVersion { get; internal set; }
        public int CustomAssets { get; internal set; }
        public int SmartAssets { get; internal set; }
        public int Groups { get; internal set; }
        public int AssetsNeedingReview { get; internal set; }
        public int MetadataEntries { get; internal set; }
        public int? LastKnownSubscriptions { get; internal set; }
        public int? DesiredEnabled { get; internal set; }
        public int? RuntimeEnabled { get; internal set; }
        public int? Mismatches { get; internal set; }
        public DiagnosticRuntimeStatus RuntimeStatus { get; internal set; }
        public DateTime? RuntimeReadAtUtc { get; internal set; }
        public int? PendingChanges { get; internal set; }
        public bool? ApplyInProgress { get; internal set; }
        public bool? GmodRunning { get; internal set; }
        public bool PendingRuntimeApply { get; internal set; }
        public bool PendingRuntimeWrite { get; internal set; }
        public bool RuntimeWriteConflict { get; internal set; }
    }

    public enum DiagnosticRuntimeStatus
    {
        Unavailable,
        Missing,
        Valid,
        Invalid,
        Unreadable
    }
}
