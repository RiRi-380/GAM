param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$InstallerPath,

    [string]$OutputDirectory = ".",

    [string]$PrivateKeyBase64 = $env:GAM_UPDATE_SIGNING_KEY_B64
)

$ErrorActionPreference = "Stop"

$releaseVersion = $Version.Trim().TrimStart('v')
$manifestVersion = if ($Version.Trim().StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
    $Version.Trim()
} else {
    "v$releaseVersion"
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    if (Test-Path "GAM-Setup.exe") {
        $InstallerPath = "GAM-Setup.exe"
    } else {
        $InstallerPath = "GAM-Setup-$releaseVersion.exe"
    }
}

if ([string]::IsNullOrWhiteSpace($PrivateKeyBase64)) {
    throw "GAM_UPDATE_SIGNING_KEY_B64 is required to sign the update manifest."
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

if (-not (Get-Command openssl -ErrorAction SilentlyContinue)) {
    throw "OpenSSL is required to sign the update manifest."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$installer = Get-Item -LiteralPath $InstallerPath
$manifestPath = Join-Path $OutputDirectory "GAM-UpdateManifest-$releaseVersion.json"
$signaturePath = Join-Path $OutputDirectory "GAM-UpdateManifest-$releaseVersion.sig"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer.FullName).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    version = $manifestVersion
    installerAssetName = $installer.Name
    installerSha256 = $hash
    installerSize = $installer.Length
}

$json = $manifest | ConvertTo-Json -Compress
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $OutputDirectory).Path + [System.IO.Path]::DirectorySeparatorChar + (Split-Path $manifestPath -Leaf), $json, $utf8NoBom)

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gam-update-sign-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    $privateKeyPath = Join-Path $tempDir "update_private.pem"
    $trimmedPrivateKey = $PrivateKeyBase64.Trim()
    if ($trimmedPrivateKey.StartsWith("-----BEGIN", [StringComparison]::Ordinal))
    {
        $pemText = $trimmedPrivateKey.Replace("\n", [Environment]::NewLine)
        [System.IO.File]::WriteAllText($privateKeyPath, $pemText, $utf8NoBom)
    }
    else
    {
        [System.IO.File]::WriteAllBytes($privateKeyPath, [Convert]::FromBase64String($trimmedPrivateKey))
    }

    & openssl dgst -sha256 -sign $privateKeyPath -out $signaturePath $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw "OpenSSL signing failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force
    }
}

Write-Host "Update manifest signed: $manifestPath"
Write-Host "Update manifest signature: $signaturePath"
