using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Tests;

public sealed class NewSubscriptionRuntimeApplicationTests : IDisposable
{
    private const string ExistingAddonId = "100";
    private const string NewAddonId = "200";

    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;
    private readonly string noMountPath;

    public NewSubscriptionRuntimeApplicationTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-new-subscription-tests-" + Guid.NewGuid().ToString("N"));
        workshopPath = Path.Combine(
            rootPath,
            "steamapps",
            "workshop",
            "content",
            "4000");
        appDataPath = Path.Combine(rootPath, "appdata");
        gmodRootPath = Path.Combine(
            rootPath,
            "steamapps",
            "common",
            "GarrysMod");
        noMountPath = Path.Combine(
            gmodRootPath,
            "garrysmod",
            "cfg",
            "addonnomount.txt");

        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(noMountPath)!);
        WritePayload(ExistingAddonId);
        manifestPath = WorkshopManifestTestData.Write(rootPath, ExistingAddonId);
    }

    [Theory]
    [InlineData(AddonState.Disabled)]
    [InlineData(AddonState.Excluded)]
    public async Task WorkshopRefresh_NewSubscriptionFollowsOffSubscribeState(
        AddonState subscribeState)
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await SetSubscribeStateAsync(manager, subscribeState);

        SubscribeNewAddon();
        manager.InvalidateWorkshopScanCache();
        var inventory = await manager.ScanWorkshopFolderAsync();

        Assert.False(manager.GetFinalAddonStates()[NewAddonId]);
        Assert.False(manager.GetActualAddonEnabledState(NewAddonId));
        Assert.False(manager.GetConfiguration().LastGamAppliedAddonStates[NewAddonId]);
        Assert.False(inventory.Single(addon => addon.Id == NewAddonId).IsEnabled);
        Assert.False(ReadPersistedConfiguration().AddonMetadata[NewAddonId].IsEnabled);
        Assert.DoesNotContain(
            NewAddonId,
            manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    [Fact]
    public async Task WorkshopRefresh_EnabledSubscribeClearsStaleDisableForNewSubscription()
    {
        WriteDisabledIds(NewAddonId);
        using var manager = CreateManager();
        await manager.InitializeAsync();

        SubscribeNewAddon();
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();

        Assert.True(manager.GetFinalAddonStates()[NewAddonId]);
        Assert.True(manager.GetActualAddonEnabledState(NewAddonId));
        Assert.True(manager.GetConfiguration().LastGamAppliedAddonStates[NewAddonId]);
        Assert.DoesNotContain(NewAddonId, ReadDisabledIds());
    }

    [Theory]
    [InlineData(AddonState.Disabled, AddonState.Enabled, true)]
    [InlineData(AddonState.Enabled, AddonState.Excluded, false)]
    [InlineData(AddonState.Excluded, AddonState.Enabled, false)]
    public async Task WorkshopRefresh_NewSubscriptionUsesFinalResolverState(
        AddonState subscribeState,
        AddonState customState,
        bool expectedEnabled)
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();

        var custom = new Asset("Pre-existing reference")
        {
            Id = "pre-existing-reference",
            Addons = [NewAddonId]
        };
        custom.SetWholeState(customState);
        manager.GetConfiguration().Assets.Add(custom);
        await manager.SaveConfigurationImmediatelyAsync();
        await SetSubscribeStateAsync(manager, subscribeState);

        SubscribeNewAddon();
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();

        Assert.Equal(expectedEnabled, manager.GetFinalAddonStates()[NewAddonId]);
        Assert.Equal(expectedEnabled, manager.GetActualAddonEnabledState(NewAddonId));
        Assert.Equal(
            expectedEnabled,
            manager.GetConfiguration().LastGamAppliedAddonStates[NewAddonId]);
    }

    [Fact]
    public async Task ScanForNewAddons_NewSubscriptionAppliesSubscribeState()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await SetSubscribeStateAsync(manager, AddonState.Excluded);

        SubscribeNewAddon();
        await manager.ScanForNewAddonsAsync();

        Assert.False(manager.GetActualAddonEnabledState(NewAddonId));
        Assert.Contains(NewAddonId, ReadDisabledIds());
    }

    [Fact]
    public async Task Initialize_NewSubscriptionSincePreviousRunAppliesPersistedSubscribeState()
    {
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            await SetSubscribeStateAsync(manager, AddonState.Disabled);
        }

        SubscribeNewAddon();

        using var restarted = CreateManager();
        await restarted.InitializeAsync();

        Assert.False(restarted.GetActualAddonEnabledState(NewAddonId));
        Assert.False(restarted.GetConfiguration().LastGamAppliedAddonStates[NewAddonId]);
    }

    [Fact]
    public async Task WorkshopRefresh_GmodRunningQueuesWithoutWritingUntilApply()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await SetSubscribeStateAsync(manager, AddonState.Disabled);

        var queued = 0;
        manager.GmodRunningProvider = () => true;
        manager.QueueRuntimeApplyProvider = () => queued++;

        SubscribeNewAddon();
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();

        Assert.True(queued >= 1);
        Assert.True(manager.GetActualAddonEnabledState(NewAddonId));
        Assert.False(manager.GetFinalAddonStates()[NewAddonId]);

        manager.GmodRunningProvider = () => false;
        Assert.True(await manager.UpdateAddonStatesAsync());
        Assert.False(manager.GetActualAddonEnabledState(NewAddonId));
    }

    [Fact]
    public async Task Initialize_GmodRunningQueuesWhenPendingProviderArrives()
    {
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            await SetSubscribeStateAsync(manager, AddonState.Disabled);
        }

        SubscribeNewAddon();
        using var restarted = CreateManager();
        restarted.GmodRunningProvider = () => true;
        await restarted.InitializeAsync();

        Assert.True(restarted.GetActualAddonEnabledState(NewAddonId));
        var queued = 0;
        restarted.QueueRuntimeApplyProvider = () => queued++;
        Assert.Equal(1, queued);

        restarted.GmodRunningProvider = () => false;
        Assert.True(await restarted.UpdateAddonStatesAsync());
        Assert.False(restarted.GetActualAddonEnabledState(NewAddonId));
    }

    [Fact]
    public async Task WorkshopRefresh_NonAuthoritativeSubscriptionSnapshotDoesNotApply()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await SetSubscribeStateAsync(manager, AddonState.Disabled);
        var before = File.ReadAllText(noMountPath, Encoding.UTF8);

        WritePayload(NewAddonId);
        File.WriteAllText(manifestPath, "{ malformed", new UTF8Encoding(false));
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();

        Assert.Equal(before, File.ReadAllText(noMountPath, Encoding.UTF8));
        Assert.DoesNotContain(
            NewAddonId,
            manager.GetConfiguration().KnownSubscribedAddonIds);
        Assert.DoesNotContain(
            NewAddonId,
            manager.GetConfiguration().SubscriptionFirstSeenAtUtc.Keys);
    }

    [Fact]
    public async Task WorkshopRefresh_NewSubscriptionDoesNotOverwriteUnrelatedGmodTransition()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await SetSubscribeStateAsync(manager, AddonState.Disabled);
        Assert.False(manager.GetActualAddonEnabledState(ExistingAddonId));

        // GMod/user enables an existing addon outside GAM while a different ID
        // becomes newly subscribed.
        WriteDisabledIds();
        SubscribeNewAddon();
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();

        Assert.True(manager.GetActualAddonEnabledState(ExistingAddonId));
        Assert.False(manager.GetActualAddonEnabledState(NewAddonId));
        Assert.Equal([NewAddonId], ReadDisabledIds().OrderBy(id => id));
    }

    [Fact]
    public async Task MalformedRuntimeSnapshotDefersNewSubscriptionAndRestartRetriesIt()
    {
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            await SetSubscribeStateAsync(manager, AddonState.Disabled);

            SubscribeNewAddon();
            const string malformed = "not an addonnomount document";
            File.WriteAllText(noMountPath, malformed, new UTF8Encoding(false));
            manager.InvalidateWorkshopScanCache();
            await manager.ScanWorkshopFolderAsync();

            Assert.Equal(malformed, File.ReadAllText(noMountPath, Encoding.UTF8));
            Assert.Contains(
                NewAddonId,
                manager.GetConfiguration().SubscriptionFirstSeenAtUtc.Keys);
            Assert.DoesNotContain(
                NewAddonId,
                manager.GetConfiguration().LastGamAppliedAddonStates.Keys);
        }

        WriteDisabledIds(ExistingAddonId);
        using var restarted = CreateManager();
        await restarted.InitializeAsync();

        Assert.False(restarted.GetActualAddonEnabledState(NewAddonId));
        Assert.False(restarted.GetConfiguration().LastGamAppliedAddonStates[NewAddonId]);
    }

    private AddonManager CreateManager()
    {
        return new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        })
        {
            StateMatchTimeout = TimeSpan.Zero
        };
    }

    private static async Task SetSubscribeStateAsync(
        AddonManager manager,
        AddonState state)
    {
        var subscribe = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId);
        await manager.ApplyAssetDefaultStateAsync(subscribe.Id, state);
    }

    private void SubscribeNewAddon()
    {
        WritePayload(NewAddonId);
        WorkshopManifestTestData.Write(
            rootPath,
            ExistingAddonId,
            NewAddonId);
    }

    private void WritePayload(string addonId)
    {
        var payloadPath = Path.Combine(
            workshopPath,
            addonId,
            "lua",
            "payload.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllText(payloadPath, "payload", new UTF8Encoding(false));
    }

    private void WriteDisabledIds(params string[] addonIds)
    {
        _ = new GmodAddonStateStore(gmodRootPath).WriteDisabledIds(addonIds);
    }

    private HashSet<string> ReadDisabledIds()
    {
        return new GmodAddonStateStore(gmodRootPath).GetDisabledIds();
    }

    private Configuration ReadPersistedConfiguration()
    {
        var json = File.ReadAllText(
            Path.Combine(appDataPath, "config.json"),
            Encoding.UTF8);
        return JsonConvert.DeserializeObject<Configuration>(json)
            ?? throw new InvalidOperationException(
                "Persisted configuration could not be deserialized.");
    }

    public void Dispose()
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(25);
            }
        }
    }
}
