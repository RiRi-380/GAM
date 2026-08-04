using Avalonia.Interactivity;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public enum AssetGroupDeleteChoice
{
    Cancel,
    KeepAssets,
    DeleteAssets
}

public partial class AssetGroupDeleteDialog : Avalonia.Controls.Window
{
    public AssetGroupDeleteDialog()
    {
        InitializeComponent();
        Configure("Asset Group", 1, 1, 2, 1);
    }

    public AssetGroupDeleteDialog(string groupName, int childCount)
        : this()
    {
        Configure(groupName, childCount, 0, childCount, 0);
    }

    public AssetGroupDeleteDialog(
        string groupName,
        int directAssetCount,
        int directGroupCount,
        int recursiveAssetCount,
        int recursiveGroupCount)
        : this()
    {
        Configure(
            groupName,
            directAssetCount,
            directGroupCount,
            recursiveAssetCount,
            recursiveGroupCount);
    }

    private void Configure(
        string groupName,
        int directAssetCount,
        int directGroupCount,
        int recursiveAssetCount,
        int recursiveGroupCount)
    {
        var normalizedDirectAssets = System.Math.Max(0, directAssetCount);
        var normalizedDirectGroups = System.Math.Max(0, directGroupCount);
        var normalizedRecursiveAssets = System.Math.Max(0, recursiveAssetCount);
        var normalizedRecursiveGroups = System.Math.Max(0, recursiveGroupCount);
        var hasContents = normalizedRecursiveAssets > 0 || normalizedRecursiveGroups > 0;
        QuestionText.Text = L.Format("AssetGroup.DeleteQuestion", groupName);
        DescriptionText.Text = !hasContents
            ? L.Get("AssetGroup.DeleteEmptyDescription")
            : L.Format(
                "AssetGroup.DeleteChoiceDescriptionNested",
                normalizedDirectAssets,
                normalizedDirectGroups,
                normalizedRecursiveAssets,
                normalizedRecursiveGroups);
        DeleteAssetsDescriptionText.Text = L.Format(
            "AssetGroup.DeleteWithContentsDescription",
            normalizedRecursiveAssets,
            normalizedRecursiveGroups);

        DeleteAssetsButton.IsVisible = hasContents;
        KeepAssetsTitleText.Text = !hasContents
            ? L.Get("AssetGroup.DeleteButton")
            : L.Get("AssetGroup.DeleteOnlyPromoteContents");
        KeepAssetsDescriptionText.Text = L.Format(
            "AssetGroup.DeleteOnlyPromoteContentsDescription",
            normalizedDirectAssets,
            normalizedDirectGroups);
        KeepAssetsDescriptionText.IsVisible = hasContents;
    }

    private void OnKeepAssets(object? sender, RoutedEventArgs e)
    {
        Close(AssetGroupDeleteChoice.KeepAssets);
    }

    private void OnDeleteAssets(object? sender, RoutedEventArgs e)
    {
        Close(AssetGroupDeleteChoice.DeleteAssets);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(AssetGroupDeleteChoice.Cancel);
    }
}
