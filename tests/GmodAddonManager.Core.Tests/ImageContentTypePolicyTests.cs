using GmodAddonManager.Core.Services;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public class ImageContentTypePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    public void AllowsImageDownloadAllowsImageOrSteamOctetStream(string? mediaType)
    {
        Assert.True(ImageContentTypePolicy.AllowsImageDownload(mediaType));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public void AllowsImageDownloadRejectsNonImageContentType(string mediaType)
    {
        Assert.False(ImageContentTypePolicy.AllowsImageDownload(mediaType));
    }
}
