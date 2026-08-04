using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Undo操作の種類
    /// </summary>
    public enum UndoActionType
    {
        AssetCreated,
        AssetDeleted,
        AssetEnabled,
        AssetDisabled,
        AssetExcluded,
        AddonAddedToAsset,
        AddonRemovedFromAsset,
        AddonStateChanged,
        AssetMerged,
        AssetEdited,
        AssetRenamed,
        AssetImageChanged,
        AssetFavoriteChanged,
        AllOff,
        AssetVersionRestored,
        AssetGroupCreated,
        AssetGroupDeleted,
        AssetGroupStateChanged,
        AssetGroupMembershipChanged,
        AssetOrderChanged,
        AssetGroupRenamed,
        AssetGroupImageChanged,
        AssetGroupFavoriteChanged,
        GamBundleImported,
        AssetGroupDepthLimitChanged,
        AssetMemoChanged,
        AssetGroupMemoChanged
    }

    /// <summary>
    /// Undo操作の情報
    /// </summary>
    public class UndoAction
    {
        public string Id { get; set; }
        public UndoActionType Type { get; set; }
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }
        
        // 操作に関連するデータ
        public string? AssetId { get; set; }
        public string? AssetName { get; set; }
        public string? AddonId { get; set; }
        public string? AddonName { get; set; }
        public bool? PreviousEnabledState { get; set; }
        public AddonState? PreviousDefaultAddonState { get; set; }
        public AddonState? PreviousAddonState { get; set; }
        public AddonState? NewAddonState { get; set; }
        public bool? IsAssetToggle { get; set; }
        public AddonState? AddonState { get; set; }  // AddonAddedToAsset/AddonRemovedFromAsset用
        public List<string>? AffectedAddonIds { get; set; }
        public List<string>? AffectedAssetIds { get; set; }
        public Dictionary<string, AddonState>? PreviousAddonStates { get; set; }
        public Dictionary<string, AddonState>? PreviousAssetStates { get; set; }
        public string? PreviousAssetName { get; set; }
        public string? PreviousImagePath { get; set; }
        public byte[]? PreviousImageBytes { get; set; }
        public bool AssetNameChanged { get; set; }
        public bool AssetImageChanged { get; set; }
        public bool? PreviousFavoriteState { get; set; }
        public List<string>? PreviousMembership { get; set; }
        public int? PreviousCurrentVersion { get; set; }

        // Asset Group and manual-order operations. A multi-entity user action
        // is represented by exactly one payload containing every prior value.
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }
        public AddonState? PreviousGroupDefaultState { get; set; }
        public string? PreviousGroupName { get; set; }
        public string? PreviousGroupImagePath { get; set; }
        public byte[]? PreviousGroupImageBytes { get; set; }
        public bool? PreviousGroupFavoriteState { get; set; }
        public Dictionary<string, string?>? PreviousAssetParentGroupIds { get; set; }
        public Dictionary<string, int>? PreviousAssetSortOrders { get; set; }
        public Dictionary<string, int>? PreviousAssetGroupSortOrders { get; set; }
        public Dictionary<string, string?>? PreviousGroupParentGroupIds { get; set; }
        public Dictionary<string, AddonState>? PreviousGroupDefaultStates { get; set; }
        public List<string>? AffectedGroupIds { get; set; }
        public int? PreviousMaxNestedGroupDepth { get; set; }
        public string? PreviousMemo { get; set; }
        
        // 削除されたアセットの復元用データ
        public Asset? DeletedAsset { get; set; }
        public List<Asset>? DeletedAssets { get; set; }
        public AssetGroup? DeletedAssetGroup { get; set; }
        public List<AssetGroup>? DeletedAssetGroups { get; set; }
        
        public UndoAction()
        {
            Id = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
            Description = "";
        }
        
        public UndoAction(UndoActionType type, string description) : this()
        {
            Type = type;
            Description = description;
        }
    }
}
