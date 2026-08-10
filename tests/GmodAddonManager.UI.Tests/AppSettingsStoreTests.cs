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

    [Theory]
    [InlineData("{ \"Language\": null }")]
    [InlineData("{ \"Language\": \"fr-FR\" }")]
    [InlineData("{ \"Language\": \"\" }")]
    public void LoadFrom_NormalizesUnsupportedLanguageWithoutWritingDuringStartup(
        string persistedJson)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"language-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, persistedJson);

        var loaded = AppSettings.LoadFrom(path);

        Assert.Equal("ja-JP", loaded.Language);
        Assert.Equal(persistedJson, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void SaveTo_RepairsUnsupportedLanguageOnTheNormalPersistencePath()
    {
        var path = Path.Combine(root, "repaired-language.json");
        var settings = new AppSettings { Language = "fr-FR" };

        settings.SaveTo(path);

        Assert.Equal("ja-JP", settings.Language);
        Assert.Equal("ja-JP", AppSettings.LoadFrom(path).Language);
        Assert.DoesNotContain("fr-FR", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-us", "en-US")]
    [InlineData(" EN-US ", "en-US")]
    [InlineData("JA-jp", "ja-JP")]
    public void LoadFrom_NormalizesSupportedLanguageCaseAndWhitespace(
        string persistedLanguage,
        string expectedLanguage)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"canonical-language-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            Newtonsoft.Json.JsonConvert.SerializeObject(new { Language = persistedLanguage }));

        var loaded = AppSettings.LoadFrom(path);

        Assert.Equal(expectedLanguage, loaded.Language);
    }

    [Fact]
    public void LocalAddonDiscovery_IsDisabledByDefaultAndRequiresExplicitOptIn()
    {
        Directory.CreateDirectory(root);
        var defaultPath = Path.Combine(root, "default-settings.json");
        File.WriteAllText(defaultPath, "{ \"Language\": \"ja-JP\" }");

        Assert.False(AppSettings.LoadFrom(defaultPath).EnableLocalAddonDiscoveryExperimental);

        var enabledPath = Path.Combine(root, "enabled-settings.json");
        new AppSettings { EnableLocalAddonDiscoveryExperimental = true }.SaveTo(enabledPath);

        Assert.True(AppSettings.LoadFrom(enabledPath).EnableLocalAddonDiscoveryExperimental);
    }

    [Fact]
    public void LocalAddonDiscovery_DoesNotInheritTheRetiredManagementSwitch()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy-settings.json");
        File.WriteAllText(path, "{ \"EnableLocalAddonsExperimental\": true }");

        Assert.False(AppSettings.LoadFrom(path).EnableLocalAddonDiscoveryExperimental);
    }

    [Fact]
    public void MemberHistory_IsHiddenByDefaultAndPersistsExplicitOptIn()
    {
        Directory.CreateDirectory(root);
        var defaultPath = Path.Combine(root, "history-default-settings.json");
        File.WriteAllText(defaultPath, "{ \"Language\": \"ja-JP\" }");

        Assert.False(AppSettings.LoadFrom(defaultPath).EnableMemberHistoryExperimental);

        var enabledPath = Path.Combine(root, "history-enabled-settings.json");
        new AppSettings { EnableMemberHistoryExperimental = true }.SaveTo(enabledPath);

        Assert.True(AppSettings.LoadFrom(enabledPath).EnableMemberHistoryExperimental);
    }

    [Fact]
    public void GmodDisabledCard_IsExpandedByDefaultAndPersistsCollapseChoice()
    {
        Directory.CreateDirectory(root);
        var legacyPath = Path.Combine(root, "legacy-collapse-settings.json");
        File.WriteAllText(legacyPath, "{ \"Language\": \"ja-JP\" }");

        Assert.False(AppSettings.LoadFrom(legacyPath).CollapseGmodDisabledAddons);

        var collapsedPath = Path.Combine(root, "collapsed-settings.json");
        new AppSettings { CollapseGmodDisabledAddons = true }.SaveTo(collapsedPath);

        Assert.True(AppSettings.LoadFrom(collapsedPath).CollapseGmodDisabledAddons);
    }

    [Fact]
    public void SharePreferences_AreOffForNewAndLegacySettings()
    {
        var newSettings = new AppSettings();
        Assert.False(newSettings.IncludeImagesInShare);
        Assert.False(newSettings.IncludeMemosInShare);

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy-share-settings.json");
        File.WriteAllText(path, "{ \"Language\": \"ja-JP\" }");

        var loaded = AppSettings.LoadFrom(path);

        Assert.False(loaded.IncludeImagesInShare);
        Assert.False(loaded.IncludeMemosInShare);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharePreferences_PersistEveryCombination(bool includeImages, bool includeMemos)
    {
        var path = Path.Combine(
            root,
            $"share-{includeImages}-{includeMemos}.json");
        new AppSettings
        {
            IncludeImagesInShare = includeImages,
            IncludeMemosInShare = includeMemos
        }.SaveTo(path);

        var loaded = AppSettings.LoadFrom(path);

        Assert.Equal(includeImages, loaded.IncludeImagesInShare);
        Assert.Equal(includeMemos, loaded.IncludeMemosInShare);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
