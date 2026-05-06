using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services;

public interface IDisableManifestImportService
{
    Task<DisableManifestPreview> PreviewAsync(string path, CancellationToken cancellationToken = default);

    Task<DisableManifestImportResult> ImportAsync(
        string path,
        DisableManifestImportOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class DisableManifestImportService : IDisableManifestImportService
{
    public const string AssetId = DisableManifestImportServiceConstants.AssetId;
    public const string DefaultAssetName = DisableManifest.DefaultName;

    private readonly AddonManager addonManager;
    private readonly IDisableManifestParser parser;

    public DisableManifestImportService(AddonManager addonManager, IDisableManifestParser? parser = null)
    {
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        this.parser = parser ?? new DisableManifestParser();
    }

    public async Task<DisableManifestPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var manifest = await parser.ParseFileAsync(path, cancellationToken);
        ValidateManifest(manifest);

        return new DisableManifestPreview
        {
            ValidCount = manifest.AddonIds.Count,
            DuplicateCount = manifest.DuplicateCount,
            InvalidCount = manifest.InvalidLines.Count,
            AlreadyExcludedCount = 0,
            NewlyExcludedCount = manifest.AddonIds.Count,
            SampleIds = manifest.AddonIds.Take(10).ToArray(),
            InvalidLines = manifest.InvalidLines,
            WillRequirePendingApply = false,
            IsSoftMode = addonManager.DisableMode == DisableMode.Soft,
            Mode = DisableManifestMode.New,
            AssetId = string.Empty,
            AssetName = ResolveAssetName(null, manifest, null),
            CreatesDisabledAsset = true
        };
    }

    public async Task<DisableManifestImportResult> ImportAsync(
        string path,
        DisableManifestImportOptions options,
        CancellationToken cancellationToken = default)
    {
        options ??= new DisableManifestImportOptions();

        var manifest = await parser.ParseFileAsync(path, cancellationToken);
        ValidateManifest(manifest);

        if (options.RequireSoftMode && addonManager.DisableMode != DisableMode.Soft)
        {
            throw new InvalidOperationException("Disable list import is supported only in Soft disable mode.");
        }

        var config = addonManager.GetConfiguration();
        var assetName = MakeUniqueAssetName(config, ResolveAssetName(null, manifest, options.AssetName));
        var asset = new Asset(assetName)
        {
            Id = CreateNewAssetId(config),
            Enabled = false,
            IsSystem = false,
            DefaultAddonState = AddonState.Disabled
        };

        config.Assets.Add(asset);

        foreach (var addonId in manifest.AddonIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureKnownAddonMetadata(config, addonId);
            asset.Addons.Add(addonId);
            asset.AddonStates[addonId] = AddonState.Excluded;
        }

        await addonManager.SaveConfigurationImmediatelyAsync();

        return new DisableManifestImportResult
        {
            AppliedCount = manifest.AddonIds.Count,
            DuplicateCount = manifest.DuplicateCount,
            InvalidCount = manifest.InvalidLines.Count,
            AlreadyExcludedCount = 0,
            NewlyExcludedCount = manifest.AddonIds.Count,
            AppliedImmediately = false,
            QueuedPendingApply = false,
            AssetId = asset.Id,
            AssetName = asset.Name,
            Mode = DisableManifestMode.New,
            CreatedDisabledAsset = true
        };
    }

    private static void ValidateManifest(DisableManifest manifest)
    {
        if (!manifest.HasMagicHeader ||
            !manifest.SchemaVersion.Equals(DisableManifest.SupportedSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported disable manifest schema.");
        }

        if (!manifest.HasAction ||
            !manifest.Action.Equals(DisableManifest.SupportedAction, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only action: exclude is supported.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.AppId) &&
            !manifest.AppId.Equals(DisableManifest.SupportedAppId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This manifest is not for Garry's Mod appid 4000.");
        }

        if (manifest.AddonIds.Count == 0)
        {
            throw new InvalidOperationException("No valid Workshop addon IDs found.");
        }
    }

    private static string ResolveAssetName(Asset? existingAsset, DisableManifest manifest, string? optionAssetName)
    {
        if (!string.IsNullOrWhiteSpace(optionAssetName))
        {
            return optionAssetName.Trim();
        }

        if (existingAsset != null &&
            !string.IsNullOrWhiteSpace(existingAsset.Name) &&
            !IsLegacyDefaultName(existingAsset.Name))
        {
            return existingAsset.Name;
        }

        if (!string.IsNullOrWhiteSpace(manifest.Name) &&
            !IsLegacyDefaultName(manifest.Name))
        {
            return manifest.Name.Trim();
        }

        return DefaultAssetName;
    }

    private static bool IsLegacyDefaultName(string name)
    {
        var trimmed = name.Trim();
        return DisableManifest.LegacyDefaultNames.Any(
            legacyName => string.Equals(trimmed, legacyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string MakeUniqueAssetName(Configuration config, string baseName)
    {
        var candidate = string.IsNullOrWhiteSpace(baseName)
            ? DefaultAssetName
            : baseName.Trim();

        if (!config.Assets.Any(asset => string.Equals(asset.Name, candidate, StringComparison.Ordinal)))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var suffixedName = $"{candidate} ({suffix})";
            if (!config.Assets.Any(asset => string.Equals(asset.Name, suffixedName, StringComparison.Ordinal)))
            {
                return suffixedName;
            }
        }

        return $"{candidate} ({Guid.NewGuid():N})";
    }

    private static string CreateNewAssetId(Configuration config)
    {
        string assetId;
        do
        {
            assetId = DisableManifestImportServiceConstants.NewAssetIdPrefix + Guid.NewGuid().ToString("N");
        }
        while (config.Assets.Any(asset => string.Equals(asset.Id, assetId, StringComparison.Ordinal)));

        return assetId;
    }

    private static void EnsureKnownAddonMetadata(Configuration config, string addonId)
    {
        if (config.AddonMetadata.ContainsKey(addonId))
        {
            return;
        }

        config.AddonMetadata[addonId] = new WorkshopAddon(addonId, string.Empty)
        {
            Title = $"Workshop-{addonId}",
            IsEnabled = true,
            NeedsTitleUpdate = true,
            IsGmaFile = false
        };
    }
}
