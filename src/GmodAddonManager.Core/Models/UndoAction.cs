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
        AssetVersionRestored
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
        
        // 削除されたアセットの復元用データ
        public Asset? DeletedAsset { get; set; }
        
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
