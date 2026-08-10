using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.UI.Views;

public enum AssetCreationTarget
{
    Asset,
    AssetGroup
}

public sealed class AssetCreationGroupMemberOption
{
    public AssetCreationGroupMemberOption(string id, string name)
        : this(id, name, AssetListEntryKind.Asset, isFavorite: false, sortOrder: int.MaxValue)
    {
    }

    public AssetCreationGroupMemberOption(
        string id,
        string name,
        AssetListEntryKind kind,
        bool isFavorite,
        int sortOrder)
    {
        Id = id;
        Name = name;
        Kind = kind;
        IsFavorite = isFavorite;
        SortOrder = sortOrder;
    }

    public string Id { get; }
    public string Name { get; }
    public AssetListEntryKind Kind { get; }
    public bool IsGroup => Kind == AssetListEntryKind.Group;
    public bool IsFavorite { get; }
    public int SortOrder { get; }
    public string KindText => IsGroup
        ? L.Get("AssetGroup.Kind.Group")
        : L.Get("AssetGroup.Kind.Asset");
    public bool IsSelected { get; set; }
}

public partial class SimpleAssetCreateDialog : Window
{
    private readonly bool allowSmartAssets;
    private readonly bool allowAssetGroups;
    private readonly Func<string, string?>? nameValidator;
    private readonly ObservableCollection<AssetCreationGroupMemberOption> groupMemberOptions;

    public SimpleAssetCreateDialog()
        : this(allowSmartAssets: false)
    {
    }

    public SimpleAssetCreateDialog(bool allowSmartAssets)
        : this(
            allowSmartAssets,
            allowAssetGroups: false,
            eligibleGroupAssets: null,
            eligibleChildGroups: null,
            nameValidator: null)
    {
    }

    public SimpleAssetCreateDialog(
        bool allowSmartAssets,
        bool allowAssetGroups,
        IEnumerable<Asset>? eligibleGroupAssets,
        IEnumerable<AssetGroup>? eligibleChildGroups,
        Func<string, string?>? nameValidator)
    {
        this.allowSmartAssets = allowSmartAssets;
        this.allowAssetGroups = allowAssetGroups;
        this.nameValidator = nameValidator;
        var assetOptions = (eligibleGroupAssets ?? Array.Empty<Asset>())
            .Where(asset => !asset.IsSystem)
            .Select(asset => new AssetCreationGroupMemberOption(
                asset.Id,
                asset.Name,
                AssetListEntryKind.Asset,
                asset.IsFavorite,
                asset.SortOrder));
        var groupOptions = (eligibleChildGroups ?? Array.Empty<AssetGroup>())
            .Select(group => new AssetCreationGroupMemberOption(
                group.Id,
                group.Name,
                AssetListEntryKind.Group,
                group.IsFavorite,
                group.SortOrder));
        groupMemberOptions = new ObservableCollection<AssetCreationGroupMemberOption>(
            assetOptions
                .Concat(groupOptions)
                .OrderBy(option => option.IsFavorite ? 0 : 1)
                .ThenBy(option => option.SortOrder < 0 ? int.MaxValue : option.SortOrder)
                .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(option => option.Kind)
                .ThenBy(option => option.Id, StringComparer.Ordinal));

        InitializeComponent();
        GroupMemberItemsControl.ItemsSource = groupMemberOptions;
        AssetTargetRadio.IsChecked = true;
        CreationModeComboBox.SelectedIndex = 0;
        CreationTargetPanel.IsVisible = allowAssetGroups;
        CreationModeComboBox.IsEnabled = allowSmartAssets;
        CreationModeComboBox.IsVisible = allowSmartAssets;
        CreationModeLabel.IsVisible = allowSmartAssets;

        if (!allowAssetGroups && !allowSmartAssets)
        {
            Height = 250;
            MinHeight = 230;
            CanResize = false;
        }

        UpdateTargetPanels();
        UpdateCreateButtonState();
        Opened += OnOpened;
    }

    /// <summary>
    /// Non-null only when the dialog was completed in one of the automatic
    /// membership modes. The dialog result remains the entity name so legacy
    /// fixed-Asset creation callers retain their existing contract.
    /// </summary>
    public AssetMembershipRule? SelectedMembershipRule { get; private set; }

    public AssetCreationTarget SelectedCreationTarget { get; private set; } =
        AssetCreationTarget.Asset;

    public IReadOnlyList<string> SelectedGroupMemberAssetIds { get; private set; } =
        Array.Empty<string>();

