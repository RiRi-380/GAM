using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class AccessibilityContractTests
{
    [Fact]
    public void MainToolbarIconButtonsExposeLocalizedAutomationNames()
    {
        var xaml = ReadUiFile("Views", "MainWindow.axaml");

        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize MainWindow.Settings}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize MainWindow.RescanAddons}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize MainWindow.UndoLastAction}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize MainWindow.AllOff}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpText=\"{loc:Localize MainWindow.AllOffTooltip}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AssetCardActionsExposeAutomationNames()
    {
        var xaml = ReadUiFile("Views", "AssetListView.axaml");

        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize AssetList.DetailsTooltip}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize GamShare.ToggleSelection}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize AssetList.DeleteTooltip}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding FavoriteButtonText}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AssetCardsAndImageEditorsHaveKeyboardPathsAndVisibleFocus()
    {
        var xaml = ReadUiFile("Views", "AssetListView.axaml");
        var codeBehind = ReadUiFile("Views", "AssetListView.axaml.cs");

        Assert.Contains("KeyDown=\"OnEntryKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnEntryImageKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Border.assetEntryCard:focus", xaml, StringComparison.Ordinal);
        Assert.Contains("Border.assetImage:focus", xaml, StringComparison.Ordinal);
        Assert.Contains("Border.groupImage:focus", xaml, StringComparison.Ordinal);
        Assert.Contains("e.Key is not (Key.Enter or Key.Space)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryExecuteEditImage(entry)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void AddonCardsAndResponsiveFilterButtonsAreKeyboardAccessible()
    {
        var xaml = ReadUiFile("Views", "AddonGridView.axaml");
        var codeBehind = ReadUiFile("Views", "AddonGridView.axaml.cs");

        Assert.Contains("AutomationProperties.Name=\"{Binding Title}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"addonCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnAddonKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Border.addonCard:focus", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize AddonGrid.OpenFilters}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize AddonGrid.CloseFilters}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("private void OnAddonKeyDown", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Key is not (Key.Enter or Key.Space)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("addonVm.IsLocal", codeBehind, StringComparison.Ordinal);
        Assert.Contains("gridVm.SelectAddon(", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionDeleteButtonExposesLocalizedAutomationName()
    {
        var xaml = ReadUiFile("Views", "VersionManagementDialog.axaml");

        Assert.Contains(
            "AutomationProperties.Name=\"{loc:Localize VersionManagement.DeleteVersionTooltip}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string ReadUiFile(params string[] relativePath)
    {
        var segments = new[]
        {
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI"
        }.Concat(relativePath).ToArray();
        return File.ReadAllText(Path.Combine(segments));
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "GmodAddonManager.UI",
                    "GmodAddonManager.UI.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
