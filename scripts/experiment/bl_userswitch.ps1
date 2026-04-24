param(
    [Parameter(Mandatory = $true)][string]$AddonnomountPath,
    [string]$DisabledIdsFile = '',
    [string[]]$DisabledIds = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Build-Addonnomount([string[]]$ids) {
    $lines = @('"addonnomount"', '{')
    $idx = 1
    foreach ($id in $ids) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        $lines += "`t`"$idx`"`t`t`"$id`""
        $idx += 1
    }
    $lines += '}'
    return ($lines -join "`n") + "`n"
}

if (-not [string]::IsNullOrWhiteSpace($DisabledIdsFile)) {
    if (-not (Test-Path $DisabledIdsFile)) { throw "DisabledIdsFile not found: $DisabledIdsFile" }
    $DisabledIds = Get-Content -Path $DisabledIdsFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$content = Build-Addonnomount $DisabledIds
New-Item -ItemType Directory -Force -Path (Split-Path $AddonnomountPath -Parent) | Out-Null
Set-Content -Path $AddonnomountPath -Value $content -Encoding UTF8
