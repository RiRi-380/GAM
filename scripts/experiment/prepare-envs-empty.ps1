param(
    [string]$EnvRoot = 'C:\project\GAM\experiment_envs',
    [string]$LogsRoot = 'C:\project\GAM\experiment_logs',
    [int]$Seed = 20260130,
    [int]$Repeats = 20,
    [double[]]$Ratios = @(0.8, 0.2),
    [int[]]$Sizes = @(20, 50, 100, 200, 500),
    [string]$RunTag = 'softscale',
    [string]$SourceWorkshopPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SteamPath {
    $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamPath) { $steamPath = (Get-ItemProperty 'HKLM:\Software\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
    if ($steamPath) { return ($steamPath -replace '/', '\') }
    return $null
}

function Get-WorkshopPath([string]$steamPath) {
    $path = Join-Path $steamPath 'steamapps\workshop\content\4000'
    if (Test-Path $path) { return $path }

    $libVdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path $libVdf)) { return $null }

    $content = Get-Content $libVdf -Raw
    $matches = [regex]::Matches($content, '"path"\s*"([^"]+)"')
    foreach ($m in $matches) {
        $lib = ($m.Groups[1].Value -replace '\\','\') -replace '/', '\'
        $candidate = Join-Path $lib 'steamapps\workshop\content\4000'
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

function Shuffle-List([string[]]$list, [int]$seed) {
    $rand = New-Object System.Random($seed)
    $arr = [System.Collections.Generic.List[string]]::new()
    $list | ForEach-Object { $arr.Add($_) }
    for ($i = $arr.Count - 1; $i -gt 0; $i--) {
        $j = $rand.Next(0, $i + 1)
        $tmp = $arr[$i]
        $arr[$i] = $arr[$j]
        $arr[$j] = $tmp
    }
    return ,$arr.ToArray()
}

function Ensure-Dir([string]$path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

if ([string]::IsNullOrWhiteSpace($SourceWorkshopPath)) {
    $steamPath = Get-SteamPath
    if (-not $steamPath) { throw 'Steam path not found. Set -SourceWorkshopPath.' }
    $SourceWorkshopPath = Get-WorkshopPath $steamPath
    if (-not $SourceWorkshopPath) { throw 'Workshop content path not found. Set -SourceWorkshopPath.' }
}

if (-not (Test-Path $SourceWorkshopPath)) {
    throw "Source workshop path not found: $SourceWorkshopPath"
}

$addonDirs = Get-ChildItem $SourceWorkshopPath -Directory | Where-Object { $_.Name -match '^\d+$' } | Sort-Object Name
$addonIds = $addonDirs.Name
$addonCount = $addonIds.Count
if ($addonCount -lt 1) { throw "Not enough addons in source workshop path (found $addonCount)." }

$maxSize = ($Sizes | Measure-Object -Maximum).Maximum
if ($addonCount -lt $maxSize) {
    throw "Not enough addons for requested sizes. Requested max=$maxSize, available=$addonCount."
}

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runId = "run_${timestamp}_${RunTag}"
$runEnvRoot = Join-Path $EnvRoot $runId
$runLogRoot = Join-Path $LogsRoot $runId
Ensure-Dir $runEnvRoot
Ensure-Dir $runLogRoot

$shuffled = Shuffle-List $addonIds $Seed

$runIndex = [ordered]@{
    runId = $runId
    createdAt = (Get-Date).ToString('O')
    seed = $Seed
    sourceWorkshopPath = $SourceWorkshopPath
    sizes = $Sizes
    ratios = $Ratios
    envs = @()
    runs = @()
}

foreach ($size in $Sizes) {
    $envName = "Env$size"
    $envRoot = Join-Path $runEnvRoot $envName
    $envWorkshop = Join-Path $envRoot 'steamapps\workshop\content\4000'
    $envGmod = Join-Path $envRoot 'steamapps\common\GarrysMod'
    $envCfg = Join-Path $envGmod 'garrysmod\cfg'
    $envCache = Join-Path $envGmod 'garrysmod\cache\workshop'

    Ensure-Dir $envWorkshop
    Ensure-Dir $envCfg
    Ensure-Dir $envCache

    $selected = $shuffled[0..($size-1)]
    foreach ($id in $selected) {
        $dst = Join-Path $envWorkshop $id
        Ensure-Dir $dst
    }

    $addonnomount = Join-Path $envCfg 'addonnomount.txt'
    $content = @"
"addonnomount"
{
}
"@
    Set-Content -Path $addonnomount -Value $content -Encoding UTF8

    $localIds = Get-ChildItem $envWorkshop -Directory | Where-Object { $_.Name -match '^\d+$' } | Select-Object -ExpandProperty Name
    $extra = @($localIds | Where-Object { $_ -notin $selected })
    $missing = @($selected | Where-Object { $_ -notin $localIds })
    if ($extra.Count -gt 0 -or $missing.Count -gt 0) {
        throw "S_local mismatch in $envName. extra=$($extra.Count) missing=$($missing.Count)"
    }

    $envManifest = [ordered]@{
        envName = $envName
        envRoot = $envRoot
        workshopPath = $envWorkshop
        gmodRoot = $envGmod
        addonIds = $selected
        size = $size
        s_local_count = $localIds.Count
        s_local_extra = $extra
        s_local_missing = $missing
    }

    $envManifestPath = Join-Path $envRoot 'env_manifest.json'
    $envManifest | ConvertTo-Json -Depth 8 | Set-Content -Path $envManifestPath -Encoding UTF8

    $runIndex.envs += [ordered]@{
        envName = $envName
        envRoot = $envRoot
        envManifest = $envManifestPath
        workshopPath = $envWorkshop
        size = $size
    }

    foreach ($ratio in $Ratios) {
        $half = [math]::Floor($size / 2)
        if ($half -lt 1) { continue }
        $overlap = [math]::Max(1, [math]::Min($half, [math]::Round($ratio * $half)))

        $aSet = $selected[0..($half-1)]
        $aSet = Shuffle-List $aSet ($Seed + 1)
        $overlapSet = $aSet[0..($overlap-1)]
        $remaining = $selected | Where-Object { $_ -notin $aSet }
        $remaining = Shuffle-List $remaining ($Seed + 2)
        $need = $half - $overlap
        if ($need -le 0) {
            $extraSet = @()
        } else {
            $extraSet = $remaining | Select-Object -First $need
        }
        $bSet = @($overlapSet + $extraSet) | Select-Object -First $half

        $runIdLocal = "${envName}_r$ratio"
        $manifest = [ordered]@{
            runId = $runIdLocal
            envName = $envName
            workshopPath = $envWorkshop
            addonIds = $selected
            assetSets = [ordered]@{
                Base = @{ enabled = @() }
                A = @{ enabled = $aSet }
                B = @{ enabled = $bSet }
            }
            tasks = @(
                @{ id = 'T1'; from = 'Base'; to = 'A' },
                @{ id = 'T2'; from = 'A'; to = 'B' },
                @{ id = 'T3'; from = 'B'; to = 'A' },
                @{ id = 'T4'; from = 'A'; to = 'A' }
            )
            repeats = $Repeats
            mSize = $size
            overlapRatio = $ratio
            seed = $Seed
        }

        $manifestPath = Join-Path $runLogRoot "manifest_${runIdLocal}.json"
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

        $runIndex.runs += [ordered]@{
            runId = $runIdLocal
            envName = $envName
            manifest = $manifestPath
        }
    }
}

$runIndexPath = Join-Path $runLogRoot 'run_index.json'
$runIndex | ConvertTo-Json -Depth 8 | Set-Content -Path $runIndexPath -Encoding UTF8

Write-Output "Prepared empty envs under: $runEnvRoot"
Write-Output "Run index: $runIndexPath"
