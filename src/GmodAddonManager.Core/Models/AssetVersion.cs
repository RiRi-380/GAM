using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// インポート種別の定数
    /// </summary>
    public static class ImportTypes
    {
        public const string Collection = "Collection";
        public const string GamFormat = "GAM";
    }
    
    /// <summary>
    /// アセットのバージョン情報を表すクラス
    /// </summary>
    public class AssetVersion
    {
        /// <summary>
        /// バージョン番号
        /// </summary>
        [JsonProperty("version")]
        public int Version { get; set; }
        
        /// <summary>
        /// バージョン作成日時
        /// </summary>
        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }
        
        /// <summary>
        /// アドオンIDのリスト（互換性のため残す）
        /// </summary>
        [JsonProperty("addonIds")]
        public List<string> AddonIds { get; set; }
        
        /// <summary>
        /// GAM形式のコンテンツ（v2以降で使用）
        /// </summary>
        [JsonProperty("gamContent")]
        public string? GamContent { get; set; }
        
        /// <summary>
        /// アドオンの状態を保存するかのフラグ
        /// </summary>
        [JsonProperty("includeAddonStates")]
        public bool IncludeAddonStates { get; set; }
        
        /// <summary>
        /// アドオンごとの状態（IncludeAddonStatesがtrueの場合のみ使用）
        /// </summary>
        [JsonProperty("addonStates")]
        public Dictionary<string, AddonState>? AddonStates { get; set; }
        
        /// <summary>
        /// バージョンのメモ（オプション）
        /// </summary>
        [JsonProperty("note")]
        public string? Note { get; set; }
        
        /// <summary>
        /// インポートベースラインバージョンかどうか
        /// </summary>
        [JsonProperty("isImportBaseline")]
        public bool IsImportBaseline { get; set; }
        
        /// <summary>
        /// 新規サブスクライブしたアドオンID（インポート時のみ使用）
        /// </summary>
        [JsonProperty("newlySubscribedAddonIds")]
        public List<string>? NewlySubscribedAddonIds { get; set; }
        
        /// <summary>
        /// インポート種別（URL/GAM形式）
        /// </summary>
        [JsonProperty("importType")]
        public string? ImportType { get; set; }
        
        public AssetVersion()
        {
            Version = 0;
            CreatedAt = DateTime.Now;
            AddonIds = new List<string>();
            IncludeAddonStates = true;
            AddonStates = null;
            Note = null;
            IsImportBaseline = false;
            NewlySubscribedAddonIds = null;
            ImportType = null;
        }
        
        public AssetVersion(int version, List<string> addonIds, bool includeStates = true) : this()
        {
            Version = version;
            AddonIds = new List<string>(addonIds);
            IncludeAddonStates = includeStates;
        }
    }
}