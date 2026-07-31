using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class PendingChangeManagerTests
{
    [Theory]
    [InlineData("apply_states")]
    [InlineData("APPLY_STATES")]
    [InlineData(" apply_states ")]
    public void ParseActionType_ApplyStates_ReturnsApplyStates(string action)
    {
        var actual = PendingChangeManager.ParseActionType(action);
        Assert.Equal(PendingChangeActionType.ApplyStates, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("delete")]
    [InlineData("enable")]
    [InlineData("disable_asset")]
    public void ParseActionType_UnknownAction_ReturnsUnknown(string? action)
    {
        var actual = PendingChangeManager.ParseActionType(action);
        Assert.Equal(PendingChangeActionType.Unknown, actual);
    }

    [Fact]
    public void QueueChanges_CoalescesEveryOperationIntoOneFullApplyMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "gam-pending-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.Combine(gmod, "garrysmod", "cfg"));

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                DisableCacheScan = true
            });
            var pending = new PendingChangeManager(manager, appData);

            pending.QueueChange(new GmodAddonManager.Core.Models.AddonChange("enable", "100"));
            pending.QueueChange(new GmodAddonManager.Core.Models.AddonChange("disable", "200"));
            pending.QueueApplyStates();

            var marker = Assert.Single(pending.GetPendingChanges());
            Assert.Equal("apply_states", marker.Action);
            Assert.Equal("*", marker.AddonId);
            Assert.Equal(1, pending.GetPendingChangeCount());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task QueueDuringApply_ReplacementMarkerSurvivesWhenTimestampMatchesAppliedMarker()
    {
        const string addonId = "100";
        const string unknownId = "999";
        var root = Path.Combine(
            Path.GetTempPath(),
            "gam-pending-generation-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        var workshopManifest = WorkshopManifestTestData.Write(root, addonId);
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(noMount, "\"addonnomount\"\n{\n}\n");

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths = [workshopManifest],
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            File.WriteAllText(
                noMount,
                "\"addonnomount\"\n{\n\t\"1\"\t\t\"100\"\n\t\"2\"\t\t\"999\"\n}\n");

            var pending = new PendingChangeManager(manager, appData);
            pending.QueueApplyStates();
            var appliedMarker = Assert.Single(pending.GetPendingChanges());
            var fixedTimestamp = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);
            appliedMarker.Timestamp = fixedTimestamp;

            GmodAddonManager.Core.Models.AddonChange? replacementMarker = null;
            pending.BeforeRuntimeApplyAsync = () =>
            {
                pending.QueueApplyStates();
                replacementMarker = Assert.Single(pending.GetPendingChanges());
                replacementMarker.Timestamp = fixedTimestamp;
                return Task.CompletedTask;
            };

            await pending.ApplyPendingChangesAsync();

            Assert.NotNull(replacementMarker);
            Assert.NotSame(appliedMarker, replacementMarker);
            Assert.Same(replacementMarker, Assert.Single(pending.GetPendingChanges()));
            Assert.True(pending.HasPendingChanges());
            Assert.DoesNotContain(addonId, File.ReadAllText(noMount), StringComparison.Ordinal);
            Assert.Contains(unknownId, File.ReadAllText(noMount), StringComparison.Ordinal);
            var persistedPending = File.ReadAllText(Path.Combine(appData, "pending.json"));
            Assert.Contains("\"action\": \"apply_states\"", persistedPending, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunningGmod_DefersAndAppliesOnlyLatestFullDesiredState()
    {
        const string addonId = "100";
        const string unknownId = "999";
        var root = Path.Combine(Path.GetTempPath(), "gam-pending-runtime-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        var workshopManifest = WorkshopManifestTestData.Write(root, addonId);
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(
            noMount,
            "\"addonnomount\"\n{\n\t\"1\"\t\t\"999\"\n}\n");

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths = [workshopManifest],
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            manager.GetConfiguration().AddonMetadata[addonId] =
                new GmodAddonManager.Core.Models.WorkshopAddon(addonId, string.Empty);
            var asset = new GmodAddonManager.Core.Models.Asset("Test")
            {
                Addons = [addonId]
            };
            manager.GetConfiguration().Assets.Add(asset);
            var pending = new PendingChangeManager(manager, appData);
            var running = true;
            manager.GmodRunningProvider = () => running;
            File.WriteAllText(
                noMount,
                "\"addonnomount\"\n{\n\t\"1\"\t\t\"100\"\n\t\"2\"\t\t\"999\"\n}\n");

            asset.SetWholeState(GmodAddonManager.Core.Models.AddonState.Excluded);
            await manager.UpdateAddonStatesAsync();
            asset.SetWholeState(GmodAddonManager.Core.Models.AddonState.Enabled);
            await manager.UpdateAddonStatesAsync();

            Assert.Equal(1, pending.GetPendingChangeCount());
            var whileRunning = File.ReadAllText(noMount);
            Assert.Contains(addonId, whileRunning, StringComparison.Ordinal);
            Assert.Contains(unknownId, whileRunning, StringComparison.Ordinal);

            running = false;
            await pending.ApplyPendingChangesAsync();

            Assert.False(pending.HasPendingChanges());
            var afterExit = File.ReadAllText(noMount);
            Assert.DoesNotContain(addonId, afterExit, StringComparison.Ordinal);
            Assert.Contains(unknownId, afterExit, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplyPendingChangesAsync_UsesInjectedGmodRunningAuthority()
    {
        const string addonId = "100";
        var root = Path.Combine(
            Path.GetTempPath(),
            "gam-pending-authority-gate-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        var manifest = WorkshopManifestTestData.Write(root, addonId);
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(noMount, "\"addonnomount\"\n{\n\t\"1\"\t\t\"100\"\n}\n");

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths = [manifest],
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            manager.GetConfiguration().AddonMetadata[addonId] =
                new GmodAddonManager.Core.Models.WorkshopAddon(addonId, string.Empty);

            var isGmodRunning = true;
            manager.GmodRunningProvider = () => isGmodRunning;
            var pending = new PendingChangeManager(manager, appData);
            pending.QueueApplyStates();
            var beforeRuntimeApplyCount = 0;
            pending.BeforeRuntimeApplyAsync = () =>
            {
                beforeRuntimeApplyCount++;
                return Task.CompletedTask;
            };

            await pending.ApplyPendingChangesAsync();

            Assert.True(pending.HasPendingChanges());
            Assert.Equal(0, beforeRuntimeApplyCount);

            isGmodRunning = false;
            await pending.ApplyPendingChangesAsync();

            Assert.False(pending.HasPendingChanges());
            Assert.Equal(1, beforeRuntimeApplyCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedRuntimeWrite_KeepsPendingMarkerAndMalformedFile()
    {
        const string addonId = "100";
        var root = Path.Combine(
            Path.GetTempPath(),
            "gam-pending-failure-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        var manifest = WorkshopManifestTestData.Write(root, addonId);
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(noMount, "\"addonnomount\"\n{\n}\n");

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths = [manifest],
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            manager.GetConfiguration().AddonMetadata[addonId] =
                new GmodAddonManager.Core.Models.WorkshopAddon(addonId, string.Empty);
            var pending = new PendingChangeManager(manager, appData);
            pending.QueueApplyStates();
            const string malformed = "not a valid addonnomount document";
            File.WriteAllText(noMount, malformed);

            await pending.ApplyPendingChangesAsync();

            Assert.True(pending.HasPendingChanges());
            Assert.Equal(malformed, File.ReadAllText(noMount));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnavailableSubscriptionTruth_DoesNotApplyMetadataFallback()
    {
        const string addonId = "100";
        var root = Path.Combine(
            Path.GetTempPath(),
            "gam-pending-authority-tests-" + Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "workshop");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "GarrysMod");
        var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(
            noMount,
            "\"addonnomount\"\n{\n\t\"1\"\t\t\"100\"\n}\n");

        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths =
                    [Path.Combine(root, "missing-appworkshop_4000.acf")],
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            manager.GetConfiguration().AddonMetadata[addonId] =
                new GmodAddonManager.Core.Models.WorkshopAddon(addonId, string.Empty);
            var pending = new PendingChangeManager(manager, appData);
            pending.QueueApplyStates();

            await pending.ApplyPendingChangesAsync();

            Assert.True(pending.HasPendingChanges());
            Assert.Contains(addonId, File.ReadAllText(noMount), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
