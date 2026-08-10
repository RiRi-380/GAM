using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;
using System.Reflection;

namespace GmodAddonManager.UI.Tests;

public sealed class AssetTargetPickerViewModelTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "GAM_UI_AssetTargetPicker_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RootAndNestedContainersMatchLeftPaneWithoutFlattening()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-navigation"));
        var configuration = manager.GetConfiguration();
        var rootGroup = new AssetGroup("Favorite group")
        {
            Id = "group-root",
            IsFavorite = true,
            SortOrder = 0
        };
        var childGroup = new AssetGroup("Child group")
        {
            Id = "group-child",
            ParentGroupId = rootGroup.Id,
            IsFavorite = true,
            SortOrder = 1
        };
        configuration.AssetGroups.AddRange([rootGroup, childGroup]);
        configuration.Assets.AddRange(
        [
            new Asset("Favorite root")
            {
                Id = "asset-favorite-root",
                IsFavorite = true,
                SortOrder = 1
            },
            new Asset("Normal root")
            {
                Id = "asset-normal-root",
                SortOrder = 0
            },
            new Asset("Favorite child")
            {
                Id = "asset-favorite-child",
                ParentGroupId = rootGroup.Id,
                IsFavorite = true,
                SortOrder = 0
            },
            new Asset("Normal child")
            {
                Id = "asset-normal-child",
                ParentGroupId = rootGroup.Id,
                SortOrder = 0
            },
            new Asset("Grandchild")
            {
                Id = "asset-grandchild",
                ParentGroupId = childGroup.Id,
                SortOrder = 0
            },
            new Asset("Smart root")
            {
                Id = "asset-smart-root",
                SortOrder = 0,
                MembershipRule = new AssetMembershipRule(
                    AssetMembershipRuleKind.Type,
                    "Map")
            }
        ]);

        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings { CollapseGmodDisabledAddons = false });
        leftPane.LoadAssets();
        using var picker = new AssetTargetPickerViewModel(manager, leftPane.Assets);

        var expectedRoot = leftPane.Entries
            .Where(entry => !entry.IsSystem && !entry.IsSmart)
            .Select(entry => entry.Id)
            .ToArray();
        Assert.Equal(
            ["group-root", "asset-favorite-root", "asset-normal-root"],
            picker.Entries.Select(entry => entry.Id));
        Assert.Equal(expectedRoot, picker.Entries.Select(entry => entry.Id));
        Assert.DoesNotContain(picker.Entries, entry => entry.Id == "asset-favorite-child");
        Assert.DoesNotContain(picker.Entries, entry => entry.Id == "group-child");
        Assert.DoesNotContain(picker.Entries, entry => entry.Id == "asset-smart-root");

        picker.OpenGroup(picker.Entries.Single(entry => entry.Id == rootGroup.Id));

        Assert.Null(leftPane.CurrentGroupId);
        Assert.Equal(rootGroup.Id, picker.CurrentGroupId);
        Assert.Equal(
            ["asset-favorite-child", "group-child", "asset-normal-child"],
            picker.Entries.Select(entry => entry.Id));
        Assert.DoesNotContain(picker.Entries, entry => entry.Id == "asset-grandchild");
        Assert.Equal(
            [L.Get("AssetList.Header"), "Favorite group"],
            picker.Breadcrumbs.Select(item => item.Name));

        picker.OpenGroup(picker.Entries.Single(entry => entry.Id == childGroup.Id));

        Assert.Equal(["asset-grandchild"], picker.Entries.Select(entry => entry.Id));
        Assert.Equal(
            [L.Get("AssetList.Header"), "Favorite group", "Child group"],
            picker.Breadcrumbs.Select(item => item.Name));

        picker.ReturnToParent();
        Assert.Equal(rootGroup.Id, picker.CurrentGroupId);

        using var navigateRoot = picker.Breadcrumbs[0].NavigateCommand.Execute().Subscribe();
        Assert.True(picker.IsAtRoot);
        Assert.Equal(expectedRoot, picker.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public async Task GroupsRemainNavigableWhenOnlySmartAssetsAreInside()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-smart-only"));
        var configuration = manager.GetConfiguration();
        var smartOnlyGroup = new AssetGroup("Smart only")
        {
            Id = "group-smart-only"
        };
        configuration.AssetGroups.Add(smartOnlyGroup);
        configuration.Assets.Add(new Asset("Managed by rule")
        {
            Id = "asset-smart-child",
            ParentGroupId = smartOnlyGroup.Id,
            MembershipRule = new AssetMembershipRule(
                AssetMembershipRuleKind.Tag,
                "Build")
        });

        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings());
        leftPane.LoadAssets();
        using var picker = new AssetTargetPickerViewModel(manager, leftPane.Assets);

        var groupEntry = picker.Entries.Single(entry => entry.Id == smartOnlyGroup.Id);
        Assert.True(groupEntry.IsGroup);

        picker.OpenGroup(groupEntry);

        Assert.True(picker.IsInsideGroup);
        Assert.True(picker.IsEmpty);
        Assert.Empty(picker.Entries);
        Assert.DoesNotContain("asset-smart-child", picker.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public async Task CreationUsesCurrentGroupAndInheritsItsDefaultState()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-create"));
        var group = new AssetGroup("Excluded targets")
        {
            Id = "group-excluded",
            DefaultChildState = AddonState.Excluded
        };
        manager.GetConfiguration().AssetGroups.Add(group);

        using var grid = new AddonGridViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            Path.Combine(rootPath, "picker-sort.json"));
        using var groupedAsset = await grid.CreateAssetFromSelectionAsync(
            "Created in group",
            group.Id);
        using var rootAsset = await grid.CreateAssetFromSelectionAsync(
            "Created at root",
            parentGroupId: null);

        Assert.NotNull(groupedAsset);
        Assert.NotNull(rootAsset);
        var groupedModel = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == groupedAsset.Id);
        var rootModel = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == rootAsset.Id);
        Assert.Equal(group.Id, groupedModel.ParentGroupId);
        Assert.Equal(AddonState.Excluded, groupedModel.State);
        Assert.Null(rootModel.ParentGroupId);
    }

    [AvaloniaFact]
    public async Task DialogKeepsGroupsNavigationOnlyAndAssetsConfirmable()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-dialog"));
        var group = new AssetGroup("Target group")
        {
            Id = "group-target"
        };
        manager.GetConfiguration().AssetGroups.Add(group);
        manager.GetConfiguration().Assets.Add(new Asset("Target asset")
        {
            Id = "asset-target",
            ParentGroupId = group.Id
        });

        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings());
        leftPane.LoadAssets();
        using var dialog = new AssetSelectionDialog(manager, leftPane.Assets);
        dialog.Show();
        try
        {
            var list = Assert.IsType<ListBox>(dialog.FindControl<ListBox>("AssetListBox"));
            var ok = Assert.IsType<Button>(dialog.FindControl<Button>("OkButton"));
            var back = Assert.IsType<Button>(dialog.FindControl<Button>("BackButton"));
            var picker = Assert.IsType<AssetTargetPickerViewModel>(dialog.PickerViewModel);
            var groupEntry = picker.Entries.Single(entry => entry.Id == group.Id);

            list.SelectedItem = groupEntry;
            Assert.False(ok.IsEnabled);
            Assert.False(back.IsVisible);

            picker.OpenGroup(groupEntry);

            Assert.True(back.IsVisible);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(picker.IsAtRoot);
            Assert.False(back.IsVisible);

            picker.OpenGroup(picker.Entries.Single(entry => entry.Id == group.Id));
            var assetEntry = picker.Entries.Single(entry => entry.Id == "asset-target");
            list.SelectedItem = assetEntry;
            Assert.True(ok.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }

    [Fact]
    public void DialogWiresGroupBackBreadcrumbAndWindowNavigationContracts()
    {
        var xaml = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetSelectionDialog.axaml");
        var code = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetSelectionDialog.axaml.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "PointerReleased=\"OnEntryPointerReleased\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"OnBackClick\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding Breadcrumbs}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding NavigateCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "PointerWheelChanged=\"OnBreadcrumbPointerWheelChanged\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SizeChanged=\"OnBreadcrumbSizeChanged\"",
            xaml,
            StringComparison.Ordinal);

        Assert.Contains(
            "AddHandler(\n            KeyDownEvent,\n            OnWindowNavigationKeyDown,\n            RoutingStrategies.Tunnel);",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoveHandler(KeyDownEvent, OnWindowNavigationKeyDown);",
            code,
            StringComparison.Ordinal);
        Assert.Contains("private void OnEntryPointerReleased", code, StringComparison.Ordinal);
        Assert.Contains("pickerViewModel?.OpenGroup(entry);", code, StringComparison.Ordinal);
        Assert.Contains("private void OnBackClick", code, StringComparison.Ordinal);
        Assert.Contains("pickerViewModel?.ReturnToParent();", code, StringComparison.Ordinal);
        Assert.Contains("private void OnWindowNavigationKeyDown", code, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.Back", code, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.Left", code, StringComparison.Ordinal);
        Assert.Contains("KeyModifiers.Alt", code, StringComparison.Ordinal);
        Assert.Contains("private void OnBreadcrumbPointerWheelChanged", code, StringComparison.Ordinal);
        Assert.Contains("private void OnBreadcrumbSizeChanged", code, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DialogCreateActionPassesCurrentGroupAndSelectsRegisteredAsset()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-dialog-create"));
        var group = new AssetGroup("Create target")
        {
            Id = "group-create-target",
            DefaultChildState = AddonState.Disabled
        };
        manager.GetConfiguration().AssetGroups.Add(group);

        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings());
        leftPane.LoadAssets();

        var callback = new TaskCompletionSource<(string Name, string? ParentGroupId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var dialog = new AssetSelectionDialog(
            manager,
            leftPane.Assets,
            (name, parentGroupId) =>
            {
                callback.TrySetResult((name, parentGroupId));
                var model = new Asset(name)
                {
                    Id = "asset-created-from-dialog",
                    ParentGroupId = parentGroupId,
                    State = AddonState.Disabled
                };
                manager.GetConfiguration().Assets.Add(model);
                return Task.FromResult<AssetItemViewModel?>(new AssetItemViewModel(
                    model,
                    manager,
                    pendingChangeManager,
                    processWatcher));
            });
        var picker = Assert.IsType<AssetTargetPickerViewModel>(dialog.PickerViewModel);
        picker.OpenGroup(picker.Entries.Single(entry => entry.Id == group.Id));
        dialog.Show();

        try
        {
            var createAsset = Assert.IsType<Button>(
                dialog.FindControl<Button>("CreateAssetButton"));
            var list = Assert.IsType<ListBox>(
                dialog.FindControl<ListBox>("AssetListBox"));
            var ok = Assert.IsType<Button>(dialog.FindControl<Button>("OkButton"));
            Assert.True(createAsset.IsVisible);

            createAsset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var nameDialog = await WaitForOwnedWindowAsync<SimpleAssetCreateDialog>(dialog);
            var nameBox = Assert.IsType<TextBox>(
                nameDialog.FindControl<TextBox>("AssetNameTextBox"));
            var submit = Assert.IsType<Button>(
                nameDialog.FindControl<Button>("CreateButton"));
            nameBox.Text = "Created through dialog";
            await WaitUntilAsync(() => submit.IsEnabled);

            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var callbackResult = await callback.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() =>
                picker.Entries.Any(entry => entry.Id == "asset-created-from-dialog"));

            Assert.Equal("Created through dialog", callbackResult.Name);
            Assert.Equal(group.Id, callbackResult.ParentGroupId);
            Assert.Equal(group.Id, picker.CurrentGroupId);
            var createdEntry = picker.Entries.Single(entry =>
                entry.Id == "asset-created-from-dialog");
            Assert.Same(createdEntry, list.SelectedItem);
            Assert.True(ok.IsEnabled);
        }
        finally
        {
            foreach (var ownedWindow in dialog.OwnedWindows.ToArray())
            {
                ownedWindow.Close();
            }
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task DialogCreateActionRejectsDuplicateAssetNameWithVisibleFeedback()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-dialog-duplicate"));
        await manager.CreateAssetAsync("Existing Target");
        manager.GetConfiguration().AssetGroups.Add(new AssetGroup("Existing Group")
        {
            Id = "existing-group-name-collision"
        });
        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings());
        leftPane.LoadAssets();

        var callbackCount = 0;
        using var dialog = new AssetSelectionDialog(
            manager,
            leftPane.Assets.Where(asset => !asset.IsSystem),
            (_, _) =>
            {
                callbackCount++;
                return Task.FromResult<AssetItemViewModel?>(null);
            });
        dialog.Show();

        try
        {
            var createAsset = Assert.IsType<Button>(
                dialog.FindControl<Button>("CreateAssetButton"));
            createAsset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var nameDialog = await WaitForOwnedWindowAsync<SimpleAssetCreateDialog>(dialog);
            var nameBox = Assert.IsType<TextBox>(
                nameDialog.FindControl<TextBox>("AssetNameTextBox"));
            var validation = Assert.IsType<TextBlock>(
                nameDialog.FindControl<TextBlock>("NameValidationText"));
            var submit = Assert.IsType<Button>(
                nameDialog.FindControl<Button>("CreateButton"));

            nameBox.Text = "existing target";
            await WaitUntilAsync(() => validation.IsVisible);

            Assert.Equal(
                L.Format("Error.AssetNameAlreadyExists", "existing target"),
                validation.Text);
            Assert.False(submit.IsEnabled);
            Assert.Equal(0, callbackCount);

            nameBox.Text = "EXISTING GROUP";
            await WaitUntilAsync(() =>
                validation.Text == L.Format(
                    "Error.AssetNameAlreadyExists",
                    "EXISTING GROUP"));
            Assert.True(validation.IsVisible);
            Assert.False(submit.IsEnabled);
            Assert.Equal(0, callbackCount);
        }
        finally
        {
            foreach (var ownedWindow in dialog.OwnedWindows.ToArray())
            {
                ownedWindow.Close();
            }
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task DialogCreateActionShowsUnexpectedCreationFailure()
    {
        using var manager = await CreateManagerAsync();
        using var dialog = new AssetSelectionDialog(
            manager,
            Array.Empty<AssetItemViewModel>(),
            (_, _) => throw new InvalidOperationException("simulated failure"));
        dialog.Show();

        try
        {
            var createAsset = Assert.IsType<Button>(
                dialog.FindControl<Button>("CreateAssetButton"));
            var createError = Assert.IsType<TextBlock>(
                dialog.FindControl<TextBlock>("CreateAssetErrorText"));
            createAsset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var nameDialog = await WaitForOwnedWindowAsync<SimpleAssetCreateDialog>(dialog);
            var nameBox = Assert.IsType<TextBox>(
                nameDialog.FindControl<TextBox>("AssetNameTextBox"));
            var submit = Assert.IsType<Button>(
                nameDialog.FindControl<Button>("CreateButton"));
            nameBox.Text = "Unique Target";
            await WaitUntilAsync(() => submit.IsEnabled);

            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => createError.IsVisible);

            Assert.Equal(
                L.Get("Error.AssetCreateFailedGeneric"),
                createError.Text);
        }
        finally
        {
            foreach (var ownedWindow in dialog.OwnedWindows.ToArray())
            {
                ownedWindow.Close();
            }
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task OpeningNestedTransferTargetNavigatesLeftPaneAndSelectsTheAsset()
    {
        using var manager = await CreateManagerAsync();
        using var processWatcher = new GmodProcessWatcher();
        var pendingChangeManager = new PendingChangeManager(
            manager,
            Path.Combine(rootPath, "pending-open-nested-target"));
        var group = new AssetGroup("Nested target group")
        {
            Id = "group-open-target"
        };
        var target = new Asset("Nested target")
        {
            Id = "asset-open-target",
            ParentGroupId = group.Id,
            Addons = ["100"]
        };
        manager.GetConfiguration().AssetGroups.Add(group);
        manager.GetConfiguration().Assets.Add(target);

        using var leftPane = new AssetListViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            new AppSettings());
        leftPane.LoadAssets();
        using var grid = new AddonGridViewModel(
            manager,
            pendingChangeManager,
            processWatcher,
            Path.Combine(rootPath, "open-nested-target-sort.json"));
        var previousAssetList = ViewModelLocator.AssetListViewModel;
        ViewModelLocator.AssetListViewModel = leftPane;

        try
        {
            Assert.True(leftPane.IsAtRoot);
            Assert.DoesNotContain(leftPane.Entries, entry => entry.Id == target.Id);
            var selectMethod = typeof(AddonGridViewModel).GetMethod(
                "SelectAssetInUi",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(selectMethod);

            selectMethod!.Invoke(grid, [target.Id]);
            await WaitUntilAsync(() => leftPane.SelectedAsset?.Id == target.Id);

            Assert.Equal(group.Id, leftPane.CurrentGroupId);
            Assert.Equal(target.Id, leftPane.SelectedAsset?.Id);
            Assert.Contains(leftPane.Entries, entry => entry.Id == target.Id);
            Assert.Equal(
                [L.Get("AssetList.Header"), group.Name],
                leftPane.Breadcrumbs.Select(item => item.Name));
        }
        finally
        {
            ViewModelLocator.AssetListViewModel = previousAssetList;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
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
                "WorkshopItemDetails" { }
                "WorkshopItemsInstalled" { }
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

    private static async Task<TWindow> WaitForOwnedWindowAsync<TWindow>(Window owner)
        where TWindow : Window
    {
        TWindow? found = null;
        await WaitUntilAsync(() =>
        {
            found = owner.OwnedWindows.OfType<TWindow>().SingleOrDefault();
            return found != null;
        });
        return found!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the headless UI state to update.");
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
            {
                return File.ReadAllText(Path.Combine(
                    new[] { directory.FullName }.Concat(parts).ToArray()));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
