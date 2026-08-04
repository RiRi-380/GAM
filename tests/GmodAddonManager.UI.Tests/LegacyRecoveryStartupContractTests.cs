using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class LegacyRecoveryStartupContractTests
{
    [Fact]
    public void LegacyPhysicalRecoveryRunsBeforeAddonManagerInitialization()
    {
        var app = ReadRepositoryFile(
            new[] { "src", "GmodAddonManager.UI", "App.axaml.cs" });

        var recovery = app.IndexOf("RecoverIfNeededAsync(", StringComparison.Ordinal);
        var manager = app.IndexOf("addonManager = new AddonManager", StringComparison.Ordinal);

        Assert.True(recovery >= 0, "Legacy Hard-layout recovery is not wired into startup.");
        Assert.True(manager > recovery, "AddonManager starts before legacy payload recovery.");
        Assert.Contains("SteamProcessChecker.IsGmodRunning()", app, StringComparison.Ordinal);
        Assert.Contains(
            "LegacyHardLayoutRecoveryStatus.DeferredWhileGmodIsRunning",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "LegacyHardLayoutRecoveryStatus.Blocked",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRecoveryAndPortableUpdateMessagesAreLocalized()
    {
        foreach (var resourceName in new[] { "en-US.json", "ja-JP.json" })
        {
            var resource = ReadRepositoryFile(new[]
            {
                "src", "GmodAddonManager.UI", "Resources", resourceName
            });
            Assert.Contains("\"LegacyRecovery.Blocked\"", resource, StringComparison.Ordinal);
            Assert.Contains("\"LegacyRecovery.GmodRunning\"", resource, StringComparison.Ordinal);
            Assert.Contains("\"UpdateDialog.PortableArchiveReady\"", resource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AccessDeniedDoesNotTellUsersToElevateSoftMode()
    {
        var app = ReadRepositoryFile(
            new[] { "src", "GmodAddonManager.UI", "App.axaml.cs" });
        Assert.Contains("Error.AccessDeniedTitle", app, StringComparison.Ordinal);
        Assert.Contains("Error.AccessDeniedMessage", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Error.AdminRequired", app, StringComparison.Ordinal);

        foreach (var resourceName in new[] { "en-US.json", "ja-JP.json" })
        {
            var resource = ReadRepositoryFile(new[]
            {
                "src", "GmodAddonManager.UI", "Resources", resourceName
            });
            Assert.Contains("\"Error.AccessDeniedTitle\"", resource, StringComparison.Ordinal);
            Assert.Contains("\"Error.AccessDeniedMessage\"", resource, StringComparison.Ordinal);
            Assert.DoesNotContain("Error.AdminRequired", resource, StringComparison.Ordinal);
            Assert.DoesNotContain("run the app as administrator", resource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("管理者としてこのアプリ", resource, StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(
        string[] segments,
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException(Path.Combine(segments));
    }
}
