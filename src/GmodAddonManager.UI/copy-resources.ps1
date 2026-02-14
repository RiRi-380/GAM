# PowerShell script to copy resources to output directories
param(
    [string]$Configuration = "Debug"
)

$sourceDir = $PSScriptRoot
$targetDir = Join-Path $sourceDir "bin\$Configuration\net6.0"

# Create Resources directory if it doesn't exist
$resourcesTarget = Join-Path $targetDir "Resources"
if (!(Test-Path $resourcesTarget)) {
    New-Item -ItemType Directory -Path $resourcesTarget -Force
}

# Copy localization files
$resourcesSource = Join-Path $sourceDir "Resources"
if (Test-Path $resourcesSource) {
    Copy-Item -Path "$resourcesSource\*.json" -Destination $resourcesTarget -Force
    Write-Host "Copied localization files to $resourcesTarget"
}

# Create Assets directory if needed (though Avalonia should handle this)
$assetsSource = Join-Path $sourceDir "Assets"
if (Test-Path $assetsSource) {
    $assetsTarget = Join-Path $targetDir "Assets"
    if (!(Test-Path $assetsTarget)) {
        # Note: AvaloniaResources are usually embedded, so this might not be needed
        Write-Host "Assets folder exists but is handled by Avalonia as embedded resources"
    }
}

Write-Host "Resource copy complete for $Configuration configuration"