using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class InitialAddonStateImportServiceTests
{
    [Fact]
    public void Import_CreatesNeutralOffAssetFromSubscribedDisabledIntersection()
    {
        var configuration = new Configuration();
        configuration.CreateDefaultAssets();
        var service = new InitialAddonStateImportService();

        var result = service.Import(
            configuration,
            subscribedIds: ["100", "200"],
            disabledIds: ["200", "300"],
            completedAtUtc: new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc));

        Assert.True(result.Completed);
        Assert.True(result.CreatedAsset);
        Assert.Equal(["200"], result.ImportedAddonIds);
        var imported = Assert.Single(
            configuration.Assets,
            asset => asset.Name == InitialAddonStateImportService.ImportedAssetName);
        Assert.True(imported.IsSystem);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledId, imported.Id);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledDefaultState, imported.GetWholeState());
        Assert.Equal(["200"], imported.Addons);
        Assert.DoesNotContain("300", imported.Addons);
        Assert.True(configuration.InitialRuntimeImportCompleted);
        Assert.True(configuration.SubscriptionBaselineInitialized);
        Assert.Equal(["100", "200"], configuration.KnownSubscribedAddonIds);
        Assert.Empty(configuration.SubscriptionFirstSeenAtUtc);
    }

    [Fact]
    public void Import_DoesNotCreateEmptyAsset()
    {
        var configuration = new Configuration();
        configuration.CreateDefaultAssets();

        var result = new InitialAddonStateImportService().Import(
            configuration,
            subscribedIds: ["100"],
            disabledIds: ["200"],
            completedAtUtc: DateTime.UtcNow);

        Assert.False(result.CreatedAsset);
        Assert.Equal(2, configuration.Assets.Count);
        Assert.Equal(
            AddonState.Enabled,
            configuration.Assets[0].GetWholeState());
        var disabled = Assert.Single(
            configuration.Assets,
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
        Assert.Empty(disabled.Addons);
    }

    [Fact]
    public void Import_IsIdempotent()
    {
        var configuration = new Configuration();
        configuration.CreateDefaultAssets();
        var service = new InitialAddonStateImportService();

        service.Import(configuration, ["100"], ["100"], DateTime.UtcNow);
        var assetCount = configuration.Assets.Count;
        var second = service.Import(configuration, ["200"], ["200"], DateTime.UtcNow);

        Assert.True(second.Completed);
        Assert.False(second.CreatedAsset);
        Assert.Equal(assetCount, configuration.Assets.Count);
        Assert.DoesNotContain(
            configuration.Assets,
            asset => asset.Addons.Contains("200"));
    }
}
