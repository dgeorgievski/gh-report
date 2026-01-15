#!/usr/bin/env pwsh
# Test script for the refactored repos.fs

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Testing Refactored GitHub Inventory Tool" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

$dllPath = if (Test-Path "bin\Release\net10.0\repos.dll") { "bin\Release\net10.0\repos.dll" } 
           elseif (Test-Path "bin\Release\net9.0\repos.dll") { "bin\Release\net9.0\repos.dll" }
           else { $null }

if (-not $dllPath) {
    Write-Host "Error: Program not built. Run: dotnet build repos.fsproj -c Release" -ForegroundColor Red
    exit 1
}

if (-not $env:GITHUB_TOKEN) {
    Write-Host "Error: GITHUB_TOKEN environment variable not set" -ForegroundColor Red
    Write-Host "Please set it with: `$env:GITHUB_TOKEN = 'your-token-here'" -ForegroundColor Yellow
    exit 1
}

if (-not $env:GITHUB_SERVER_BASE) {
    Write-Host "Warning: GITHUB_SERVER_BASE not set. Using default (api.github.com)" -ForegroundColor Yellow
}

Write-Host "✓ Prerequisites OK`n" -ForegroundColor Green

# Show new features
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "NEW FEATURES IN REFACTORED VERSION:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "1. ✨ New Collaborators Column" -ForegroundColor Green
Write-Host "   - Combines GitHub Teams + Direct Collaborators" -ForegroundColor Gray
Write-Host "   - Teams: 'Team Name[permission]'" -ForegroundColor Gray
Write-Host "   - Users: 'FirstName LastName[role_name]'" -ForegroundColor Gray
Write-Host ""
Write-Host "2. 🗑️  Removed Columns" -ForegroundColor Green
Write-Host "   - Archived (removed)" -ForegroundColor Gray
Write-Host "   - Writer (removed)" -ForegroundColor Gray
Write-Host "   - Maintainer (removed)" -ForegroundColor Gray
Write-Host "   - Admin (removed)" -ForegroundColor Gray
Write-Host ""
Write-Host "3. 📁 Multi-File Output" -ForegroundColor Green
Write-Host "   - repos-30d.csv    (Last Accessed ≤ 30 days)" -ForegroundColor Gray
Write-Host "   - repos-60d.csv    (Last Accessed > 30 and ≤ 60 days)" -ForegroundColor Gray
Write-Host "   - repos-120d.csv   (Last Accessed > 60 and ≤ 120 days)" -ForegroundColor Gray
Write-Host "   - repos-gt-120d.csv (Last Accessed > 120 days)" -ForegroundColor Gray
Write-Host ""
Write-Host "4. ⚡ Performance Improvements" -ForegroundColor Green
Write-Host "   - Parallel API calls for teams + collaborators" -ForegroundColor Gray
Write-Host "   - Parallel fetch of languages, collaborators, commits" -ForegroundColor Gray
Write-Host ""

# Test run (limited to just show it works)
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "RUNNING TEST (Limited to 2 repos)" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Command: dotnet $dllPath --format csv --output test-output.csv --count 2`n" -ForegroundColor Yellow

dotnet $dllPath --format csv --output test-output.csv --count 2

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "OUTPUT FILES GENERATED:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$outputFiles = @("test-output-30d.csv", "test-output-60d.csv", "test-output-120d.csv", "test-output-gt-120d.csv")

foreach ($file in $outputFiles) {
    if (Test-Path $file) {
        $lineCount = (Get-Content $file).Count
        $size = (Get-Item $file).Length
        Write-Host "✓ $file" -ForegroundColor Green
        Write-Host "  Lines: $lineCount | Size: $size bytes" -ForegroundColor Gray
        Write-Host "  Preview:" -ForegroundColor Gray
        Get-Content $file -Head 3 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        Write-Host "✗ $file (not created - no repos in this time bucket)" -ForegroundColor Yellow
    }
    Write-Host ""
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "✓ TEST COMPLETE!" -ForegroundColor Green
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "To run full inventory:" -ForegroundColor Yellow
Write-Host "  dotnet $dllPath --format csv --output repos.csv`n" -ForegroundColor White
