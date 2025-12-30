# WisperFlow Python Dependencies Installer
# This script installs the Python packages needed for faster-whisper

$ErrorActionPreference = "SilentlyContinue"

Write-Host "================================" -ForegroundColor Cyan
Write-Host "  WisperFlow Python Setup" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Find Python
$python = $null
$pythonVersions = @("python", "py -3.12", "py -3.11", "py -3.10", "py -3.9", "py")

foreach ($pyCmd in $pythonVersions) {
    try {
        $version = & $pyCmd.Split()[0] $pyCmd.Split()[1..99] --version 2>&1
        if ($LASTEXITCODE -eq 0 -and $version -match "Python 3\.(9|10|11|12)") {
            $python = $pyCmd
            Write-Host "Found Python: $version" -ForegroundColor Green
            break
        }
    } catch {}
}

if (-not $python) {
    Write-Host "Python 3.9-3.12 not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Python from: https://www.python.org/downloads/" -ForegroundColor Yellow
    Write-Host "Make sure to check 'Add Python to PATH' during installation." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host ""
Write-Host "Installing faster-whisper and dependencies..." -ForegroundColor Yellow
Write-Host ""

# Get the script directory (where requirements.txt should be)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$requirementsFile = Join-Path $scriptDir "requirements.txt"

if (Test-Path $requirementsFile) {
    # Install from requirements.txt
    if ($python -like "py *") {
        $pyArgs = $python.Split()[1..99] + @("-m", "pip", "install", "-r", $requirementsFile, "--quiet")
        & py @pyArgs
    } else {
        & $python -m pip install -r $requirementsFile --quiet
    }
} else {
    # Install packages directly
    $packages = @(
        "faster-whisper",
        "soundfile",
        "librosa"
    )
    
    foreach ($pkg in $packages) {
        Write-Host "Installing $pkg..." -ForegroundColor Gray
        if ($python -like "py *") {
            $pyArgs = $python.Split()[1..99] + @("-m", "pip", "install", $pkg, "--quiet")
            & py @pyArgs
        } else {
            & $python -m pip install $pkg --quiet
        }
    }
}

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "================================" -ForegroundColor Cyan
    Write-Host "  Installation Complete!" -ForegroundColor Green
    Write-Host "================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "faster-whisper is now available for use in WisperFlow." -ForegroundColor White
} else {
    Write-Host ""
    Write-Host "Installation may have had errors." -ForegroundColor Yellow
    Write-Host "You can try running this manually:" -ForegroundColor Yellow
    Write-Host "  pip install faster-whisper soundfile librosa" -ForegroundColor White
}

Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

