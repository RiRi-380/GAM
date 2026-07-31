using GmodAddonManager.UI.Models;

namespace GmodAddonManager.UI.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"gam-app-settings-{Guid.NewGuid():N}");

    [Fact]
    public void SaveTo_SecondSaveAtomicallyRotatesThePreviousValidSettingsToBackup()
    {
        var path = Path.Combine(root, "settings.json");
        var settings = new AppSettings { Language = "ja-JP" };
        settings.SaveTo(path);
        var firstJson = File.ReadAllText(path);

        settings.Language = "en-US";
        settings.SaveTo(path);

        Assert.Equal("en-US", AppSettings.LoadFrom(path).Language);
        Assert.Equal("ja-JP", AppSettings.LoadFrom(path + ".bak").Language);
        Assert.Equal(firstJson, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void LoadFrom_CorruptPrimaryUsesBackupWithoutWritingBeforeTheAppLock()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        const string corrupt = "{ invalid";
        File.WriteAllText(path, corrupt);
        new AppSettings { Language = "en-US" }.SaveTo(path + ".bak");
        var backupJson = File.ReadAllText(path + ".bak");

        var recovered = AppSettings.LoadFrom(path);

        Assert.Equal("en-US", recovered.Language);
        Assert.Equal(corrupt, File.ReadAllText(path));
        Assert.Equal(backupJson, File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(root, "settings.json.corrupt-*.bak"));

        recovered.SaveTo(path);
        Assert.Equal("en-US", AppSettings.LoadFrom(path).Language);
        var corruptArchive = Assert.Single(Directory.GetFiles(root, "settings.json.corrupt-*.bak"));
        Assert.Equal(corrupt, File.ReadAllText(corruptArchive));
        Assert.Equal(backupJson, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void LoadFrom_WhenPrimaryAndBackupAreInvalid_FailsWithoutOverwritingEither()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{ primary");
        File.WriteAllText(path + ".bak", "{ backup");

        Assert.Throws<InvalidOperationException>(() => AppSettings.LoadFrom(path));

        Assert.Equal("{ primary", File.ReadAllText(path));
        Assert.Equal("{ backup", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(root, "settings.json.corrupt-*.bak"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
