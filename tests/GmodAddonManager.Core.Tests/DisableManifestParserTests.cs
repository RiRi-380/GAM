using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class DisableManifestParserTests
{
    [Fact]
    public void ParseReadsBareIdsUrlsCommentsAndDuplicates()
    {
        var parser = new DisableManifestParser();
        var manifest = parser.Parse("""
            # GAM-DISABLE v1
            # appid: 4000
            # action: exclude
            # mode: merge
            # name: 一括除外リスト

            https://steamcommunity.com/sharedfiles/filedetails/?id=104479467 # Door STool
            https://steamcommunity.com/workshop/filedetails/?id=104483020
            104487316
            104483020 # duplicate
            """);

        Assert.True(manifest.HasMagicHeader);
        Assert.True(manifest.HasAction);
        Assert.Equal("exclude", manifest.Action);
        Assert.Equal(DisableManifestMode.Merge, manifest.Mode);
        Assert.Collection(
            manifest.AddonIds,
            id => Assert.Equal("104479467", id),
            id => Assert.Equal("104483020", id),
            id => Assert.Equal("104487316", id));
        Assert.Equal(1, manifest.DuplicateCount);
        Assert.Empty(manifest.InvalidLines);
    }

    [Fact]
    public void ParseRecordsInvalidLinesAndUnsupportedMode()
    {
        var parser = new DisableManifestParser();
        var manifest = parser.Parse("""
            # GAM-DISABLE v1
            # action: exclude
            # mode: unsupported
            invalid line
            https://example.test/file?id=123
            104479467
            """);

        Assert.Collection(manifest.AddonIds, id => Assert.Equal("104479467", id));
        Assert.Equal(3, manifest.InvalidLines.Count);
        Assert.Contains(manifest.InvalidLines, line => line.LineNumber == 3 && line.Reason == "Unsupported mode");
        Assert.Contains(manifest.InvalidLines, line => line.LineNumber == 4 && line.Reason == "Workshop ID not found");
        Assert.Contains(manifest.InvalidLines, line => line.LineNumber == 5 && line.Reason == "Workshop ID not found");
    }

    [Fact]
    public void ParseUsesReplaceModeAndOptionalMetadata()
    {
        var parser = new DisableManifestParser();
        var manifest = parser.Parse("""
            # GAM-DISABLE v1
            # appid: 4000
            # action: exclude
            # mode: replace
            # name: Bulk Block
            # source: gpt
            104479467
            """);

        Assert.Equal(DisableManifestMode.Replace, manifest.Mode);
        Assert.Equal("Bulk Block", manifest.Name);
        Assert.Equal("gpt", manifest.Source);
        Assert.Equal("4000", manifest.AppId);
    }

    [Fact]
    public void ParseUsesNewModeAndGptGeneratedName()
    {
        var parser = new DisableManifestParser();
        var manifest = parser.Parse("""
            # GAM-DISABLE v1
            # action: exclude
            # mode: new
            # name: GPT車両除外候補
            104479467
            """);

        Assert.Equal(DisableManifestMode.New, manifest.Mode);
        Assert.Equal("GPT車両除外候補", manifest.Name);
    }

    [Fact]
    public void ParseWithoutMandatoryHeaderLeavesValidationToImportService()
    {
        var parser = new DisableManifestParser();
        var manifest = parser.Parse("104479467");

        Assert.False(manifest.HasMagicHeader);
        Assert.False(manifest.HasAction);
        Assert.Collection(manifest.AddonIds, id => Assert.Equal("104479467", id));
    }
}
