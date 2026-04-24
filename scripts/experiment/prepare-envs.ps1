param(
    [string]$EnvRoot = 'C:\project\GAM\experiment_envs',
    [string]$LogsRoot = 'C:\project\GAM\experiment_logs',
    [int]$Seed = 20260127,
    [int]$Repeats = 10,
    [double[]]$Ratios = @(0.8, 0.2)
)

$ErrorActionPreference = 'Stop'

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

$steamPath = Get-SteamPath
if (-not $steamPath) { throw 'Steam path not found.' }
$workshopPath = Get-WorkshopPath $steamPath
if (-not $workshopPath) { throw 'Workshop content path not found.' }

$addonDirs = Get-ChildItem $workshopPath -Directory | Where-Object { $_.Name -match '^\d+$' } | Sort-Object Name
$addonIds = $addonDirs.Name
$addonCount = $addonIds.Count
if ($addonCount -lt 5) { throw "Not enough addons for experiment (found $addonCount)." }

# Sizes based on availability
$sizes = @()
if ($addonCount -ge 5) { $sizes += 5 }
if ($addonCount -ge 10) { $sizes += 10 }
if ($addonCount -ge 20) { $sizes += 20 }
if ($sizes.Count -eq 0) { $sizes += $addonCount }
if ($sizes[-1] -ne $addonCount) { $sizes += $addonCount }
$sizes = $sizes | Sort-Object -Unique

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runEnvRoot = Join-Path $EnvRoot "run_$timestamp"
$runLogRoot = Join-Path $LogsRoot "run_$timestamp"
Ensure-Dir $runEnvRoot
Ensure-Dir $runLogRoot

$shuffled = Shuffle-List $addonIds $Seed

$runIndex = [ordered]@{
    runId = "run_$timestamp"
    createdAt = (Get-Date).ToString('O')
    seed = $Seed
    sourceWorkshopPath = $workshopPath
    sizes = $sizes
    ratios = $Ratios
    envs = @()
    runs = @()
}

foreach ($size in $sizes) {
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

    $copyLog = Join-Path $runLogRoot "copy_${envName}.log"
    foreach ($id in $selected) {
        $src = Join-Path $workshopPath $id
        $dst = Join-Path $envWorkshop $id
        Ensure-Dir $dst
        $rc = Start-Process -FilePath robocopy -ArgumentList @("`"$src`"", "`"$dst`"", '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:1', '/W:1', '/NFL', '/NDL', '/NP', '/NJH', '/NJS') -PassThru -Wait
        Add-Content -Path $copyLog -Value "Copied $id ExitCode=$($rc.ExitCode)"
        if ($rc.ExitCode -ge 8) { throw "Robocopy failed for $id (ExitCode=$($rc.ExitCode))" }
    }

    # Create empty addonnomount.txt
    $addonnomount = Join-Path $envCfg 'addonnomount.txt'
    if (-not (Test-Path $addonnomount)) {
        $content = @"
"addonnomount"
{
}
"@
        Set-Content -Path $addonnomount -Value $content -Encoding UTF8
    }

    $envManifest = [ordered]@{
        envName = $envName
        envRoot = $envRoot
        workshopPath = $envWorkshop
        gmodRoot = $envGmod
        addonIds = $selected
        size = $size
    }

    $envManifestPath = Join-Path $envRoot 'env_manifest.json'
    $envManifest | ConvertTo-Json -Depth 6 | Set-Content -Path $envManifestPath -Encoding UTF8

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

        $rand = New-Object System.Random($Seed + [int]($ratio * 1000) + $size)
        $aSet = $selected[0..($half-1)]
        $aSet = Shuffle-List $aSet ($Seed + 1)

        $overlapSet = $aSet[0..($overlap-1)]
        $remaining = $selected | Where-Object { $_ -notin $aSet }
        $remaining = Shuffle-List $remaining ($Seed + 2)
        $need = $half - $overlap
        if ($need -le 0) {
            $extra = @()
        } else {
            $extra = $remaining | Select-Object -First $need
        }
        $bSet = @($overlapSet + $extra) | Select-Object -First $half

        $runId = "${envName}_r$ratio"
        $manifest = [ordered]@{
            runId = $runId
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

        $manifestPath = Join-Path $runLogRoot "manifest_${runId}.json"
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

        $runIndex.runs += [ordered]@{
            runId = $runId
            envName = $envName
            manifest = $manifestPath
        }
    }
}

$runIndexPath = Join-Path $runLogRoot 'run_index.json'
$runIndex | ConvertTo-Json -Depth 8 | Set-Content -Path $runIndexPath -Encoding UTF8

Write-Output "Prepared envs under: $runEnvRoot"
Write-Output "Run index: $runIndexPath"
