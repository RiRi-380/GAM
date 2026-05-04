using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models;

public sealed class DisableManifest
{
    public const string SupportedSchemaVersion = "GAM-DISABLE v1";
    public const string SupportedAction = "exclude";
    public const string SupportedAppId = "4000";
    public const string DefaultName = "一括除外リスト";
    public const string LegacyDefaultName = "GPT Disable List";

    public bool HasMagicHeader { get; init; }
    public bool HasAction { get; init; }
    public string SchemaVersion { get; init; } = string.Empty;
    public string AppId { get; init; } = SupportedAppId;
    public string Action { get; init; } = string.Empty;
    public DisableManifestMode Mode { get; init; } = DisableManifestMode.Merge;
    public string Name { get; init; } = DefaultName;
    public string Source { get; init; } = string.Empty;
    public IReadOnlyList<string> AddonIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DisableManifestInvalidLine> InvalidLines { get; init; } = Array.Empty<DisableManifestInvalidLine>();
    public int DuplicateCount { get; init; }
}

public enum DisableManifestMode
{
    Merge,
    Replace,
    New
}

public sealed class DisableManifestInvalidLine
{
    public int LineNumber { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class DisableManifestImportOptions
{
    public DisableManifestMode? Mode { get; init; }
    public string? AssetName { get; init; }
    public bool RequireSoftMode { get; init; } = true;
}

public sealed class DisableManifestPreview
{
    public int ValidCount { get; init; }
    public int DuplicateCount { get; init; }
    public int InvalidCount { get; init; }
    public int AlreadyExcludedCount { get; init; }
    public int NewlyExcludedCount { get; init; }
    public IReadOnlyList<string> SampleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DisableManifestInvalidLine> InvalidLines { get; init; } = Array.Empty<DisableManifestInvalidLine>();
    public bool WillRequirePendingApply { get; init; }
    public bool IsSoftMode { get; init; }
    public DisableManifestMode Mode { get; init; } = DisableManifestMode.Merge;
    public string AssetId { get; init; } = DisableManifestImportServiceConstants.AssetId;
    public string AssetName { get; init; } = DisableManifest.DefaultName;
    public bool CreatesDisabledAsset { get; init; }
}

public sealed class DisableManifestImportResult
{
    public int AppliedCount { get; init; }
    public int DuplicateCount { get; init; }
    public int InvalidCount { get; init; }
    public int AlreadyExcludedCount { get; init; }
    public int NewlyExcludedCount { get; init; }
    public bool AppliedImmediately { get; init; }
    public bool QueuedPendingApply { get; init; }
    public string AssetId { get; init; } = DisableManifestImportServiceConstants.AssetId;
    public string AssetName { get; init; } = DisableManifest.DefaultName;
    public DisableManifestMode Mode { get; init; } = DisableManifestMode.Merge;
    public bool CreatedDisabledAsset { get; init; }
}

public static class DisableManifestImportServiceConstants
{
    public const string AssetId = "gpt-disable-list";
    public const string NewAssetIdPrefix = "disable-list-";
}
