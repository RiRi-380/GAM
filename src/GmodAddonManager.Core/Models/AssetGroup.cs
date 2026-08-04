using System;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// An organizational container in a bounded, single-parent tree. Direct
    /// Asset membership remains authoritative on Asset.ParentGroupId while
    /// child-Group membership is authoritative on AssetGroup.ParentGroupId.
    /// </summary>
    public sealed class AssetGroup
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("imagePath")]
        public string? ImagePath { get; set; }

        [JsonProperty("memo")]
        public string Memo { get; set; }

        /// <summary>
        /// The single owning Asset Group. Null means this Group is a root entry.
        /// </summary>
        [JsonProperty("parentGroupId")]
        public string? ParentGroupId { get; set; }

        [JsonProperty("isFavorite")]
        public bool IsFavorite { get; set; }

        [JsonProperty("sortOrder")]
        public int SortOrder { get; set; }

        /// <summary>
        /// State inherited by newly created children. For an empty Group this
        /// is also its displayed state; bulk state changes update it.
        /// </summary>
        [JsonProperty("defaultChildState")]
        public AddonState DefaultChildState { get; set; }

        public AssetGroup()
        {
            Id = Guid.NewGuid().ToString();
            Name = string.Empty;
            ImagePath = null;
            Memo = string.Empty;
            ParentGroupId = null;
            IsFavorite = false;
            SortOrder = int.MaxValue;
            DefaultChildState = AddonState.Enabled;
        }

        public AssetGroup(string name) : this()
        {
            Name = name;
        }
    }

    /// <summary>
    /// UI-facing aggregate state. Mixed is never persisted or supplied to the
    /// leaf Asset resolver.
    /// </summary>
    public enum AssetGroupDisplayState
    {
        Enabled = 0,
        Disabled = 1,
        Excluded = 2,
        Mixed = 3
    }

    /// <summary>
    /// Controls whether deleting an Asset Group only unwraps its child Assets
    /// or removes those Asset definitions as part of the same transaction.
    /// Neither mode changes Steam subscriptions or deletes addon payloads.
    /// </summary>
    public enum AssetGroupDeleteMode
    {
        KeepAssets = 0,
        DeleteAssets = 1
    }

    public enum AssetListEntryKind
    {
        Asset = 0,
        Group = 1
    }
}
