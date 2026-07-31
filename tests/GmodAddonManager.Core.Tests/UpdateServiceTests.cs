using GmodAddonManager.Core.Services;
using System.Diagnostics.CodeAnalysis;

namespace GmodAddonManager.Core.Tests;

[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Tests intentionally validate raw URL string inputs.")]
public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_RejectsNonHttpsCustomApiBeforeNetworkAccess()
    {
        var service = new UpdateService(
            "1.0.0",
            new UpdateSource { ApiUrl = "http://updates.example.invalid/releases" });

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("HTTPS", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.0.7.0", "1.0.7")]
    [InlineData("1.0.8.0", "1.0.8")]
    [InlineData("v1.0.9+69e8bf562388f955", "1.0.9")]
    [InlineData("v1.0.10-beta+local", "1.0.10")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void NormalizeVersionNumber_RemovesDisplayOnlySuffixesAndTrailingZeroRevision(
        string version,
        string expected)
    {
        var normalized = UpdateService.NormalizeVersionNumber(version);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("v1.0.7.0", "v1.0.7")]
    [InlineData("1.0.8+abc", "v1.0.8")]
    [InlineData("", "unknown")]
    public void NormalizeVersionLabel_UsesConsistentUserFacingFormat(string version, string expected)
    {
        var label = UpdateService.NormalizeVersionLabel(version);

        Assert.Equal(expected, label);
    }

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

    [Fact]
    public void SelectInstallerAsset_VersionedSetupExe_IsSelectedWithoutManifestRequirement()
    {
        var assets = new[]
        {
            new GitHubAsset { Name = "GAM-Portable-1.0.5.zip", BrowserDownloadUrl = "https://example.com/GAM-Portable-1.0.5.zip" },
            new GitHubAsset { Name = "GAM-UpdateManifest-1.0.5.json", BrowserDownloadUrl = "https://example.com/GAM-UpdateManifest-1.0.5.json" },
            new GitHubAsset { Name = "GAM-UpdateManifest-1.0.5.sig", BrowserDownloadUrl = "https://example.com/GAM-UpdateManifest-1.0.5.sig" },
            new GitHubAsset { Name = "GAM-Setup-1.0.5.exe", BrowserDownloadUrl = "https://example.com/GAM-Setup-1.0.5.exe", Digest = $"sha256:{new string('a', 64)}" }
        };

        var selected = UpdateService.SelectInstallerAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("GAM-Setup-1.0.5.exe", selected.Name);
    }

    [Fact]
    public void SelectInstallerAsset_PrefersSetupExeOverGenericExe()
    {
        var assets = new[]
        {
            new GitHubAsset { Name = "GAM.exe", BrowserDownloadUrl = "https://example.com/GAM.exe" },
            new GitHubAsset { Name = "GAM-Setup.exe", BrowserDownloadUrl = "https://example.com/GAM-Setup.exe" }
        };

        var selected = UpdateService.SelectInstallerAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("GAM-Setup.exe", selected.Name);
    }

    [Fact]
    public void SelectInstallerAsset_GenericExecutableIsNotTreatedAsAnInstaller()
    {
        var selected = UpdateService.SelectInstallerAsset(new[]
        {
            new GitHubAsset { Name = "GAM.exe", BrowserDownloadUrl = "https://example.com/GAM.exe" }
        });

        Assert.Null(selected);
    }

    [Fact]
    public void BuildInstallerLauncherScript_WaitsForCurrentProcessAndStartsInstaller()
    {
        var script = UpdateService.BuildInstallerLauncherScript(
            12345,
            @"C:\Temp\GAM's Setup.exe",
            "/VERYSILENT /SP-");

        Assert.Contains("Wait-Process -Id 12345", script);
        Assert.Contains("$installerPath = 'C:\\Temp\\GAM''s Setup.exe'", script);
        Assert.Contains("$installerArguments = '/VERYSILENT /SP-'", script);
        Assert.Contains("Start-Process -FilePath $installerPath -ArgumentList $installerArguments -Wait", script);
        Assert.Contains("Remove-Item -LiteralPath $installerPath", script);
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", script);
    }

    [Fact]
    public void CreateInstallerLauncherStartInfo_RunsPowerShellScriptHidden()
    {
        var startInfo = UpdateService.CreateInstallerLauncherStartInfo(@"C:\Temp\launcher.ps1");

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Contains("-File", startInfo.ArgumentList);
        Assert.Contains(@"C:\Temp\launcher.ps1", startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("", false)]
    [InlineData("sha256:1234", false)]
    [InlineData("md5:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void TryNormalizeSha256Digest_ValidatesGitHubDigestFormat(string digest, bool expected)
    {
        var actual = UpdateService.TryNormalizeSha256Digest(digest, out var normalized);

        Assert.Equal(expected, actual);
        Assert.Equal(expected ? digest.Substring(digest.IndexOf(':') + 1).ToLowerInvariant() : string.Empty, normalized);
    }

    [Fact]
    public void VerifyDownloadedFileDigest_RejectsTamperedPayload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gam-update-digest-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "verified payload");
            UpdateService.VerifyDownloadedFileDigest(
                path,
                "sha256:3aac0a1146ffe55bac7c05f61401fb1e7e4e6a94110b91585c646fe8cf745f28");

            File.AppendAllText(path, " tampered");
            Assert.Throws<InvalidDataException>(() => UpdateService.VerifyDownloadedFileDigest(
                path,
                "sha256:3aac0a1146ffe55bac7c05f61401fb1e7e4e6a94110b91585c646fe8cf745f28"));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
