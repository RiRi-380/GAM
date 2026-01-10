$ErrorActionPreference = "Stop"

# 環境変数で資格情報を渡す想定（安全のため平文埋め込みは避けてください）
$steamUser = $env:GAM_STEAM_USER
$steamPass = $env:GAM_STEAM_PASSWORD
$steamGuard = $env:GAM_STEAM_GUARD
$steamCmd = $env:GAM_STEAMCMD_PATH
$steamLibrary = $env:GAM_STEAM_LIBRARY

if (-not $steamUser -or -not $steamPass -or -not $steamCmd) {
    Write-Error "Set GAM_STEAM_USER / GAM_STEAM_PASSWORD / GAM_STEAMCMD_PATH (and optionally GAM_STEAM_GUARD, GAM_STEAM_LIBRARY)."
}

$runner = "tester/runner/GamTester/GamTester.csproj"
$dataset = "tester/datasets/steam-workshop-ab.json"
$scenario = "tester/scenarios/switch-a-b.json"
$results = "tester/results/runs-steamcmd.csv"

dotnet run --project $runner -- --mode steamcmd `
  --steamcmd-path "$steamCmd" `
  --steam-user "$steamUser" `
  --steam-password "$steamPass" `
  --steam-guard "$steamGuard" `
  --steam-library "$steamLibrary" `
  --dataset $dataset `
  --scenario $scenario `
  --condition BL `
  --repeat 1 `
  --results $results

dotnet run --project $runner -- --mode steamcmd `
  --steamcmd-path "$steamCmd" `
  --steam-user "$steamUser" `
  --steam-password "$steamPass" `
  --steam-guard "$steamGuard" `
  --steam-library "$steamLibrary" `
  --dataset $dataset `
  --scenario $scenario `
  --condition LM `
  --repeat 1 `
  --results $results

Write-Host "SteamCMD benchmark complete. Results: $results"
