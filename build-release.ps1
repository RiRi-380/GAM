# Build a local GAM release using the same packaging contract as GitHub Actions.
[CmdletBinding()]
param(
    [string]$Version = "v2.0.2",
    [ValidateSet("prompt", "run", "skip")]
    [string]$RunMode = "prompt",
    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$versionPattern = '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
if ($Version -notmatch $versionPattern) {
    throw "Version must be an exact stable semantic version such as v2.0.0."
}

$normalizedVersion = $Version.TrimStart('v')
$tagVersion = "v$normalizedVersion"
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
$declaredVersion = $buildProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($declaredVersion -ne $normalizedVersion) {
    throw "Directory.Build.props declares $declaredVersion, but the requested build is $normalizedVersion."
}

$publishDirectory = Join-Path $repoRoot "publish"
$portableDirectory = Join-Path $repoRoot "publish-portable"
$distDirectory = Join-Path $repoRoot "dist"
$portableZip = Join-Path $repoRoot "GAM-Portable-$normalizedVersion.zip"
$stableInstaller = Join-Path $repoRoot "GAM-Setup.exe"
$legacyManifest = Join-Path $repoRoot "GAM-UpdateManifest-$normalizedVersion.json"
$legacySignature = Join-Path $repoRoot "GAM-UpdateManifest-$normalizedVersion.sig"
$managedManifestName = "GAM-ReleaseFiles.txt"
$portableMarkerName = ".gam-portable.json"
$solutionPath = Join-Path $repoRoot "GmodAddonManager.sln"
$uiProjectPath = Join-Path $repoRoot "src\GmodAddonManager.UI\GmodAddonManager.UI.csproj"
$assetsPath = Join-Path $repoRoot "src\GmodAddonManager.UI\obj\project.assets.json"
$globalJsonPath = Join-Path $repoRoot "global.json"
[string]$requiredSdkVersion = (Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json).sdk.version
$dotnetCommand = Get-Command $DotNetPath -ErrorAction Stop
$dotnetExecutable = $dotnetCommand.Source

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & $dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$savedErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $detectedSdkVersion = & $dotnetExecutable --version 2>&1
    $dotnetVersionExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if (($dotnetVersionExitCode -ne 0) -or ($detectedSdkVersion -ne $requiredSdkVersion)) {
    throw "The release build requires .NET SDK $requiredSdkVersion. Use -DotNetPath to select that dotnet executable."
}

$fileVersion = "$normalizedVersion.0"
$versionProps = @(
    "-p:Version=$normalizedVersion",
    "-p:FileVersion=$fileVersion",
    "-p:AssemblyVersion=$fileVersion",
    "-p:InformationalVersion=$tagVersion+local",
    "-p:IncludeSourceRevisionInInformationalVersion=false"
)

Write-Host "Building GAM $tagVersion..." -ForegroundColor Green

foreach ($directory in @($publishDirectory, $portableDirectory, $distDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
foreach ($generatedFile in @(
        $portableZip,
        $stableInstaller,
        $legacyManifest,
        $legacySignature)) {
    if (Test-Path -LiteralPath $generatedFile) {
        Remove-Item -LiteralPath $generatedFile -Force
    }
}

Write-Host "Restoring locked dependencies and auditing vulnerabilities..." -ForegroundColor Yellow
Invoke-DotNet @("restore", $solutionPath, "--locked-mode")

Write-Host "Publishing the self-contained multi-file application..." -ForegroundColor Yellow
$publishArguments = @(
    "publish",
    $uiProjectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "-p:TreatWarningsAsErrors=true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false"
) + $versionProps + @("-o", $publishDirectory)
Invoke-DotNet $publishArguments

& (Join-Path $repoRoot "scripts\prepare-release-notices.ps1") `
    -PublishDirectory $publishDirectory `
    -ProjectAssetsPath $assetsPath

if (Test-Path -LiteralPath (Join-Path $publishDirectory $portableMarkerName)) {
    throw "The installer staging directory must not contain $portableMarkerName."
}

# The installer reads the previous release's copy of this manifest and deletes
# only obsolete, explicitly managed files. It never recursively deletes {app}
# or any file under the user's AppData directory.
$managedFiles = @(
    Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($publishDirectory.TrimEnd('\').Length + 1).Replace('/', '\')
        }
    $managedManifestName
) | Sort-Object -Unique
[System.IO.File]::WriteAllLines(
    (Join-Path $publishDirectory $managedManifestName),
    [string[]]$managedFiles,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Creating portable staging directory and marker..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $portableDirectory -Recurse -Force
[System.IO.File]::WriteAllText(
    (Join-Path $portableDirectory $portableMarkerName),
    '{"formatVersion":1,"distribution":"portable"}',
    [System.Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $portableDirectory,
    $portableZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$innoSetupCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$innoSetupPath = $innoSetupCandidates |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    Select-Object -First 1
if (-not $innoSetupPath) {
    $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($isccCommand) {
        $innoSetupPath = $isccCommand.Source
    }
}

if ($innoSetupPath) {
    Write-Host "Building the upgrade-compatible installer..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null
    & $innoSetupPath (Join-Path $repoRoot "installer\setup.iss") "/DMyAppVersion=$normalizedVersion"
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    $versionedInstaller = Join-Path $distDirectory "GAM-Setup-$normalizedVersion.exe"
    Copy-Item -LiteralPath $versionedInstaller -Destination $stableInstaller -Force
    $versionedInstallerHash = (Get-FileHash -LiteralPath $versionedInstaller -Algorithm SHA256).Hash
    $stableInstallerHash = (Get-FileHash -LiteralPath $stableInstaller -Algorithm SHA256).Hash
    if ($versionedInstallerHash -ne $stableInstallerHash) {
        throw "The stable and versioned installer files are not byte-identical."
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GAM_UPDATE_SIGNING_KEY_B64) -or
        -not [string]::IsNullOrWhiteSpace($env:GAM_UPDATE_SIGNING_KEY_PEM)) {
        & (Join-Path $repoRoot "scripts\sign-update-manifest.ps1") `
            -Version $tagVersion `
            -InstallerPath $stableInstaller `
            -OutputDirectory $repoRoot
    }
    else {
        Write-Warning (
            "Legacy v1.0.3-v1.0.5 signature assets were not generated because " +
            "no update signing key is configured. The GitHub release workflow " +
            "generates and verifies them with repository secrets.")
    }
} else {
    Write-Warning "Inno Setup was not found; installer verification was skipped."
}

Write-Host "`nBuild completed." -ForegroundColor Green
Write-Host "Portable: $portableZip" -ForegroundColor Cyan
Write-Host "Installer staging: $publishDirectory" -ForegroundColor Cyan
if (Test-Path -LiteralPath (Join-Path $distDirectory "GAM-Setup-$normalizedVersion.exe")) {
    Write-Host "Installer: $(Join-Path $distDirectory "GAM-Setup-$normalizedVersion.exe")" -ForegroundColor Cyan
    Write-Host "Stable v1 updater alias: $stableInstaller" -ForegroundColor Cyan
    if ((Test-Path -LiteralPath $legacyManifest) -and
        (Test-Path -LiteralPath $legacySignature)) {
        Write-Host "Signed v1 update metadata: $legacyManifest" -ForegroundColor Cyan
        Write-Host "Signed v1 update signature: $legacySignature" -ForegroundColor Cyan
    }
}

$portableExecutable = Join-Path $portableDirectory "GmodAddonManager.UI.exe"
switch ($RunMode) {
    "run" { Start-Process -FilePath $portableExecutable }
    "skip" { }
    default {
        $response = Read-Host "Do you want to run the portable build? (y/n)"
        if ($response -eq 'y') {
            Start-Process -FilePath $portableExecutable
        }
    }
}
