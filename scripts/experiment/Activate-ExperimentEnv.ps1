param(
    [Parameter(Mandatory = $true)][string]$EnvName,
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

function Get-SteamPaths {
    $steamPath = (Get-ItemProperty -Path "HKCU:\\Software\\Valve\\Steam" -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamPath) { $steamPath = "C:\\Program Files (x86)\\Steam" }
    $steamPath = $steamPath -replace "/", "\\"
    $libFile = Join-Path $steamPath "steamapps\\libraryfolders.vdf"
    if (-not (Test-Path $libFile)) { throw "libraryfolders.vdf not found: $libFile" }
    $paths = @()
    foreach ($line in Get-Content $libFile) {
        if ($line -match '\"path\"\\s+\"([^\"]+)\"') { $paths += ($Matches[1] -replace "\\\\", "\\") }
    }
    $paths = @($paths | Sort-Object -Unique)
    if ($paths.Count -eq 0) { $paths = @($steamPath) }
    $gmod = $null
    $workshop = $null
    foreach ($p in $paths) {
        if (-not $gmod) {
            $g = Join-Path $p "steamapps\\common\\GarrysMod"
            if (Test-Path $g) { $gmod = $g }
        }
        if (-not $workshop) {
            $w = Join-Path $p "steamapps\\workshop\\content\\4000"
            if (Test-Path $w) { $workshop = $w }
        }
    }
    if (-not $workshop) {
        $fallbackWorkshop = Join-Path $steamPath "steamapps\\workshop\\content\\4000"
        if (Test-Path $fallbackWorkshop) { $workshop = $fallbackWorkshop }
    }
    if (-not $gmod) { throw "GarrysMod path not found." }
    if (-not $workshop) { throw "Workshop content path not found." }
    return @{ SteamPath = $steamPath; GmodPath = $gmod; WorkshopPath = $workshop }
}

function Assert-NoProcess {
    $names = @("steam", "steamwebhelper", "hl2", "gmod")
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName.ToLower() }
    if ($running) {
        $list = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
        throw "Processes running: $list. Close Steam/GMod before activation."
    }
}

Ensure-Dir $LogRoot
$script:LogFile = Join-Path $LogRoot ("activate_" + $EnvName + "_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")
Write-Log "Activating environment: $EnvName"

Assert-NoProcess

$paths = Get-SteamPaths
$envPath = Join-Path $EnvRoot $EnvName
$manifestPath = Join-Path $envPath "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "Env manifest not found: $manifestPath" }
$manifest = Get-Content $manifestPath | ConvertFrom-Json

$workshopPath = $paths.WorkshopPath
$workshopRoot = Split-Path -Parent $workshopPath
$workshopOrig = Join-Path $workshopRoot "4000__orig"

if (-not (Test-Path $workshopOrig)) {
    if (Test-Path $workshopPath) {
        Write-Log "Moving original workshop content to $workshopOrig"
        Move-Item -Path $workshopPath -Destination $workshopOrig
    }
}

if (Test-Path $workshopPath) {
    $isJunction = (Get-Item $workshopPath).Attributes -match "ReparsePoint"
    if ($isJunction) {
        Write-Log "Removing existing workshop junction at $workshopPath"
        cmd /c rmdir "$workshopPath" | Out-Null
    }
}

Write-Log "Creating workshop junction: $workshopPath -> $($manifest.workshopContentPath)"
New-Item -ItemType Junction -Path $workshopPath -Target $manifest.workshopContentPath | Out-Null

$acfPath = Join-Path $workshopRoot "appworkshop_4000.acf"
$acfBackup = "$acfPath.bak"
if ((Test-Path $acfPath) -and -not (Test-Path $acfBackup)) {
    Write-Log "Backing up Steam workshop cache file"
    Move-Item -Path $acfPath -Destination $acfBackup
}

$appDataRoot = Join-Path $env:APPDATA "GmodAddonManager"
$appDataBackup = Join-Path $env:APPDATA "GmodAddonManager__orig"
if (-not (Test-Path $appDataBackup)) {
    if (Test-Path $appDataRoot) {
        Write-Log "Moving original AppData to $appDataBackup"
        Move-Item -Path $appDataRoot -Destination $appDataBackup
    }
}
if (Test-Path $appDataRoot) {
    $isJunction = (Get-Item $appDataRoot).Attributes -match "ReparsePoint"
    if ($isJunction) {
        Write-Log "Removing existing AppData junction at $appDataRoot"
        cmd /c rmdir "$appDataRoot" | Out-Null
    }
}

Write-Log "Creating AppData junction: $appDataRoot -> $($manifest.appdataPath)"
New-Item -ItemType Junction -Path $appDataRoot -Target $manifest.appdataPath | Out-Null

$gmodCfg = Join-Path $paths.GmodPath "garrysmod\\cfg"
Ensure-Dir $gmodCfg
$targetNoMount = Join-Path $gmodCfg "addonnomount.txt"
Write-Log "Copying base addonnomount to $targetNoMount"
Copy-Item -Path $manifest.baseAddonnomount -Destination $targetNoMount -Force

Write-Log "Activation complete."
