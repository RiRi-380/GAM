using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class DisableManifestParserTests
{
    private readonly DisableManifestParser parser = new();

    [Fact]
    public void Parse_ValidManifest_NormalizesIdsAndReportsDuplicates()
    {
        var manifest = parser.Parse(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: new\n" +
            "# name: \u524a\u9664\u5019\u88dc\n" +
            "https://steamcommunity.com/sharedfiles/filedetails/?id=104479467 # Door STool\n" +
            "https://steamcommunity.com/workshop/filedetails/?id=104483020\n" +
            "104483020 # duplicate\n" +
            "not a workshop id\n");

        Assert.True(manifest.HasMagicHeader);
        Assert.True(manifest.HasAction);
        Assert.Equal(DisableManifestMode.New, manifest.Mode);
        Assert.Equal("\u524a\u9664\u5019\u88dc", manifest.Name);
        Assert.Equal(new[] { "104479467", "104483020" }, manifest.AddonIds);
        Assert.Equal(1, manifest.DuplicateCount);
        Assert.Single(manifest.InvalidLines);
        Assert.Equal(9, manifest.InvalidLines[0].LineNumber);
    }

    [Fact]
    public void Parse_HeaderCommentsAndEmptyLines_AreIgnored()
    {
        var manifest = parser.Parse(
            "\n" +
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# source: gpt\n" +
            "# free form comment\n" +
            "\n" +
            "123456789\n");

        Assert.Equal("gpt", manifest.Source);
        Assert.Equal(new[] { "123456789" }, manifest.AddonIds);
        Assert.Empty(manifest.InvalidLines);
    }

    [Fact]
    public void Parse_UnsupportedMode_IsInvalidLineButIdsStillParse()
    {
        var manifest = parser.Parse(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: destructive\n" +
            "123456789\n");

        Assert.Equal(DisableManifestMode.Merge, manifest.Mode);
        Assert.Single(manifest.InvalidLines);
        Assert.Equal("123456789", manifest.AddonIds.Single());
    }
}
