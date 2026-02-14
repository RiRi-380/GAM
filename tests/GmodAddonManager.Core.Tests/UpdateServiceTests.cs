using GmodAddonManager.Core.Services;
using System.Diagnostics.CodeAnalysis;

namespace GmodAddonManager.Core.Tests;

[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Tests intentionally validate raw URL string inputs.")]
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("https://github.com/RiRi-380/GAM/releases/download/v1.0.0/GAM-Setup-v1.0.0.exe")]
    [InlineData("https://example.com/path/installer-latest.exe")]
    [InlineData("https://example.com/path/GAM-Setup-v1.0.0.exe?download=1")]
    public void ResolveInstallerArguments_SetupOrInstallerExe_ReturnsSilentArgs(string downloadUrl)
    {
        var args = UpdateService.ResolveInstallerArguments(downloadUrl);

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://example.com/path/GAM-Portable-v1.0.0.zip")]
    [InlineData("https://example.com/path/GAM.exe")]
    [InlineData("not-a-url")]
    public void ResolveInstallerArguments_NonInstallerInput_ReturnsEmpty(string downloadUrl)
    {
        var args = UpdateService.ResolveInstallerArguments(downloadUrl);

        Assert.Equal(string.Empty, args);
    }
}
