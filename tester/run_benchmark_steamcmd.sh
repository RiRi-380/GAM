#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${GAM_STEAM_USER:-}" || -z "${GAM_STEAM_PASSWORD:-}" || -z "${GAM_STEAMCMD_PATH:-}" ]]; then
  echo "Set GAM_STEAM_USER / GAM_STEAM_PASSWORD / GAM_STEAMCMD_PATH (optionally GAM_STEAM_GUARD, GAM_STEAM_LIBRARY)." >&2
  exit 1
fi

runner="tester/runner/GamTester/GamTester.csproj"
dataset="tester/datasets/steam-workshop-ab.json"
scenario="tester/scenarios/switch-a-b.json"
results="tester/results/runs-steamcmd.csv"

common=(
  --mode steamcmd
  --steamcmd-path "${GAM_STEAMCMD_PATH}"
  --steam-user "${GAM_STEAM_USER}"
  --steam-password "${GAM_STEAM_PASSWORD}"
)
if [[ -n "${GAM_STEAM_GUARD:-}" ]]; then
  common+=(--steam-guard "${GAM_STEAM_GUARD}")
fi
if [[ -n "${GAM_STEAM_LIBRARY:-}" ]]; then
  common+=(--steam-library "${GAM_STEAM_LIBRARY}")
fi

dotnet run --project "$runner" -- "${common[@]}" \
  --dataset "$dataset" \
  --scenario "$scenario" \
  --condition BL \
  --repeat 1 \
  --results "$results"

dotnet run --project "$runner" -- "${common[@]}" \
  --dataset "$dataset" \
  --scenario "$scenario" \
  --condition LM \
  --repeat 1 \
  --results "$results"

echo "SteamCMD benchmark complete. Results: $results"
