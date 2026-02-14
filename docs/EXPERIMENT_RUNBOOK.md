# Experiment Runbook (Exp1 Minimal)

## Preconditions
- Garry's Mod is closed.
- For StrictLinkMode, ensure workshop/cache are on the same drive and GAM has privileges.

## Operational Rules
- Keep only one GAM UI instance running at a time (close it between trials or blocks).
- Align the initial state to the task "from" side before `TaskStart` (T1: A->B starts from A, T2: B->A starts from B).
- Use the same PowerShell session for UI + CLI, or copy the same env block into each shell.

## Environment Variables
Set before launching the app:
- `GAM_EXPERIMENT_ID=Exp1`
- `GAM_CONDITION=LM` (or BL)
- `GAM_TASK_ID=T1`
- `GAM_SESSION_ID=<unique_id>` (optional)
- `GAM_TRIAL_INDEX=1` (optional)
- `GAM_PARTICIPANT_ID=P01` (optional)
- `GAM_STRICT_LINK_MODE=1` (recommended for LM)
- `GAM_EXPERIMENT_FORCE_HARD_DISABLE=1` (recommended for LM; overrides UI setting)
- `GAM_EXPERIMENT_LOG_PATH=<path>` (optional)
- `GAM_ENABLE_IPC=1` (optional, starts named pipe server)
- `GAM_EXPERIMENT_PIPE_NAME=GAMExperiment` (optional)
- `GAM_PERF_TRACE_ID=<id>` (optional)
- `GAM_PERFMON_CSV_PATH=<path>` (optional)
- `GAM_WPR_ETL_PATH=<path>` (optional)
- `GAM_STEAM_LOG_SNAPSHOT_PATH=<path>` (optional)
- `GAM_EXTERNAL_METRICS_ID=<id>` (optional)

## Steps (Example: LM A->B)
1. Launch the app normally.
2. Create Asset A and Asset B (distinct addon sets). Use short codes (`A`, `B`) as the asset names.
   - The logger also accepts `Asset A`/`Asset B` and normalizes the prefix, but the short codes keep labels stable.
3. Setup (task outside): Apply the "from" asset (A for T1, B for T2).
4. Task (inside): Apply the "to" asset and record TaskStart/TaskEnd markers.
5. Collect logs from:
   - `%APPDATA%/GmodAddonManager/logs/experiment_events.jsonl`

## Output
Use `duration_ms` from end events (or timestamps) to compute switch time.

## IPC Markers (Task/BL)
The UI process hosts a named pipe when `GAM_EXPERIMENT_LOG_PATH` is set or `GAM_ENABLE_IPC=1`.
Default pipe name: `\\.\pipe\GAMExperiment` (override with `GAM_EXPERIMENT_PIPE_NAME`).

CLI examples (PowerShell). Use the short asset codes (A/B) or pass explicit IDs:
```
dotnet run --project tools\GmodAddonManager.ExperimentCli -- task start --task T1 --note "Asset grouping" --from-asset-label "A" --to-asset-label "B"
dotnet run --project tools\GmodAddonManager.ExperimentCli -- task end --task T1 --success 1 --from-asset-label "A" --to-asset-label "B"
dotnet run --project tools\GmodAddonManager.ExperimentCli -- bl start --method SteamUI --note "Unsub/Resub A->B" --from-asset-label "A" --to-asset-label "B"
dotnet run --project tools\GmodAddonManager.ExperimentCli -- bl end --method SteamUI --from-asset-label "A" --to-asset-label "B"
```

If the label is ambiguous or not found, the CLI returns an error and logs a `result=fail` event with `error_code`.

## Baseline-Copy (Optional)
Define a baseline by copying presets instead of applying assets:
- Prepare `preset_A/addonnomount.txt` and `preset_B/addonnomount.txt`.
- Use `GAM_CONDITION=BL-Copy`.
- Setup (task outside): copy the "from" preset and verify it matches the target file.
  - Example (T1, from=A):
    - `Copy-Item -Force <preset_A\\addonnomount.txt> <gmod\\cfg\\addonnomount.txt>`
    - Compare SHA256 hashes; if they differ, abort the trial and redo setup:
```
$from = "A"
Copy-Item -Force "<preset_${from}\\addonnomount.txt>" "<gmod\\cfg\\addonnomount.txt>"
$hPreset = (Get-FileHash "<preset_${from}\\addonnomount.txt>" -Algorithm SHA256).Hash
$hActual = (Get-FileHash "<gmod\\cfg\\addonnomount.txt>" -Algorithm SHA256).Hash
if ($hPreset -ne $hActual) { throw "Setup failed: addonnomount.txt is not preset_${from}" }
```
- Task flow (A -> B):
  1. `TaskStart` (CLI) with `from=A`, `to=B`.
  2. Copy preset file with overwrite:
     - `Copy-Item -Force <preset_B\\addonnomount.txt> <gmod\\cfg\\addonnomount.txt>`
  3. `TaskEnd` (CLI). Success is evaluated by `final_hash == expected_hash`.
- If you invoke `UpdateAddonStates` during BL, document it explicitly and treat it as part of the baseline procedure.

## Counterbalancing Order (Recommended)
To reduce order effects, run both blocks:
- Block X: LM -> BL (A->B, B->A, then BL A->B, B->A)
- Block Y: BL -> LM (A->B, B->A, then LM A->B, B->A)
