# Experiment Time Definition

## Scope
This document defines how "switch time" is measured for asset application and addon state updates.

## Start Events
- `AssetApplyExclusiveStart`: emitted when `ApplyAssetExclusiveAsync` begins.
- `UpdateAddonStatesStart`: emitted when `UpdateAddonStatesAsync` begins.

## End Events
- `AssetApplyExclusiveEnd`: emitted after internal operations finish and state verification completes.
- `UpdateAddonStatesEnd`: emitted after file operations finish and state verification completes.

## Completion Rule
Completion is defined when the post-operation state snapshot matches the expected snapshot:
- `after_hash == expected_hash` (state match).
- If the match does not occur within the timeout, the end event is still emitted with `result=fail` and `error_code=state_mismatch`.

## Duration
- Use `duration_ms` from the end event if present.
- Otherwise compute `timestamp` difference between Start and End events with the same `operation_id`.

## Notes
- `expected_hash` is derived from the configuration after the apply/update action.
- State hashing follows `docs/STATE_HASH.md`.
