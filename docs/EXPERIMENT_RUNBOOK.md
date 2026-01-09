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
- `GAM_TRIAL_INDEX=1` (optional)
- `GAM_STRICT_LINK_MODE=1` (recommended for LM)
- `GAM_EXPERIMENT_LOG_PATH=<path>` (optional)
- `GAM_ENABLE_IPC=1` (optional, starts named pipe server)
- `GAM_EXPERIMENT_PIPE_NAME=GAMExperiment` (optional)
- `GAM_PERF_TRACE_ID=<id>` (optional)
- `GAM_PERFMON_CSV_PATH=<path>` (optional)
- `GAM_WPR_ETL_PATH=<path>` (optional)
- `GAM_STEAM_LOG_SNAPSHOT_PATH=<path>` (optional)
- `GAM_EXTERNAL_METRICS_ID=<id>` (optional)

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

## IPC Markers (Task/BL)
The UI process hosts a named pipe when `GAM_EXPERIMENT_LOG_PATH` is set or `GAM_ENABLE_IPC=1`.
Default pipe name: `\\.\pipe\GAMExperiment` (override with `GAM_EXPERIMENT_PIPE_NAME`).

CLI examples (PowerShell):
```
dotnet run --project tools\GmodAddonManager.ExperimentCli -- task start --task T1 --note "Asset grouping"
dotnet run --project tools\GmodAddonManager.ExperimentCli -- task end --task T1 --success 1
dotnet run --project tools\GmodAddonManager.ExperimentCli -- bl start --method SteamUI --note "Unsub/Resub A->B"
dotnet run --project tools\GmodAddonManager.ExperimentCli -- bl end --method SteamUI
```
