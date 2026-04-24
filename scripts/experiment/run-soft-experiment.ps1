param(
    [string]$RunIndexPath
)

$ErrorActionPreference = 'Stop'

if (-not $RunIndexPath) {
    $latest = Get-ChildItem 'C:\project\GAM\experiment_logs' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw 'No run index found. Provide -RunIndexPath.' }
    $RunIndexPath = Join-Path $latest.FullName 'run_index.json'
}

if (-not (Test-Path $RunIndexPath)) { throw "Run index not found: $RunIndexPath" }

$runIndex = Get-Content $RunIndexPath -Raw | ConvertFrom-Json
$logRoot = Split-Path $RunIndexPath -Parent

# Use preview outputs and temp paths to avoid locked repo bin/obj.
$env:GAM_PREVIEW = 'true'
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'gam_dotnet_home'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
$env:DOTNET_NOLOGO = '1'
$env:HTTP_PROXY = ''
$env:HTTPS_PROXY = ''
$env:NUGET_RESTORE_IGNORE_FAILED_SOURCES = 'true'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\\packages'

function Test-AppDataWritable {
    $testPath = Join-Path $env:APPDATA ("gam_write_test_" + [guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Set-Content -Path $testPath -Value "ok" -Force | Out-Null
        Remove-Item -Path $testPath -Force | Out-Null
        return $true
    } catch {
        return $false
    }
}

# AppData may be read-only in this environment.
$appDataWritable = Test-AppDataWritable

# Build a local nuget source from cached packages to avoid network dependency.
$localSource = Join-Path $env:TEMP 'gam_local_nuget'
New-Item -ItemType Directory -Force -Path $localSource | Out-Null
$pkgList = @(
    @{Name='newtonsoft.json'; Version='13.0.3'},
    @{Name='microsoft.data.sqlite'; Version='8.0.0'},
    @{Name='microsoft.win32.registry'; Version='5.0.0'},
    @{Name='system.management'; Version='5.0.0'},
    @{Name='skiasharp'; Version='2.88.8'},
    @{Name='polly'; Version='8.2.0'},
    @{Name='steamworks.net'; Version='15.0.1'}
)
foreach ($p in $pkgList) {
    $nupkg = Join-Path $env:USERPROFILE (".nuget\\packages\\{0}\\{1}\\{0}.{1}.nupkg" -f $p.Name, $p.Version)
    if (Test-Path $nupkg) {
        Copy-Item -Path $nupkg -Destination $localSource -Force
    } else {
        throw "Cached package missing: $($p.Name) $($p.Version)"
    }
}

# Force MSBuild outputs out of repo to avoid locked obj.
$artifactRoot = Join-Path $env:TEMP ("gam_artifacts_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

# Ensure restore/build succeeds using local packages.
dotnet restore tools\\GmodAddonManager.ExperimentRunner --source $localSource --ignore-failed-sources -p:GAM_PREVIEW=true -p:UseArtifactsOutput=true -p:ArtifactsPath=$artifactRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

$backupRoot = Join-Path $logRoot 'backup_appdata'
$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupPath = Join-Path $backupRoot "appdata_$timestamp"

# Backup AppData config
$appData = $env:APPDATA
$gamAppData = Join-Path $appData 'GmodAddonManager'
if (Test-Path $gamAppData) {
    New-Item -ItemType Directory -Force -Path $backupPath | Out-Null
    Copy-Item -Path $gamAppData -Destination $backupPath -Recurse -Force
}

foreach ($run in $runIndex.runs) {
    $manifest = $run.manifest
    if (-not (Test-Path $manifest)) { throw "Manifest missing: $manifest" }

    $runId = $run.runId
    $eventLog = Join-Path $logRoot "events_${runId}.jsonl"
    $canonicalLog = Join-Path $logRoot "canonical_${runId}.jsonl"

    $env:GAM_EXPERIMENT_ID = $runId
    $env:GAM_CONDITION = 'LM-Soft'
    $env:GAM_TASK_ID = ''
    $env:GAM_EXPERIMENT_LOG_PATH = $eventLog
    $env:GAM_EXPERIMENT_LOG = '1'
    $env:GAM_STRICT_LINK_MODE = '0'

    Write-Output "Running $runId"

    dotnet run --no-restore -p:GAM_PREVIEW=true -p:UseArtifactsOutput=true -p:ArtifactsPath=$artifactRoot --project tools\GmodAddonManager.ExperimentRunner -- --manifest $manifest --event-log $eventLog --canonical-log $canonicalLog --note "run_id=$runId"
    if ($LASTEXITCODE -ne 0) { throw "Runner failed for $runId" }
}

# Restore AppData if writable
if ($appDataWritable -and (Test-Path $backupPath)) {
    if (Test-Path $gamAppData) {
        Remove-Item -Path $gamAppData -Recurse -Force
    }
    Copy-Item -Path (Join-Path $backupPath 'GmodAddonManager') -Destination $appData -Recurse -Force
} elseif (-not $appDataWritable) {
    Write-Output "AppData is not writable; skipped restore to $gamAppData"
}

Write-Output "All runs complete. Logs at $logRoot"
