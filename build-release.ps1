# Build script for GAM - Same as GitHub Actions
param(
    [string]$Version = "v1.0.0",
    [switch]$NonInteractive
)

$releaseVersion = $Version.Trim().TrimStart('v')

try {
    [Version]$versionValue = $releaseVersion
}
catch {
    throw "Version must be a numeric semantic version like v1.0.1 or 1.0.1. Received: $Version"
}

$buildComponent = if ($versionValue.Build -ge 0) { $versionValue.Build } else { 0 }
$revisionComponent = if ($versionValue.Revision -ge 0) { $versionValue.Revision } else { 0 }
$assemblyVersion = "{0}.{1}.{2}.{3}" -f $versionValue.Major, $versionValue.Minor, $buildComponent, $revisionComponent
$fileVersion = $assemblyVersion
$informationalVersion = $Version.Trim()
$compatibilityInstallerName = "GAM-Setup.exe"

Write-Host "Building GAM $Version..." -ForegroundColor Green
Write-Host "Resolved release version: $releaseVersion" -ForegroundColor Cyan
Write-Host "Resolved assembly/file version: $assemblyVersion" -ForegroundColor Cyan

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "publish") {
    Remove-Item -Path "publish" -Recurse -Force
}
if (Test-Path "dist") {
    Remove-Item -Path "dist" -Recurse -Force
}

# Restore dependencies
Write-Host "Restoring dependencies..." -ForegroundColor Yellow
dotnet restore

# Build self-contained executable
Write-Host "Building self-contained executable..." -ForegroundColor Yellow
dotnet publish src/GmodAddonManager.UI/GmodAddonManager.UI.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$releaseVersion `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$fileVersion `
    -p:InformationalVersion=$informationalVersion `
    -o publish

# Copy steam_api64.dll to publish folder
Write-Host "Copying steam_api64.dll..." -ForegroundColor Yellow
$steamDllSource = "src/GmodAddonManager.UI/bin/Release/net6.0/win-x64/steam_api64.dll"
if (-not (Test-Path $steamDllSource)) {
    # Try alternative location
    $steamDllSource = "steam_api64.dll"
}
if (Test-Path $steamDllSource) {
    Copy-Item $steamDllSource -Destination "publish/steam_api64.dll" -Force
    Write-Host "steam_api64.dll copied successfully" -ForegroundColor Green
} else {
    Write-Host "WARNING: steam_api64.dll not found! Workshop features will not work." -ForegroundColor Red
}

$debugSymbolFiles = Get-ChildItem -Path "publish" -Filter "*.pdb" -File -ErrorAction SilentlyContinue
if ($debugSymbolFiles) {
    $debugSymbolFiles | Remove-Item -Force
    Write-Host "Removed debug symbol files from distributable outputs" -ForegroundColor Green
}

# Create portable ZIP
Write-Host "Creating portable ZIP..." -ForegroundColor Yellow
$zipPath = "GAM-Portable-$releaseVersion.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}
Compress-Archive -Path publish/* -DestinationPath $zipPath

# Build installer (if Inno Setup is installed)
$innoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $innoSetupPath) {
    Write-Host "Building installer..." -ForegroundColor Yellow
    
    # Check for VC++ Redistributable
    $vcRedistPath = Join-Path $PSScriptRoot "redist\VC_redist.x64.exe"
    if (-not (Test-Path $vcRedistPath)) {
        Write-Host "⚠️ WARNING: Visual C++ Redistributable not found!" -ForegroundColor Yellow
        Write-Host "  Expected at: $vcRedistPath" -ForegroundColor Yellow
        Write-Host "  Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe" -ForegroundColor Yellow
        Write-Host "  The installer will be built without VC++ Redistributable." -ForegroundColor Yellow
        Write-Host "  Users may need to install it manually." -ForegroundColor Yellow
    } else {
        Write-Host "✓ Visual C++ Redistributable found" -ForegroundColor Green
    }
    
    & $innoSetupPath installer/setup.iss /DMyAppVersion=$releaseVersion
    
    # Move installer to root directory for easy access
    $installerSource = "dist\GAM-Setup-$releaseVersion.exe"
    if (Test-Path $installerSource) {
        Move-Item $installerSource . -Force
        Write-Host "Installer created: GAM-Setup-$releaseVersion.exe" -ForegroundColor Green

        $compatibilityInstallerPath = Join-Path $PSScriptRoot $compatibilityInstallerName
        Copy-Item "GAM-Setup-$releaseVersion.exe" $compatibilityInstallerPath -Force
        Write-Host "Compatibility installer created: $compatibilityInstallerName" -ForegroundColor Green
    }
} else {
    Write-Host "Inno Setup not found. Skipping installer build." -ForegroundColor Red
    Write-Host "Install from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
}

Write-Host "`nBuild completed!" -ForegroundColor Green
Write-Host "Outputs:" -ForegroundColor Cyan
Write-Host "  - Portable: $zipPath" -ForegroundColor White
Write-Host "  - Executable: publish\GmodAddonManager.UI.exe" -ForegroundColor White

if (Test-Path $compatibilityInstallerName) {
    Write-Host "  - Installer alias: $compatibilityInstallerName" -ForegroundColor White
}

$shouldPromptToRun = -not $NonInteractive -and -not $env:CI
if ($shouldPromptToRun) {
    # Option to run the built executable
    $response = Read-Host "`nDo you want to run the built executable? (y/n)"
    if ($response -eq 'y') {
        Start-Process "publish\GmodAddonManager.UI.exe"
    }
}
