using System;
using System.Collections.Generic;
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
    private readonly PendingChangeManager? pendingChangeManager;
    private readonly Func<bool>? isGmodRunning;

    public DisableManifestImportService(
        AddonManager addonManager,
        PendingChangeManager? pendingChangeManager = null,
        Func<bool>? isGmodRunning = null,
        IDisableManifestParser? parser = null)
    {
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        this.pendingChangeManager = pendingChangeManager;
        this.isGmodRunning = isGmodRunning;
        this.parser = parser ?? new DisableManifestParser();
    }

    public async Task<DisableManifestPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var manifest = await parser.ParseFileAsync(path, cancellationToken);
        ValidateManifest(manifest);

        var config = addonManager.GetConfiguration();
        var existingAsset = manifest.Mode == DisableManifestMode.New
            ? null
            : config.Assets.FirstOrDefault(a => string.Equals(a.Id, AssetId, StringComparison.Ordinal));
        var assetName = ResolveAssetName(existingAsset, manifest, null);
        var excludedIds = existingAsset?.AddonStates
            .Where(kvp => kvp.Value == AddonState.Excluded)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var alreadyExcludedCount = manifest.AddonIds.Count(excludedIds.Contains);

        return new DisableManifestPreview
        {
            ValidCount = manifest.AddonIds.Count,
            DuplicateCount = manifest.DuplicateCount,
            InvalidCount = manifest.InvalidLines.Count,
            AlreadyExcludedCount = alreadyExcludedCount,
            NewlyExcludedCount = manifest.AddonIds.Count - alreadyExcludedCount,
            SampleIds = manifest.AddonIds.Take(10).ToArray(),
            InvalidLines = manifest.InvalidLines,
            WillRequirePendingApply = manifest.Mode != DisableManifestMode.New && IsGmodRunning(),
            IsSoftMode = addonManager.DisableMode == DisableMode.Soft,
            Mode = manifest.Mode,
            AssetId = AssetId,
            AssetName = assetName,
            CreatesDisabledAsset = manifest.Mode == DisableManifestMode.New
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
            throw new InvalidOperationException("除外リストのインポートは Soft disable mode でのみ利用できます。");
        }

        var mode = options.Mode ?? manifest.Mode;
        var config = addonManager.GetConfiguration();
        if (mode == DisableManifestMode.New)
        {
            return await ImportNewAssetAsync(config, manifest, options, cancellationToken);
        }

        var asset = config.Assets.FirstOrDefault(a => string.Equals(a.Id, AssetId, StringComparison.Ordinal));
        var createdAsset = false;
        if (asset == null)
        {
            asset = new Asset(ResolveAssetName(null, manifest, options.AssetName))
            {
                Id = AssetId,
                Enabled = true,
                IsSystem = false,
                DefaultAddonState = AddonState.Disabled
            };
            config.Assets.Add(asset);
            createdAsset = true;
        }

        if (createdAsset ||
            !string.IsNullOrWhiteSpace(options.AssetName) ||
            IsLegacyDefaultName(asset.Name))
        {
            asset.Name = ResolveAssetName(asset, manifest, options.AssetName);
        }

        asset.Enabled = true;
        asset.IsSystem = false;
        asset.DefaultAddonState = AddonState.Disabled;

        var previouslyExcluded = asset.AddonStates
            .Where(kvp => kvp.Value == AddonState.Excluded)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (mode == DisableManifestMode.Replace)
        {
            asset.Addons.Clear();
            asset.AddonStates.Clear();
        }

        var alreadyExcludedCount = 0;
        var newlyExcludedCount = 0;

        foreach (var addonId in manifest.AddonIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureKnownAddonMetadata(config, addonId);

            if (!asset.Addons.Contains(addonId))
            {
                asset.Addons.Add(addonId);
            }

            asset.AddonStates[addonId] = AddonState.Excluded;

            if (previouslyExcluded.Contains(addonId))
            {
                alreadyExcludedCount++;
            }
            else
            {
                newlyExcludedCount++;
            }
        }

        await addonManager.SaveConfigurationImmediatelyAsync();

        var gmodRunning = IsGmodRunning();
        if (gmodRunning)
        {
            pendingChangeManager?.QueueChange(new AddonChange("apply_states", AssetId));
            return new DisableManifestImportResult
            {
                AppliedCount = manifest.AddonIds.Count,
                DuplicateCount = manifest.DuplicateCount,
                InvalidCount = manifest.InvalidLines.Count,
                AlreadyExcludedCount = alreadyExcludedCount,
                NewlyExcludedCount = newlyExcludedCount,
                AppliedImmediately = false,
                QueuedPendingApply = true,
                AssetId = AssetId,
                AssetName = asset.Name,
                Mode = mode
            };
        }

        await addonManager.UpdateAddonStatesAsync();
        await addonManager.SaveConfigurationImmediatelyAsync();

        return new DisableManifestImportResult
        {
            AppliedCount = manifest.AddonIds.Count,
            DuplicateCount = manifest.DuplicateCount,
            InvalidCount = manifest.InvalidLines.Count,
            AlreadyExcludedCount = alreadyExcludedCount,
            NewlyExcludedCount = newlyExcludedCount,
            AppliedImmediately = true,
            QueuedPendingApply = false,
            AssetId = AssetId,
            AssetName = asset.Name,
            Mode = mode,
            CreatedDisabledAsset = false
        };
    }

    private async Task<DisableManifestImportResult> ImportNewAssetAsync(
        Configuration config,
        DisableManifest manifest,
        DisableManifestImportOptions options,
        CancellationToken cancellationToken)
    {
        var assetName = MakeUniqueAssetName(
            config,
            ResolveAssetName(null, manifest, options.AssetName));
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
        return string.Equals(
            name.Trim(),
            DisableManifest.LegacyDefaultName,
            StringComparison.OrdinalIgnoreCase);
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
            Title = $"Workshop {addonId}",
            IsEnabled = true,
            NeedsTitleUpdate = true,
            IsGmaFile = false
        };
    }

    private bool IsGmodRunning()
    {
        return isGmodRunning?.Invoke() ?? false;
    }
}
