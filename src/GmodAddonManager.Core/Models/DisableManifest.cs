using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models;

public sealed class DisableManifest
{
    public const string SupportedSchemaVersion = "GAM-DISABLE v1";
    public const string SupportedAction = "exclude";
    public const string SupportedAppId = "4000";
    public const string DefaultName = "Cleanup Candidates";
    public const string LegacyDefaultName = "GPT Disable List";
    public static readonly string[] LegacyDefaultNames =
    {
        LegacyDefaultName,
        "Disable Candidates",
        "\u4e00\u62ec\u9664\u5916\u30ea\u30b9\u30c8",
        "\u524a\u9664\u5019\u88dc",
        "GPT\u524a\u9664\u5019\u88dc"
    };

    public bool HasMagicHeader { get; set; }
    public bool HasAction { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string AppId { get; set; } = SupportedAppId;
    public string Action { get; set; } = string.Empty;
    public DisableManifestMode Mode { get; set; } = DisableManifestMode.Merge;
    public string Name { get; set; } = DefaultName;
    public string Source { get; set; } = string.Empty;
    public IReadOnlyList<string> AddonIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<DisableManifestInvalidLine> InvalidLines { get; set; } = Array.Empty<DisableManifestInvalidLine>();
    public int DuplicateCount { get; set; }
}

public enum DisableManifestMode
{
    Merge,
    Replace,
    New
}

public sealed class DisableManifestInvalidLine
{
    public int LineNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DisableManifestImportOptions
{
    public DisableManifestMode? Mode { get; set; }
    public string? AssetName { get; set; }
    public bool RequireSoftMode { get; set; } = true;
}

public sealed class DisableManifestPreview
{
    public int ValidCount { get; set; }
    public int DuplicateCount { get; set; }
    public int InvalidCount { get; set; }
    public int AlreadyExcludedCount { get; set; }
    public int NewlyExcludedCount { get; set; }
    public IReadOnlyList<string> SampleIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<DisableManifestInvalidLine> InvalidLines { get; set; } = Array.Empty<DisableManifestInvalidLine>();
    public bool WillRequirePendingApply { get; set; }
    public bool IsSoftMode { get; set; }
    public DisableManifestMode Mode { get; set; } = DisableManifestMode.New;
    public string AssetId { get; set; } = string.Empty;
    public string AssetName { get; set; } = DisableManifest.DefaultName;
    public bool CreatesDisabledAsset { get; set; }
}

public sealed class DisableManifestImportResult
{
    public int AppliedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int InvalidCount { get; set; }
    public int AlreadyExcludedCount { get; set; }
    public int NewlyExcludedCount { get; set; }
    public bool AppliedImmediately { get; set; }
    public bool QueuedPendingApply { get; set; }
    public string AssetId { get; set; } = string.Empty;
    public string AssetName { get; set; } = DisableManifest.DefaultName;
    public DisableManifestMode Mode { get; set; } = DisableManifestMode.New;
    public bool CreatedDisabledAsset { get; set; }
}

public static class DisableManifestImportServiceConstants
{
    public const string AssetId = "gpt-disable-list";
    public const string NewAssetIdPrefix = "disable-list-";
}
