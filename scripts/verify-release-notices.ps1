[CmdletBinding()]
param(
    [string]$ProjectAssetsPath = "src\GmodAddonManager.UI\obj\project.assets.json",
    [string]$PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    if ((Get-Item -LiteralPath $Path).Length -le 0) {
        throw "$Description is empty: $Path"
    }
}

$noticeFileNames = @(
    "LICENSE",
    "NOTICE",
    "THIRD-PARTY-NOTICES.txt",
    "MICROSOFT-DOTNET-LIBRARY-LICENSE.txt",
    "MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt"
)

foreach ($fileName in $noticeFileNames) {
    Assert-File (Join-Path $repositoryRoot $fileName) "Repository notice file"
}

$licensePath = Join-Path $repositoryRoot "LICENSE"
$licenseText = Get-Content -LiteralPath $licensePath -Raw -Encoding UTF8
$normalizedLicenseText = $licenseText.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n")
$licenseBytes = [System.Text.Encoding]::UTF8.GetBytes($normalizedLicenseText)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $normalizedLicenseHash = (
        [BitConverter]::ToString($sha256.ComputeHash($licenseBytes))).Replace("-", "")
} finally {
    $sha256.Dispose()
}
$expectedNormalizedGplV3Hash =
    "8B1BA204BB69A0ADE2BFCF65EF294A920F6BB361B317DBA43C7EF29D96332B9B"
if ($normalizedLicenseHash -ne $expectedNormalizedGplV3Hash) {
    throw (
        "LICENSE must contain only the unmodified GNU GPL v3 text " +
        "(normalized SHA-256 mismatch: $normalizedLicenseHash)."
    )
}

$thirdPartyNoticesPath = Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.txt"
$noticeLines = Get-Content -LiteralPath $thirdPartyNoticesPath -Encoding UTF8
if ($noticeLines.Count -eq 0 -or
    $noticeLines[0] -ne "THIRD-PARTY SOFTWARE NOTICES FOR GMOD ADDON MANAGER") {
    throw "THIRD-PARTY-NOTICES.txt must use the version-independent GAM heading."
}
$expectedThirdPartyNoticesHash =
    "67490A15B5DBD2EEBC00A8CA567BB0A3847C8EDA4DDB31C5A08FBB3B818F2615"
$actualThirdPartyNoticesHash = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $thirdPartyNoticesPath).Hash
if ($actualThirdPartyNoticesHash -ne $expectedThirdPartyNoticesHash) {
    throw "THIRD-PARTY-NOTICES.txt integrity mismatch: $actualThirdPartyNoticesHash"
}

$inventory = @{}
foreach ($line in $noticeLines) {
    if ($line -notmatch '^Package:\s+(.+)\s+([^\s]+)$') {
        continue
    }

    $key = "$($matches[1].Trim())/$($matches[2].Trim())"
    if ($inventory.ContainsKey($key)) {
        throw "Duplicate package notice entry: $key"
    }

    $inventory[$key] = $true
}

$resolvedProjectAssetsPath = Resolve-RepositoryPath $ProjectAssetsPath
Assert-File $resolvedProjectAssetsPath "NuGet project.assets.json"
$projectAssets = Get-Content -LiteralPath $resolvedProjectAssetsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$resolvedPackages = @{}
foreach ($library in $projectAssets.libraries.PSObject.Properties) {
    if ([string]$library.Value.type -ne "package") {
        continue
    }

    $resolvedPackages[$library.Name] = $true
}

$missingPackages = @(
    $resolvedPackages.Keys |
        Where-Object { -not $inventory.ContainsKey($_) } |
        Sort-Object
)
$extraPackages = @(
    $inventory.Keys |
        Where-Object { -not $resolvedPackages.ContainsKey($_) } |
        Sort-Object
)
if ($missingPackages.Count -gt 0 -or $extraPackages.Count -gt 0) {
    $details = @()
    if ($missingPackages.Count -gt 0) {
        $details += "Missing notice entries: $($missingPackages -join ', ')"
    }
    if ($extraPackages.Count -gt 0) {
        $details += "Stale notice entries: $($extraPackages -join ', ')"
    }
    throw ($details -join [Environment]::NewLine)
}

$microsoftNoticeHashes = @{
    "MICROSOFT-DOTNET-LIBRARY-LICENSE.txt" =
        "7F6839A61CE892B79C6549E2DC5A81FDBD240A0B260F8881216B45B7FDA8B45D"
    "MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt" =
        "DEB4427A295E1ED474B0D81C5A0D972C1B550B9A715CDA939CDFA9236B1B418F"
}
foreach ($entry in $microsoftNoticeHashes.GetEnumerator()) {
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (
        Join-Path $repositoryRoot $entry.Key)).Hash
    if ($actualHash -ne $entry.Value) {
        throw "Microsoft notice hash mismatch for $($entry.Key): $actualHash"
    }
}

if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $resolvedPublishDirectory = Resolve-RepositoryPath $PublishDirectory
    if (-not (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container)) {
        throw "Publish directory is missing: $resolvedPublishDirectory"
    }

    foreach ($fileName in $noticeFileNames) {
        $sourcePath = Join-Path $repositoryRoot $fileName
        $publishedPath = Join-Path $resolvedPublishDirectory $fileName
        Assert-File $publishedPath "Published notice file"
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
        $publishedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedPath).Hash
        if ($sourceHash -ne $publishedHash) {
            throw "Published notice differs from repository source: $fileName"
        }
    }

    $distributionLicensesPath = Join-Path $resolvedPublishDirectory "DISTRIBUTION-LICENSES.txt"
    Assert-File $distributionLicensesPath "Combined distribution license"
    $distributionLicenses = Get-Content -LiteralPath $distributionLicensesPath -Raw -Encoding UTF8
    if ($distributionLicenses -notmatch 'GNU GENERAL PUBLIC LICENSE' -or
        $distributionLicenses -notmatch 'MICROSOFT \.NET LIBRARY') {
        throw "DISTRIBUTION-LICENSES.txt does not contain both GAM and .NET license terms."
    }

    $depsFiles = @(Get-ChildItem -LiteralPath $resolvedPublishDirectory -Filter "*.deps.json" -File)
    if ($depsFiles.Count -ne 1) {
        throw "Expected exactly one published .deps.json file, found $($depsFiles.Count)."
    }

    $publishedDeps = Get-Content -LiteralPath $depsFiles[0].FullName -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $runtimePacks = @(
        $publishedDeps.libraries.PSObject.Properties |
            Where-Object { [string]$_.Value.type -eq "runtimepack" } |
            ForEach-Object { $_.Name }
    )
    $expectedRuntimePack = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.10"
    if ($runtimePacks.Count -ne 1 -or $runtimePacks[0] -ne $expectedRuntimePack) {
        throw "Unexpected .NET runtime pack: $($runtimePacks -join ', ')"
    }

    Assert-File (Join-Path $resolvedPublishDirectory "coreclr.dll") "Self-contained .NET runtime"
}

Write-Host (
    "Verified release notices for {0} resolved packages{1}." -f
    $resolvedPackages.Count,
    $(if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { "" } else { " and the publish output" })
) -ForegroundColor Green
