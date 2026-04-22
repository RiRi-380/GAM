using GmodAddonManager.Core.Services;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("GAM-Setup-1.0.1.exe")]
    [InlineData("GAM-Setup-v1.0.1.exe")]
    [InlineData("GAM-installer.exe")]
    public void IsInstallerAssetNameInstallerExeReturnsTrue(string assetName)
    {
        Assert.True(UpdateService.IsInstallerAssetName(assetName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GAM-Portable-1.0.1.zip")]
    [InlineData("VC_redist.x64.exe")]
    [InlineData("release-notes.txt")]
    public void IsInstallerAssetNameNonInstallerAssetReturnsFalse(string? assetName)
    {
        Assert.False(UpdateService.IsInstallerAssetName(assetName));
    }
}
