# Experiment Log Schema

## Format
- JSON Lines (JSONL), one event per line.
- Default path: `%APPDATA%/GmodAddonManager/logs/experiment_events.jsonl`
- Override path: `GAM_EXPERIMENT_LOG_PATH`

## Context Fields (required)
- `schema_version`: schema version string (current: `3`).
- `event_scope`: `user` | `system` | `external`.
- `monotonic_ms`: monotonic timestamp in milliseconds (Stopwatch-based).
- `strict_link_mode`: boolean (`true`/`false`) indicating StrictLinkMode at log time.
- `timestamp`: ISO-8601 UTC timestamp.
- `session_id`: session identifier (GUID or user-provided).
- `experiment_id`: experiment name (e.g., Exp1, Exp2).
- `condition`: condition label (e.g., LM, BL).
- `task_id`: task label (e.g., T1, T2).
- `action_type`: event type.
- `target_id`: asset_id or addon_id depending on action.
- `result`: `start`, `success`, or `fail`.
- `duration_ms`: duration in milliseconds (typically on End events).
- `before_hash`: state hash before the action.
- `after_hash`: state hash after the action.

## Optional Fields
- `expected_hash`: expected state hash.
- `task_success`: boolean success for TaskEnd.
- `final_hash`: final state hash at TaskEnd.
- `error_code`: machine-readable error code.
- `operation_id`: correlates Start/End events.
- `asset_id`: asset identifier when the target is an addon.
- `asset_label`: short asset label (e.g., "A", "B").
- `asset_display_name`: UI-facing asset name for reports.
- `from_asset_ids`: list of asset ids before a switch.
- `from_asset_labels`: list of asset labels before a switch.
- `from_asset_display_names`: list of asset display names before a switch.
- `to_asset_id`: target asset id for a switch.
- `to_asset_label`: target asset label for a switch.
- `to_asset_display_name`: target asset display name for a switch.
- `parent_operation_id`: parent operation id (nested events).
- `state_hash_scope`: scope for before/after hash (e.g., `actual`, `actual:addons.txt`).
- `expected_hash_scope`: scope for expected hash.
- `state_changed`: boolean flag if before/after hashes differ.
- `from_asset_resolve_method`: resolution method for from_asset (e.g., `label_exact`).
- `to_asset_resolve_method`: resolution method for to_asset.
- `trial_index`: repetition index within the same condition.
- `gmod_running`: boolean (true when GMod is running).
- `pending_change_queued`: boolean (true when pending changes exist).
- `pending_queue_length`: pending change count.
- `bl_method`: BL switching method (e.g., SteamUI/UnsubResub).
- `note`: free-form note for external/manual events.
- `perf_trace_id`: identifier for perf trace.
- `perfmon_csv_path`: path to perfmon output.
- `wpr_etl_path`: path to WPR ETL trace.
- `steam_log_snapshot_path`: path to captured Steam log snapshot.
- `external_metrics_id`: identifier to correlate external metrics.
- `metrics`: object with fields below.

### metrics fields
- `link_created_hardlink_count`
- `link_created_junction_count`
- `copy_bytes`
- `files_touched_count`

## Action Types
Core:
- `SessionStart`, `SessionEnd`
- `AssetApplyExclusiveStart`, `AssetApplyExclusiveEnd`
- `UpdateAddonStatesStart`, `UpdateAddonStatesEnd`
- `AddonToggle`
- `AssetAddAddon`, `AssetRemoveAddon`
- `UndoStart`, `UndoEnd`
- `TaskStart`, `TaskEnd`

StrictLinkMode:
- `LinkFallbackCopy` (error_code `copy_used:<context>`)
- `StrictLinkViolation` (error_code `strict_link_copy_blocked:<context>`)

External (BL):
- `BlSwitchStart`, `BlSwitchEnd`

## Notes
- `TaskStart`/`TaskEnd` and `BlSwitchStart`/`BlSwitchEnd` are logged with `event_scope=external` by default.
- State hashing follows `docs/STATE_HASH.md`.
- Time definitions follow `docs/EXPERIMENT_TIME_DEFINITION.md`.
