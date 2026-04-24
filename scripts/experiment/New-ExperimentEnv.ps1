param(
    [string]$EnvRoot = "C:\\project\\GAM\\experiment_envs",
    [string]$LogRoot = "C:\\project\\GAM\\experiment_logs",
    [int[]]$Sizes = @(20, 200, 500),
    [double[]]$Ratios = @(0.8, 0.2),
    [int]$Seed = 20260127,
    [switch]$DryRun
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

function Ensure-Dir {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Get-SteamPaths {
    $steamPath = (Get-ItemProperty -Path "HKCU:\\Software\\Valve\\Steam" -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamPath) {
        $steamPath = "C:\\Program Files (x86)\\Steam"
    }
    $steamPath = $steamPath -replace "/", "\\"

    $libFile = Join-Path $steamPath "steamapps\\libraryfolders.vdf"
    if (-not (Test-Path $libFile)) {
        throw "libraryfolders.vdf not found: $libFile"
    }

    $paths = @()
    foreach ($line in Get-Content $libFile) {
        if ($line -match '\"path\"\\s+\"([^\"]+)\"') {
            $p = $Matches[1] -replace "\\\\", "\\"
            $paths += $p
        }
    }
    $paths = @($paths | Sort-Object -Unique)
    if ($paths.Count -eq 0) {
        $paths = @($steamPath)
    }

    $gmodPaths = @()
    $workshopPaths = @()
    foreach ($p in $paths) {
        $g = Join-Path $p "steamapps\\common\\GarrysMod"
        if (Test-Path $g) { $gmodPaths += $g }
        $w = Join-Path $p "steamapps\\workshop\\content\\4000"
        if (Test-Path $w) { $workshopPaths += $w }
    }

    if ($workshopPaths.Count -eq 0) {
        $fallbackWorkshop = Join-Path $steamPath "steamapps\\workshop\\content\\4000"
        if (Test-Path $fallbackWorkshop) {
            $workshopPaths += $fallbackWorkshop
        }
    }
    if ($workshopPaths.Count -eq 0) {
        throw "Workshop content path not found."
    }
    if ($gmodPaths.Count -eq 0) {
        throw "GarrysMod path not found."
    }

    return @{
        SteamPath = $steamPath
        WorkshopPath = $workshopPaths[0]
        GmodPath = $gmodPaths[0]
        LibraryPaths = $paths
    }
}

function Get-AddonIds {
    param([string]$WorkshopPath)
    $dirs = Get-ChildItem -Path $WorkshopPath -Directory | Where-Object { $_.Name -match '^[0-9]+$' }
    return @($dirs.Name | Sort-Object)
}

function Shuffle-Ids {
    param([string[]]$Ids, [int]$Seed)
    $rng = New-Object System.Random($Seed)
    $arr = $Ids.Clone()
    for ($i = $arr.Count - 1; $i -gt 0; $i--) {
        $j = $rng.Next(0, $i + 1)
        $tmp = $arr[$i]
        $arr[$i] = $arr[$j]
        $arr[$j] = $tmp
    }
    return ,$arr
}

function New-AssetSets {
    param([string[]]$Ids, [double]$Ratio, [int]$Seed)
    $rng = New-Object System.Random($Seed)
    $idsArr = $Ids.Clone()
    # Shuffle to pick A
    for ($i = $idsArr.Count - 1; $i -gt 0; $i--) {
        $j = $rng.Next(0, $i + 1)
        $tmp = $idsArr[$i]
        $idsArr[$i] = $idsArr[$j]
        $idsArr[$j] = $tmp
    }
    $half = [int]([Math]::Floor($idsArr.Count / 2))
    $a = $idsArr[0..($half - 1)]
    $aSet = @{}
    foreach ($id in $a) { $aSet[$id] = $true }
    $b = @()
    $intersectionCount = [int]([Math]::Floor($half * $Ratio))
    $aShuffled = $a.Clone()
    for ($i = $aShuffled.Count - 1; $i -gt 0; $i--) {
        $j = $rng.Next(0, $i + 1)
        $tmp = $aShuffled[$i]
        $aShuffled[$i] = $aShuffled[$j]
        $aShuffled[$j] = $tmp
    }
    $b += $aShuffled[0..($intersectionCount - 1)]
    foreach ($id in $Ids) {
        if (-not $aSet.ContainsKey($id)) {
            $b += $id
            if ($b.Count -ge $half) { break }
        }
    }
    return @{
        A = $a
        B = $b
        ACount = $a.Count
        BCount = $b.Count
        IntersectionCount = $intersectionCount
    }
}

Ensure-Dir $EnvRoot
Ensure-Dir $LogRoot
$script:LogFile = Join-Path $LogRoot ("setup_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")

Write-Log "Starting environment setup"
$paths = Get-SteamPaths
Write-Log ("SteamPath=" + $paths.SteamPath)
Write-Log ("WorkshopPath=" + $paths.WorkshopPath)
Write-Log ("GmodPath=" + $paths.GmodPath)

$allIds = Get-AddonIds -WorkshopPath $paths.WorkshopPath
Write-Log ("Workshop addon directory count=" + $allIds.Count)
$maxSize = ($Sizes | Measure-Object -Maximum).Maximum
if ($allIds.Count -lt $maxSize) {
    throw "Not enough workshop addons for max size $maxSize. Found $($allIds.Count)."
}

$shuffled = Shuffle-Ids -Ids $allIds -Seed $Seed
$envSelections = @()

foreach ($size in ($Sizes | Sort-Object)) {
    $envName = "Env$size"
    $ids = $shuffled[0..($size - 1)]
    $envSelections += @{
        Name = $envName
        Size = $size
        Ids = $ids
    }
    Write-Log ("Prepared selection for $envName size=$size")
}

$manifest = @{
    Seed = $Seed
    Sizes = $Sizes
    Ratios = $Ratios
    WorkshopPath = $paths.WorkshopPath
    GmodPath = $paths.GmodPath
    EnvSelections = @()
}

foreach ($env in $envSelections) {
    $envPath = Join-Path $EnvRoot $env.Name
    $workshopOut = Join-Path $envPath "workshop_content"
    $appdataOut = Join-Path $envPath "appdata"
    $manifestOut = Join-Path $envPath "manifest.json"
    $baseNoMountOut = Join-Path $envPath "base_addonnomount.txt"
    Ensure-Dir $envPath
    Ensure-Dir $workshopOut
    Ensure-Dir $appdataOut

    Write-Log ("Preparing $($env.Name) at $envPath")
    if (-not $DryRun) {
        foreach ($id in $env.Ids) {
            $src = Join-Path $paths.WorkshopPath $id
            $dst = Join-Path $workshopOut $id
            if (-not (Test-Path $dst)) {
                Ensure-Dir $dst
                $robocopy = @($src, $dst, "/MIR", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")
                & robocopy @robocopy | Out-Null
            }
        }
    }

    $assetsByRatio = @{}
    foreach ($ratio in $Ratios) {
        $ratioKey = "r" + ($ratio.ToString("0.00") -replace "\\.", "")
        $assetSeed = $Seed + $env.Size + [int]($ratio * 100)
        $set = New-AssetSets -Ids $env.Ids -Ratio $ratio -Seed $assetSeed
        $assetsByRatio[$ratioKey] = @{
            Ratio = $ratio
            A = $set.A
            B = $set.B
            ACount = $set.ACount
            BCount = $set.BCount
            IntersectionCount = $set.IntersectionCount
        }
        Write-Log ("$($env.Name) ratio=$ratio -> A=$($set.ACount), B=$($set.BCount), inter=$($set.IntersectionCount)")
    }

    # Build base addonnomount (all M disabled)
    $sortedIds = $env.Ids | Sort-Object {[long]$_}
    $lines = @()
    $lines += '"addonnomount"'
    $lines += "{"
    $i = 1
    foreach ($id in $sortedIds) {
        $lines += "`t`"$i`"`t`t`"$id`""
        $i++
    }
    $lines += "}"
    if (-not $DryRun) {
        Set-Content -Path $baseNoMountOut -Value $lines -Encoding UTF8
    }

    # Build config.json
    $addonMetadata = @{}
    foreach ($id in $env.Ids) {
        $addonMetadata[$id] = @{
            id = $id
            title = "Workshop-$id"
            size = 0
            lastUpdated = (Get-Date).ToUniversalTime().ToString("o")
            thumbnailUrl = ""
            author = ""
            isEnabled = $true
            folderPath = (Join-Path $workshopOut $id)
            description = ""
            type = ""
            tags = @()
            isGmaFile = $false
            needsTitleUpdate = $false
            isFavorite = $false
        }
    }

    $assets = @()
    # System assets
    $assets += @{
        id = "subscribe-system-asset"
        name = "Subscribe"
        isSystem = $true
        enabled = $false
        addons = @("*")
        addonStates = @{}
        defaultAddonState = 0
        workshopCollectionId = $null
        autoUpdateCollection = $true
        currentVersion = 0
        versionHistory = @()
    }
    $assets += @{
        id = "junction-system-asset"
        name = "Junction"
        isSystem = $true
        enabled = $false
        addons = @()
        addonStates = @{}
        defaultAddonState = 0
        workshopCollectionId = $null
        autoUpdateCollection = $true
        currentVersion = 0
        versionHistory = @()
    }

    # Base asset
    $baseAddonStates = @{}
    foreach ($id in $env.Ids) { $baseAddonStates[$id] = 1 }
    $assets += @{
        id = ([guid]::NewGuid().ToString())
        name = "Base"
        isSystem = $false
        enabled = $false
        addons = $env.Ids
        addonStates = $baseAddonStates
        defaultAddonState = 1
        workshopCollectionId = $null
        autoUpdateCollection = $true
        currentVersion = 0
        versionHistory = @()
    }

    foreach ($ratioKey in $assetsByRatio.Keys) {
        $entry = $assetsByRatio[$ratioKey]
        $aName = "A_$ratioKey"
        $bName = "B_$ratioKey"
        $aStates = @{}
        $bStates = @{}
        foreach ($id in $env.Ids) {
            if ($entry.A -contains $id) {
                $aStates[$id] = 0
            } else {
                $aStates[$id] = 1
            }
            if ($entry.B -contains $id) {
                $bStates[$id] = 0
            } else {
                $bStates[$id] = 1
            }
        }
        $assets += @{
            id = ([guid]::NewGuid().ToString())
            name = $aName
            isSystem = $false
            enabled = $false
            addons = $env.Ids
            addonStates = $aStates
            defaultAddonState = 1
            workshopCollectionId = $null
            autoUpdateCollection = $true
            currentVersion = 0
            versionHistory = @()
        }
        $assets += @{
            id = ([guid]::NewGuid().ToString())
            name = $bName
            isSystem = $false
            enabled = $false
            addons = $env.Ids
            addonStates = $bStates
            defaultAddonState = 1
            workshopCollectionId = $null
            autoUpdateCollection = $true
            currentVersion = 0
            versionHistory = @()
        }
    }

    $config = @{
        version = "1.0"
        lastUpdated = (Get-Date).ToUniversalTime().ToString("o")
        assets = $assets
        addonMetadata = $addonMetadata
        junctionHistory = @{}
    }

    $configPath = Join-Path $appdataOut "config.json"
    if (-not $DryRun) {
        $config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
        $settings = @{
            Language = "ja-JP"
            ShowConsoleOnStartup = $false
            DisableMode = 0
            StrictLinkMode = $false
            EnableBackgroundTitleUpdates = $false
            EnableBackgroundAddonPreload = $false
            EnableLocalAddonsExperimental = $false
            DeveloperModePhrase = ""
            UpdateRepository = ""
        }
        $settings | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $appdataOut "settings.json") -Encoding UTF8
        @{ changes = @() } | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $appdataOut "pending.json") -Encoding UTF8
    }

    $envManifest = @{
        name = $env.Name
        size = $env.Size
        ids = $env.Ids
        assets = $assetsByRatio
        workshopContentPath = $workshopOut
        appdataPath = $appdataOut
        baseAddonnomount = $baseNoMountOut
    }
    if (-not $DryRun) {
        $envManifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestOut -Encoding UTF8
    }

    $manifest.EnvSelections += $envManifest
}

$manifestPath = Join-Path $EnvRoot "manifest.json"
if (-not $DryRun) {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
}

Write-Log "Environment setup completed."
