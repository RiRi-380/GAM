using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class UpdateDialogVisualContractTests
{
    [Fact]
    public void UpdateDialogUsesOpaqueDefinedThemeResources()
    {
        var dialogPath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "UpdateDialog.axaml");

        var xaml = File.ReadAllText(dialogPath);

        Assert.DoesNotContain("{DynamicResource AccentBrush}", xaml);
        Assert.DoesNotContain("{DynamicResource BackgroundBrush}", xaml);
        Assert.DoesNotContain("{DynamicResource BorderBrush}", xaml);

        Assert.Contains("SystemControlBackgroundAltHighBrush", xaml);
        Assert.Contains("SystemAccentColor", xaml);
        Assert.Contains("SystemControlBackgroundChromeMediumBrush", xaml);
        Assert.Contains("SystemControlForegroundBaseMediumLowBrush", xaml);

        var document = XDocument.Parse(xaml);
        XNamespace avalonia = "https://github.com/avaloniaui";
        var window = document.Root ?? throw new InvalidOperationException("UpdateDialog.axaml has no root element.");

        Assert.Equal(avalonia + "Window", window.Name);
        Assert.Equal(
            "{DynamicResource SystemControlBackgroundAltHighBrush}",
            window.Attribute("Background")?.Value);

        var rootGrid = window.Elements(avalonia + "Grid").FirstOrDefault()
            ?? throw new InvalidOperationException("UpdateDialog.axaml has no root Grid.");

        Assert.Equal(
            "{DynamicResource SystemControlBackgroundAltHighBrush}",
            rootGrid.Attribute("Background")?.Value);
    }

    private static string FindRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
