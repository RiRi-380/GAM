using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AssetStateResolverTests
{
    private const string AddonId = "104479467";
    private readonly AssetStateResolver resolver = new();

    [Fact]
    public void Resolve_EnabledSubscribe_EnablesSubscribedAddon()
    {
        var subscribe = CreateSubscribe(AddonState.Enabled);

        var result = Resolve(AddonId, new[] { subscribe }, AddonId);

        Assert.True(result.IsRuntimeTarget);
        Assert.True(result.DesiredEnabled);
        Assert.True(result.EnabledBySubscribe);
        Assert.Equal(AddonStateResolutionReason.Enabled, result.Reason);
        Assert.Empty(result.EnabledByAssets);
        Assert.Empty(result.ExcludedByAssets);
    }

    [Fact]
    public void Resolve_DisabledSubscribe_HasNoEnabledSource()
    {
        var subscribe = CreateSubscribe(AddonState.Disabled);

        var result = Resolve(AddonId, new[] { subscribe }, AddonId);

        Assert.True(result.IsRuntimeTarget);
        Assert.False(result.DesiredEnabled);
        Assert.False(result.EnabledBySubscribe);
        Assert.Equal(AddonStateResolutionReason.NoEnabledSource, result.Reason);
    }

    [Fact]
    public void Resolve_MultipleEnabledCustomAssets_ReportsEveryContributor()
    {
        var first = CreateCustom("fps", "FPS", AddonState.Enabled, AddonId);
        var second = CreateCustom("shared", "Shared", AddonState.Enabled, AddonId);

        var result = Resolve(AddonId, new[] { first, second }, AddonId);

        Assert.True(result.DesiredEnabled);
        Assert.False(result.EnabledBySubscribe);
        Assert.Equal(new[] { "fps", "shared" }, result.EnabledByAssets.Select(x => x.AssetId));
    }

    [Fact]
    public void Resolve_DisabledCustomAsset_IsNeutral()
    {
        var disabled = CreateCustom("disabled", "Disabled", AddonState.Disabled, AddonId);

        var result = Resolve(AddonId, new[] { disabled }, AddonId);

        Assert.False(result.DesiredEnabled);
        Assert.Equal(AddonStateResolutionReason.NoEnabledSource, result.Reason);
        Assert.Empty(result.EnabledByAssets);
        Assert.Empty(result.ExcludedByAssets);
    }

    [Fact]
    public void Resolve_ExcludedCustomAsset_OverridesAllEnabledSources()
    {
        var subscribe = CreateSubscribe(AddonState.Enabled);
        var enabled = CreateCustom("enabled", "Enabled", AddonState.Enabled, AddonId);
        var excluded = CreateCustom("excluded", "Excluded", AddonState.Excluded, AddonId);

        var result = Resolve(
            AddonId,
            new[] { subscribe, enabled, excluded },
            AddonId);

        Assert.False(result.DesiredEnabled);
        Assert.True(result.EnabledBySubscribe);
        Assert.Equal(AddonStateResolutionReason.Excluded, result.Reason);
        Assert.Equal("enabled", Assert.Single(result.EnabledByAssets).AssetId);
        Assert.Equal("excluded", Assert.Single(result.ExcludedByAssets).AssetId);
    }

    [Fact]
    public void Resolve_UnsubscribedCustomReference_IsNotRuntimeTarget()
    {
        var enabled = CreateCustom("enabled", "Enabled", AddonState.Enabled, AddonId);
        var excluded = CreateCustom("excluded", "Excluded", AddonState.Excluded, AddonId);

        var result = Resolve(AddonId, new[] { enabled, excluded });

        Assert.False(result.IsSubscribed);
        Assert.False(result.IsRuntimeTarget);
        Assert.False(result.DesiredEnabled);
        Assert.Equal(AddonStateResolutionReason.NotSubscribed, result.Reason);
        Assert.Equal("enabled", Assert.Single(result.EnabledByAssets).AssetId);
        Assert.Equal("excluded", Assert.Single(result.ExcludedByAssets).AssetId);
    }

    [Fact]
    public void Resolve_PerAddonCompatibilityState_DoesNotOverrideWholeAssetState()
    {
        var asset = CreateCustom("asset", "Asset", AddonState.Enabled, AddonId);
        asset.AddonStates[AddonId] = AddonState.Excluded;

        var result = Resolve(AddonId, new[] { asset }, AddonId);

        Assert.True(result.DesiredEnabled);
        Assert.Empty(result.ExcludedByAssets);
        Assert.Equal("asset", Assert.Single(result.EnabledByAssets).AssetId);
    }

    [Fact]
    public void Resolve_InactiveCompatibilityAsset_MapsToNeutralDisabled()
    {
        var asset = CreateCustom("asset", "Asset", AddonState.Excluded, AddonId);
        asset.Enabled = false;
        var subscribe = CreateSubscribe(AddonState.Enabled);

        var result = Resolve(AddonId, new[] { subscribe, asset }, AddonId);

        Assert.True(result.DesiredEnabled);
        Assert.Empty(result.ExcludedByAssets);
    }

    [Fact]
    public void Resolve_NonSubscribeSystemAsset_IsIgnored()
    {
        var legacyJunction = new Asset("Junction", isSystem: true)
        {
            Id = "junction-system-asset",
            Enabled = true,
            DefaultAddonState = AddonState.Excluded
        };
        legacyJunction.Addons.Add(AddonId);
        var subscribe = CreateSubscribe(AddonState.Enabled);

        var result = Resolve(AddonId, new[] { subscribe, legacyJunction }, AddonId);

        Assert.True(result.DesiredEnabled);
        Assert.Empty(result.ExcludedByAssets);
    }

    private ResolvedAddonState Resolve(
        string addonId,
        IEnumerable<Asset> assets,
        params string[] subscribedAddonIds)
    {
        return resolver.Resolve(
            addonId,
            assets,
            new HashSet<string>(subscribedAddonIds, StringComparer.Ordinal));
    }

    private static Asset CreateSubscribe(AddonState state)
    {
        return new Asset("Subscribe", isSystem: true)
        {
            Id = AssetStateResolver.SubscribeSystemAssetId,
            Enabled = true,
            DefaultAddonState = state
        };
    }

    private static Asset CreateCustom(
        string id,
        string name,
        AddonState state,
        params string[] addonIds)
    {
        var asset = new Asset(name)
        {
            Id = id,
            Enabled = true,
            DefaultAddonState = state
        };

        foreach (var addonId in addonIds)
        {
            asset.Addons.Add(addonId);
        }

        return asset;
    }
}
