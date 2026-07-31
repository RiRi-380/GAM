using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class Asset
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("imagePath")]
        public string? ImagePath { get; set; }

        [JsonProperty("isSystem")]
        public bool IsSystem { get; set; }

        /// <summary>
        /// Asset全体の状態。Custom AssetはEnabled/Disabled/Excluded、
        /// Subscribe AssetはEnabled/Disabledだけを使用する。
        /// </summary>
        [JsonProperty("state")]
        public AddonState State { get; set; }

        /// <summary>
        /// ユーザーが上部へ固定したCustom Assetか。
        /// </summary>
        [JsonProperty("isFavorite")]
        public bool IsFavorite { get; set; }

        /// <summary>
        /// 旧構成の個別状態が混在しており、移行後の確認が必要か。
        /// </summary>
        [JsonProperty("needsMigrationReview")]
        public bool NeedsMigrationReview { get; set; }

        /// <summary>
        /// 旧schemaとの読込互換用。新schemaではStateをtruth sourceとし、
        /// このプロパティは書き出さない。
        /// </summary>
        [JsonProperty("enabled")]
        public bool Enabled
        {
            get => State != AddonState.Disabled;
            set
            {
                if (!value)
                {
                    State = AddonState.Disabled;
                }
                else if (State == AddonState.Disabled)
                {
                    State = AddonState.Enabled;
                }
            }
        }

        public bool ShouldSerializeEnabled() => false;

        [JsonProperty("addons")]
        public List<string> Addons { get; set; }
        
        /// <summary>
        /// アドオンごとの状態を管理するディクショナリ
        /// キー: アドオンID、値: AddonState
        /// </summary>
        [JsonProperty("addonStates")]
        public Dictionary<string, AddonState> AddonStates { get; set; }

        public bool ShouldSerializeAddonStates() => false;
        
        /// <summary>
        /// アセット内の全アドオンのデフォルト状態
        /// </summary>
        [JsonProperty("defaultAddonState")]
        public AddonState DefaultAddonState
        {
            get => State;
            set => State = value;
        }

        public bool ShouldSerializeDefaultAddonState() => false;
        
        /// <summary>
        /// WorkshopコレクションID（公開している場合）
        /// </summary>
        [JsonProperty("workshopCollectionId")]
        public string? WorkshopCollectionId { get; set; }

        public bool ShouldSerializeWorkshopCollectionId() => false;
        
        /// <summary>
        /// コレクションの自動更新が有効か
        /// </summary>
        [JsonProperty("autoUpdateCollection")]
        public bool AutoUpdateCollection { get; set; }

        public bool ShouldSerializeAutoUpdateCollection() => false;
        
        /// <summary>
        /// 現在のバージョン
        /// </summary>
        [JsonProperty("currentVersion")]
        public int CurrentVersion { get; set; }
        
        /// <summary>
        /// バージョン履歴
        /// </summary>
        [JsonProperty("versionHistory")]
        public List<AssetVersion> VersionHistory { get; set; }
        
        /// <summary>
        /// インポートベースラインバージョンを持つかどうか
        /// </summary>
        [JsonIgnore]
        public bool HasImportBaseline => VersionHistory?.Any(v => v.IsImportBaseline) ?? false;

        public Asset()
        {
            Id = Guid.NewGuid().ToString();
            Name = string.Empty;
            ImagePath = null;
            IsSystem = false;
            State = AddonState.Enabled;
            IsFavorite = false;
            NeedsMigrationReview = false;
            Addons = new List<string>();
            AddonStates = new Dictionary<string, AddonState>();
            WorkshopCollectionId = null;
            AutoUpdateCollection = false;
            CurrentVersion = 0;
            VersionHistory = new List<AssetVersion>();
        }

        public Asset(string name, bool isSystem = false) : this()
        {
            Name = name;
            IsSystem = isSystem;
        }

        public bool ContainsAllAddons()
        {
            return Addons.Contains("*");
        }

        public void SetAllAddons()
        {
            Addons.Clear();
            Addons.Add("*");
        }
        
        /// <summary>
        /// アドオンを追加し、デフォルトで有効状態に設定
        /// </summary>
        public void AddAddon(string addonId, AddonState state = AddonState.Enabled)
        {
            if (!Addons.Contains(addonId))
            {
                Addons.Add(addonId);
            }
        }
        
        /// <summary>
        /// アドオンの状態を取得
        /// </summary>
        public AddonState GetAddonState(string addonId)
        {
            return State;
        }
        
        /// <summary>
        /// アドオンの状態を設定
        /// </summary>
        public void SetAddonState(string addonId, AddonState state)
        {
            if (ContainsAllAddons() || Addons.Contains(addonId))
            {
                SetWholeState(state);
            }
        }

        public AddonState GetWholeState()
        {
            return State;
        }

        public void SetWholeState(AddonState state)
        {
            State = state;
            AddonStates.Clear();
        }
        
        /// <summary>
        /// アドオンを削除
        /// </summary>
        public void RemoveAddon(string addonId)
        {
            Addons.Remove(addonId);
            AddonStates.Remove(addonId);
        }
    }
}
