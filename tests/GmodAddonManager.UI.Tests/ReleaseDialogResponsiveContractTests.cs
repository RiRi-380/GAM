using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace GmodAddonManager.UI.Tests;

public sealed class ReleaseDialogResponsiveContractTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    [Theory]
    [InlineData("StartupPathRecoveryDialog.axaml", 480, 360)]
    [InlineData("PathHealthDialog.axaml", 480, 360)]
    [InlineData("VersionManagementDialog.axaml", 520, 360)]
    [InlineData("VersionDetailsDialog.axaml", 480, 360)]
    public void DialogMinimumFitsInside640By480WorkingArea(
        string fileName,
        int expectedMinWidth,
        int expectedMinHeight)
    {
        var document = LoadView(fileName);
        var window = document.Root!;

        Assert.Equal(expectedMinWidth, (int?)window.Attribute("MinWidth"));
        Assert.Equal(expectedMinHeight, (int?)window.Attribute("MinHeight"));
        Assert.InRange((int?)window.Attribute("MinWidth") ?? int.MaxValue, 1, 640);
        Assert.InRange((int?)window.Attribute("MinHeight") ?? int.MaxValue, 1, 480);
    }

    [Theory]
    [InlineData("StartupPathRecoveryDialog.axaml")]
    [InlineData("PathHealthDialog.axaml")]
    public void RequiredPathDialogScrollsBodyWhileKeepingFooterOutsideScroller(
        string fileName)
    {
        var document = LoadView(fileName);
        var rootGrid = Assert.Single(
            document.Root!.Elements(AvaloniaNamespace + "Grid"));
        var bodyScroller = Assert.Single(
            rootGrid.Elements(AvaloniaNamespace + "ScrollViewer"));

        Assert.Equal("1", (string?)bodyScroller.Attribute("Grid.Row"));
        Assert.Equal("Disabled", (string?)bodyScroller.Attribute(
            "HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)bodyScroller.Attribute(
            "VerticalScrollBarVisibility"));
        Assert.Contains(
            rootGrid.Elements(AvaloniaNamespace + "Grid"),
            element => (string?)element.Attribute("Grid.Row") == "2");
    }

    [Fact]
    public void VersionManagementUsesCompactSidebarAndWrappingSortControls()
    {
        var document = LoadView("VersionManagementDialog.axaml");
        var firstColumn = document
            .Descendants(AvaloniaNamespace + "ColumnDefinition")
            .First();

        Assert.Equal("260", (string?)firstColumn.Attribute("Width"));
        Assert.Equal("220", (string?)firstColumn.Attribute("MinWidth"));
        Assert.Contains(
            document.Descendants(AvaloniaNamespace + "WrapPanel"),
            panel => (string?)panel.Attribute("Orientation") == "Horizontal");
        Assert.True(document.Descendants(AvaloniaNamespace + "ScrollViewer").Count() >= 2);
    }

    private static XDocument LoadView(string fileName) =>
        XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", fileName));

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
