using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonDisableStatePipelineTests : IDisposable
{
    private const string AddonId = "104479467";
    private readonly string rootPath;

    public AddonDisableStatePipelineTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "gam-disable-state-tests-" + Guid.NewGuid().ToString("N"));
        WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
        AppDataPath = Path.Combine(rootPath, "appdata");
        GmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
        NoMountPath = Path.Combine(GmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(WorkshopPath);
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(GmodRootPath);
        WorkshopManifestPath = WorkshopManifestTestData.Write(rootPath, AddonId);
    }

    private string WorkshopPath { get; }
    private string AppDataPath { get; }
    private string GmodRootPath { get; }
    private string NoMountPath { get; }
    private string WorkshopManifestPath { get; }

    [Fact]
    public async Task ApplyAssetDefaultStateAsync_Disabled_IsNeutralWhileSubscribeRemainsEnabled()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var asset = AddCustomAsset(manager, "Disabled Asset", AddonState.Enabled);

        await manager.ApplyAssetDefaultStateAsync(asset.Id, AddonState.Disabled);

        Assert.Equal(AddonState.Disabled, asset.GetWholeState());
        Assert.Empty(asset.AddonStates);
        Assert.True(manager.GetFinalAddonStates()[AddonId]);
        Assert.DoesNotContain(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcludedAsset_BeatsSubscribedEnabledState()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var asset = AddCustomAsset(manager, "Selection Disabled Asset", AddonState.Enabled);

        await manager.ApplyAssetDefaultStateAsync(asset.Id, AddonState.Excluded);

        Assert.True(asset.Enabled);
        Assert.Equal(AddonState.Excluded, asset.GetWholeState());
        Assert.False(manager.GetFinalAddonStates()[AddonId]);
        Assert.Contains(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsset_ExcludedVetoesCustomSourcesAndUndoRestoresThem()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        AddCustomAsset(manager, "FPS", AddonState.Enabled);
        WriteNoMountFile("999999999");
        var subscribe = manager.GetConfiguration().Assets
            .Single(asset => asset.Id == SystemAssetDefinitions.SubscribeId);

        await manager.ApplyAssetDefaultStateAsync(
            subscribe.Id,
            AddonState.Excluded);

        Assert.Equal(AddonState.Excluded, subscribe.GetWholeState());
        Assert.False(manager.GetFinalAddonStates()[AddonId]);
        var excludedContent = File.ReadAllText(NoMountPath);
        Assert.Contains(AddonId, excludedContent, StringComparison.Ordinal);
        Assert.Contains("999999999", excludedContent, StringComparison.Ordinal);
        Assert.DoesNotContain(
            AddonId,
            manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        Assert.Equal(
            UndoActionType.AssetExcluded,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());

        Assert.Equal(AddonState.Enabled, subscribe.GetWholeState());
        Assert.True(manager.GetFinalAddonStates()[AddonId]);
        var restoredContent = File.ReadAllText(NoMountPath);
        Assert.DoesNotContain(AddonId, restoredContent, StringComparison.Ordinal);
        Assert.Contains("999999999", restoredContent, StringComparison.Ordinal);
        Assert.DoesNotContain(
            AddonId,
            manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    [Fact]
    public async Task SubscribeAsset_ExcludedAppliesToNewSubscriptionOnNextExplicitReconcile()
    {
        const string newlySubscribedId = "2084096447";
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var subscribe = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId);
        await manager.ApplyAssetDefaultStateAsync(
            subscribe.Id,
            AddonState.Excluded);

        WorkshopManifestTestData.Write(rootPath, AddonId, newlySubscribedId);
        AddKnownAddon(manager, newlySubscribedId);

        Assert.True(await manager.UpdateAddonStatesAsync());

        Assert.False(manager.GetFinalAddonStates()[newlySubscribedId]);
        Assert.Contains(
            newlySubscribedId,
            File.ReadAllText(NoMountPath),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            newlySubscribedId,
            manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    [Fact]
    public async Task DisabledAsset_DoesNotVetoEnabledAsset()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        AddCustomAsset(manager, "Enabled Asset", AddonState.Enabled);
        AddCustomAsset(manager, "Disabled Asset", AddonState.Disabled);

        await manager.UpdateAddonStatesAsync();

        Assert.True(manager.GetFinalAddonStates()[AddonId]);
        Assert.DoesNotContain(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAddonStatesAsync_RecordsExternalDisableButDefaultOffAssetDoesNotVetoEnabledSource()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        AddCustomAsset(manager, "Enabled Asset", AddonState.Enabled);
        Assert.True(await manager.UpdateAddonStatesAsync());

        WriteNoMountFile(AddonId, "999999999");

        Assert.True(await manager.UpdateAddonStatesAsync());

        var content = File.ReadAllText(NoMountPath);
        Assert.True(manager.GetFinalAddonStates()[AddonId]);
        Assert.DoesNotContain(AddonId, content, StringComparison.Ordinal);
        Assert.Contains("999999999", content, StringComparison.Ordinal);
        var gmodDisabled = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
        Assert.Equal(AddonState.Disabled, gmodDisabled.GetWholeState());
        Assert.Contains(AddonId, gmodDisabled.Addons);
    }

    [Fact]
    public async Task MalformedNoMount_ReportsActualStateAsUnknown()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        File.WriteAllText(NoMountPath, "not a valid addonnomount document");

        var snapshot = manager.CaptureState();

        Assert.Empty(snapshot.States);
        Assert.Equal("actual:addonnomount.txt:invalid", snapshot.Source);
        Assert.Null(manager.GetActualAddonEnabledState(AddonId));
    }

    [Fact]
    public async Task ExplicitApply_DoesNotOverwriteMalformedNoMount()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var malformed = "not a valid addonnomount document";
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        File.WriteAllText(NoMountPath, malformed);

        Assert.False(await manager.UpdateAddonStatesAsync());

        Assert.Equal(malformed, File.ReadAllText(NoMountPath));
        Assert.Null(manager.GetActualAddonEnabledState(AddonId));
    }

    [Fact]
    public async Task ExplicitApply_DoesNotOverwriteStructurallyMalformedNoMountBody()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var malformed =
            "\"addonnomount\"\n" +
            "{\n" +
            "\t\"1\"\t\t\"999999999\"\n" +
            "\tgarbage\n" +
            "}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        File.WriteAllText(NoMountPath, malformed);

        var actual = manager.CaptureState();
        var applied = await manager.UpdateAddonStatesAsync();

        Assert.Empty(actual.States);
        Assert.Equal("actual:addonnomount.txt:invalid", actual.Source);
        Assert.False(applied);
        Assert.Equal(malformed, File.ReadAllText(NoMountPath));
        Assert.Null(manager.GetActualAddonEnabledState(AddonId));
    }

    private AddonManager CreateManager()
    {
        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = WorkshopPath,
            CustomAppDataPath = AppDataPath,
            CustomGmodInstallPath = GmodRootPath,
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true,
            CustomWorkshopCacheFilePaths = [WorkshopManifestPath],
            ScanCacheTtl = TimeSpan.Zero
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
        return manager;
    }

    private void AddKnownAddon(AddonManager manager, string addonId)
    {
        manager.GetConfiguration().AddonMetadata[addonId] = new WorkshopAddon(
            addonId,
            Path.Combine(WorkshopPath, addonId));
    }

    private static Asset AddCustomAsset(AddonManager manager, string name, AddonState addonState)
    {
        var asset = new Asset(name)
        {
            Id = Guid.NewGuid().ToString("N")
        };
        asset.AddAddon(AddonId);
        asset.SetWholeState(addonState);
        manager.GetConfiguration().Assets.Add(asset);
        return asset;
    }

    private void WriteNoMountFile(params string[] disabledIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("\"addonnomount\"");
        builder.AppendLine("{");
        for (var i = 0; i < disabledIds.Length; i++)
        {
            builder.Append("\t\"").Append(i + 1).Append("\"\t\t\"")
                .Append(disabledIds[i]).AppendLine("\"");
        }
        builder.AppendLine("}");
        File.WriteAllText(NoMountPath, builder.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
