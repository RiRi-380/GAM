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

    public static IEnumerable<object?[]> BorderColorCases()
    {
        yield return new object?[] { AddonState.Enabled, true, null, false, "#303030", "Enabled", false };
        yield return new object?[] { AddonState.Enabled, false, null, false, "#303030", "Enabled", false };
        yield return new object?[] { AddonState.Disabled, true, null, false, "#FF9800", "Disabled", false };
        yield return new object?[] { AddonState.Disabled, false, null, false, "#FF9800", "Disabled", false };
        yield return new object?[] { AddonState.Excluded, true, null, false, "#F44336", "Excluded", true };
        yield return new object?[] { AddonState.Excluded, false, null, false, "#F44336", "Excluded", false };
        yield return new object?[] { AddonState.Enabled, true, AddonState.Disabled, false, "#FF9800", "Disabled", false };
        yield return new object?[] { AddonState.Enabled, true, AddonState.Excluded, false, "#F44336", "Excluded", true };
        yield return new object?[] { AddonState.Disabled, true, AddonState.Excluded, false, "#F44336", "Excluded", true };
        yield return new object?[] { AddonState.Excluded, true, AddonState.Excluded, true, "#4A90E2", "Excluded", true };
    }

    [Theory]
    [MemberData(nameof(BorderColorCases))]
    public async Task BorderColorMatchesDisplayContract(
        AddonState localState,
        bool assetEnabled,
        AddonState? globalState,
        bool isSelected,
        string expectedBorderColor,
        string expectedStateText,
        bool expectedIsExcludedAnywhere)
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: assetEnabled, localState);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        if (globalState.HasValue)
        {
            addon.SetAddonStateMarkers(new Dictionary<string, AddonState>
            {
                ["addon-1"] = globalState.Value
            });
        }

        addon.SetCurrentAsset(assetViewModel);
        addon.IsSelected = isSelected;

        Assert.Equal(expectedIsExcludedAnywhere, addon.IsExcludedAnywhere);
        Assert.Equal(expectedBorderColor, addon.BorderColor);
        Assert.Equal(expectedStateText, addon.StateText);
    }

    [Fact]
    public async Task InactiveAssetExcludedAddonShowsLocalRedBorderOnly()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: false, AddonState.Excluded);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.False(addon.IsExcludedAnywhere);
        Assert.Equal("#F44336", addon.BorderColor);
        Assert.Equal("Excluded", addon.StateText);
    }

    [Fact]
    public async Task EnabledAssetExcludedAddonShowsEffectiveRedBorder()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Excluded);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.True(addon.IsExcludedAnywhere);
        Assert.Equal("#F44336", addon.BorderColor);
        Assert.Equal("Excluded", addon.StateText);
    }

    [Fact]
    public async Task InactiveAssetDisabledAddonShowsLocalOrangeBorderOnly()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: false, AddonState.Disabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.False(addon.IsExcludedAnywhere);
        Assert.Equal("#FF9800", addon.BorderColor);
        Assert.Equal("Disabled", addon.StateText);
    }

    [Fact]
    public async Task GlobalDisabledMarkerShowsOrangeWhenCurrentAssetIsEnabled()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Enabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetAddonStateMarkers(new Dictionary<string, AddonState>
        {
            ["addon-1"] = AddonState.Disabled
        });
        addon.SetCurrentAsset(assetViewModel);

        Assert.False(addon.IsExcludedAnywhere);
        Assert.Equal("#FF9800", addon.BorderColor);
        Assert.Equal("Disabled", addon.StateText);
    }

    [Fact]
    public async Task GlobalExcludedMarkerOverridesLocalDisabledState()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Disabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetAddonStateMarkers(new Dictionary<string, AddonState>
        {
            ["addon-1"] = AddonState.Excluded
        });
        addon.SetCurrentAsset(assetViewModel);

        Assert.True(addon.IsExcludedAnywhere);
        Assert.Equal("#F44336", addon.BorderColor);
        Assert.Equal("Excluded", addon.StateText);
    }

    [Fact]
    public async Task AddonStateMarkersIgnoreInactiveAssetsAndPreferExcluded()
    {
        using var manager = await CreateManagerAsync();
        CreateAsset(manager, enabled: false, AddonState.Excluded, addonId: "addon-2");
        CreateAsset(manager, enabled: true, AddonState.Disabled, addonId: "addon-1");
        CreateAsset(manager, enabled: true, AddonState.Excluded, addonId: "addon-1");
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(manager, Path.Combine(rootPath, "appdata"));
        using var grid = new AddonGridViewModel(manager, pendingChangeManager, processWatcher);

        var markers = InvokeBuildAddonStateMarkers(grid, manager.GetConfiguration());

        Assert.True(markers.TryGetValue("addon-1", out var addon1State));
        Assert.Equal(AddonState.Excluded, addon1State);
        Assert.False(markers.ContainsKey("addon-2"));
    }

    [Fact]
    public async Task InactiveAssetMembershipMarkersIncludeOtherDisabledAssetsOnly()
    {
        using var manager = await CreateManagerAsync();
        var currentAsset = CreateAsset(manager, enabled: true, AddonState.Enabled, addonId: "addon-1", assetName: "Asset A");
        currentAsset.AddAddon("addon-2", AddonState.Enabled);
        currentAsset.AddAddon("addon-3", AddonState.Enabled);
        var inactiveAsset = CreateAsset(manager, enabled: false, AddonState.Enabled, addonId: "addon-1", assetName: "Asset B");
        inactiveAsset.AddAddon("addon-2", AddonState.Disabled);
        inactiveAsset.AddAddon("addon-3", AddonState.Excluded);
        CreateAsset(manager, enabled: true, AddonState.Disabled, addonId: "addon-1", assetName: "Asset C");
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(manager, Path.Combine(rootPath, "appdata"));
        using var grid = new AddonGridViewModel(manager, pendingChangeManager, processWatcher);

        var markers = InvokeBuildInactiveAssetMembershipMarkers(
            grid,
            manager.GetConfiguration(),
            currentAsset.Id);

        Assert.Equal(new[] { "Asset B" }, markers["addon-1"]);
        Assert.Equal(new[] { "Asset B" }, markers["addon-2"]);
        Assert.Equal(new[] { "Asset B" }, markers["addon-3"]);

        var selfSkippedMarkers = InvokeBuildInactiveAssetMembershipMarkers(
            grid,
            manager.GetConfiguration(),
            inactiveAsset.Id);

        Assert.False(selfSkippedMarkers.ContainsKey("addon-1"));
        Assert.False(selfSkippedMarkers.ContainsKey("addon-2"));
        Assert.False(selfSkippedMarkers.ContainsKey("addon-3"));
    }

    [Fact]
    public async Task InactiveAssetBadgeDoesNotChangeDisplayState()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Enabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetInactiveAssetMembershipMarkers(new Dictionary<string, IReadOnlyList<string>>
        {
            ["addon-1"] = new[] { "Asset B" }
        });
        addon.SetCurrentAsset(assetViewModel);

        Assert.True(addon.IsInInactiveAsset);
        Assert.Contains("Asset B", addon.InactiveAssetTooltip);
        Assert.Equal("#303030", addon.BorderColor);
        Assert.Equal("Enabled", addon.StateText);
    }

    [Fact]
    public async Task StateMarkerRefreshReflectsCrossAssetMarkersAfterUnsavedAssetMutation()
    {
        using var manager = await CreateManagerAsync();
        var currentAsset = CreateAsset(manager, enabled: true, AddonState.Enabled, addonId: "addon-1");
        currentAsset.AddAddon("addon-2", AddonState.Enabled);
        var otherAsset = CreateAsset(manager, enabled: true, AddonState.Disabled, addonId: "addon-1");
        var addon2 = CreateAddonViewModel(manager, addonId: "addon-2");
        using var currentAssetViewModel = CreateAssetViewModel(currentAsset, manager);
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(manager, Path.Combine(rootPath, "appdata"));
        using var grid = new AddonGridViewModel(manager, pendingChangeManager, processWatcher);

        SetAllAddons(grid, addon2);
        addon2.SetCurrentAsset(currentAssetViewModel);
        InvokeRefreshAddonStateMarkers(grid, manager.GetConfiguration());

        Assert.Equal("#303030", addon2.BorderColor);
        Assert.Equal("Enabled", addon2.StateText);

        manager.AddAddonToAsset(otherAsset.Id, "addon-2", AddonState.Disabled);
        InvokeRefreshAddonStateMarkers(grid, manager.GetConfiguration());

        Assert.Equal("#FF9800", addon2.BorderColor);
        Assert.Equal("Disabled", addon2.StateText);
    }

    [Fact]
    public async Task AddonStateFilterTreatsDisabledAndExcludedAsOff()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Enabled, addonId: "addon-1");
        asset.AddAddon("addon-2", AddonState.Disabled);
        asset.AddAddon("addon-3", AddonState.Excluded);
        using var assetViewModel = CreateAssetViewModel(asset, manager);
        var addon1 = CreateAddonViewModel(manager, addonId: "addon-1");
        var addon2 = CreateAddonViewModel(manager, addonId: "addon-2");
        var addon3 = CreateAddonViewModel(manager, addonId: "addon-3");

        addon1.SetCurrentAsset(assetViewModel);
        addon2.SetCurrentAsset(assetViewModel);
        addon3.SetCurrentAsset(assetViewModel);

        Assert.True(InvokeMatchesAddonStateFilter(addon1, filterIndex: 1));
        Assert.False(InvokeMatchesAddonStateFilter(addon2, filterIndex: 1));
        Assert.False(InvokeMatchesAddonStateFilter(addon3, filterIndex: 1));
        Assert.False(InvokeMatchesAddonStateFilter(addon1, filterIndex: 2));
        Assert.True(InvokeMatchesAddonStateFilter(addon2, filterIndex: 2));
        Assert.True(InvokeMatchesAddonStateFilter(addon3, filterIndex: 2));
    }

    [Fact]
    public async Task ClearingCurrentAssetClearsStaleLocalState()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Disabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);
        Assert.Equal("#FF9800", addon.BorderColor);
        Assert.Equal("Disabled", addon.StateText);

        addon.SetCurrentAsset(null);

        Assert.Equal("#303030", addon.BorderColor);
        Assert.Equal("Unknown", addon.StateText);
    }

    private async Task<AddonManager> CreateManagerAsync()
    {
        var workshopPath = Path.Combine(rootPath, "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
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

    private static Asset CreateAsset(
        AddonManager manager,
        bool enabled,
        AddonState state,
        string addonId = "addon-1",
        string assetName = "Test Asset")
    {
        var asset = new Asset(assetName)
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = enabled,
            DefaultAddonState = AddonState.Enabled
        };
        asset.AddAddon(addonId, state);
        manager.GetConfiguration().Assets.Add(asset);
        return asset;
    }

    private static AddonItemViewModel CreateAddonViewModel(AddonManager manager, string addonId = "addon-1")
    {
        return new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = addonId,
                Title = "Test Addon",
                FolderPath = string.Empty,
                NeedsTitleUpdate = false
            },
            manager);
    }

    private static AssetItemViewModel CreateAssetViewModel(Asset asset, AddonManager manager)
    {
        return new AssetItemViewModel(asset, manager, null!, null!, showExclusiveApply: true);
    }

    private static Dictionary<string, AddonState> InvokeBuildAddonStateMarkers(
        AddonGridViewModel grid,
        Configuration configuration)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "BuildAddonStateMarkers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(grid, new object[] { configuration });
        return Assert.IsType<Dictionary<string, AddonState>>(result);
    }

    private static Dictionary<string, IReadOnlyList<string>> InvokeBuildInactiveAssetMembershipMarkers(
        AddonGridViewModel grid,
        Configuration configuration,
        string? currentAssetId)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "BuildInactiveAssetMembershipMarkers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(grid, new object?[] { configuration, currentAssetId });
        return Assert.IsType<Dictionary<string, IReadOnlyList<string>>>(result);
    }

    private static bool InvokeMatchesAddonStateFilter(AddonItemViewModel addon, int filterIndex)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "MatchesAddonStateFilter",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { addon, filterIndex });
        return Assert.IsType<bool>(result);
    }

    private static void SetAllAddons(AddonGridViewModel grid, params AddonItemViewModel[] addons)
    {
        var field = typeof(AddonGridViewModel).GetField(
            "allAddons",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        field!.SetValue(grid, new System.Collections.ObjectModel.ObservableCollection<AddonItemViewModel>(addons));
    }

    private static void InvokeRefreshAddonStateMarkers(
        AddonGridViewModel grid,
        Configuration configuration)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "RefreshAddonStateMarkers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(grid, new object[] { configuration });
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
