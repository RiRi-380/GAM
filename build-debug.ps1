# Quick debug build script for GAM
Write-Host "Building GAM (Debug)..." -ForegroundColor Green

# Restore dependencies
Write-Host "Restoring dependencies..." -ForegroundColor Yellow
dotnet restore

# Build debug version
Write-Host "Building debug version..." -ForegroundColor Yellow
dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Debug

$exePath = "src\GmodAddonManager.UI\bin\Debug\net10.0\GmodAddonManager.UI.exe"

if (Test-Path $exePath) {
    Write-Host "`nBuild completed!" -ForegroundColor Green
    Write-Host "Starting application..." -ForegroundColor Cyan
    Start-Process $exePath
} else {
    Write-Host "Build failed or exe not found at: $exePath" -ForegroundColor Red
}
