#!/usr/bin/env pwsh
# Build script for repos.fs (F# version)

Write-Host "Building GitHub Repository Inventory Tool (F# version)..." -ForegroundColor Green

# Change to the script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptDir

try {
    # Check if .NET SDK is installed
    $dotnetVersion = dotnet --version 2>$null
    if (-not $dotnetVersion) {
        Write-Host "Error: .NET SDK is not installed or not in PATH" -ForegroundColor Red
        Write-Host "Please install .NET SDK from https://dotnet.microsoft.com/download" -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Using .NET SDK $dotnetVersion" -ForegroundColor Cyan
    
    # Restore dependencies
    Write-Host "Restoring dependencies..." -ForegroundColor Cyan
    dotnet restore repos.fsproj
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error restoring dependencies" -ForegroundColor Red
        exit 1
    }
    
    # Build for Windows
    Write-Host "Building for Windows (x64)..." -ForegroundColor Cyan
    dotnet build repos.fsproj -c Release
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed" -ForegroundColor Red
        exit 1
    }
    
    # Publish self-contained executable
    Write-Host "Publishing self-contained executable..." -ForegroundColor Cyan
    dotnet publish repos.fsproj -c Release -r win-x64 --self-contained true -o ./publish
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Successfully built repos.exe" -ForegroundColor Green
        
        # Copy to current directory for convenience
        if (Test-Path ".\publish\repos.exe") {
            Copy-Item ".\publish\repos.exe" -Destination ".\repos.exe" -Force
            Write-Host "Copied to repos.exe" -ForegroundColor Green
        }
    } else {
        Write-Host "Publish failed" -ForegroundColor Red
        exit 1
    }
    
    # Optionally build for Linux
    $buildLinux = Read-Host "Build for Linux as well? (y/N)"
    if ($buildLinux -eq 'y' -or $buildLinux -eq 'Y') {
        Write-Host "Publishing for Linux (x64)..." -ForegroundColor Cyan
        dotnet publish repos.fsproj -c Release -r linux-x64 --self-contained false -o ./publish-linux
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Successfully built repos (Linux)" -ForegroundColor Green
        } else {
            Write-Host "Linux build failed" -ForegroundColor Red
        }
    }
    
    # Display file info
    Write-Host "`nBuild artifacts:" -ForegroundColor Cyan
    if (Test-Path ".\repos.exe") {
        $fileInfo = Get-Item ".\repos.exe"
        Write-Host "  repos.exe - $([math]::Round($fileInfo.Length / 1MB, 2)) MB" -ForegroundColor White
    }
    
    Write-Host "`nBuild complete! Run with: .\repos.exe --help" -ForegroundColor Green
    
} finally {
    Pop-Location
}
