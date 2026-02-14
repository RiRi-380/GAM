using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public sealed class ExperimentEventMetrics
    {
        [JsonProperty("link_created_hardlink_count")]
        public int? LinkCreatedHardlinkCount { get; set; }

        [JsonProperty("link_created_junction_count")]
        public int? LinkCreatedJunctionCount { get; set; }

        [JsonProperty("copy_bytes")]
        public long? CopyBytes { get; set; }

        [JsonProperty("files_touched_count")]
        public int? FilesTouchedCount { get; set; }
    }
}
