using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class ExperimentEvent
    {
        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; } = "1";

        [JsonProperty("strict_link_mode")]
        public bool? StrictLinkMode { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonProperty("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("experiment_id")]
        public string ExperimentId { get; set; } = string.Empty;

        [JsonProperty("condition")]
        public string Condition { get; set; } = string.Empty;

        [JsonProperty("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonProperty("action_type")]
        public string ActionType { get; set; } = string.Empty;

        [JsonProperty("target_id")]
        public string? TargetId { get; set; }

        [JsonProperty("result")]
        public string? Result { get; set; }

        [JsonProperty("duration_ms")]
        public long? DurationMs { get; set; }

        [JsonProperty("before_hash")]
        public string? BeforeHash { get; set; }

        [JsonProperty("after_hash")]
        public string? AfterHash { get; set; }

        [JsonProperty("expected_hash")]
        public string? ExpectedHash { get; set; }

        [JsonProperty("error_code")]
        public string? ErrorCode { get; set; }

        [JsonProperty("operation_id")]
        public string? OperationId { get; set; }

        [JsonProperty("asset_id")]
        public string? AssetId { get; set; }
    }
}
