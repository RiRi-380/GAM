param(
    [Parameter(Mandatory = $true)]
    [string]$AcfPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$CountPerGroup = 10
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $AcfPath)) {
    Write-Error "ACF file not found: $AcfPath"
}

$content = Get-Content -Path $AcfPath -Raw
# PublishedFileId entries are quoted numbers: "123456789"
$ids = [System.Text.RegularExpressions.Regex]::Matches($content, '"(\d{5,})"') |
    ForEach-Object { $_.Groups[1].Value } |
    Select-Object -Unique

if ($ids.Count -eq 0) {
    Write-Error "No Workshop IDs found in $AcfPath"
}

$addons = @()
$group = "A"
$countInGroup = 0
foreach ($id in $ids) {
    $addons += [pscustomobject]@{
        id        = $id
        group     = $group
        type      = "gma"
        sizeBytes = 0
    }
    $countInGroup++
    if ($countInGroup -ge $CountPerGroup) {
        $group = if ($group -eq "A") { "B" } else { "A" }
        $countInGroup = 0
    }
}

$dataset = [pscustomobject]@{
    name   = "steam-workshop-from-acf"
    addons = $addons
}

$json = $dataset | ConvertTo-Json -Depth 5 -Compress
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath) | Out-Null
Set-Content -Path $OutputPath -Value $json -Encoding UTF8

Write-Host "Generated dataset with $($addons.Count) IDs into $OutputPath"
