using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonClassificationServiceTests
{
    [Fact]
    public void CanonicalDefinitions_MatchCurrentFilterContract()
    {
        Assert.Equal(
            ["Gamemode", "Map", "Weapon", "Vehicle", "NPC", "Tool", "Entity", "Effects", "Model", "ServerContent"],
            AddonClassificationService.SupportedTypes);
        Assert.Equal(
            ["Build", "Cartoon", "Comic", "Fun", "Movie", "Roleplay", "Scenic", "Realism", "Water"],
            AddonClassificationService.SupportedTags);
    }

    [Theory]
    [InlineData("server content", AssetMembershipRuleKind.Type, "ServerContent")]
    [InlineData("ROLEPLAY", AssetMembershipRuleKind.Tag, "Roleplay")]
    public void TryNormalizeRule_ProducesStableCanonicalValue(
        string value,
        AssetMembershipRuleKind kind,
        string expected)
    {
        var valid = AddonClassificationService.TryNormalizeRule(
            new AssetMembershipRule(kind, value),
            out var normalized,
            out var error);

        Assert.True(valid, error);
        Assert.Equal(AssetMembershipRule.CurrentSchemaVersion, normalized.SchemaVersion);
        Assert.Equal(kind, normalized.Kind);
        Assert.Equal(expected, normalized.Value);
    }

    [Fact]
    public void Evaluate_TypeCanMatchKnownWorkshopTag()
    {
        var addon = new WorkshopAddon("100", string.Empty)
        {
            Tags = ["weapons"],
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };

        Assert.Equal(
            AddonClassificationMatch.Match,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Type, "Weapon")));
    }

    [Fact]
    public void Evaluate_TypeWithOnlyNonTypeTagsKnown_RemainsUnknown()
    {
        var addon = new WorkshopAddon("100", string.Empty)
        {
            Type = string.Empty,
            TypeMetadataStatus = AddonClassificationMetadataStatus.Unknown,
            Tags = ["Fun"],
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };

        Assert.Equal(
            AddonClassificationMatch.Unknown,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Type, "Map")));
    }

    [Fact]
    public void Evaluate_ConfirmedTypeAndTagNonmatches_AreNoMatch()
    {
        var addon = new WorkshopAddon("100", string.Empty)
        {
            TypeMetadataStatus = AddonClassificationMetadataStatus.Known,
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };

        Assert.Equal(
            AddonClassificationMatch.NoMatch,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Type, "Map")));
        Assert.Equal(
            AddonClassificationMatch.NoMatch,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Tag, "Fun")));
    }

    [Fact]
    public void Evaluate_TagAliasesMirrorFilterBehavior()
    {
        var addon = new WorkshopAddon("100", string.Empty)
        {
            Tags = ["RP", "Scenery"],
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };

        Assert.Equal(
            AddonClassificationMatch.Match,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Tag, "Roleplay")));
        Assert.Equal(
            AddonClassificationMatch.Match,
            AddonClassificationService.Evaluate(
                addon,
                new AssetMembershipRule(AssetMembershipRuleKind.Tag, "Scenic")));
    }

    [Theory]
    [InlineData(" Server-Content ", "servercontent")]
    [InlineData("SCENERY", "scenic")]
    [InlineData("role_playing", "roleplay")]
    public void Canonicalize_MirrorsExistingFilterKeys(string input, string expected)
    {
        Assert.Equal(expected, AddonClassificationService.Canonicalize(input));
    }

    [Theory]
    [InlineData("weapons", "Weapon")]
    [InlineData("effects", "Effects")]
    [InlineData("server-content", "ServerContent")]
    public void InferTypeFromTags_MirrorsExistingFilterMappings(
        string tag,
        string expected)
    {
        Assert.Equal(
            expected,
            AddonClassificationService.InferTypeFromTags([tag]));
    }

    [Fact]
    public void InferTypeFromTags_NonTypeTagDoesNotClaimKnownType()
    {
        Assert.Null(AddonClassificationService.InferTypeFromTags(["Fun"]));
    }
}
