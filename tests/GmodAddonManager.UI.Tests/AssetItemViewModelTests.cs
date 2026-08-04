using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System.Reactive.Linq;
using System.Windows.Input;

namespace GmodAddonManager.UI.Tests;

public sealed class AssetItemViewModelTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "GAM_UI_AssetItemVM_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CustomAsset_UsesWholeAssetStateWithoutImportMetadata()
    {
        using var manager = await CreateManagerAsync();
        var asset = new Asset("Cleanup")
        {
            Id = "custom-test",
            State = AddonState.Excluded
        };
        asset.AddAddon("104479467");

        using var viewModel = new AssetItemViewModel(asset, manager, null!, null!);

        Assert.True(viewModel.CanEditAddonDefaultState);
        Assert.True(viewModel.IsExcludedState);
        Assert.Equal(1, viewModel.StateColumnSpan);
    }

    [Fact]
    public async Task VersionDisplay_ShowsChangedOnlyWhenLiveMembershipDiffers()
    {
        using var manager = await CreateManagerAsync();
        manager.CreateAsset("Versioned");
        var asset = manager.GetConfiguration().Assets.Single(item => item.Name == "Versioned");
        asset.AddAddon("100");
        await manager.CreateAssetVersionAsync(asset.Id);

        using var viewModel = new AssetItemViewModel(
            asset,
            manager,
            null!,
            null!);
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;

        try
        {
            LocalizationManager.Instance.ChangeLanguage("en-US");
            Assert.Equal("v1", viewModel.VersionDisplay);

            asset.AddAddon("200");
            viewModel.RefreshFromModel(asset);
            Assert.Equal("v1 · Changed", viewModel.VersionDisplay);

            LocalizationManager.Instance.ChangeLanguage("ja-JP");
            Assert.Equal("v1・変更あり", viewModel.VersionDisplay);

            asset.RemoveAddon("200");
            viewModel.RefreshFromModel(asset);
            Assert.Equal("v1", viewModel.VersionDisplay);
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task SubscribeAsset_UsesCurrentSubscriptionSetInsteadOfRetainedMetadata()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        configuration.KnownSubscribedAddonIds.Add("100");
        configuration.AddonMetadata["100"] = new WorkshopAddon("100", string.Empty);
        configuration.AddonMetadata["200"] = new WorkshopAddon("200", string.Empty)
        {
            IsAvailable = false
        };

        var subscribe = configuration.Assets.Single(asset => asset.Id == "subscribe-system-asset");
        using var viewModel = new AssetItemViewModel(
            subscribe,
            manager,
            null!,
            null!);

        Assert.Equal(["100"], viewModel.GetAddonIds());
        Assert.Equal(1, viewModel.AddonCount);
    }

    [Fact]
    public async Task SubscribeAsset_ExposesLocalizedExcludeAllStateAndExplainsItsPrecedence()
    {
        using var manager = await CreateManagerAsync();
        var subscribe = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId);
        using var viewModel = new AssetItemViewModel(
            subscribe,
            manager,
            null!,
            null!);
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;

        try
        {
            Assert.True(viewModel.CanSetExcluded);
            Assert.True(((ICommand)viewModel.SetExcludedCommand).CanExecute(null));
            Assert.Equal(2, viewModel.StateColumnSpan);

            LocalizationManager.Instance.ChangeLanguage("ja-JP");
            Assert.Equal("すべて除外", viewModel.ExcludedStateLabel);
            Assert.Contains("強制的に無効", viewModel.ExcludedStateTooltip);
            Assert.Contains("有効なCustom Asset", viewModel.DisabledStateTooltip);

            LocalizationManager.Instance.ChangeLanguage("en-US");
            Assert.Equal("Exclude All", viewModel.ExcludedStateLabel);
            Assert.Contains("Force all subscribed addons off", viewModel.ExcludedStateTooltip);
            Assert.Contains("can still enable", viewModel.DisabledStateTooltip);

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var execution = viewModel.SetExcludedCommand.Execute().Subscribe(
                _ => { },
                completion.SetException,
                () => completion.SetResult(true));
            await completion.Task;

            Assert.True(viewModel.IsExcludedState);
            Assert.Equal(AddonState.Excluded, subscribe.GetWholeState());
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task SmartAsset_ShowsRuleAndAutomationState_AndHidesMembershipVersions()
    {
        using var manager = await CreateManagerAsync();
        var asset = new Asset("Roleplay")
        {
            MembershipRule = new AssetMembershipRule(
                AssetMembershipRuleKind.Tag,
                "Roleplay"),
            SmartAutomationState = new SmartAssetAutomationState
            {
                Status = SmartAssetAutomationStatus.Active
            }
        };
        using var viewModel = new AssetItemViewModel(
            asset,
            manager,
            null!,
            null!);
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;

        try
        {
            LocalizationManager.Instance.ChangeLanguage("ja-JP");
            Assert.True(viewModel.IsSmart);
            Assert.False(viewModel.CanManageVersions);
            Assert.Equal("タグ: Roleplay", viewModel.SmartRuleText);
            Assert.Equal("自動同期", viewModel.SmartAutomationStatusText);
            Assert.False(viewModel.IsSmartAutomationFrozen);

            asset.SmartAutomationState.Status =
                SmartAssetAutomationStatus.FrozenInvalidRule;
            viewModel.RefreshFromModel(asset);

            Assert.True(viewModel.IsSmartAutomationFrozen);
            Assert.Equal("自動同期を停止中", viewModel.SmartAutomationStatusText);
            Assert.Contains("現在のメンバーを維持", viewModel.SmartAutomationDescription);
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }
    }

    [Fact]
    public async Task ImportedAsset_CanKeepUnavailableMembershipWithoutChangingGlobalSetting()
    {
        using var manager = await CreateManagerAsync();
        var asset = new Asset("Imported")
        {
            RetainMissingReferences = true
        };
        using var viewModel = new AssetItemViewModel(
            asset,
            manager,
            null!,
            null!);

        Assert.True(viewModel.IncludesUnavailableMembership);
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

    private async Task<AddonManager> CreateManagerAsync()
    {
        var workshopPath = Path.Combine(rootPath, "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
        var gmodPath = Path.Combine(rootPath, "gmod");
        var workshopManifestPath = Path.Combine(rootPath, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.Combine(gmodPath, "garrysmod", "cfg"));
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
            CustomGmodInstallPath = gmodPath,
            CustomAppDataPath = appDataPath,
            CustomWorkshopCacheFilePaths = [workshopManifestPath],
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });

        await manager.InitializeAsync();
        return manager;
    }
}
