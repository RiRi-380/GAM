using System.Text.Json;

namespace GmodAddonManager.UI.Tests;

public sealed class MemberHistoryUiContractTests
{
    [Fact]
    public void AssetCardsDoNotExposeMembershipHistoryControls()
    {
        var source = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetListView.axaml"));

        Assert.DoesNotContain("VersionDisplay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionManageCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedAssetDetailsOwnTheExperimentalHistoryEntryPoint()
    {
        var xaml = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml"));
        var codeBehind = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml.cs"));

        Assert.Contains("x:Name=\"MemberHistoryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanManageMemberHistory}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new VersionManagementDialog(model, addonManager)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("model.IsSystem || model.IsSmart", codeBehind, StringComparison.Ordinal);

        var assetItem = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AssetItemViewModel.cs"));
        Assert.Contains("EnableMemberHistoryExperimental", assetItem, StringComparison.Ordinal);
        Assert.Contains(
            "memberHistoryExperimentalEnabled && !IsSystem && !IsSmart",
            assetItem,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US.json", "History")]
    [InlineData("ja-JP.json", "履歴")]
    public void LocalizationNamesTheFeatureHistory(
        string fileName,
        string expectedName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Resources",
            fileName)));

        Assert.Equal(
            expectedName,
            document.RootElement.GetProperty("AssetDetails.History").GetString());
        Assert.Contains(
            expectedName,
            document.RootElement.GetProperty("VersionManagement.Title").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsExposeHistoryAsAnImmediateExperimentalOptIn()
    {
        var xaml = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml"));
        var codeBehind = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs"));
        var mainViewModel = File.ReadAllText(RepoFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs"));

        Assert.Contains("MemberHistoryExperimentalCheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableMemberHistoryExperimental", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AssetItemViewModel.ApplyGlobalSettings(AppSettings.Load())", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("AssetItemViewModel.ApplyGlobalSettings(updatedSettings)", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("asset.NotifySettingsChanged()", mainViewModel, StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] segments)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments));
    }
}
