# Experiment Log Schema

## Format
- JSON Lines (JSONL), one event per line.
- Default path: `%APPDATA%/GmodAddonManager/logs/experiment_events.jsonl`
- Override path: `GAM_EXPERIMENT_LOG_PATH`

## Context Fields (required)
- `schema_version`: schema version string (current: `1`).
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
- `error_code`: machine-readable error code.
- `operation_id`: correlates Start/End events.
- `asset_id`: asset identifier when the target is an addon.

## Action Types
Core:
- `SessionStart`, `SessionEnd`
- `AssetApplyExclusiveStart`, `AssetApplyExclusiveEnd`
- `UpdateAddonStatesStart`, `UpdateAddonStatesEnd`
- `AddonToggle`
- `AssetAddAddon`, `AssetRemoveAddon`
- `UndoStart`, `UndoEnd`

StrictLinkMode:
- `LinkFallbackCopy` (error_code `copy_used:<context>`)
- `StrictLinkViolation` (error_code `strict_link_copy_blocked:<context>`)

## Notes
- State hashing follows `docs/STATE_HASH.md`.
- Time definitions follow `docs/EXPERIMENT_TIME_DEFINITION.md`.
