using System.Reflection;
using System.Globalization;
using Avalonia.Headless.XUnit;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using Xunit;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonItemViewModelStateDisplayTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-ui-state-tests",
        Guid.NewGuid().ToString("N"));

    static AddonItemViewModelStateDisplayTests()
    {
        LocalizationManager.Instance.ChangeLanguage("en-US");
    }

    public AddonItemViewModelStateDisplayTests()
    {
        LocalizationManager.Instance.ChangeLanguage("en-US");
    }

    [Fact]
    public async Task ActualAndDesiredStatesRemainDistinctWhileRuntimeApplyIsPending()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);
        var resolved = CreateResolvedState(
            desiredEnabled: false,
            AddonStateResolutionReason.Excluded,
            excludedBy: new[] { "Global blacklist" });

        addon.RefreshRuntimeState(resolved, actualState: true, hasQueuedRuntimeApply: true);

        Assert.True(addon.ActualEnabled);
        Assert.False(addon.DesiredEnabled);
        Assert.Equal("Enabled", addon.ActualStateText);
        Assert.Equal("Excluded", addon.DesiredStateText);
        Assert.Contains("Global blacklist", addon.StateReasonText);
        Assert.True(addon.IsRuntimeApplyPending);
        Assert.Equal("Applies after GMod exits", addon.PendingStateText);
        Assert.Equal("#303030", addon.BorderColor);
    }

    [Fact]
    public async Task ActualOffCardUsesResolvedReasonOnlyToExplainItsOffState()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);
        var excluded = CreateResolvedState(
            desiredEnabled: false,
            AddonStateResolutionReason.Excluded,
            excludedBy: new[] { "Capture" });

        addon.RefreshRuntimeState(excluded, actualState: false, hasQueuedRuntimeApply: false);

        Assert.Equal("Disabled", addon.ActualStateText);
        Assert.Equal("Excluded", addon.DesiredStateText);
        Assert.True(addon.IsDisplayOff);
        Assert.True(addon.IsExcludedAnywhere);
        Assert.False(addon.IsRuntimeApplyPending);
        Assert.Equal("#F44336", addon.BorderColor);
    }

    [Fact]
    public async Task ResolutionReasonNamesSubscribeAndContributingAssets()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);
        var resolved = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: true,
            desiredEnabled: true,
            enabledBySubscribe: true,
            reason: AddonStateResolutionReason.Enabled,
            enabledByAssets: new[]
            {
                new ResolvedAddonStateSource("fps", "FPS"),
                new ResolvedAddonStateSource("recording", "Recording")
            },
            excludedByAssets: Array.Empty<ResolvedAddonStateSource>());

        addon.RefreshRuntimeState(resolved, actualState: true, hasQueuedRuntimeApply: false);

        Assert.Equal("Enabled", addon.DesiredStateText);
        Assert.Contains("Subscribe", addon.StateReasonText);
        Assert.Contains("FPS", addon.StateReasonText);
        Assert.Contains("Recording", addon.StateReasonText);
    }

    [Fact]
    public async Task SubscribeCardNamesTheGmodAuthorityWithoutRedundantCrossAssetChip()
    {
        using var manager = await CreateManagerAsync();
        var subscribe = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.SetWholeState(AddonState.Disabled);
        using var subscribeViewModel = new AssetItemViewModel(
            subscribe,
            manager,
            null!,
            null!);
        using var addon = CreateAddonViewModel(manager);
        var resolved = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: true,
            desiredEnabled: true,
            enabledBySubscribe: false,
            reason: AddonStateResolutionReason.Enabled,
            enabledByAssets:
            [
                new ResolvedAddonStateSource(
                    SystemAssetDefinitions.GmodDisabledId,
                    SystemAssetDefinitions.GmodDisabledName)
            ],
            excludedByAssets: Array.Empty<ResolvedAddonStateSource>());
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;

        try
        {
            LocalizationManager.Instance.ChangeLanguage("en-US");
            addon.SetCurrentAsset(subscribeViewModel);
            addon.RefreshRuntimeState(
                resolved,
                actualState: true,
                hasQueuedRuntimeApply: false);

            Assert.Equal("GMod: Enabled", addon.ActualStateBadgeText);
            Assert.False(addon.HasAssetContextNotice);
            Assert.Empty(addon.AssetContextNoticeText);
            Assert.Contains(SystemAssetDefinitions.GmodDisabledName, addon.RuntimeStateTooltip);

            LocalizationManager.Instance.ChangeLanguage("ja-JP");
            Assert.Equal("GMod: 有効", addon.ActualStateBadgeText);
            Assert.Empty(addon.AssetContextNoticeText);
            Assert.Contains("GAMの希望状態", addon.RuntimeStateTooltip);
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task DisabledCustomAssetStillExplainsAnotherEnabledAsset()
    {
        using var manager = await CreateManagerAsync();
        var neutral = new Asset("Neutral custom")
        {
            Id = "neutral-custom"
        };
        neutral.SetWholeState(AddonState.Disabled);
        using var neutralViewModel = new AssetItemViewModel(
            neutral,
            manager,
            null!,
            null!);
        using var addon = CreateAddonViewModel(manager);
        var resolved = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: true,
            desiredEnabled: true,
            enabledBySubscribe: false,
            reason: AddonStateResolutionReason.Enabled,
            enabledByAssets:
            [
                new ResolvedAddonStateSource("fps", "FPS")
            ],
            excludedByAssets: Array.Empty<ResolvedAddonStateSource>());

        addon.SetCurrentAsset(neutralViewModel);
        addon.RefreshRuntimeState(
            resolved,
            actualState: true,
            hasQueuedRuntimeApply: false);

        Assert.True(addon.HasAssetContextNotice);
        Assert.Equal("Enabled by another Asset", addon.AssetContextNoticeText);
    }

    [Fact]
    public async Task CardContextNoticePrioritizesQueuedApplyOverAssetContribution()
    {
        using var manager = await CreateManagerAsync();
        var subscribe = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.SetWholeState(AddonState.Disabled);
        using var subscribeViewModel = new AssetItemViewModel(
            subscribe,
            manager,
            null!,
            null!);
        using var addon = CreateAddonViewModel(manager);

        addon.SetCurrentAsset(subscribeViewModel);
        addon.RefreshRuntimeState(
            CreateResolvedState(
                desiredEnabled: false,
                AddonStateResolutionReason.NoEnabledSource),
            actualState: true,
            hasQueuedRuntimeApply: true);

        Assert.Equal("Pending apply", addon.AssetContextNoticeText);
        Assert.True(addon.HasAssetContextNotice);
    }

    [Fact]
    public async Task SubscribeEnabledStillExplainsExclusionOverride()
    {
        using var manager = await CreateManagerAsync();
        var subscribe = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.SetWholeState(AddonState.Enabled);
        using var subscribeViewModel = new AssetItemViewModel(
            subscribe,
            manager,
            null!,
            null!);
        using var addon = CreateAddonViewModel(manager);
        var resolved = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: true,
            desiredEnabled: false,
            enabledBySubscribe: true,
            reason: AddonStateResolutionReason.Excluded,
            enabledByAssets: Array.Empty<ResolvedAddonStateSource>(),
            excludedByAssets:
            [
                new ResolvedAddonStateSource("capture", "Capture")
            ]);

        addon.SetCurrentAsset(subscribeViewModel);
        addon.RefreshRuntimeState(
            resolved,
            actualState: false,
            hasQueuedRuntimeApply: false);

        Assert.True(addon.HasAssetContextNotice);
        Assert.Equal("Exclusion takes priority", addon.AssetContextNoticeText);
    }

    [Fact]
    public async Task SubscribeExcludeAllReasonNamesEveryExclusionAuthority()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);
        var resolved = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: true,
            desiredEnabled: false,
            enabledBySubscribe: false,
            reason: AddonStateResolutionReason.Excluded,
            enabledByAssets: new[]
            {
                new ResolvedAddonStateSource("fps", "FPS")
            },
            excludedByAssets: new[]
            {
                new ResolvedAddonStateSource(
                    SystemAssetDefinitions.SubscribeId,
                    SystemAssetDefinitions.SubscribeName),
                new ResolvedAddonStateSource(
                    SystemAssetDefinitions.GmodDisabledId,
                    SystemAssetDefinitions.GmodDisabledName)
            });
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;

        try
        {
            LocalizationManager.Instance.ChangeLanguage("en-US");
            addon.RefreshRuntimeState(
                resolved,
                actualState: false,
                hasQueuedRuntimeApply: false);
            Assert.Contains("All subscribed addons excluded", addon.StateReasonText);
            Assert.Contains(SystemAssetDefinitions.SubscribeName, addon.StateReasonText);
            Assert.Contains(SystemAssetDefinitions.GmodDisabledName, addon.StateReasonText);

            LocalizationManager.Instance.ChangeLanguage("ja-JP");
            Assert.Contains("すべて除外", addon.StateReasonText);
            Assert.Contains(SystemAssetDefinitions.SubscribeName, addon.StateReasonText);
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task ActualStateFilterDoesNotTreatUnknownAsEnabled()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);
        addon.RefreshRuntimeState(
            CreateResolvedState(true, AddonStateResolutionReason.Enabled),
            actualState: null,
            hasQueuedRuntimeApply: false);

        Assert.False(InvokeMatchesAddonStateFilter(addon, filterIndex: 1));
        Assert.False(InvokeMatchesAddonStateFilter(addon, filterIndex: 2));
        Assert.True(InvokeMatchesAddonStateFilter(addon, filterIndex: 0));
    }

    [Fact]
    public async Task QueuedApplyIsVisibleWhenActualStateCannotBeRead()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);

        addon.RefreshRuntimeState(
            CreateResolvedState(false, AddonStateResolutionReason.NoEnabledSource),
            actualState: null,
            hasQueuedRuntimeApply: true);

        Assert.Equal("Unknown", addon.ActualStateText);
        Assert.True(addon.IsRuntimeApplyPending);
        Assert.Equal("Applies after GMod exits", addon.PendingStateText);
    }

    [Fact]
    public async Task RetainedUnavailableReferenceIsMarkedMissing()
    {
        using var manager = await CreateManagerAsync();
        using var addon = new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "123456789",
                Title = "Missing Addon",
                FolderPath = string.Empty,
                IsAvailable = false,
                IsDownloadPending = false,
                NeedsTitleUpdate = false
            },
            manager);

        Assert.False(addon.IsAvailable);
        Assert.True(addon.IsMissing);
        Assert.Equal(0.55, addon.CardOpacity);

        addon.UpdateFromWorkshopAddon(new WorkshopAddon
        {
            Id = addon.AddonId,
            Title = addon.Title,
            FolderPath = string.Empty,
            IsAvailable = true,
            IsDownloadPending = false,
            NeedsTitleUpdate = false
        });

        Assert.True(addon.IsAvailable);
        Assert.False(addon.IsMissing);
        Assert.Equal(1.0, addon.CardOpacity);
    }

    [Fact]
    public async Task LocalAddonUsesReadOnlyGmodPresentationInsteadOfSteamState()
    {
        using var manager = await CreateManagerAsync();
        using var addon = new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "local_123456789abc",
                Title = "Local test",
                FolderPath = rootPath,
                IsLocal = true,
                IsAvailable = true,
                IsEnabled = true,
                NeedsTitleUpdate = false
            },
            manager);
        var notSubscribed = new ResolvedAddonState(
            addon.AddonId,
            isSubscribed: false,
            desiredEnabled: false,
            enabledBySubscribe: false,
            AddonStateResolutionReason.NotSubscribed,
            Array.Empty<ResolvedAddonStateSource>(),
            Array.Empty<ResolvedAddonStateSource>());

        addon.RefreshRuntimeState(
            notSubscribed,
            actualState: false,
            hasQueuedRuntimeApply: true);

        Assert.True(addon.IsLocal);
        Assert.True(addon.ActualEnabled);
        Assert.Equal("Enabled", addon.ActualStateText);
        Assert.Equal("Managed by GMod", addon.DesiredStateText);
        Assert.Equal("Local addon (read-only in GAM)", addon.StateReasonText);
        Assert.False(addon.IsRuntimeApplyPending);
        Assert.False(addon.IsMissing);
        Assert.False(addon.HasWorkshopId);
        Assert.Equal(1.0, addon.CardOpacity);
    }

    [Fact]
    public async Task SortPreferenceDefaultsToRecentDescendingAndPersistsGlobally()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-appdata"));
        var settingsPath = Path.Combine(rootPath, "addon-sort.json");

        using (var grid = new AddonGridViewModel(
                   manager,
                   pendingChangeManager,
                   processWatcher,
                   settingsPath))
        {
            Assert.Equal(0, grid.SelectedSortModeIndex);
            Assert.Equal(
                new[] { "Subscription date", "Name", "Size", "Workshop updated" },
                grid.SortModeOptions);
            Assert.Equal("Descending ↓", grid.SortDirectionLabel);

            grid.SelectedSortModeIndex = (int)AddonSortMode.Size;
            using var execution = grid.ToggleSortDirectionCommand.Execute().Subscribe();
            Assert.Equal("Ascending ↑", grid.SortDirectionLabel);

            var previousLanguage = LocalizationManager.Instance.CurrentLanguage;
            try
            {
                LocalizationManager.Instance.ChangeLanguage("ja-JP");
                Assert.Equal(
                    new[] { "購読日時", "名前", "容量", "Workshop更新" },
                    grid.SortModeOptions);
                Assert.Equal("昇順 ↑", grid.SortDirectionLabel);
            }
            finally
            {
                LocalizationManager.Instance.ChangeLanguage(previousLanguage);
            }
        }

        using var restoredGrid = new AddonGridViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            settingsPath);
        Assert.Equal((int)AddonSortMode.Size, restoredGrid.SelectedSortModeIndex);
        Assert.Equal("Ascending ↑", restoredGrid.SortDirectionLabel);
    }

    [AvaloniaFact]
    public async Task AssetSwitchUpdatesVisibleMembershipWithoutWaitingForSearchDebounce()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "selection-grid-appdata"));
        using var grid = new AddonGridViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            Path.Combine(rootPath, "selection-grid-sort.json"));
        var asset = new Asset("Group child")
        {
            Id = "group-child",
            Addons = ["100"]
        };
        manager.GetConfiguration().Assets.Add(asset);
        using var assetViewModel = new AssetItemViewModel(
            asset,
            manager,
            pendingChangeManager,
            processWatcher);

        grid.AllAddons.Add(CreateSortableAddon(
            manager,
            "100",
            "Included",
            100,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch));
        grid.AllAddons.Add(CreateSortableAddon(
            manager,
            "200",
            "Not included",
            200,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch));
        grid.ApplyFilter();
        Assert.Equal(2, grid.FilteredAddons.Count);

        grid.SetCurrentAsset(assetViewModel);

        var visible = Assert.Single(grid.FilteredAddons);
        Assert.Equal("100", visible.AddonId);
    }

    [AvaloniaFact]
    public async Task GridSortControlsReorderTheVisibleListForEveryModeAndDirection()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "sort-grid-appdata"));
        using var grid = new AddonGridViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            Path.Combine(rootPath, "sort-grid.json"));

        var alpha = CreateSortableAddon(
            manager,
            "3",
            "Alpha",
            300,
            new DateTime(2026, 1, 1, 10, 0, 0, 900, DateTimeKind.Utc).AddTicks(9),
            new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9));
        var bravo = CreateSortableAddon(
            manager,
            "2",
            "Bravo",
            100,
            new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(8),
            new DateTime(2026, 1, 1, 10, 0, 0, 100, DateTimeKind.Utc).AddTicks(1));
        var charlie = CreateSortableAddon(
            manager,
            "1",
            "Charlie",
            200,
            new DateTime(2026, 1, 1, 10, 0, 0, 100, DateTimeKind.Utc).AddTicks(1),
            new DateTime(2026, 1, 1, 10, 0, 0, 900, DateTimeKind.Utc).AddTicks(9));

        grid.AllAddons.Add(alpha);
        grid.AllAddons.Add(bravo);
        grid.AllAddons.Add(charlie);
        grid.ApplyFilter();

        AssertModeAndBothDirections(
            AddonSortMode.RecentlySubscribed,
            ["Alpha", "Charlie", "Bravo"],
            ["Bravo", "Charlie", "Alpha"]);
        AssertModeAndBothDirections(
            AddonSortMode.Name,
            ["Charlie", "Bravo", "Alpha"],
            ["Alpha", "Bravo", "Charlie"]);
        AssertModeAndBothDirections(
            AddonSortMode.Size,
            ["Alpha", "Charlie", "Bravo"],
            ["Bravo", "Charlie", "Alpha"]);
        AssertModeAndBothDirections(
            AddonSortMode.WorkshopUpdated,
            ["Charlie", "Bravo", "Alpha"],
            ["Alpha", "Bravo", "Charlie"]);

        void AssertModeAndBothDirections(
            AddonSortMode mode,
            string[] descending,
            string[] ascending)
        {
            grid.SelectedSortModeIndex = (int)mode;
            Assert.Equal(descending, grid.FilteredAddons.Select(addon => addon.Title));

            using (grid.ToggleSortDirectionCommand.Execute().Subscribe())
            {
                Assert.Equal(ascending, grid.FilteredAddons.Select(addon => addon.Title));
            }

            using (grid.ToggleSortDirectionCommand.Execute().Subscribe())
            {
                Assert.Equal(descending, grid.FilteredAddons.Select(addon => addon.Title));
            }
        }
    }

    [Fact]
    public async Task SortPresentationDisplaysTheExactActiveTimestampKey()
    {
        using var manager = await CreateManagerAsync();
        var firstSeenUtc = new DateTime(
            2025,
            12,
            31,
            23,
            59,
            58,
            987,
            DateTimeKind.Utc).AddTicks(6);
        var workshopUpdatedUtc = new DateTime(
            2026,
            1,
            1,
            0,
            0,
            1,
            123,
            DateTimeKind.Utc).AddTicks(4);
        using var addon = new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "123456789",
                Title = "Timestamped",
                FolderPath = string.Empty,
                LastUpdated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FirstSeenSubscribedAtUtc = firstSeenUtc,
                WorkshopUpdatedAtUtc = workshopUpdatedUtc,
                NeedsTitleUpdate = false
            },
            manager);

        addon.SetSortPresentationMode(AddonSortMode.RecentlySubscribed);
        Assert.Equal(
            firstSeenUtc.ToLocalTime().ToString(
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture),
            addon.SortValueText);

        addon.SetSortPresentationMode(AddonSortMode.WorkshopUpdated);
        Assert.Equal(
            workshopUpdatedUtc.ToLocalTime().ToString(
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture),
            addon.SortValueText);

        addon.SetSortPresentationMode(AddonSortMode.Name);
        Assert.Equal(string.Empty, addon.SortValueText);

        addon.SetSortPresentationMode(AddonSortMode.Size);
        Assert.Equal(string.Empty, addon.SortValueText);
    }

    [Fact]
    public async Task RecentSortPresentationDoesNotInventBaselineSubscriptionTime()
    {
        using var manager = await CreateManagerAsync();
        using var addon = CreateAddonViewModel(manager);

        addon.SetSortPresentationMode(AddonSortMode.RecentlySubscribed);

        Assert.Equal("Subscription date unknown", addon.SortValueText);
    }

    [Fact]
    public async Task MetadataRefreshUpdatesSortPresentationSizeAndNotifiesGrid()
    {
        using var manager = await CreateManagerAsync();
        using var addon = new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "123456789",
                Title = "Before",
                FolderPath = string.Empty,
                Size = 10,
                LastUpdated = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                WorkshopUpdatedAtUtc = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                NeedsTitleUpdate = false
            },
            manager);
        addon.SetSortPresentationMode(AddonSortMode.WorkshopUpdated);
        var notificationCount = 0;
        addon.SortSourceChanged += (_, _) => notificationCount++;
        var refreshedWorkshopTime = new DateTime(
            2026,
            2,
            3,
            4,
            5,
            6,
            DateTimeKind.Utc);

        addon.UpdateFromWorkshopAddon(new WorkshopAddon
        {
            Id = addon.AddonId,
            Title = "After",
            FolderPath = string.Empty,
            Size = 2048,
            LastUpdated = refreshedWorkshopTime.AddMinutes(-1),
            WorkshopUpdatedAtUtc = refreshedWorkshopTime,
            NeedsTitleUpdate = false
        });

        Assert.Equal(1, notificationCount);
        Assert.Equal("After", addon.Title);
        Assert.Equal("2 KB", addon.FileSizeText);
        Assert.Equal(
            refreshedWorkshopTime.ToLocalTime().ToString(
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture),
            addon.SortValueText);

        addon.UpdateTitle("Later title");

        Assert.Equal(2, notificationCount);
    }

    private async Task<AddonManager> CreateManagerAsync()
    {
        var instancePath = Path.Combine(rootPath, Guid.NewGuid().ToString("N"));
        var workshopPath = Path.Combine(instancePath, "workshop", "content", "4000");
        var appDataPath = Path.Combine(instancePath, "appdata");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);

        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });

        await manager.InitializeAsync();
        return manager;
    }

    private static AddonItemViewModel CreateAddonViewModel(AddonManager manager)
    {
        return new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "123456789",
                Title = "Test Addon",
                FolderPath = string.Empty,
                NeedsTitleUpdate = false
            },
            manager);
    }

    private static AddonItemViewModel CreateSortableAddon(
        AddonManager manager,
        string id,
        string title,
        long size,
        DateTime firstSeenUtc,
        DateTime workshopUpdatedUtc)
    {
        return new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = id,
                Title = title,
                FolderPath = string.Empty,
                Size = size,
                FirstSeenSubscribedAtUtc = firstSeenUtc,
                LastUpdated = workshopUpdatedUtc,
                WorkshopUpdatedAtUtc = workshopUpdatedUtc,
                NeedsTitleUpdate = false
            },
            manager);
    }

    private static ResolvedAddonState CreateResolvedState(
        bool desiredEnabled,
        AddonStateResolutionReason reason,
        IReadOnlyList<string>? enabledBy = null,
        IReadOnlyList<string>? excludedBy = null)
    {
        return new ResolvedAddonState(
            "123456789",
            isSubscribed: true,
            desiredEnabled,
            enabledBySubscribe: reason == AddonStateResolutionReason.Enabled,
            reason,
            (enabledBy ?? Array.Empty<string>())
                .Select((name, index) => new ResolvedAddonStateSource($"enabled-{index}", name))
                .ToArray(),
            (excludedBy ?? Array.Empty<string>())
                .Select((name, index) => new ResolvedAddonStateSource($"excluded-{index}", name))
                .ToArray());
    }

    private static bool InvokeMatchesAddonStateFilter(AddonItemViewModel addon, int filterIndex)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "MatchesAddonStateFilter",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, new object[] { addon, filterIndex }));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            DeleteDirectoryWithRetry(rootPath);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }
}
