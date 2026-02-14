using System.Collections.Generic;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class ExperimentEvent
    {
        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; } = "5";

        [JsonProperty("strict_link_mode")]
        public bool? StrictLinkMode { get; set; }

        [JsonProperty("event_scope")]
        public string EventScope { get; set; } = "system";

        [JsonProperty("monotonic_ms")]
        public long? MonotonicMs { get; set; }

        [JsonProperty("event_seq")]
        public long? EventSeq { get; set; }

        [JsonProperty("tz_offset_minutes")]
        public int? TzOffsetMinutes { get; set; }

        [JsonProperty("trial_index")]
        public int? TrialIndex { get; set; }

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

        [JsonProperty("participant_id")]
        public string? ParticipantId { get; set; }

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

        [JsonProperty("state_hash_scope")]
        public string? StateHashScope { get; set; }

        [JsonProperty("expected_hash")]
        public string? ExpectedHash { get; set; }

        [JsonProperty("expected_hash_scope")]
        public string? ExpectedHashScope { get; set; }

        [JsonProperty("state_changed")]
        public bool? StateChanged { get; set; }

        [JsonProperty("task_success")]
        public bool? TaskSuccess { get; set; }

        [JsonProperty("final_hash")]
        public string? FinalHash { get; set; }

        [JsonProperty("error_code")]
        public string? ErrorCode { get; set; }

        [JsonProperty("operation_id")]
        public string? OperationId { get; set; }

        [JsonProperty("parent_operation_id")]
        public string? ParentOperationId { get; set; }

        [JsonProperty("asset_id")]
        public string? AssetId { get; set; }

        [JsonProperty("asset_label")]
        public string? AssetLabel { get; set; }

        [JsonProperty("asset_display_name")]
        public string? AssetDisplayName { get; set; }

        [JsonProperty("from_asset_ids")]
        public List<string>? FromAssetIds { get; set; }

        [JsonProperty("from_asset_labels")]
        public List<string>? FromAssetLabels { get; set; }

        [JsonProperty("from_asset_display_names")]
        public List<string>? FromAssetDisplayNames { get; set; }

        [JsonProperty("to_asset_id")]
        public string? ToAssetId { get; set; }

        [JsonProperty("to_asset_label")]
        public string? ToAssetLabel { get; set; }

        [JsonProperty("to_asset_display_name")]
        public string? ToAssetDisplayName { get; set; }

        [JsonProperty("from_asset_resolve_method")]
        public string? FromAssetResolveMethod { get; set; }

        [JsonProperty("to_asset_resolve_method")]
        public string? ToAssetResolveMethod { get; set; }

        [JsonProperty("gmod_running")]
        public bool? GmodRunning { get; set; }

        [JsonProperty("pending_change_queued")]
        public bool? PendingChangeQueued { get; set; }

        [JsonProperty("pending_queue_length")]
        public int? PendingQueueLength { get; set; }

        [JsonProperty("bl_method")]
        public string? BlMethod { get; set; }

        [JsonProperty("note")]
        public string? Note { get; set; }

        [JsonProperty("perf_trace_id")]
        public string? PerfTraceId { get; set; }

        [JsonProperty("perfmon_csv_path")]
        public string? PerfmonCsvPath { get; set; }

        [JsonProperty("wpr_etl_path")]
        public string? WprEtlPath { get; set; }

        [JsonProperty("steam_log_snapshot_path")]
        public string? SteamLogSnapshotPath { get; set; }

        [JsonProperty("external_metrics_id")]
        public string? ExternalMetricsId { get; set; }

        [JsonProperty("metrics")]
        public ExperimentEventMetrics? Metrics { get; set; }
    }
}
