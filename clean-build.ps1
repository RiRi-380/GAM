# Clean build script for GAM
Write-Host "Starting clean build..." -ForegroundColor Green

# Clean solution
Write-Host "Cleaning solution..." -ForegroundColor Yellow
dotnet clean

# Remove bin and obj directories
Write-Host "Removing bin and obj directories..." -ForegroundColor Yellow
Get-ChildItem -Path . -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Restore packages
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore

# Build in Release mode
Write-Host "Building in Release mode..." -ForegroundColor Yellow
dotnet build -c Release

# Publish (optional - uncomment if needed)
# Write-Host "Publishing application..." -ForegroundColor Yellow
# dotnet publish -c Release -o ./publish

Write-Host "Clean build completed!" -ForegroundColor Green
Write-Host "The build output is in: src\GmodAddonManager.UI\bin\Release\net6.0\" -ForegroundColor Cyan