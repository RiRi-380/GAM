using System.Reflection;
using System.Windows.Input;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class GmodDisabledSystemAssetUiTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "GAM_UI_GmodDisabledAsset_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FixedAssetIsSecondAndEveryMutationControlIsLocked()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var disabledAsset = GetOrCreateDisabledAsset(configuration);
        disabledAsset.Name = "Stored name must not control the fixed UI label";
        disabledAsset.State = AddonState.Enabled;
        disabledAsset.IsSystem = false;
        disabledAsset.AddAddon("100");
        configuration.Assets.Add(new Asset("Favorite", isSystem: false)
        {
            Id = "favorite",
            IsFavorite = true
        });

        using var assetList = new AssetListViewModel(manager, null!, null!);
        assetList.LoadAssets();

        Assert.Equal("subscribe-system-asset", assetList.Assets[0].Id);
        var viewModel = assetList.Assets[1];
        Assert.Equal(AssetItemViewModel.GmodDisabledSystemAssetId, viewModel.Id);
        Assert.Equal(AssetItemViewModel.GmodDisabledSystemAssetName, viewModel.Name);
        Assert.Equal(1, viewModel.AddonCount);
        Assert.Equal(["100"], viewModel.GetAddonIds());
        Assert.True(viewModel.IsSystem);
        Assert.True(viewModel.IsExcludedState);
        Assert.Equal("#F44336", viewModel.AssetStateColor);

        Assert.False(viewModel.CanEditName);
        Assert.False(viewModel.CanEditImage);
        Assert.False(viewModel.CanDelete);
        Assert.False(viewModel.CanFavorite);
        Assert.False(viewModel.CanManageVersions);
        Assert.False(viewModel.CanToggleAssetActive);
        Assert.False(viewModel.CanEditAddonDefaultState);
        Assert.False(viewModel.CanSetExcluded);

        Assert.False(((ICommand)viewModel.EditCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.DeleteCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.ToggleFavoriteCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.VersionManageCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.ToggleEnabledCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.SetEnabledCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.SetDisabledCommand).CanExecute(null));
        Assert.False(((ICommand)viewModel.SetExcludedCommand).CanExecute(null));
    }

    [Fact]
    public async Task RefreshFixedAssetUpdatesMembershipCountWithoutRebuildingCards()
    {
        using var manager = await CreateManagerAsync();
        var disabledAsset = GetOrCreateDisabledAsset(manager.GetConfiguration());
        using var assetList = new AssetListViewModel(manager, null!, null!);
        assetList.LoadAssets();
        var viewModel = Assert.Single(
            assetList.Assets,
            asset => asset.Id == AssetItemViewModel.GmodDisabledSystemAssetId);

        Assert.Equal(0, viewModel.AddonCount);

        disabledAsset.AddAddon("100");
        disabledAsset.AddAddon("200");
        assetList.RefreshGmodDisabledAsset();

        Assert.Same(viewModel, assetList.GetAssetById(AssetItemViewModel.GmodDisabledSystemAssetId));
        Assert.Equal(2, viewModel.AddonCount);
    }

    [Fact]
    public async Task RefreshFixedAssetAddsTheSecondCardEvenWhenMembershipIsEmpty()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        configuration.Assets.RemoveAll(
            asset => asset.Id == AssetItemViewModel.GmodDisabledSystemAssetId);
        using var assetList = new AssetListViewModel(manager, null!, null!);
        assetList.LoadAssets();
        Assert.DoesNotContain(
            assetList.Assets,
            asset => asset.Id == AssetItemViewModel.GmodDisabledSystemAssetId);

        GetOrCreateDisabledAsset(configuration);
        assetList.RefreshGmodDisabledAsset();

        Assert.Equal("subscribe-system-asset", assetList.Assets[0].Id);
        var fixedAsset = assetList.Assets[1];
        Assert.Equal(AssetItemViewModel.GmodDisabledSystemAssetId, fixedAsset.Id);
        Assert.Equal(0, fixedAsset.AddonCount);
    }

    [Fact]
    public void FixedAssetDetailsKeepEveryMembershipEntryIncludingUnavailableMetadata()
    {
        var method = typeof(AssetDetailsDialog).GetMethod(
            "BuildMembershipItems",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var rows = Assert.IsType<List<AssetAddonMembershipItem>>(method!.Invoke(
            null,
            [
                new[] { "100", "metadata-absent" },
                new Dictionary<string, WorkshopAddon>(StringComparer.Ordinal)
                {
                    ["100"] = new WorkshopAddon("100", string.Empty)
                    {
                        Title = "Disabled addon",
                        IsAvailable = true
                    }
                },
                new HashSet<string>(StringComparer.Ordinal) { "100" },
                true
            ]));

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsUnavailable);
        Assert.True(rows[1].IsUnavailable);
        Assert.False(string.IsNullOrWhiteSpace(rows[1].Title));
    }

    [Fact]
    public void FixedAssetUsesExplicitVisibleVersusMembershipCount()
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "FormatFixedMembershipCountDisplay",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal("(5)", method!.Invoke(null, [5, 5]));
        Assert.Equal("(3/5)", method.Invoke(null, [3, 5]));
    }

    [Fact]
    public void DetailsWiringTreatsFixedAssetAsAnAuthoritativeMembershipList()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml.cs"));

        Assert.Contains(
            "assetViewModel.IsSubscribeAsset || assetViewModel.IsGmodDisabledAsset",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualRefreshReconcilesAfterInventoryAndRefreshesAssetAndGrid()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var methodStart = source.IndexOf(
            "public async Task RefreshAddonsAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void UpdateAddonStatistics()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var inventoryIndex = method.IndexOf(
            "await AddonGridViewModel.LoadAddonsAsync();",
            StringComparison.Ordinal);
        var reconcileIndex = method.IndexOf(
            "await addonManager.RefreshGmodDisabledAddonsFromRuntimeAsync();",
            StringComparison.Ordinal);
        var presentationRefreshIndex = method.IndexOf(
            "RefreshGmodDisabledAssetPresentation();",
            StringComparison.Ordinal);

        Assert.True(inventoryIndex >= 0);
        Assert.True(reconcileIndex > inventoryIndex);
        Assert.True(presentationRefreshIndex > reconcileIndex);
        Assert.Contains(
            "AssetListViewModel.RefreshGmodDisabledAsset();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddonGridViewModel.ApplyFilter();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SystemAssetCannotBeATargetOrSourceOfManualMembershipChanges()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs"));

        Assert.Contains(
            ".Where(asset => !asset.IsSystem)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (currentAsset == null || currentAsset.IsSystem) return;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixedCardShowsExcludedAsReadOnlyState()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetListView.axaml"));
        var marker = "IsVisible=\"{Binding IsGmodDisabledAsset}\"";
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(markerIndex >= 0);
        var blockStart = source.LastIndexOf(
            "<TextBlock",
            markerIndex,
            StringComparison.Ordinal);
        Assert.True(blockStart >= 0);
        var fixedStateBlock = source[blockStart..(markerIndex + marker.Length)];
        Assert.Contains(
            "Text=\"{loc:Localize AssetList.Excluded}\"",
            fixedStateBlock,
            StringComparison.Ordinal);
        Assert.Contains("Foreground=\"#F44336\"", fixedStateBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void GmodStopDelegatesPendingConflictSyncToPendingManager()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "App.axaml.cs"));
        var methodStart = source.IndexOf(
            "private async Task ApplyPendingChangesAfterGmodStoppedAsync()",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void Cleanup()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var pendingBranchStart = method.IndexOf(
            "if (pendingChangeManager?.HasPendingChanges() == true)",
            StringComparison.Ordinal);
        var elseIndex = method.IndexOf(
            "else",
            pendingBranchStart,
            StringComparison.Ordinal);
        var applyIndex = method.IndexOf(
            "await pendingChangeManager.ApplyPendingChangesAsync();",
            StringComparison.Ordinal);
        var reconcileIndex = method.IndexOf(
            "await addonManager.RefreshGmodDisabledAddonsFromRuntimeAsync();",
            StringComparison.Ordinal);
        var presentationIndex = method.IndexOf(
            "mainViewModel.RefreshGmodDisabledAssetPresentation();",
            StringComparison.Ordinal);

        Assert.True(pendingBranchStart >= 0);
        Assert.True(applyIndex > pendingBranchStart);
        Assert.True(elseIndex > applyIndex);
        Assert.True(reconcileIndex > elseIndex);
        Assert.True(presentationIndex > reconcileIndex);
        Assert.DoesNotContain(
            "RefreshGmodDisabledAddonsFromRuntimeAsync",
            method[pendingBranchStart..elseIndex],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplyPendingChangesAsync",
            method[elseIndex..presentationIndex],
            StringComparison.Ordinal);
        Assert.Contains(
            "await Dispatcher.UIThread.InvokeAsync",
            method,
            StringComparison.Ordinal);
        Assert.Contains("desktop.MainWindow?.IsVisible == true", method, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            DeleteDirectoryWithRetry(rootPath);
        }
    }

    private static Asset GetOrCreateDisabledAsset(Configuration configuration)
    {
        var existing = configuration.Assets.FirstOrDefault(
            asset => asset.Id == AssetItemViewModel.GmodDisabledSystemAssetId);
        if (existing != null)
        {
            return existing;
        }

        var created = new Asset(
            AssetItemViewModel.GmodDisabledSystemAssetName,
            isSystem: true)
        {
            Id = AssetItemViewModel.GmodDisabledSystemAssetId,
            State = AddonState.Excluded
        };
        configuration.Assets.Add(created);
        return created;
    }

    private async Task<AddonManager> CreateManagerAsync()
    {
        var workshopPath = Path.Combine(rootPath, "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
        var workshopManifestPath = Path.Combine(rootPath, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        File.WriteAllText(
            workshopManifestPath,
            """
            "AppWorkshop"
            {
                "WorkshopItemDetails"
                {
                    "100"
                    {
                        "subscribedby" "76561198000000000"
                    }
                }
                "WorkshopItemsInstalled"
                {
                    "100"
                    {
                        "size" "1"
                    }
                }
            }
            """);

        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomWorkshopCacheFilePaths = [workshopManifestPath],
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });

        await manager.InitializeAsync();
        return manager;
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
