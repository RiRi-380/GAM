using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public partial class BulkAssetStateDialog : Window
{
    private readonly Dictionary<CheckBox, Asset> choices = new();

    public BulkAssetStateDialog() => InitializeComponent();

    public BulkAssetStateDialog(IEnumerable<Asset> assets, bool previewEnabled) : this()
    {
        ApplyButton.Content = L.Get(previewEnabled ? "Bulk.Preview" : "Bulk.Apply");
        foreach (var asset in assets.Where(asset => !asset.IsSystem)
                     .OrderBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var state = L.Get($"AssetList.{asset.State}");
            var choice = new CheckBox { Content = $"{asset.Name}   ({state})" };
            choices.Add(choice, asset);
            choice.IsCheckedChanged += OnSelectionChanged;
            AssetChoices.Children.Add(choice);
        }
    }

    private void OnSelectionChanged(object? sender, RoutedEventArgs e)
    {
        var selected = choices.Where(pair => pair.Key.IsChecked == true).Select(pair => pair.Value).ToList();
        var states = selected.Select(asset => asset.State).Distinct().ToList();
        SelectionState.Text = selected.Count == 0 ? string.Empty : L.Format("Bulk.Selection", selected.Count,
            states.Count == 1 ? L.Get($"AssetList.{states[0]}") : L.Get("Bulk.Mixed"));
        ApplyButton.IsEnabled = selected.Count > 0;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var target = (AddonState)TargetState.SelectedIndex;
        Close(choices.Where(pair => pair.Key.IsChecked == true)
            .ToDictionary(pair => pair.Value.Id, _ => target, StringComparer.Ordinal));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
