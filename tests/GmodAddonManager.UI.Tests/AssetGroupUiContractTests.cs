using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class AssetGroupUiContractTests : IDisposable
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "GAM_UI_AssetGroups_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EveryContainerMixesAssetsAndGroupsWithinFavoriteBands()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var group = new AssetGroup("FPS pack")
        {
            Id = "group-fps",
            IsFavorite = true,
            SortOrder = 0
        };
        configuration.AssetGroups.Add(group);
        configuration.AssetGroups.Add(new AssetGroup("Child group")
        {
            Id = "group-child",
            ParentGroupId = group.Id,
            IsFavorite = true,
            SortOrder = 1
        });
        configuration.Assets.AddRange(
        [
            new Asset("Favorite root")
            {
                Id = "favorite-root",
                IsFavorite = true,
                SortOrder = 1
            },
            new Asset("Normal root")
            {
                Id = "normal-root",
                SortOrder = 0
            },
            new Asset("Child normal")
            {
                Id = "child-normal",
                ParentGroupId = group.Id,
                SortOrder = 0
            },
            new Asset("Child favorite")
            {
                Id = "child-favorite",
                ParentGroupId = group.Id,
                IsFavorite = true,
                SortOrder = 0
            }
        ]);

        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings { CollapseGmodDisabledAddons = false });
        viewModel.LoadAssets();

        Assert.Equal(
            ["group-fps", "favorite-root", "normal-root"],
            viewModel.GetReorderableEntries().Select(entry => entry.Id));
        Assert.Contains(viewModel.Assets, asset => asset.Id == "child-normal");
        Assert.DoesNotContain(viewModel.Entries, entry => entry.Id == "child-normal");

        viewModel.OpenGroup(group.Id);

        Assert.True(viewModel.IsInsideGroup);
        Assert.Equal("FPS pack", viewModel.CurrentHeader);
        Assert.Equal(
            ["child-favorite", "group-child", "child-normal"],
            viewModel.Entries.Select(entry => entry.Id));
        Assert.Equal("child-favorite", viewModel.SelectedAsset?.Id);
        Assert.True(viewModel.Entries.Single(entry => entry.Id == "group-child").IsGroup);
        Assert.Equal(
            [L.Get("AssetList.Header"), "FPS pack"],
            viewModel.Breadcrumbs.Select(item => item.Name));
    }

    [Fact]
    public async Task ReorderTargetIsClampedToTheMovingEntryFavoriteBand()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        configuration.Assets.AddRange(
        [
            new Asset("Favorite A") { Id = "fav-a", IsFavorite = true, SortOrder = 0 },
            new Asset("Favorite B") { Id = "fav-b", IsFavorite = true, SortOrder = 1 },
            new Asset("Normal A") { Id = "normal-a", SortOrder = 0 },
            new Asset("Normal B") { Id = "normal-b", SortOrder = 1 }
        ]);

        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();
        var entries = viewModel.GetReorderableEntries();
        var favorite = entries.Single(entry => entry.Id == "fav-a");
        var normal = entries.Single(entry => entry.Id == "normal-b");

        Assert.Equal(1, viewModel.GetClampedReorderTargetIndex(favorite, int.MaxValue));
        Assert.Equal(2, viewModel.GetClampedReorderTargetIndex(normal, int.MinValue));
    }

    [Fact]
    public void ScrolledReorderTargetMapsVisibleBoundariesToGlobalIndices()
    {
        var entryKeys = Enumerable.Range(0, 30)
            .Select(index => $"asset:{index}")
            .ToList();
        var visibleBoundaries = Enumerable.Range(20, 9)
            .Select((index, visibleIndex) =>
                (EntryKey: entryKeys[index], CenterY: 50d + (visibleIndex * 100d)))
            .ToList();

        var beforeFirstVisible = Views.AssetListView.ResolveRequestedReorderTargetIndex(
            entryKeys,
            entryKeys[24],
            visibleBoundaries,
            pointerY: 0);
        var afterVisibleNeighbor = Views.AssetListView.ResolveRequestedReorderTargetIndex(
            entryKeys,
            entryKeys[24],
            visibleBoundaries,
            pointerY: 600);

        Assert.Equal(20, beforeFirstVisible);
        Assert.Equal(25, afterVisibleNeighbor);
    }

    [Fact]
    public async Task CollapsePreferenceUsesInjectedPersistenceBoundary()
    {
        using var manager = await CreateManagerAsync();
        bool? savedValue = null;
        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings { CollapseGmodDisabledAddons = false },
            value => savedValue = value);
        viewModel.LoadAssets();

        using var execution = viewModel.ToggleGmodDisabledCollapseCommand
            .Execute()
            .Subscribe();

        Assert.True(viewModel.IsGmodDisabledCollapsed);
        Assert.True(savedValue);
    }

    [Fact]
    public async Task ShareSelectionPersistsAcrossGroupNavigationAndDisablesMutations()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var group = new AssetGroup("Grouped") { Id = "share-group", SortOrder = 0 };
        configuration.AssetGroups.Add(group);
        configuration.Assets.AddRange(
        [
            new Asset("Loose") { Id = "share-loose", SortOrder = 0 },
            new Asset("Child")
            {
                Id = "share-child",
                ParentGroupId = group.Id,
                SortOrder = 0
            }
        ]);

        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();
        var loose = viewModel.Entries.Single(entry => entry.Id == "share-loose");
        var groupEntry = viewModel.Entries.Single(entry => entry.Id == group.Id);

        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            LocalizationManager.Instance.ChangeLanguage("en-US");
            viewModel.ToggleShareSelection(loose);
            Assert.Equal("1 Asset / 0 Groups", viewModel.ShareSelectionSummary);
            viewModel.ToggleShareSelection(groupEntry);
            Assert.Equal("1 Asset / 1 Group", viewModel.ShareSelectionSummary);
        }
        finally
        {
            LocalizationManager.Instance.ChangeLanguage(previousLanguage);
        }

        Assert.True(viewModel.IsShareMode);
        Assert.Equal(1, viewModel.SharedAssetCount);
        Assert.Equal(1, viewModel.SharedGroupCount);
        Assert.All(viewModel.Entries.Where(entry => !entry.IsSystem), entry =>
        {
            Assert.False(entry.CanReorder);
            Assert.False(entry.CanDelete);
            Assert.False(entry.CanEditAddonDefaultState);
        });

        viewModel.OpenGroup(group.Id);
        var child = Assert.Single(viewModel.Entries);
        Assert.Equal("share-child", child.Id);
        Assert.Equal(1, viewModel.SharedAssetCount);
        Assert.Equal(1, viewModel.SharedGroupCount);
        viewModel.ToggleShareSelection(child);
        Assert.Equal(1, viewModel.SharedAssetCount);

        viewModel.ReturnToRoot();
        Assert.True(viewModel.Entries.Single(entry => entry.Id == group.Id).IsShareSelected);
        Assert.True(viewModel.Entries.Single(entry => entry.Id == "share-loose").IsShareSelected);

        viewModel.CancelShareMode();
        Assert.False(viewModel.IsShareMode);
        Assert.Equal(0, viewModel.SharedAssetCount);
        Assert.Equal(0, viewModel.SharedGroupCount);
        Assert.All(viewModel.Entries.Where(entry => !entry.IsSystem), entry =>
            Assert.True(entry.CanReorder));
    }

    [Fact]
    public async Task EmptyGroupOwnsTheCenterUntilMembershipChanges()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var group = new AssetGroup("Empty") { Id = "empty-group", SortOrder = 0 };
        configuration.AssetGroups.Add(group);

        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();
        Assert.True(viewModel.IsAddonGridVisible);

        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        viewModel.OpenGroup(group.Id);

        Assert.True(viewModel.IsCurrentGroupEmpty);
        Assert.True(viewModel.IsCurrentGroupEmptyVisible);
        Assert.False(viewModel.IsAddonGridVisible);
        Assert.Null(viewModel.SelectedAsset);
        Assert.Contains(nameof(viewModel.IsCurrentGroupEmpty), notifications);
        Assert.Contains(nameof(viewModel.IsAddonGridVisible), notifications);

        configuration.Assets.Add(new Asset("Child")
        {
            Id = "empty-group-child",
            ParentGroupId = group.Id,
            SortOrder = 0
        });
        notifications.Clear();
        viewModel.LoadAssets();

        Assert.False(viewModel.IsCurrentGroupEmpty);
        Assert.False(viewModel.IsCurrentGroupEmptyVisible);
        Assert.True(viewModel.IsAddonGridVisible);
        Assert.Equal("empty-group-child", viewModel.SelectedAsset?.Id);
        Assert.Contains(nameof(viewModel.IsAddonGridVisible), notifications);

        configuration.Assets.RemoveAll(asset => asset.Id == "empty-group-child");
        notifications.Clear();
        viewModel.LoadAssets();

        Assert.True(viewModel.IsCurrentGroupEmpty);
        Assert.True(viewModel.IsCurrentGroupEmptyVisible);
        Assert.False(viewModel.IsAddonGridVisible);
        Assert.Null(viewModel.SelectedAsset);
        Assert.Contains(nameof(viewModel.IsCurrentGroupEmptyVisible), notifications);

        viewModel.ReturnToRoot();
        Assert.True(viewModel.IsAddonGridVisible);
        Assert.False(viewModel.IsCurrentGroupEmptyVisible);
    }

    [Fact]
    public async Task ChildGroupWithoutDirectAssetsShowsOverviewAndBuildsFullBreadcrumb()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var root = new AssetGroup("Root") { Id = "nav-root", SortOrder = 0 };
        var child = new AssetGroup("Child")
        {
            Id = "nav-child",
            ParentGroupId = root.Id,
            SortOrder = 0
        };
        configuration.AssetGroups.AddRange([root, child]);

        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();

        Assert.Contains(viewModel.Entries, entry => entry.Id == root.Id);
        Assert.DoesNotContain(viewModel.Entries, entry => entry.Id == child.Id);

        viewModel.OpenGroup(root.Id);
        var childEntry = Assert.Single(viewModel.Entries);
        Assert.True(childEntry.IsGroup);
        Assert.False(viewModel.IsCurrentGroupEmpty);
        Assert.True(viewModel.IsCurrentGroupEmptyVisible);
        Assert.False(viewModel.IsAddonGridVisible);

        viewModel.OpenGroup(child.Id);
        Assert.Equal(
            [L.Get("AssetList.Header"), "Root", "Child"],
            viewModel.Breadcrumbs.Select(item => item.Name));
        Assert.True(viewModel.Breadcrumbs[^1].IsCurrent);

        ((System.Windows.Input.ICommand)viewModel.BackCommand).Execute(null);
        Assert.Equal("nav-root", viewModel.CurrentGroupId);

        ((System.Windows.Input.ICommand)viewModel.BackCommand).Execute(null);
        Assert.True(viewModel.IsAtRoot);

        viewModel.OpenGroup(child.Id);
        ((System.Windows.Input.ICommand)viewModel.Breadcrumbs[1].NavigateCommand).Execute(null);
        Assert.Equal("nav-root", viewModel.CurrentGroupId);

        viewModel.OpenGroup(child.Id);
        ((System.Windows.Input.ICommand)viewModel.Breadcrumbs[0].NavigateCommand).Execute(null);
        Assert.True(viewModel.IsAtRoot);
    }

    [Fact]
    public void CreateAndDetailsDialogsKeepNamesBoundedAndValidateBeforeClosing()
    {
        var create = LoadXaml("SimpleAssetCreateDialog.axaml");
        var createName = create.Descendants(AvaloniaNamespace + "TextBox")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "AssetNameTextBox");
        Assert.Equal("200", (string?)createName.Attribute("MaxLength"));
        Assert.Contains("AssetTargetRadio", create.ToString(), StringComparison.Ordinal);
        Assert.Contains("GroupTargetRadio", create.ToString(), StringComparison.Ordinal);
        Assert.Contains("NameValidationText", create.ToString(), StringComparison.Ordinal);

        var details = LoadXaml("AssetGroupEditDialog.axaml");
        var detailsText = details.ToString();
        var editName = details.Descendants(AvaloniaNamespace + "TextBox")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "GroupNameTextBox");
        Assert.Equal("200", (string?)editName.Attribute("MaxLength"));
        Assert.Contains("AssetGroup.DetailsTitle", detailsText, StringComparison.Ordinal);
        var detailsPage = details.Descendants(AvaloniaNamespace + "Grid")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "DetailsPage");
        var structurePage = details.Descendants(AvaloniaNamespace + "Grid")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "StructurePage");
        Assert.NotNull(detailsPage);
        Assert.Equal("False", (string?)structurePage.Attribute("IsVisible"));
        Assert.Contains("StructureMemberItemsControl", detailsText, StringComparison.Ordinal);
        Assert.Contains("MemoTextBox", detailsText, StringComparison.Ordinal);
        Assert.True(
            detailsText.IndexOf("MemoTextBox", StringComparison.Ordinal) <
            detailsText.IndexOf("GroupNameTextBox", StringComparison.Ordinal));
        Assert.Contains("OnEditStructure", detailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("OnChangeImage", detailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRemoveImage", detailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedImageText", detailsText, StringComparison.Ordinal);

        var detailsCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetGroupEditDialog.axaml.cs");
        Assert.DoesNotContain("SourceImagePath", detailsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveImage", detailsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetImageCrop", detailsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAssetGroupMembersAsync", detailsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetGroupStructureDialog", detailsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog<AssetGroupStructureEditResult", detailsCode, StringComparison.Ordinal);
        Assert.Contains("DetailsPage.IsVisible = false;", detailsCode, StringComparison.Ordinal);
        Assert.Contains("StructurePage.IsVisible = true;", detailsCode, StringComparison.Ordinal);
        Assert.Contains("pendingMemberAssetIds", detailsCode, StringComparison.Ordinal);

        var groupViewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetGroupItemViewModel.cs");
        Assert.Contains("result.MemberAssetIds", groupViewModel, StringComparison.Ordinal);
        Assert.Contains("result.MemberGroupIds", groupViewModel, StringComparison.Ordinal);

        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "SimpleAssetCreateDialog.axaml.cs");
        Assert.Contains("nameValidator?.Invoke", source, StringComparison.Ordinal);
        Assert.Contains("SelectedGroupMemberAssetIds", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task StructureEditorSwitchesPagesInPlaceAndOnlyApplyChangesPendingMembers()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        var group = new AssetGroup("Target Group") { Id = "target-group" };
        configuration.AssetGroups.Add(group);
        configuration.Assets.AddRange(
        [
            new Asset("Already inside")
            {
                Id = "inside-asset",
                ParentGroupId = group.Id,
                SortOrder = 0
            },
            new Asset("Sibling")
            {
                Id = "sibling-asset",
                SortOrder = 1
            }
        ]);

        var dialog = new Views.AssetGroupEditDialog(
            group,
            configuration,
            manager,
            _ => null);
        dialog.Show();
        try
        {
            var detailsPage = Assert.IsType<Grid>(dialog.FindControl<Grid>("DetailsPage"));
            var structurePage = Assert.IsType<Grid>(dialog.FindControl<Grid>("StructurePage"));
            var editButton = Assert.IsType<Button>(
                dialog.FindControl<Button>("EditStructureButton"));
            var backButton = Assert.IsType<Button>(
                dialog.FindControl<Button>("StructureBackButton"));
            var applyButton = Assert.IsType<Button>(
                dialog.FindControl<Button>("StructureApplyButton"));
            var memberItems = Assert.IsType<ItemsControl>(
                dialog.FindControl<ItemsControl>("StructureMemberItemsControl"));
            var nameBox = Assert.IsType<TextBox>(
                dialog.FindControl<TextBox>("GroupNameTextBox"));
            var memoBox = Assert.IsType<TextBox>(
                dialog.FindControl<TextBox>("MemoTextBox"));
            CheckBox FindOptionCheckBox(string id) => Assert.Single(
                memberItems.GetVisualDescendants().OfType<CheckBox>(),
                checkBox =>
                    checkBox.DataContext is Views.AssetGroupStructureOption option &&
                    option.Id == id);

            nameBox.Text = "Pending rename";
            memoBox.Text = "Pending memo";
            editButton.Focus();
            Assert.True(editButton.IsFocused);
            editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            Assert.False(detailsPage.IsVisible);
            Assert.True(structurePage.IsVisible);
            Assert.Empty(dialog.OwnedWindows);
            Assert.Equal(L.Get("AssetGroup.EditStructure"), dialog.Title);
            Assert.True(backButton.IsFocused);

            var options = Assert.IsAssignableFrom<IEnumerable<Views.AssetGroupStructureOption>>(
                    memberItems.ItemsSource)
                .ToList();
            Assert.True(options.Single(option => option.Id == "inside-asset").IsSelected);
            FindOptionCheckBox("sibling-asset").IsChecked = true;
            Assert.True(options.Single(option => option.Id == "sibling-asset").IsSelected);

            backButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(detailsPage.IsVisible);
            Assert.False(structurePage.IsVisible);
            Assert.Equal("Pending rename", nameBox.Text);
            Assert.Equal("Pending memo", memoBox.Text);
            Assert.Equal(L.Get("AssetGroup.DetailsTitle"), dialog.Title);
            Assert.True(editButton.IsFocused);

            editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            options = Assert.IsAssignableFrom<IEnumerable<Views.AssetGroupStructureOption>>(
                    memberItems.ItemsSource)
                .ToList();
            Assert.False(options.Single(option => option.Id == "sibling-asset").IsSelected);
            FindOptionCheckBox("sibling-asset").IsChecked = true;
            Assert.True(options.Single(option => option.Id == "sibling-asset").IsSelected);

            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(detailsPage.IsVisible);
            Assert.False(structurePage.IsVisible);

            editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            options = Assert.IsAssignableFrom<IEnumerable<Views.AssetGroupStructureOption>>(
                    memberItems.ItemsSource)
                .ToList();
            Assert.True(options.Single(option => option.Id == "sibling-asset").IsSelected);
            Assert.Empty(dialog.OwnedWindows);
        }
        finally
        {
            dialog.Close();
        }
    }

    [Fact]
    public void GroupCardUsesLayerGlyphAndPersistentImageBadge()
    {
        var document = LoadXaml("AssetListView.axaml");
        var xaml = document.ToString();
        var code = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml.cs");
        var groupTile = document.Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                (string?)element.Attribute("IsVisible") == "{Binding IsGroup}" &&
                (string?)element.Attribute("PointerPressed") == "OnEntryImagePointerPressed");
        var defaultIcon = groupTile.Descendants(AvaloniaNamespace + "Grid")
            .Single(element =>
                (string?)element.Attribute("Classes") == "groupDefaultIcon");
        var customImageBadge = groupTile.Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                (string?)element.Attribute("Classes") == "groupImageBadge");
        var summary = document.Descendants(AvaloniaNamespace + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") == "{Binding AddonCountDisplay}");

        Assert.DoesNotContain("GroupBadgeText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            groupTile.Descendants(AvaloniaNamespace + "TextBlock"),
            element => string.Equals(
                (string?)element.Attribute("Text"),
                "GROUP",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Classes.group=\"{Binding IsGroup}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderIcon", groupTile.ToString(), StringComparison.Ordinal);
        Assert.Equal("{Binding HasNoCustomImage}", (string?)defaultIcon.Attribute("IsVisible"));
        Assert.Equal("False", (string?)defaultIcon.Attribute("IsHitTestVisible"));
        Assert.Equal("44", (string?)defaultIcon.Attribute("Width"));
        Assert.Empty(defaultIcon.Descendants(AvaloniaNamespace + "TextBlock"));
        Assert.Single(
            defaultIcon.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupLayerBack");
        Assert.Single(
            defaultIcon.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupLayerMiddle");
        Assert.Single(
            defaultIcon.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupLayerFront");
        Assert.Equal("{Binding HasCustomImage}", (string?)customImageBadge.Attribute("IsVisible"));
        Assert.Equal("False", (string?)customImageBadge.Attribute("IsHitTestVisible"));
        Assert.Equal("22", (string?)customImageBadge.Attribute("Width"));
        Assert.Equal("22", (string?)customImageBadge.Attribute("Height"));
        Assert.Equal("0,0,4,4", (string?)customImageBadge.Attribute("Margin"));
        Assert.Equal("Right", (string?)customImageBadge.Attribute("HorizontalAlignment"));
        Assert.Equal("Bottom", (string?)customImageBadge.Attribute("VerticalAlignment"));
        Assert.Single(
            customImageBadge.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupBadgeLayerBack");
        Assert.Single(
            customImageBadge.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupBadgeLayerMiddle");
        Assert.Single(
            customImageBadge.Descendants(),
            element => (string?)element.Attribute("Classes") == "groupBadgeLayerFront");
        Assert.DoesNotContain("Text=\"›\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetGroup.OpenTooltip", xaml, StringComparison.Ordinal);
        Assert.Contains("listViewModel.OpenGroup(entry)", code, StringComparison.Ordinal);
        Assert.Equal("1", (string?)summary.Attribute("MaxLines"));
        Assert.Equal("CharacterEllipsis", (string?)summary.Attribute("TextTrimming"));
        Assert.Equal(
            "{Binding AddonCountDisplay}",
            (string?)summary.Attribute("ToolTip.Tip"));
        Assert.Equal("Grid", summary.Parent?.Name.LocalName);
        Assert.Equal("True", (string?)summary.Parent?.Attribute("ClipToBounds"));
        Assert.Equal(
            2,
            xaml.Split(
                "AutomationProperties.Name=\"{loc:Localize AssetList.Edit}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "IsHitTestVisible=\"{Binding CanEditImage}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            xaml.Split(
                "Name, Converter={StaticResource FirstCharacterConverter}",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void GroupDeleteDialogKeepsAssetsByDefaultAndMakesCascadeExplicit()
    {
        var dialog = LoadXaml("AssetGroupDeleteDialog.axaml");
        var keep = dialog.Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "KeepAssetsButton");
        var deleteAssets = dialog.Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "DeleteAssetsButton");
        var cancel = dialog.Descendants(AvaloniaNamespace + "Button")
            .Single(element => (string?)element.Attribute("IsCancel") == "True");

        Assert.Equal("True", (string?)keep.Attribute("IsDefault"));
        Assert.Contains("destructive", (string?)deleteAssets.Attribute("Classes"));
        Assert.Equal("OnDeleteAssets", (string?)deleteAssets.Attribute("Click"));
        Assert.Equal("OnCancel", (string?)cancel.Attribute("Click"));

        var viewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetGroupItemViewModel.cs");
        Assert.Contains("AssetGroupDeleteMode.DeleteAssets", viewModel, StringComparison.Ordinal);
        Assert.Contains("DeleteAssetGroupAsync(Id, mode)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void AddonCardUsesColorAndAccessibleTooltipWithoutRedundantActualStateBadge()
    {
        var document = LoadXaml("AddonGridView.axaml");
        var xaml = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AddonGridView.axaml");
        var card = document.Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                (string?)element.Attribute("PointerPressed") == "OnAddonPointerPressed");

        Assert.Equal(
            "{Binding RuntimeStateTooltip}",
            (string?)card.Attribute("ToolTip.Tip"));
        Assert.Equal(
            "{Binding RuntimeStateTooltip}",
            (string?)card.Attribute("AutomationProperties.HelpText"));
        Assert.DoesNotContain("ActualStateBadgeText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualStateBadgeBackground", xaml, StringComparison.Ordinal);
        Assert.Contains("HasAssetContextNotice", xaml, StringComparison.Ordinal);
        Assert.Contains("AssetContextNoticeText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWiresBreadcrumbNavigationCollapseAndDirectDragReorderFeedback()
    {
        var document = LoadXaml("AssetListView.axaml");
        var xaml = document.ToString();
        var code = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml.cs");
        var viewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");
        var headerLayout = document.Descendants(AvaloniaNamespace + "Grid")
            .Single(element =>
                (string?)element.Attribute("Classes") == "assetHeaderLayout");
        var breadcrumbRow = headerLayout.Elements(AvaloniaNamespace + "Grid")
            .Single(element =>
                (string?)element.Attribute("Classes") == "breadcrumbRow");
        var breadcrumbScroller = breadcrumbRow.Descendants(AvaloniaNamespace + "ScrollViewer")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "BreadcrumbScrollViewer");
        var actions = headerLayout.Elements(AvaloniaNamespace + "WrapPanel").Single();

        Assert.Contains("ItemsSource=\"{Binding Entries}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Breadcrumbs}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NavigateCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding BackCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsInsideGroup}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ReturnToParent", viewModel, StringComparison.Ordinal);
        Assert.Contains("OnToggleGmodDisabledCollapse", xaml, StringComparison.Ordinal);
        Assert.Contains("InsertionMarker", xaml, StringComparison.Ordinal);
        Assert.Contains("HasExceededReorderDragThreshold", code, StringComparison.Ordinal);
        Assert.Contains("BeginReorderDrag", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReorderHoldDuration", code, StringComparison.Ordinal);
        Assert.DoesNotContain("reorderHoldTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnReorderHoldElapsed", code, StringComparison.Ordinal);
        Assert.Contains("GetClampedReorderTargetIndex", viewModel, StringComparison.Ordinal);
        Assert.Contains("CollapseGmodDisabledAddons", viewModel, StringComparison.Ordinal);
        Assert.Equal("Auto,Auto", (string?)headerLayout.Attribute("RowDefinitions"));
        Assert.Equal("True", (string?)headerLayout.Attribute("ClipToBounds"));
        Assert.Equal("True", (string?)breadcrumbRow.Attribute("ClipToBounds"));
        Assert.Equal("Hidden", (string?)breadcrumbScroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("True", (string?)breadcrumbScroller.Attribute("ClipToBounds"));
        Assert.Equal("OnBreadcrumbPointerWheelChanged", (string?)breadcrumbScroller.Attribute("PointerWheelChanged"));
        Assert.Equal("1", (string?)actions.Attribute("Grid.Row"));
        Assert.Equal("Right", (string?)actions.Attribute("HorizontalAlignment"));
        Assert.Contains("Breadcrumbs.CollectionChanged", code, StringComparison.Ordinal);
        Assert.Contains("ScrollBreadcrumbToEnd", code, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"128\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"168\"", xaml, StringComparison.Ordinal);

        var mainWindow = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "MainWindow.axaml");
        Assert.Contains("AssetListViewModel.IsCurrentGroupEmptyVisible", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AssetListViewModel.CurrentGroupEmptyText", mainWindow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(7, 0, false)]
    [InlineData(0, -7, false)]
    [InlineData(7.01, 0, true)]
    [InlineData(0, -7.01, true)]
    public void DirectDragStartsOnlyAfterPointerMovementExceedsThreshold(
        double deltaX,
        double deltaY,
        bool expected)
    {
        Assert.Equal(
            expected,
            Views.AssetListView.HasExceededReorderDragThreshold(deltaX, deltaY));
    }

    [AvaloniaFact]
    public async Task PointerDragReordersCustomCardsWithoutAHoldDelay()
    {
        using var manager = await CreateManagerAsync();
        var configuration = manager.GetConfiguration();
        configuration.Assets.AddRange(
        [
            new Asset("First") { Id = "drag-first", SortOrder = 0 },
            new Asset("Second") { Id = "drag-second", SortOrder = 1 }
        ]);
        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();

        var view = new Views.AssetListView { DataContext = viewModel };
        var window = new Window
        {
            Content = view,
            Width = 420,
            Height = 720
        };
        window.Show();
        try
        {
            var cards = view.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("assetEntryCard"))
                .Where(border => border.DataContext is AssetListEntryViewModel)
                .ToDictionary(
                    border => ((AssetListEntryViewModel)border.DataContext!).Id,
                    StringComparer.Ordinal);
            var first = cards["drag-first"];
            var second = cards["drag-second"];
            var start = first.TranslatePoint(
                new Point(Math.Min(120, first.Bounds.Width / 2), 18),
                window)!.Value;
            var end = second.TranslatePoint(
                new Point(Math.Min(120, second.Bounds.Width / 2), second.Bounds.Height - 3),
                window)!.Value;

            window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);

            await WaitUntilAsync(() =>
                viewModel.GetReorderableEntries().Select(entry => entry.Id)
                    .SequenceEqual(["drag-second", "drag-first"]));
            Assert.Equal(
                ["drag-second", "drag-first"],
                viewModel.GetReorderableEntries().Select(entry => entry.Id));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SmallPointerMovementRemainsAClickAndOpensTheGroup()
    {
        using var manager = await CreateManagerAsync();
        manager.GetConfiguration().AssetGroups.Add(new AssetGroup("Open me")
        {
            Id = "click-group",
            SortOrder = 0
        });
        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings());
        viewModel.LoadAssets();

        var view = new Views.AssetListView { DataContext = viewModel };
        var window = new Window
        {
            Content = view,
            Width = 420,
            Height = 720
        };
        window.Show();
        try
        {
            var card = view.GetVisualDescendants()
                .OfType<Border>()
                .Single(border =>
                    border.Classes.Contains("assetEntryCard") &&
                    border.DataContext is AssetListEntryViewModel entry &&
                    entry.Id == "click-group");
            var start = card.TranslatePoint(
                new Point(Math.Min(120, card.Bounds.Width / 2), 18),
                window)!.Value;
            var end = start + new Vector(5, 0);

            window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);

            Assert.True(viewModel.IsInsideGroup);
            Assert.Equal("Open me", viewModel.CurrentHeader);
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatingGroupAsksBeforeLeavingTheCurrentContainer(bool openCreatedGroup)
    {
        using var manager = await CreateManagerAsync();
        var existingAsset = await manager.CreateAssetAsync("Existing Asset");
        var dialogs = new RecordingDialogService(openCreatedGroup);
        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings(),
            dialogService: dialogs);
        viewModel.LoadAssets();

        var createdGroup = await viewModel.CreateAssetGroupAndConfirmNavigationAsync(
            "Created Group",
            [existingAsset.Id],
            Array.Empty<string>());

        Assert.Equal(1, dialogs.ConfirmCallCount);
        Assert.Equal(L.Get("Success.Title"), dialogs.LastConfirmTitle);
        Assert.Equal(
            L.Format("Confirm.OpenCreatedGroup", createdGroup.Name),
            dialogs.LastConfirmMessage);
        Assert.Equal(createdGroup.Id, manager.GetConfiguration().Assets
            .Single(asset => asset.Id == existingAsset.Id)
            .ParentGroupId);

        if (openCreatedGroup)
        {
            Assert.Equal(createdGroup.Id, viewModel.CurrentGroupId);
            Assert.Contains(viewModel.Entries, entry => entry.Id == existingAsset.Id);
        }
        else
        {
            Assert.Null(viewModel.CurrentGroupId);
            Assert.Contains(viewModel.Entries, entry => entry.Id == createdGroup.Id);
            Assert.DoesNotContain(viewModel.Entries, entry => entry.Id == existingAsset.Id);
        }
    }

    [Fact]
    public async Task PromptFailureDoesNotMisreportAnAlreadyCreatedGroup()
    {
        using var manager = await CreateManagerAsync();
        var dialogs = new RecordingDialogService(
            confirmResult: false,
            confirmException: new InvalidOperationException("simulated prompt failure"));
        using var viewModel = new AssetListViewModel(
            manager,
            null!,
            null!,
            new AppSettings(),
            dialogService: dialogs);
        viewModel.LoadAssets();

        var createdGroup = await viewModel.CreateAssetGroupAndConfirmNavigationAsync(
            "Committed Group",
            Array.Empty<string>(),
            Array.Empty<string>());

        Assert.Equal(1, dialogs.ConfirmCallCount);
        Assert.Null(viewModel.CurrentGroupId);
        Assert.Contains(
            manager.GetConfiguration().AssetGroups,
            group => group.Id == createdGroup.Id);
        Assert.Contains(viewModel.Entries, entry => entry.Id == createdGroup.Id);
    }

    [Fact]
    public void AssetAndGroupIconsUseAnImageOnlyCommandAndDialog()
    {
        var listXaml = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml");
        var listCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml.cs");
        var entry = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListEntryViewModel.cs");
        var group = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetGroupItemViewModel.cs");
        var imageDialog = LoadXaml("AssetEditDialog.axaml").ToString();
        var imageDialogCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetEditDialog.axaml.cs");
        var assetDetails = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetDetailsDialog.axaml");

        Assert.Equal(
            2,
            listXaml.Split("PointerPressed=\"OnEntryImagePointerPressed\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("entry.EditImageCommand", listCode, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.EditCommand", listCode, StringComparison.Ordinal);
        Assert.Contains("public ICommand EditImageCommand", entry, StringComparison.Ordinal);
        Assert.Contains("ShowDetailsCommand = ReactiveCommand.CreateFromTask(ShowDetailsAsync)", group, StringComparison.Ordinal);
        Assert.Contains("EditImageCommand = ReactiveCommand.CreateFromTask(EditImageAsync)", group, StringComparison.Ordinal);
        Assert.DoesNotContain("EditCommand = ShowDetailsCommand", group, StringComparison.Ordinal);

        Assert.Contains("AssetEdit.Title", imageDialog, StringComparison.Ordinal);
        Assert.Contains("OnChangeImage", imageDialog, StringComparison.Ordinal);
        Assert.Contains("OnRemoveImage", imageDialog, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetNameTextBox", imageDialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Name =", imageDialogCode, StringComparison.Ordinal);
        Assert.Contains("AssetName, Mode=TwoWay", assetDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageDetailsAndBackLabelsRemainExplicitInBothLanguages()
    {
        var japanese = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            ReadRepositoryFile("src", "GmodAddonManager.UI", "Resources", "ja-JP.json"))!;
        var english = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            ReadRepositoryFile("src", "GmodAddonManager.UI", "Resources", "en-US.json"))!;

        Assert.Equal("アセットグループの詳細", japanese["AssetGroup.DetailsTitle"]);
        Assert.Equal("画像設定", japanese["AssetEdit.Title"]);
        Assert.Equal("画像を変更", japanese["AssetList.Edit"]);
        Assert.Equal("ひとつ上へ戻る", japanese["AssetGroup.BackTooltip"]);
        Assert.Equal(
            "詳細・構成を編集",
            japanese["AssetGroup.DetailsAndStructureTooltip"]);
        Assert.Equal("Asset Group Details", english["AssetGroup.DetailsTitle"]);
        Assert.Equal("Image Settings", english["AssetEdit.Title"]);
        Assert.Equal("Change Image", english["AssetList.Edit"]);
        Assert.Equal("Back one level", english["AssetGroup.BackTooltip"]);
        Assert.Equal(
            "View details and edit structure",
            english["AssetGroup.DetailsAndStructureTooltip"]);
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private sealed class RecordingDialogService(
        bool confirmResult,
        Exception? confirmException = null) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public string? LastConfirmTitle { get; private set; }
        public string? LastConfirmMessage { get; private set; }

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            if (confirmException != null)
            {
                return Task.FromException<bool>(confirmException);
            }
            return Task.FromResult(confirmResult);
        }
    }

    private static XDocument LoadXaml(string fileName)
    {
        return XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", fileName));
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
