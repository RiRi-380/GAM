# Build script for GAM. Mirrors the GitHub Actions release path.
param(
    [string]$Version = "v1.0.5",
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

Write-Host "Building GAM $Version..." -ForegroundColor Green
Write-Host "Resolved release version: $releaseVersion" -ForegroundColor Cyan
Write-Host "Resolved assembly/file version: $assemblyVersion" -ForegroundColor Cyan

# Clean previous builds.
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
foreach ($path in @("publish", "dist")) {
    if (Test-Path $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

foreach ($artifact in @(
    "GAM-Portable-$releaseVersion.zip",
    "GAM-Setup-$releaseVersion.exe",
    "GAM-Setup.exe",
    "GAM-UpdateManifest-$releaseVersion.json",
    "GAM-UpdateManifest-$releaseVersion.sig"
)) {
    if (Test-Path $artifact) {
        Remove-Item -LiteralPath $artifact -Force
    }
}

# Restore dependencies.
Write-Host "Restoring dependencies..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Build self-contained executable.
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
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Copy steam_api64.dll to publish folder.
Write-Host "Copying steam_api64.dll..." -ForegroundColor Yellow
$steamDllSource = "src/GmodAddonManager.UI/bin/Release/net8.0/win-x64/steam_api64.dll"
if (-not (Test-Path $steamDllSource)) {
    $steamDllSource = "steam_api64.dll"
}

if (Test-Path $steamDllSource) {
    Copy-Item $steamDllSource -Destination "publish/steam_api64.dll" -Force
    Write-Host "steam_api64.dll copied successfully" -ForegroundColor Green
} else {
    Write-Host "WARNING: steam_api64.dll not found. Workshop features will not work." -ForegroundColor Yellow
}

# Do not ship debug symbol files in public release artifacts.
Get-ChildItem -Path "publish" -Filter "*.pdb" -File -Recurse | Remove-Item -Force

# Create portable ZIP.
Write-Host "Creating portable ZIP..." -ForegroundColor Yellow
$zipPath = "GAM-Portable-$releaseVersion.zip"
Compress-Archive -Path publish/* -DestinationPath $zipPath

# Build installer if Inno Setup is installed.
$innoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $innoSetupPath) {
    Write-Host "Building installer..." -ForegroundColor Yellow

    $vcRedistPath = Join-Path $PSScriptRoot "redist\VC_redist.x64.exe"
    if (-not (Test-Path $vcRedistPath)) {
        Write-Host "WARNING: Visual C++ Redistributable not found." -ForegroundColor Yellow
        Write-Host "  Expected at: $vcRedistPath" -ForegroundColor Yellow
        Write-Host "  Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe" -ForegroundColor Yellow
        Write-Host "  The installer will be built without VC++ Redistributable." -ForegroundColor Yellow
    } else {
        Write-Host "Visual C++ Redistributable found." -ForegroundColor Green
    }

    & $innoSetupPath installer/setup.iss /DMyAppVersion=$releaseVersion
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $installerSource = "dist\GAM-Setup-$releaseVersion.exe"
    if (Test-Path $installerSource) {
        Copy-Item $installerSource "GAM-Setup-$releaseVersion.exe" -Force
        Copy-Item $installerSource "GAM-Setup.exe" -Force
        Write-Host "Installer created: GAM-Setup-$releaseVersion.exe" -ForegroundColor Green
        Write-Host "Compatibility installer alias created: GAM-Setup.exe" -ForegroundColor Green
    }
} else {
    Write-Host "Inno Setup not found. Skipping installer build." -ForegroundColor Yellow
    Write-Host "Install from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
}

Write-Host "`nBuild completed!" -ForegroundColor Green
Write-Host "Outputs:" -ForegroundColor Cyan
Write-Host "  - Portable: $zipPath" -ForegroundColor White
Write-Host "  - Executable: publish\GmodAddonManager.UI.exe" -ForegroundColor White
if (Test-Path "GAM-Setup-$releaseVersion.exe") {
    Write-Host "  - Installer: GAM-Setup-$releaseVersion.exe" -ForegroundColor White
    Write-Host "  - Installer alias: GAM-Setup.exe" -ForegroundColor White
}

if (-not $NonInteractive -and -not $env:CI) {
    $response = Read-Host "`nDo you want to run the built executable? (y/n)"
    if ($response -eq 'y') {
        Start-Process "publish\GmodAddonManager.UI.exe"
    }
}
