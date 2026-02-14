# Experiment Time Definition

## Scope
This document defines how "switch time" is measured for asset application and addon state updates.
It also defines TaskStart/TaskEnd markers for task-level timing.

## Start Events
- `AssetApplyExclusiveStart`: emitted when `ApplyAssetExclusiveAsync` begins.
- `UpdateAddonStatesStart`: emitted when `UpdateAddonStatesAsync` begins.
- `TaskStart`: external marker emitted when the experiment task begins.

## End Events
- `AssetApplyExclusiveEnd`: emitted after internal operations finish and state verification completes.
- `UpdateAddonStatesEnd`: emitted after file operations finish and state verification completes.
- `TaskEnd`: external marker emitted when the experiment task ends.

## Completion Rule
Completion is defined when the post-operation state snapshot matches the expected snapshot:
- For apply/update events: `after_hash == expected_hash` (state match).
- If the match does not occur within the timeout, the end event is still emitted with `result=fail` and `error_code=state_mismatch`.

## TaskEnd Definition (Experiment Rule)
Task timing depends on the study goal. Choose one rule and keep it fixed across trials:
- **System UX**: mark `TaskEnd` immediately after the system reports completion (e.g., after `AssetApplyExclusiveEnd` is confirmed).
- **Operational UX**: mark `TaskEnd` when the user judges the task complete (includes confirmation/verification time).

The chosen rule must be documented in the experiment methodology.

## Task Success Rule
- `TaskEnd.task_success` is evaluated as `final_hash == expected_hash`.
- `final_hash` is the hash captured at `TaskEnd`, and `expected_hash` is declared at `TaskStart` (or auto-filled by the logger).

## Duration
- Use `duration_ms` from the end event if present.
- Otherwise compute `monotonic_ms` difference between Start and End events with the same `operation_id`.
- If `monotonic_ms` is missing, fall back to `timestamp` difference.

## Notes
- `expected_hash` is derived from the configuration after the apply/update action.
- State hashing follows `docs/STATE_HASH.md`.