    public IReadOnlyList<string> SelectedGroupMemberGroupIds { get; private set; } =
        Array.Empty<string>();

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AssetNameTextBox.Focus();
            AssetNameTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCreateButtonState();
    }

    private void OnCreationTargetChanged(object? sender, RoutedEventArgs e)
    {
        if (sender == GroupTargetRadio)
        {
            if (!allowAssetGroups)
            {
                AssetTargetRadio.IsChecked = true;
                return;
            }

            SelectedCreationTarget = AssetCreationTarget.AssetGroup;
        }
        else if (sender == AssetTargetRadio)
        {
            SelectedCreationTarget = AssetCreationTarget.Asset;
        }
        else
        {
            return;
        }

        SelectedMembershipRule = null;
        UpdateTargetPanels();
        UpdateCreateButtonState();
    }

    private void UpdateTargetPanels()
    {
        var isGroup = SelectedCreationTarget == AssetCreationTarget.AssetGroup;
        AssetModePanel.IsVisible = !isGroup;
        GroupMemberPanel.IsVisible = isGroup;
        Title = isGroup
            ? L.Get("AssetGroup.CreateTitle")
            : L.Get("Dialog.CreateAsset");
    }

    private void OnCreationModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!allowSmartAssets && CreationModeComboBox.SelectedIndex != 0)
        {
            CreationModeComboBox.SelectedIndex = 0;
            return;
        }

        var isSmart = CreationModeComboBox.SelectedIndex is 1 or 2;
        SmartRulePanel.IsVisible =
            SelectedCreationTarget == AssetCreationTarget.Asset && isSmart;
        SelectedMembershipRule = null;

        if (!isSmart)
        {
            RuleValueComboBox.ItemsSource = null;
            UpdateCreateButtonState();
            return;
        }

        var kind = CreationModeComboBox.SelectedIndex == 1
            ? AssetMembershipRuleKind.Type
            : AssetMembershipRuleKind.Tag;
        var values = kind == AssetMembershipRuleKind.Type
            ? AddonClassificationService.SupportedTypes
            : AddonClassificationService.SupportedTags;
        RuleValueLabel.Text = kind == AssetMembershipRuleKind.Type
            ? L.Get("SmartAsset.TypeLabel")
            : L.Get("SmartAsset.TagLabel");
        SmartRuleDescription.Text = L.Get("SmartAsset.CreateDescription");
        RuleValueComboBox.ItemsSource = values
            .Select(value => new ClassificationChoice(kind, value))
            .ToList();
        RuleValueComboBox.SelectedIndex = values.Count > 0 ? 0 : -1;
        UpdateCreateButtonState();
    }

    private void OnRuleValueChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectedMembershipRule = null;
        UpdateCreateButtonState();
    }

    private void OnNameKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CreateButton.IsEnabled)
        {
            TryCloseWithName();
        }
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        TryCloseWithName();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void TryCloseWithName()
    {
        var name = AssetNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || ValidateName(name) != null)
        {
            UpdateCreateButtonState();
            return;
        }

        if (SelectedCreationTarget == AssetCreationTarget.AssetGroup)
        {
            SelectedMembershipRule = null;
            SelectedGroupMemberAssetIds = groupMemberOptions
                .Where(option => option.IsSelected && !option.IsGroup)
                .Select(option => option.Id)
                .ToArray();
            SelectedGroupMemberGroupIds = groupMemberOptions
                .Where(option => option.IsSelected && option.IsGroup)
                .Select(option => option.Id)
                .ToArray();
        }
        else
        {
            SelectedMembershipRule = BuildSelectedRule();
            if (CreationModeComboBox.SelectedIndex is 1 or 2 &&
                SelectedMembershipRule == null)
            {
                return;
            }
        }

        Close(name);
    }

    private void UpdateCreateButtonState()
    {
        if (AssetNameTextBox == null || CreateButton == null)
        {
            return;
        }

        var name = AssetNameTextBox.Text?.Trim();
        var validationError = string.IsNullOrWhiteSpace(name)
            ? null
            : ValidateName(name);
        NameValidationText.Text = validationError ?? string.Empty;
        NameValidationText.IsVisible = !string.IsNullOrWhiteSpace(validationError);

        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasRule = SelectedCreationTarget == AssetCreationTarget.AssetGroup ||
                      CreationModeComboBox.SelectedIndex == 0 ||
                      RuleValueComboBox.SelectedItem is ClassificationChoice;
        CreateButton.IsEnabled = hasName && validationError == null && hasRule;
    }

    private string? ValidateName(string name)
    {
        return nameValidator?.Invoke(name.Trim());
    }

    private AssetMembershipRule? BuildSelectedRule()
    {
        if (SelectedCreationTarget != AssetCreationTarget.Asset ||
            CreationModeComboBox.SelectedIndex == 0)
        {
            return null;
        }

        if (!allowSmartAssets ||
            RuleValueComboBox.SelectedItem is not ClassificationChoice choice)
        {
            return null;
        }

        var candidate = new AssetMembershipRule(choice.Kind, choice.Value);
        return AddonClassificationService.TryNormalizeRule(
            candidate,
            out var normalized,
            out _)
            ? normalized
            : null;
    }

    private sealed class ClassificationChoice
    {
        public ClassificationChoice(AssetMembershipRuleKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public AssetMembershipRuleKind Kind { get; }
        public string Value { get; }

        public override string ToString()
        {
            var prefix = Kind == AssetMembershipRuleKind.Type
                ? "AddonType."
                : "AddonTag.";
            var localized = L.Get(prefix + Value);
            return string.Equals(localized, prefix + Value, StringComparison.Ordinal)
                ? Value
                : localized;
        }
    }
}
