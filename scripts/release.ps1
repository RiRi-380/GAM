#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Create a new release for GAM
.DESCRIPTION
    This script creates a new release by adding a release trigger to the commit message
.PARAMETER Type
    The type of release: major, minor, or patch (default: patch)
.PARAMETER Message
    Additional commit message (optional)
.EXAMPLE
    .\release.ps1
    Creates a patch release with the latest changes
.EXAMPLE
    .\release.ps1 -Type minor -Message "Add new feature"
    Creates a minor release with a custom message
#>

param(
    [ValidateSet("major", "minor", "patch")]
    [string]$Type = "patch",
    [string]$Message = ""
)

# Colors for output
$ErrorActionPreference = "Stop"

function Write-Success {
    Write-Host $args[0] -ForegroundColor Green
}

function Write-Info {
    Write-Host $args[0] -ForegroundColor Cyan
}

function Write-Warning {
    Write-Host $args[0] -ForegroundColor Yellow
}

# Check if we're in a git repository
if (!(Test-Path .git)) {
    Write-Error "This script must be run from the root of the GAM repository"
    exit 1
}

# Check for uncommitted changes
$status = git status --porcelain
if ($status) {
    Write-Warning "You have uncommitted changes:"
    Write-Host $status
    $response = Read-Host "Do you want to commit these changes? (y/n)"
    if ($response -ne 'y') {
        Write-Info "Aborting release"
        exit 0
    }
}

# Get the latest tag
$latestTag = git describe --tags --abbrev=0 2>$null
if (!$latestTag) {
    $latestTag = "v0.0.0"
}
Write-Info "Latest tag: $latestTag"

# Calculate next version
$version = $latestTag -replace '^v', ''
$versionParts = $version -split '\.'
$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$patch = [int]$versionParts[2]

switch ($Type) {
    "major" {
        $major++
        $minor = 0
        $patch = 0
    }
    "minor" {
        $minor++
        $patch = 0
    }
    "patch" {
        $patch++
    }
}

$newVersion = "v$major.$minor.$patch"
Write-Info "Next version will be: $newVersion"

# Build commit message
$commitMessage = if ($Message) {
    "$Message `n`n[release:$Type]"
} else {
    "Release $newVersion`n`n[release:$Type]"
}

# Commit any changes
if ($status) {
    Write-Info "Committing changes..."
    git add -A
    git commit -m $commitMessage
    Write-Success "Changes committed"
} else {
    # Create empty commit to trigger release
    Write-Info "Creating release commit..."
    git commit --allow-empty -m $commitMessage
}

# Create tag for release workflow
$existingTag = git tag -l $newVersion
if ($existingTag) {
    Write-Error "Tag $newVersion already exists. Aborting to avoid duplicate release."
    exit 1
}

git tag $newVersion

# Push to trigger the release workflow
Write-Info "Pushing to GitHub..."
git push origin main
git push origin $newVersion

Write-Success "Release process started!"
Write-Info "Check the Actions tab on GitHub for build progress:"
Write-Info "https://github.com/RiRi-380/GAM/actions"
Write-Info ""
Write-Info "Once the build completes, the release will be available at:"
Write-Info "https://github.com/RiRi-380/GAM/releases/tag/$newVersion"
