# Experiment Log Schema

Note: Current releases run in soft-only mode (addonnomount.txt). Junction/hard-link metrics are legacy/experimental.

## Format
- JSON Lines (JSONL), one event per line.
- Default path: `%APPDATA%/GmodAddonManager/logs/experiment_events.jsonl`
- Override path: `GAM_EXPERIMENT_LOG_PATH`

## Context Fields (required)
- `schema_version`: schema version string (current: `5`).
- `event_scope`: `user` | `system` | `external`.
- `monotonic_ms`: monotonic timestamp in milliseconds (Stopwatch-based).
- `event_seq`: monotonic sequence number within the session.
- `tz_offset_minutes`: local timezone offset from UTC in minutes.
- `strict_link_mode`: boolean (`true`/`false`) indicating StrictLinkMode at log time.
- `timestamp`: ISO-8601 UTC timestamp.
- `session_id`: session identifier (GUID or user-provided).
- `experiment_id`: experiment name (e.g., Exp1, Exp2).
- `condition`: condition label (e.g., LM, BL).
- `task_id`: task label (e.g., T1, T2).
- `participant_id`: participant identifier (optional env `GAM_PARTICIPANT_ID`).
- `action_type`: event type.
- `target_id`: asset_id or addon_id depending on action.
- `result`: `start`, `success`, or `fail`.
- `duration_ms`: duration in milliseconds (typically on End events).
- `before_hash`: state hash before the action.
- `after_hash`: state hash after the action.

## Optional Fields
- `expected_hash`: expected state hash.
- `expected_hash_scope`: source of expected hash (e.g., `expected:actual`, `expected:addonnomount.txt`, `expected:asset:actual`).
- `state_changed`: boolean flag if before/after hashes differ.
- `state_hash_scope`: source of before/after hashes (e.g., `actual`, `actual:addonnomount.txt`, `expected:asset`).
- `task_success`: boolean success for TaskEnd (`final_hash == expected_hash`).
- `final_hash`: final state hash at TaskEnd.
- `error_code`: machine-readable error code.
- `operation_id`: correlates Start/End events.
- `parent_operation_id`: parent operation id (e.g., AssetApplyExclusive -> UpdateAddonStates).
- `asset_id`: asset identifier when the target is an addon.
- `asset_label`: short asset code/label (e.g., "A", "B").
- `asset_display_name`: descriptive asset label for reports.
- `from_asset_ids`: list of asset ids before a switch (external events or exclusive apply).
- `from_asset_labels`: list of asset codes before a switch.
- `from_asset_display_names`: list of descriptive labels before a switch.
- `to_asset_id`: asset id being applied/targeted.
- `to_asset_label`: asset code being applied/targeted.
- `to_asset_display_name`: descriptive label being applied/targeted.
- `from_asset_resolve_method`: resolution method for from_asset (e.g., `label_exact`, `label_normalized:strip_prefix(Asset )`).
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
- Use `event_seq` to break ties when `monotonic_ms` is equal.
- `timestamp` is UTC; use `tz_offset_minutes` if you need local time reconstruction.
- `asset_label` is a normalized short code; `asset_display_name` keeps the UI-visible name for reporting.
- `expected_hash_scope` mirrors the comparison basis (e.g., `expected:addonnomount.txt` when `state_hash_scope=actual:addonnomount.txt`).
- `task_success` should be computed from `TaskEnd.final_hash` and `TaskEnd.expected_hash`.
