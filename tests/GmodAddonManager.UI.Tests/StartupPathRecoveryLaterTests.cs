using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class StartupPathRecoveryLaterTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void LaterUsesOnlyACompleteDetectedPairAtStartupAndPreservesSavedSettings(
        bool openedFromSettings, bool gameAvailable, bool workshopAvailable)
    {
        var root = Directory.CreateTempSubdirectory("gam-startup-path-");
        try
        {
            var profile = root.CreateSubdirectory("profile").FullName;
            var settingsPath = Path.Combine(profile, "settings.json");
            var settings = new AppSettings
            {
                CustomGmodInstallPath = Path.Combine(root.FullName, "previous-game"),
                CustomWorkshopPath = Path.Combine(root.FullName, "previous-workshop")
            };
            settings.SaveTo(settingsPath);
            var previousSettings = File.ReadAllBytes(settingsPath);
            var game = root.CreateSubdirectory("detected-game");
            game.CreateSubdirectory("garrysmod");
            var workshop = root.CreateSubdirectory("detected-workshop");
            var decision = new StartupPathRecoveryDecision
            {
                DetectedGmodInstallPath = gameAvailable ? game.FullName : null,
                DetectedWorkshopRootPath = workshopAvailable ? workshop.FullName : null
            };

            var result = StartupPathRecoveryCoordinator.CompletePrompt(
                settings, profile, decision, new StartupPathRecoveryDialogResult(), openedFromSettings);

            Assert.False(result.Accepted);
            Assert.False(result.ApplyRepairs);
            Assert.Equal(previousSettings, File.ReadAllBytes(settingsPath));
            Assert.False(File.Exists(settingsPath + ".bak"));
            Assert.Equal(Path.Combine(root.FullName, "previous-game"), settings.CustomGmodInstallPath);
            Assert.Equal(Path.Combine(root.FullName, "previous-workshop"), settings.CustomWorkshopPath);
            if (openedFromSettings || !decision.HasDetectedCandidate)
            {
                Assert.Null(result.ResolvedGmodInstallPath);
                Assert.Null(result.ResolvedWorkshopRootPath);
                return;
            }

            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomAppDataPath = profile,
                CustomGmodInstallPath = result.ResolvedGmodInstallPath,
                CustomWorkshopPath = result.ResolvedWorkshopRootPath,
                CustomWorkshopCacheFilePaths = [Path.Combine(profile, "fixture-manifest.acf")],
                DisableCacheScan = true
            });
            Assert.Equal(game.FullName, manager.GmodInstallPath);
            Assert.Equal(workshop.FullName, manager.GetWorkshopPath());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseAndSaveStoresTheChosenPairForTheNextLaunch(bool openedFromSettings)
    {
        var root = Directory.CreateTempSubdirectory("gam-save-startup-path-");
        try
        {
            var settings = new AppSettings();
            var choice = new StartupPathRecoveryDialogResult
            {
                Accepted = true,
                ManualSelection = true,
                GmodInstallPath = root.CreateSubdirectory("selected-game").FullName,
                WorkshopRootPath = root.CreateSubdirectory("selected-workshop").FullName
            };
            var result = StartupPathRecoveryCoordinator.CompletePrompt(
                settings, root.FullName, new StartupPathRecoveryDecision(), choice, openedFromSettings);

            Assert.True(result.Accepted);
            Assert.Equal(openedFromSettings, result.ApplyRepairs);
            var persisted = AppSettings.LoadFrom(Path.Combine(root.FullName, "settings.json"));
            Assert.Equal(choice.GmodInstallPath, persisted.CustomGmodInstallPath);
            Assert.Equal(choice.WorkshopRootPath, persisted.CustomWorkshopPath);
            Assert.Equal(choice.GmodInstallPath, persisted.ConfirmedGmodInstallPath);
            Assert.Equal(choice.WorkshopRootPath, persisted.ConfirmedWorkshopPath);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void LaterHintDistinguishesStartupManualAndIncompleteEvenAfterBrowsing(string language)
    {
        var localization = LocalizationManager.Instance;
        var previousLanguage = localization.CurrentLanguage;
        try
        {
            localization.CurrentLanguage = language;
            var decision = new StartupPathRecoveryDecision
            {
                DetectedGmodInstallPath = "detected-game",
                DetectedWorkshopRootPath = "detected-workshop"
            };

            var startup = StartupPathRecoveryDialog.GetLaterHint(decision, false);
            var manual = StartupPathRecoveryDialog.GetLaterHint(decision, true);
            var browsed = StartupPathRecoveryDialog.GetLaterHint(decision, false, true);
            Assert.Contains("detected-game", browsed, StringComparison.Ordinal);
            Assert.Contains("detected-workshop", browsed, StringComparison.Ordinal);
            Assert.Equal(manual, StartupPathRecoveryDialog.GetLaterHint(decision, true, true));
            decision.DetectedWorkshopRootPath = null;
            var incomplete = StartupPathRecoveryDialog.GetLaterHint(decision, false);
            Assert.Equal(incomplete, StartupPathRecoveryDialog.GetLaterHint(decision, false, true));
            Assert.Equal(manual, StartupPathRecoveryDialog.GetLaterHint(decision, true));
            Assert.Equal(4, new[] { startup, manual, browsed, incomplete }.Distinct().Count());
            foreach (var hint in new[] { startup, manual, browsed, incomplete })
            {
                Assert.Contains(language == "ja-JP" ? "次回" : "next launch", hint, StringComparison.Ordinal);
            }
        }
        finally
        {
            localization.CurrentLanguage = previousLanguage;
        }
    }
}
