using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class LocalAddonUiContractTests
{
    [Fact]
    public void ReadOnlyLocalAddonsAreVisibleButCannotEnterAssetSelection()
    {
        var gridViewModel = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");
        var itemViewModel = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonItemViewModel.cs");
        var gridView = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml");
        var gridViewCodeBehind = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml.cs");

        Assert.Contains(
            ".Where(addon => !addon.IsDownloadPending)",
            gridViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (addon != null && !addon.IsLocal)",
            gridViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "!addon.IsLocal && !addon.IsSelected",
            gridViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilteredAddons.Where(a => a.IsSelected && !a.IsLocal)",
            gridViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Local addon (read-only in GAM)",
            ReadRepositoryFile(
                "src",
                "GmodAddonManager.UI",
                "Resources",
                "en-US.json"),
            StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsLocal}\"", gridView, StringComparison.Ordinal);
        Assert.Contains("Classes.local=\"{Binding IsLocal}\"", gridView, StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip.Tip=\"{loc:Localize AddonGrid.LocalReadOnlyReason}\"",
            gridView,
            StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"Border.local\">", gridView, StringComparison.Ordinal);
        Assert.Contains("actualEnabled = IsLocal ? addon.IsEnabled", itemViewModel, StringComparison.Ordinal);

        var pointerPressedStart = gridViewCodeBehind.IndexOf(
            "private void OnAddonPointerPressed",
            StringComparison.Ordinal);
        var pointerMovedStart = gridViewCodeBehind.IndexOf(
            "private async void OnAddonPointerMoved",
            pointerPressedStart,
            StringComparison.Ordinal);
        var localGuard = gridViewCodeBehind.IndexOf(
            "if (addonVm.IsLocal)",
            pointerPressedStart,
            StringComparison.Ordinal);
        var selection = gridViewCodeBehind.IndexOf(
            "gridVm.SelectAddon(addonVm.AddonId, isCtrlPressed);",
            pointerPressedStart,
            StringComparison.Ordinal);
        var dragStart = gridViewCodeBehind.IndexOf(
            "_dragStartPoint = point.Position;",
            pointerPressedStart,
            StringComparison.Ordinal);

        Assert.True(pointerPressedStart >= 0);
        Assert.True(pointerMovedStart > pointerPressedStart);
        Assert.InRange(localGuard, pointerPressedStart, pointerMovedStart - 1);
        Assert.InRange(selection, localGuard + 1, pointerMovedStart - 1);
        Assert.InRange(dragStart, localGuard + 1, pointerMovedStart - 1);
    }

    private static string ReadRepositoryFile(
        string segment1,
        string segment2,
        string segment3,
        string fileName,
        [CallerFilePath] string sourceFilePath = "")
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        return File.ReadAllText(
            Path.Combine(repositoryRoot, segment1, segment2, segment3, fileName));
    }
}
