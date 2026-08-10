using System.Text.Json;
using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class UiPolishContractTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    [Fact]
    public void AssetDetailsHeaderCanGrowAndProtectsLongAssetNames()
    {
        var document = LoadXaml("AssetDetailsDialog.axaml");
        var window = document.Root!;
        var header = document
            .Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                (string?)element.Attribute("DockPanel.Dock") == "Top");

        Assert.Equal("560", (string?)window.Attribute("MinWidth"));
        Assert.Equal("460", (string?)window.Attribute("MinHeight"));
        Assert.Null(header.Attribute("Height"));
        Assert.Equal("88", (string?)header.Attribute("MinHeight"));
        AssertEllipsisAndTooltip(document, "{Binding AssetName}");

        var closeButton = document
            .Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute("Click") == "OnClose");
        Assert.Equal(
            "{loc:Localize Dialog.Close}",
            (string?)closeButton.Attribute("Content"));
        Assert.True(string.IsNullOrWhiteSpace(closeButton.Value));
    }

    [Fact]
    public void VersionManagementUsesAnHonestWorkshopActionAndFooter()
    {
        var document = LoadXaml("VersionManagementDialog.axaml");
        var codeBehind = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views",
            "VersionManagementDialog.axaml.cs");
        var addonCard = document
            .Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                element.Descendants(AvaloniaNamespace + "ContextMenu").Any());
        var workshopAction = addonCard
            .Descendants(AvaloniaNamespace + "MenuItem")
            .Single();
        var closeButton = document
            .Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute("Click") == "OnCloseClick");

        Assert.Equal(
            "{loc:Localize VersionManagement.OpenWorkshop}",
            (string?)workshopAction.Attribute("Header"));
        Assert.Equal(
            "{Binding AddonItemViewModel.OpenWorkshopCommand}",
            (string?)workshopAction.Attribute("Command"));
        Assert.Null(addonCard.Attribute("PointerPressed"));
        Assert.DoesNotContain(
            addonCard.Descendants(AvaloniaNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Cursor");
        Assert.DoesNotContain(
            "OnAddonPointerPressed", codeBehind, StringComparison.Ordinal);
        Assert.Equal(
            "1", (string?)closeButton.Parent?.Attribute("Grid.Row"));

        AssertEllipsisAndTooltip(document, "{Binding AssetTitle}");
        AssertEllipsisAndTooltip(document, "{Binding SelectedVersionTitle}");
    }

    [Fact]
    public void AssetSelectionUsesLocalizedCountAndProtectsLongNames()
    {
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views",
            "AssetSelectionDialog.axaml");
        var document = XDocument.Parse(source);

        Assert.Contains(
            "Text=\"{Binding AddonCountDisplay}\"",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("{0} addons", source, StringComparison.Ordinal);
        var targetEntry = document
            .Descendants(AvaloniaNamespace + "Border")
            .Single(element =>
                (string?)element.Attribute("PointerReleased") ==
                "OnEntryPointerReleased");
        var targetName = targetEntry
            .Descendants(AvaloniaNamespace + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") == "{Binding Name}");
        Assert.Equal(
            "CharacterEllipsis",
            (string?)targetName.Attribute("TextTrimming"));
        Assert.Equal(
            "{Binding Name}",
            (string?)targetName.Attribute("ToolTip.Tip"));
    }

    [Fact]
    public void ReachableAssetScreensProtectUnboundedNames()
    {
        var assetList = LoadXaml("AssetListView.axaml");
        var detailsButton = assetList
            .Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute("Command") ==
                "{Binding ShowDetailsCommand}");

        Assert.Equal(
            "{Binding DetailsTooltip}",
            (string?)detailsButton.Attribute("ToolTip.Tip"));
        AssertEllipsisAndTooltip(assetList, "{Binding Name}");
        AssertEllipsisAndTooltip(
            LoadXaml("VersionDetailsDialog.axaml"), "{Binding AssetLabel}");
    }

    [Fact]
    public void SubscribeExcludeAllStateIsWiredAndCanUseTheEmptyActionColumn()
    {
        var assetList = LoadXaml("AssetListView.axaml");
        var excludedState = assetList
            .Descendants(AvaloniaNamespace + "RadioButton")
            .Single(element =>
                (string?)element.Attribute("Command") ==
                "{Binding SetExcludedCommand}");
        var stateGrid = excludedState.Ancestors(AvaloniaNamespace + "Grid").First();

        Assert.Equal(
            "{Binding ExcludedStateLabel}",
            (string?)excludedState.Attribute("Content"));
        Assert.Equal(
            "{Binding CanSetExcluded}",
            (string?)excludedState.Attribute("IsVisible"));
        Assert.Equal(
            "{Binding ExcludedStateTooltip}",
            (string?)excludedState.Attribute("ToolTip.Tip"));
        Assert.Equal(
            "{Binding ExcludedStateTooltip}",
            (string?)excludedState.Attribute("AutomationProperties.HelpText"));
        Assert.Equal(
            "{Binding StateColumnSpan}",
            (string?)stateGrid.Attribute("Grid.ColumnSpan"));
    }

    [Fact]
    public void SortControlsCanGrowWithoutClippingTheirContent()
    {
        var document = LoadXaml("AddonGridView.axaml");
        var sortMode = document
            .Descendants(AvaloniaNamespace + "ComboBox")
            .Single(element =>
                (string?)element.Attribute("ItemsSource") ==
                "{Binding SortModeOptions}");
        var sortDirection = document
            .Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute("Command") ==
                "{Binding ToggleSortDirectionCommand}");
        var displayedSortValue = document
            .Descendants(AvaloniaNamespace + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") ==
                "{Binding SortValueText}");

        Assert.Null(sortMode.Attribute("Height"));
        Assert.Equal("32", (string?)sortMode.Attribute("MinHeight"));
        Assert.Null(sortDirection.Attribute("Height"));
        Assert.Equal("32", (string?)sortDirection.Attribute("MinHeight"));
        Assert.Equal("10,4", (string?)sortDirection.Attribute("Padding"));
        Assert.Equal(
            "Center",
            (string?)sortDirection.Attribute("VerticalContentAlignment"));
        Assert.NotNull(displayedSortValue);
    }

    [Fact]
    public void JapaneseStaticLabelsAreLocalizedAndResourcesStayInParity()
    {
        var japanese = LoadResources("ja-JP.json");
        var english = LoadResources("en-US.json");

        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            japanese.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("アドオンタグ", japanese["AddonGrid.FilterTagHeader"]);
        Assert.Equal("アドオンの種類", japanese["AddonGrid.FilterTypeHeader"]);
        Assert.Equal("パス診断", japanese["Settings.PathHealthButton"]);
        Assert.Equal("パス診断", japanese["PathHealth.Title"]);
        Assert.Equal(
            "Steamのルートフォルダー", japanese["PathHealth.SteamRoot"]);
        Assert.Equal(
            "Garry's Modのインストール先", japanese["PathHealth.GmodInstall"]);
        Assert.Equal(
            "ワークショップのルートフォルダー",
            japanese["PathHealth.WorkshopRoot"]);
        Assert.Equal("キャッシュの場所", japanese["PathHealth.CachePath"]);
        Assert.Equal(
            "コピー元: {0}\nコピー先: {1}\n対象ID: {2}",
            japanese["PathHealth.AddonNoMountMigrationSummary"]);
        Assert.Equal(
            "ワークショップを開く",
            japanese["VersionManagement.OpenWorkshop"]);
        Assert.Equal(
            "Open Workshop", english["VersionManagement.OpenWorkshop"]);
        Assert.Equal(
            "詳細を表示",
            japanese["AssetList.DetailsTooltip"]);
        Assert.Equal(
            "Show details",
            english["AssetList.DetailsTooltip"]);
        Assert.DoesNotContain("VersionManagement.ShowAddonDetails", japanese);
        Assert.DoesNotContain("VersionManagement.ShowAddonDetails", english);
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", fileName));

    private static Dictionary<string, string> LoadResources(string fileName) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            ReadRepositoryFile(
                "src", "GmodAddonManager.UI", "Resources", fileName))
        ?? throw new InvalidDataException(
            $"Could not deserialize localization resource {fileName}.");

    private static void AssertEllipsisAndTooltip(
        XDocument document,
        string binding)
    {
        var textBlocks = document
            .Descendants(AvaloniaNamespace + "TextBlock")
            .Where(element =>
                (string?)element.Attribute("Text") == binding &&
                (string?)element.Attribute("ToolTip.Tip") == binding)
            .ToArray();

        Assert.NotEmpty(textBlocks);
        Assert.All(
            textBlocks,
            textBlock => Assert.Equal(
                "CharacterEllipsis",
                (string?)textBlock.Attribute("TextTrimming")));
    }

    private static string ReadRepositoryFile(
        string segment1,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment1, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
