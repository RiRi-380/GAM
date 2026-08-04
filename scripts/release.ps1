#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate and publish an exact GAM release tag.
.DESCRIPTION
    This script never stages or commits files and never pushes a branch. It only
    creates and pushes an annotated tag after verifying that clean local main is
    exactly origin/main and that version metadata and release notes match.
.EXAMPLE
    .\scripts\release.ps1 -Version v2.0.0 -Push
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Push
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$versionPattern = '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
if ($Version -notmatch $versionPattern) {
    throw "Version must be an exact stable semantic version such as v2.0.0."
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & git -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$branch = (& git -C $repoRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne "main") {
    throw "Releases may only be created from the main branch (current: '$branch')."
}

$status = & git -C $repoRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or $status) {
    throw "The worktree must be clean. This script will not stage or commit changes."
}

Invoke-Git fetch origin main
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
$originMain = (& git -C $repoRoot rev-parse origin/main).Trim()
if ($head -ne $originMain) {
    throw "HEAD ($head) must exactly match origin/main ($originMain)."
}

$normalizedVersion = $Version.Substring(1)
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
$declaredVersion = $buildProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($declaredVersion -ne $normalizedVersion) {
    throw "Directory.Build.props declares $declaredVersion, not $normalizedVersion."
}

$releaseNotes = Join-Path $repoRoot "docs\releases\$Version.md"
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
    throw "Release notes are missing: $releaseNotes"
}

& git -C $repoRoot rev-parse --quiet --verify "refs/tags/$Version" *> $null
if ($LASTEXITCODE -eq 0) {
    throw "Local tag $Version already exists."
}

$null = & git -C $repoRoot ls-remote --exit-code --tags origin "refs/tags/$Version" 2>$null
$remoteTagExitCode = $LASTEXITCODE
if ($remoteTagExitCode -eq 0) {
    throw "Remote tag $Version already exists."
}
if ($remoteTagExitCode -ne 2) {
    throw "Could not verify the remote tag state (git exit code $remoteTagExitCode)."
}

if (-not $Push) {
    Write-Host "Release preflight passed for $Version at $head." -ForegroundColor Green
    Write-Host "Re-run with -Push to create and push the annotated tag." -ForegroundColor Cyan
    exit 0
}

Invoke-Git tag -a $Version -m "GAM $Version"
try {
    Invoke-Git push origin "refs/tags/$Version"
} catch {
    Write-Warning "The annotated local tag remains at $Version because the push failed."
    throw
}

Write-Host "Pushed annotated tag $Version. The Release workflow is now responsible for publication." -ForegroundColor Green
