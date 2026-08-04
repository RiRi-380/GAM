using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Tests;

public sealed class DeferredRuntimeApplyTests : IDisposable
{
    private const string ExistingAddonId = "100";

    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;

    public DeferredRuntimeApplyTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-deferred-runtime-apply-tests-" + Guid.NewGuid().ToString("N"));
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

        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.Combine(gmodRootPath, "garrysmod", "cfg"));
        WritePayload();
        WriteAddonJson(["Fun"]);
        manifestPath = WorkshopManifestTestData.Write(rootPath, ExistingAddonId);
    }

    [Fact]
    public async Task Initialize_SmartMembershipChangeWhileGmodRunning_QueuesWhenProviderArrives()
    {
        using var restarted = await PrepareDeferredSmartMembershipChangeAsync();

        Assert.True(restarted.GetConfiguration().PendingRuntimeApplyRequired);
        Assert.True(ReadPersistedConfiguration().PendingRuntimeApplyRequired);

        var pending = new PendingChangeManager(restarted, appDataPath);

        Assert.Equal(1, pending.GetPendingChangeCount());
        Assert.False(restarted.GetConfiguration().PendingRuntimeApplyRequired);
        Assert.False(ReadPersistedConfiguration().PendingRuntimeApplyRequired);
    }

    [Fact]
    public async Task Initialize_DeferredIntentSurvivesProcessRestartBeforeProviderArrives()
    {
        using (var interrupted = await PrepareDeferredSmartMembershipChangeAsync())
        {
            Assert.True(interrupted.GetConfiguration().PendingRuntimeApplyRequired);
        }

        using var restarted = CreateManager();
        restarted.GmodRunningProvider = () => true;
        await restarted.InitializeAsync();

        Assert.True(restarted.GetConfiguration().PendingRuntimeApplyRequired);

        var pending = new PendingChangeManager(restarted, appDataPath);

        Assert.Equal(1, pending.GetPendingChangeCount());
        Assert.False(restarted.GetConfiguration().PendingRuntimeApplyRequired);

        Assert.False(ReadPersistedConfiguration().PendingRuntimeApplyRequired);
    }

    [Fact]
    public async Task Initialize_DeferredQueueFailure_RetainsIntentUntilSuccessfulRetry()
    {
        using var restarted = await PrepareDeferredSmartMembershipChangeAsync();
        var attempts = 0;
        var successfulQueues = 0;

        restarted.QueueRuntimeApplyProvider = () =>
        {
            attempts++;
            throw new IOException("simulated durable queue failure");
        };

        Assert.Equal(1, attempts);
        Assert.Equal(0, successfulQueues);
        Assert.True(restarted.GetConfiguration().PendingRuntimeApplyRequired);

        restarted.QueueRuntimeApplyProvider = () =>
        {
            attempts++;
            successfulQueues++;
        };

        Assert.Equal(2, attempts);
        Assert.Equal(1, successfulQueues);
        Assert.False(restarted.GetConfiguration().PendingRuntimeApplyRequired);

        // Replacing the provider after a successful queue must not enqueue the
        // already-recorded intent again.
        restarted.QueueRuntimeApplyProvider = () => successfulQueues++;
        Assert.Equal(1, successfulQueues);
    }

    [Fact]
    public async Task RuntimeQueueFailure_PersistsIntentBeforePropagating_AndRetries()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.GmodRunningProvider = () => true;
        manager.QueueRuntimeApplyProvider = () =>
            throw new IOException("simulated pending marker failure");

        await Assert.ThrowsAsync<IOException>(
            () => manager.UpdateAddonStatesWithResultAsync());

        Assert.True(manager.GetConfiguration().PendingRuntimeApplyRequired);
        Assert.True(ReadPersistedConfiguration().PendingRuntimeApplyRequired);

        var successfulQueues = 0;
        manager.QueueRuntimeApplyProvider = () => successfulQueues++;

        Assert.Equal(1, successfulQueues);
        Assert.False(manager.GetConfiguration().PendingRuntimeApplyRequired);
        Assert.False(ReadPersistedConfiguration().PendingRuntimeApplyRequired);
    }

    private async Task<AddonManager> PrepareDeferredSmartMembershipChangeAsync()
    {
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            var subscribe = manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.SubscribeId);
            await manager.ApplyAssetDefaultStateAsync(
                subscribe.Id,
                AddonState.Disabled);

            var smart = await manager.CreateSmartAssetAsync(
                "Fun Smart Asset",
                new AssetMembershipRule(AssetMembershipRuleKind.Tag, "Fun"));

            Assert.Contains(ExistingAddonId, smart.Addons);
            Assert.True(manager.GetFinalAddonStates()[ExistingAddonId]);
            Assert.True(manager.GetActualAddonEnabledState(ExistingAddonId));
        }

        // The condition is conclusively lost between runs. Initialize removes
        // the member and computes OFF, but GMod is running before the pending
        // provider has been constructed.
        WriteAddonJson([]);
        File.SetLastWriteTimeUtc(
            GetAddonJsonPath(),
            DateTime.UtcNow.AddSeconds(2));

        var restarted = CreateManager();
        restarted.GmodRunningProvider = () => true;
        await restarted.InitializeAsync();

        var smartAfterRestart = restarted.GetConfiguration().Assets.Single(
            asset => asset.IsSmart);
        Assert.DoesNotContain(ExistingAddonId, smartAfterRestart.Addons);
        Assert.False(restarted.GetFinalAddonStates()[ExistingAddonId]);
        Assert.True(restarted.GetActualAddonEnabledState(ExistingAddonId));
        return restarted;
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

    private void WritePayload()
    {
        var payloadPath = Path.Combine(
            workshopPath,
            ExistingAddonId,
            "lua",
            "payload.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllText(payloadPath, "payload", new UTF8Encoding(false));
    }

    private void WriteAddonJson(IEnumerable<string> tags)
    {
        var path = GetAddonJsonPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonConvert.SerializeObject(new
            {
                title = "Existing addon",
                type = "weapon",
                tags = tags.ToArray()
            }),
            new UTF8Encoding(false));
    }

    private string GetAddonJsonPath()
    {
        return Path.Combine(workshopPath, ExistingAddonId, "addon.json");
    }

    private Configuration ReadPersistedConfiguration()
    {
        return JsonConvert.DeserializeObject<Configuration>(
                   File.ReadAllText(Path.Combine(appDataPath, "config.json")))
               ?? throw new InvalidOperationException(
                   "The persisted test configuration could not be read.");
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
