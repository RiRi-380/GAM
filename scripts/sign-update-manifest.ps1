[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [string]$OutputDirectory = ".",

    [string]$PrivateKeyBase64 = $env:GAM_UPDATE_SIGNING_KEY_B64,

    [string]$PrivateKeyPem = $env:GAM_UPDATE_SIGNING_KEY_PEM,

    [string]$ExpectedPublicKeySpkiBase64 =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyGUpUR+SaWQJFSqucX45Gbl0pBn9tlaYbr3U5wDTv/GT4yvoqGHDiNYyt7el59Z1DV8V8tU5Kstc0IdGWOlv+V1dKqC+1ShSC7cj9AqegmnxG8jnDvJSpYg4S7iTc8JEV8c5t1WLBAVjswT63EBU9DsqdhO21r6GmCJZemu+8wa09EZu+IAO69SSjZrBaXW0vwaEq+Q6bsloRwvGlAKmaiUCjz8BJJv/82yLZTLpJH4lpwOYI5MrS+3/w0GQ+pK9Xq7yNH1KfO+ZfGdXDqqnOHzeBVqBj+gDr7fxDyRI5PE60Dw73u9RFh31l93dM6KtWYHwUE8mm1p2xV02bnqpshNO0DrgAnPh1jo7cBFazVNEDiBiNWsCrJ57i3fOVn57uIf8X5oE7JblNEKKDxCNknqL0mZMcR8d+KurA+u3lha9z1uussLigmWYFUmNvMUfEpAr332UYwvneCOWxDRxbMjcfuB6KRHqZZ9SpBKm2HzKwOJkIp1rf62hCe7EXYiJAgMBAAE="
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$trimmedVersion = $Version.Trim()
if ($trimmedVersion -notmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version must be an exact stable semantic version: $Version"
}

$releaseVersion = $trimmedVersion.TrimStart('v', 'V')
$manifestVersion = "v$releaseVersion"

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer not found: $InstallerPath"
}

if (-not (Get-Command openssl -CommandType Application -ErrorAction SilentlyContinue)) {
    throw "OpenSSL is required to sign the legacy update manifest."
}

if ([string]::IsNullOrWhiteSpace($PrivateKeyBase64) -and
    [string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
    throw "GAM_UPDATE_SIGNING_KEY_B64 or GAM_UPDATE_SIGNING_KEY_PEM is required."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resolvedOutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$installer = Get-Item -LiteralPath $InstallerPath
$manifestPath = Join-Path $resolvedOutputDirectory "GAM-UpdateManifest-$releaseVersion.json"
$signaturePath = Join-Path $resolvedOutputDirectory "GAM-UpdateManifest-$releaseVersion.sig"
$installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    version = $manifestVersion
    installerAssetName = $installer.Name
    installerSha256 = $installerHash
    installerSize = $installer.Length
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$manifestJson = $manifest | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

$temporaryDirectory = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    "gam-update-sign-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    $privateKeyPath = Join-Path $temporaryDirectory "update-private-key.pem"
    if (-not [string]::IsNullOrWhiteSpace($PrivateKeyBase64)) {
        $trimmedPrivateKey = $PrivateKeyBase64.Trim()
        if ($trimmedPrivateKey.StartsWith(
                "-----BEGIN",
                [StringComparison]::Ordinal)) {
            $normalizedPem = $trimmedPrivateKey.Replace(
                '\r\n', [Environment]::NewLine).Replace(
                '\n', [Environment]::NewLine)
            [System.IO.File]::WriteAllText($privateKeyPath, $normalizedPem, $utf8NoBom)
        }
        else {
            try {
                $privateKeyBytes = [Convert]::FromBase64String($trimmedPrivateKey)
            }
            catch {
                throw "GAM_UPDATE_SIGNING_KEY_B64 is neither PEM nor valid base64."
            }

            [System.IO.File]::WriteAllBytes($privateKeyPath, $privateKeyBytes)
        }
    }
    else {
        $normalizedPem = $PrivateKeyPem.Trim().Replace(
            '\r\n', [Environment]::NewLine).Replace(
            '\n', [Environment]::NewLine)
        [System.IO.File]::WriteAllText($privateKeyPath, $normalizedPem, $utf8NoBom)
    }

    & openssl dgst -sha256 -sign $privateKeyPath -out $signaturePath $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw "OpenSSL signing failed with exit code $LASTEXITCODE."
    }

    try {
        [void][Convert]::FromBase64String($ExpectedPublicKeySpkiBase64)
    }
    catch {
        throw "The expected legacy update public key is not valid base64."
    }

    $publicKeyPath = Join-Path $temporaryDirectory "legacy-update-public-key.pem"
    $publicKeyLines = [regex]::Matches(
        $ExpectedPublicKeySpkiBase64,
        '.{1,64}') | ForEach-Object Value
    $publicKeyPem = @(
        '-----BEGIN PUBLIC KEY-----'
        $publicKeyLines
        '-----END PUBLIC KEY-----'
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText($publicKeyPath, $publicKeyPem, $utf8NoBom)

    & openssl dgst -sha256 -verify $publicKeyPath -signature $signaturePath $manifestPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The signing key does not match the public key embedded in GAM v1.0.3-v1.0.5."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Host "Legacy update manifest signed and verified: $manifestPath"
Write-Host "Legacy update signature: $signaturePath"
