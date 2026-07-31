# Build script for GAM - Same as GitHub Actions
param(
    [string]$Version = "v2.0.0",
    [ValidateSet("prompt", "run", "skip")]
    [string]$RunMode = "prompt"
)

Write-Host "Building GAM $Version..." -ForegroundColor Green

$normalizedVersion = $Version
if ($normalizedVersion.StartsWith("v")) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}
$fileVersion = "$normalizedVersion.0"
$informationalVersion = "v$normalizedVersion+local"
$versionProps = @(
    "-p:Version=$normalizedVersion",
    "-p:FileVersion=$fileVersion",
    "-p:AssemblyVersion=$fileVersion",
    "-p:InformationalVersion=$informationalVersion",
    "-p:IncludeSourceRevisionInInformationalVersion=false"
)

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
    @versionProps `
    -o publish

# Include license in portable/installer outputs
Copy-Item "LICENSE" -Destination "publish\\LICENSE" -Force

# Create portable ZIP
Write-Host "Creating portable ZIP..." -ForegroundColor Yellow
$zipPath = "GAM-Portable-$normalizedVersion.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}
Compress-Archive -Path publish/* -DestinationPath $zipPath

# Build installer (if Inno Setup is installed)
$innoSetupCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)

$innoSetupPath = $innoSetupCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $innoSetupPath) {
    $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($isccCommand) {
        $innoSetupPath = $isccCommand.Source
    }
}

if ($innoSetupPath) {
    Write-Host "Building installer..." -ForegroundColor Yellow
    
    # Check for VC++ Redistributable
    $vcRedistPath = Join-Path $PSScriptRoot "redist\VC_redist.x64.exe"
    if (-not (Test-Path $vcRedistPath)) {
        throw "Visual C++ Redistributable is required for installer builds. Expected: $vcRedistPath"
    }

    $vcSignature = Get-AuthenticodeSignature -LiteralPath $vcRedistPath
    if ($vcSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $vcSignature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
        throw "VC++ Redistributable signature validation failed: $($vcSignature.Status)"
    }
    Write-Host "✓ Visual C++ Redistributable signature is valid" -ForegroundColor Green
    
    & $innoSetupPath installer/setup.iss /DMyAppVersion=$normalizedVersion
    
    # Move installer to root directory for easy access
    $installerSource = "dist\GAM-Setup-$normalizedVersion.exe"
    if (Test-Path $installerSource) {
        Move-Item $installerSource . -Force
        Write-Host "Installer created: GAM-Setup-$normalizedVersion.exe" -ForegroundColor Green
    }
} else {
    Write-Host "Inno Setup not found. Skipping installer build." -ForegroundColor Red
    Write-Host "Install from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
}

Write-Host "`nBuild completed!" -ForegroundColor Green
Write-Host "Outputs:" -ForegroundColor Cyan
Write-Host "  - Portable: $zipPath" -ForegroundColor White
Write-Host "  - Executable: publish\GmodAddonManager.UI.exe" -ForegroundColor White

# Option to run the built executable
switch ($RunMode) {
    "run" {
        Start-Process "publish\GmodAddonManager.UI.exe"
    }
    "skip" {
        # no-op
    }
    default {
        $response = Read-Host "`nDo you want to run the built executable? (y/n)"
        if ($response -eq 'y') {
            Start-Process "publish\GmodAddonManager.UI.exe"
        }
    }
}
