param(
    [Parameter(Mandatory = $true)][string]$EnvName,
    [Parameter(Mandatory = $true)][string]$RatioKey,
    [int]$Trials = 10,
    [string]$EnvRoot = "C:\\project\\GAM\\experiment_envs",
    [string]$LogRoot = "C:\\project\\GAM\\experiment_logs"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $Message"
    Write-Host $line
    if ($script:LogFile) {
        Add-Content -Path $script:LogFile -Value $line
    }
}

function Ensure-Dir { param([string]$Path) if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null } }

Ensure-Dir $LogRoot

# Normalize ratio key (accept r080 -> r0.80)
if ($RatioKey -match '^r\\d{3}$') {
    $RatioKey = "r" + $RatioKey.Substring(1,1) + "." + $RatioKey.Substring(2)
}
$script:LogFile = Join-Path $LogRoot ("run_" + $EnvName + "_" + $RatioKey + "_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")

$envPath = Join-Path $EnvRoot $EnvName
$manifestPath = Join-Path $envPath "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "Env manifest not found: $manifestPath" }
$manifest = Get-Content $manifestPath | ConvertFrom-Json

$assetA = "A_$RatioKey"
$assetB = "B_$RatioKey"
$assetBase = "Base"

Write-Log "Activating environment $EnvName"
& "$PSScriptRoot\\Activate-ExperimentEnv.ps1" -EnvName $EnvName -EnvRoot $EnvRoot -LogRoot $LogRoot

$resultJsonl = Join-Path $LogRoot ("results_" + $EnvName + "_" + $RatioKey + "_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".jsonl")

for ($trial = 1; $trial -le $Trials; $trial++) {
    Write-Log "Trial $trial / $Trials"
    $sessionId = [guid]::NewGuid().ToString()
    $env:GAM_EXPERIMENT_ID = "ExpSoft_${EnvName}_${RatioKey}"
    $env:GAM_CONDITION = "LM-Soft"
    $env:GAM_SESSION_ID = $sessionId
    $env:GAM_TRIAL_INDEX = $trial.ToString()
    $env:GAM_EXPERIMENT_LOG_PATH = $resultJsonl

    # Setup: Base
    Write-Log "Apply Base (setup)"
    $out = & dotnet run --project tools\\GmodAddonManager.ExperimentRunner apply --asset $assetBase --note "setup:Base"
    Add-Content -Path $resultJsonl -Value $out

    # T1: Base -> A
    Write-Log "T1 Base->A"
    $out = & dotnet run --project tools\\GmodAddonManager.ExperimentRunner apply --asset $assetA --task "T1" --from $assetBase --to $assetA --note "trial:$trial"
    Add-Content -Path $resultJsonl -Value $out

    # T2: A -> B
    Write-Log "T2 A->B"
    $out = & dotnet run --project tools\\GmodAddonManager.ExperimentRunner apply --asset $assetB --task "T2" --from $assetA --to $assetB --note "trial:$trial"
    Add-Content -Path $resultJsonl -Value $out

    # T3: B -> A
    Write-Log "T3 B->A"
    $out = & dotnet run --project tools\\GmodAddonManager.ExperimentRunner apply --asset $assetA --task "T3" --from $assetB --to $assetA --note "trial:$trial"
    Add-Content -Path $resultJsonl -Value $out

    # T4: A -> A (idempotent)
    Write-Log "T4 A->A"
    $out = & dotnet run --project tools\\GmodAddonManager.ExperimentRunner apply --asset $assetA --task "T4" --from $assetA --to $assetA --note "trial:$trial"
    Add-Content -Path $resultJsonl -Value $out
}

Write-Log "Run complete. Results: $resultJsonl"
