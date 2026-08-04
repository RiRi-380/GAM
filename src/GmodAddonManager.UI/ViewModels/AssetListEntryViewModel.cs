using Avalonia.Media.Imaging;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using System;
using System.ComponentModel;
using System.Windows.Input;

namespace GmodAddonManager.UI.ViewModels;

/// <summary>
/// Presentation adapter for the mixed Asset/Asset Group list. It deliberately
/// keeps AssetListViewModel.SelectedAsset leaf-only for MainWindow compatibility.
/// </summary>
public sealed class AssetListEntryViewModel : ViewModelBase, IDisposable
{
    private readonly AssetItemViewModel? asset;
    private readonly AssetGroupItemViewModel? group;
    private bool isShareMode;
    private bool isShareSelected;
    private bool disposed;

    public AssetListEntryViewModel(AssetItemViewModel asset, string? parentGroupId)
    {
        this.asset = asset ?? throw new ArgumentNullException(nameof(asset));
        ParentGroupId = parentGroupId;
        asset.PropertyChanged += OnInnerPropertyChanged;
    }

    public AssetListEntryViewModel(AssetGroupItemViewModel group)
    {
        this.group = group ?? throw new ArgumentNullException(nameof(group));
        ParentGroupId = group.ParentGroupId;
        group.PropertyChanged += OnInnerPropertyChanged;
    }

    public AssetItemViewModel? Asset => asset;
    public AssetGroupItemViewModel? Group => group;
    public AssetListEntryKind EntryKind => IsGroup
        ? AssetListEntryKind.Group
        : AssetListEntryKind.Asset;
    public bool IsGroup => group != null;
    public bool IsAsset => asset != null;
    public bool IsSystem => asset?.IsSystem == true;
    public bool CanReorder => !IsSystem && !isShareMode;
    public bool CanShare => !IsSystem;
    public string? ParentGroupId { get; }
    public string Id => asset?.Id ?? group!.Id;
    public string Name => asset?.Name ?? group!.Name;
    public string AddonCountDisplay => asset?.AddonCountDisplay ?? group!.AddonCountDisplay;
    public string BorderColor => IsShareSelected
        ? "#4A90E2"
        : asset?.BorderColor ?? group!.BorderColor;
    public string AssetStateColor => asset?.AssetStateColor ?? group!.AssetStateColor;
    public Bitmap? AssetImageBitmap => asset?.AssetImageBitmap ?? group!.AssetImageBitmap;
    public bool HasCustomImage => asset?.HasCustomImage ?? group!.HasCustomImage;
    public bool HasNoCustomImage => !HasCustomImage;
    public bool CanEditImage => !isShareMode && (asset?.CanEditImage ?? true);
    public bool CanDelete => !isShareMode && (asset?.CanDelete ?? true);
    public bool CanShowDetails => !isShareMode;
    public bool CanEditAddonDefaultState =>
        !isShareMode && (asset?.CanEditAddonDefaultState ?? true);
    public bool CanSetExcluded => !isShareMode && (asset?.CanSetExcluded ?? true);
    public bool CanManageVersions => asset?.CanManageVersions ?? false;
    public bool CanFavorite => !isShareMode && (asset?.CanFavorite ?? true);
    public bool IsFavorite => asset?.IsFavorite ?? group!.IsFavorite;
    public bool IsSmart => asset?.IsSmart ?? false;
    public bool IsSubscribeAsset => asset?.IsSubscribeAsset ?? false;
    public bool IsGmodDisabledAsset => asset?.IsGmodDisabledAsset ?? false;
    public bool IsShareSelected
    {
        get => isShareSelected;
        private set
        {
            SetAndRaise(ref isShareSelected, value);
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }
    public bool IsMixedState => group?.IsMixedState ?? false;
    public string MixedStateText => group?.MixedStateText ?? string.Empty;
    public string GroupBadgeTooltip => group?.GroupBadgeTooltip ?? string.Empty;
    public string SmartBadgeText => asset?.SmartBadgeText ?? string.Empty;
    public string SmartRuleText => asset?.SmartRuleText ?? string.Empty;
    public int StateColumnSpan => asset?.StateColumnSpan ?? 1;
    public string EnabledStateLabel => asset?.EnabledStateLabel ?? group!.EnabledStateLabel;
    public string DisabledStateLabel => asset?.DisabledStateLabel ?? group!.DisabledStateLabel;
    public string ExcludedStateLabel => asset?.ExcludedStateLabel ?? group!.ExcludedStateLabel;
    public string EnabledStateTooltip => asset?.EnabledStateTooltip ?? group!.EnabledStateTooltip;
    public string DisabledStateTooltip => asset?.DisabledStateTooltip ?? group!.DisabledStateTooltip;
    public string ExcludedStateTooltip => asset?.ExcludedStateTooltip ?? group!.ExcludedStateTooltip;
    public bool IsEnabledState => asset?.IsEnabledState ?? group!.IsEnabledState;
    public bool IsDisabledState => asset?.IsDisabledState ?? group!.IsDisabledState;
    public bool IsExcludedState => asset?.IsExcludedState ?? group!.IsExcludedState;
    public string VersionDisplay => asset?.VersionDisplay ?? string.Empty;
    public string FavoriteButtonText => asset?.FavoriteButtonText ?? group!.FavoriteButtonText;
    public ICommand SetEnabledCommand => asset?.SetEnabledCommand ?? group!.SetEnabledCommand;
    public ICommand SetDisabledCommand => asset?.SetDisabledCommand ?? group!.SetDisabledCommand;
    public ICommand SetExcludedCommand => asset?.SetExcludedCommand ?? group!.SetExcludedCommand;
    public ICommand ToggleFavoriteCommand => asset?.ToggleFavoriteCommand ?? group!.ToggleFavoriteCommand;
    public ICommand ShowDetailsCommand => asset?.ShowDetailsCommand ?? group!.ShowDetailsCommand;
    public ICommand VersionManageCommand => asset?.VersionManageCommand ?? group!.ShowDetailsCommand;
    public ICommand EditImageCommand => asset?.EditImageCommand ?? group!.EditImageCommand;
    public ICommand EditCommand => asset?.EditCommand ?? group!.EditCommand;
    public ICommand DeleteCommand => asset?.DeleteCommand ?? group!.DeleteCommand;

    public bool IsSelected
    {
        get => asset?.IsSelected ?? group!.IsSelected;
        set
        {
            if (asset != null)
            {
                asset.IsSelected = value;
            }
            else
            {
                group!.IsSelected = value;
            }
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }

    public void SetSharePresentation(bool shareMode, bool selected)
    {
        if (isShareMode != shareMode)
        {
            isShareMode = shareMode;
            this.RaisePropertyChanged(nameof(CanReorder));
            this.RaisePropertyChanged(nameof(CanEditImage));
            this.RaisePropertyChanged(nameof(CanDelete));
            this.RaisePropertyChanged(nameof(CanShowDetails));
            this.RaisePropertyChanged(nameof(CanEditAddonDefaultState));
            this.RaisePropertyChanged(nameof(CanSetExcluded));
            this.RaisePropertyChanged(nameof(CanFavorite));
        }
        IsShareSelected = selected;
    }

    private void OnInnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            this.RaisePropertyChanged(e.PropertyName);
        }
        else
        {
            this.RaisePropertyChanged(string.Empty);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (asset != null)
        {
            asset.PropertyChanged -= OnInnerPropertyChanged;
        }
        if (group != null)
        {
            group.PropertyChanged -= OnInnerPropertyChanged;
            group.Dispose();
        }
    }
}
