param(
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
        throw "Processes running: $list. Close Steam/GMod before deactivation."
    }
}

Ensure-Dir $LogRoot
$script:LogFile = Join-Path $LogRoot ("deactivate_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")
Write-Log "Deactivating environment"

Assert-NoProcess

$paths = Get-SteamPaths
$workshopPath = $paths.WorkshopPath
$workshopRoot = Split-Path -Parent $workshopPath
$workshopOrig = Join-Path $workshopRoot "4000__orig"

if (Test-Path $workshopPath) {
    $isJunction = (Get-Item $workshopPath).Attributes -match "ReparsePoint"
    if ($isJunction) {
        Write-Log "Removing workshop junction $workshopPath"
        cmd /c rmdir "$workshopPath" | Out-Null
    }
}

if (Test-Path $workshopOrig) {
    Write-Log "Restoring original workshop content"
    Move-Item -Path $workshopOrig -Destination $workshopPath
}

$acfPath = Join-Path $workshopRoot "appworkshop_4000.acf"
$acfBackup = "$acfPath.bak"
if ((Test-Path $acfBackup) -and -not (Test-Path $acfPath)) {
    Write-Log "Restoring appworkshop_4000.acf"
    Move-Item -Path $acfBackup -Destination $acfPath
}

$appDataRoot = Join-Path $env:APPDATA "GmodAddonManager"
$appDataBackup = Join-Path $env:APPDATA "GmodAddonManager__orig"
if (Test-Path $appDataRoot) {
    $isJunction = (Get-Item $appDataRoot).Attributes -match "ReparsePoint"
    if ($isJunction) {
        Write-Log "Removing AppData junction"
        cmd /c rmdir "$appDataRoot" | Out-Null
    }
}
if (Test-Path $appDataBackup) {
    Write-Log "Restoring original AppData"
    Move-Item -Path $appDataBackup -Destination $appDataRoot
}

Write-Log "Deactivation complete."
