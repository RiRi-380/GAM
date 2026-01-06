# Experiment Runbook (Exp1 Minimal)

## Preconditions
- Garry's Mod is closed.
- For StrictLinkMode, ensure workshop/cache are on the same drive and GAM has privileges.

## Environment Variables
Set before launching the app:
- `GAM_EXPERIMENT_ID=Exp1`
- `GAM_CONDITION=LM` (or BL)
- `GAM_TASK_ID=T1`
- `GAM_SESSION_ID=<unique_id>` (optional)
- `GAM_STRICT_LINK_MODE=1` (recommended for LM)
- `GAM_EXPERIMENT_LOG_PATH=<path>` (optional)

## Steps
1. Launch the app normally.
2. Create Asset A and Asset B (distinct addon sets).
3. Click **Apply Exclusive** on Asset A.
4. Wait for `AssetApplyExclusiveEnd` with `result=success` in the log.
5. Click **Apply Exclusive** on Asset B.
6. Wait for `AssetApplyExclusiveEnd` with `result=success` in the log.
7. Collect logs from:
   - `%APPDATA%/GmodAddonManager/logs/experiment_events.jsonl`

## Output
Use `duration_ms` from end events (or timestamps) to compute switch time.
