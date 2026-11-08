# Quick debug build script for GAM
$ErrorActionPreference = "Stop"
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed (exit $LASTEXITCODE)."
    }

    dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit $LASTEXITCODE)."
    }

    $exePath = Join-Path $PSScriptRoot "src\GmodAddonManager.UI\bin\Debug\net10.0\GmodAddonManager.UI.exe"
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "The debug executable was not found at $exePath."
    }

    Start-Process -FilePath $exePath
}
finally {
    Pop-Location
}
