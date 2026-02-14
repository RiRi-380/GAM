using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamUrlParserTests
{
    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3308350386", "3308350386")]
    [InlineData("http://www.steamcommunity.com/sharedfiles/filedetails/?id=12345", "12345")]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=999", "999")]
    [InlineData("  777777  ", "777777")]
    public void ExtractWorkshopId_ReturnsExpectedId(string input, string expected)
    {
        var actual = SteamUrlParser.ExtractWorkshopId(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://example.com/not-steam")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?x=123")]
    public void ExtractWorkshopId_InvalidInput_ReturnsNull(string input)
    {
        var actual = SteamUrlParser.ExtractWorkshopId(input);
        Assert.Null(actual);
    }

    [Fact]
    public void IsValidWorkshopUrl_ReturnsTrueForValidUrl()
    {
        Assert.True(SteamUrlParser.IsValidWorkshopUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=3308350386"));
    }

    [Fact]
    public void BuildWorkshopUrl_ReturnsCanonicalUrl()
    {
        var url = SteamUrlParser.BuildWorkshopUrl("123456");
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=123456", url);
    }

    [Fact]
    public void BuildWorkshopUrl_BlankId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SteamUrlParser.BuildWorkshopUrl(" "));
    }
}
