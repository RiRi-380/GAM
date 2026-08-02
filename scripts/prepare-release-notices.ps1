[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [string]$ProjectAssetsPath = "src\GmodAddonManager.UI\obj\project.assets.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedPublishDirectory = if ([System.IO.Path]::IsPathRooted($PublishDirectory)) {
    [System.IO.Path]::GetFullPath($PublishDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $PublishDirectory))
}
if (-not (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container)) {
    throw "Publish directory is missing: $resolvedPublishDirectory"
}

$noticeFileNames = @(
    "LICENSE",
    "THIRD-PARTY-NOTICES.txt",
    "MICROSOFT-DOTNET-LIBRARY-LICENSE.txt",
    "MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt"
)
foreach ($fileName in $noticeFileNames) {
    $sourcePath = Join-Path $repositoryRoot $fileName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required notice source is missing: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (
        Join-Path $resolvedPublishDirectory $fileName) -Force
}

$gamLicense = Get-Content -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Raw -Encoding UTF8
$dotnetLicense = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "MICROSOFT-DOTNET-LIBRARY-LICENSE.txt") -Raw -Encoding UTF8
$separator = "=" * 80
$combinedLicense = @"
GMOD ADDON MANAGER DISTRIBUTION LICENSES

GAM is licensed under GNU GPL v3 as reproduced in section 1.
This self-contained Windows distribution also includes Microsoft .NET runtime
binaries governed by the Microsoft .NET Library License reproduced in section 2.
Other bundled components are covered by THIRD-PARTY-NOTICES.txt and
MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt.

$separator
SECTION 1 - GMOD ADDON MANAGER (GNU GPL v3)
$separator

$gamLicense

$separator
SECTION 2 - MICROSOFT .NET LIBRARY
$separator

$dotnetLicense
"@
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    (Join-Path $resolvedPublishDirectory "DISTRIBUTION-LICENSES.txt"),
    $combinedLicense,
    $utf8WithoutBom)

& (Join-Path $PSScriptRoot "verify-release-notices.ps1") `
    -ProjectAssetsPath $ProjectAssetsPath `
    -PublishDirectory $resolvedPublishDirectory
