<#
.SYNOPSIS
    Deep Link Engine (DLE) - Local Developer Setup Script (Windows PowerShell)

.DESCRIPTION
    Validates developer prerequisites, generates deploy/.env with secure secrets,
    and assists in launching the local development environment via Docker Compose or .NET Aspire.

.PARAMETER CheckOnly
    Only verify prerequisites and configuration without starting services.

.PARAMETER Build
    Build container images from source and start Docker Compose stack in the background.

.PARAMETER Up
    Start existing container images in the background via Docker Compose.

.PARAMETER Aspire
    Launch local developer topology via .NET Aspire AppHost instead of Docker Compose.
#>

[CmdletBinding()]
param (
    [switch]$CheckOnly,
    [switch]$Build,
    [switch]$Up,
    [switch]$Aspire
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$EnvFile = Join-Path $ScriptDir ".env"
$EnvExample = Join-Path $ScriptDir ".env.example"

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " Deep Link Engine (DLE) - Local Developer Setup" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

# 1. Check .NET SDK
Write-Host "[1/4] Checking .NET SDK..." -NoNewline
try {
    $dotnetVersion = (dotnet --version)
    if ($dotnetVersion -like "11.*") {
        Write-Host " OK (.NET $dotnetVersion)" -ForegroundColor Green
    } else {
        Write-Host " WARNING (Found .NET $dotnetVersion; .NET 11 SDK is recommended)" -ForegroundColor Yellow
    }
} catch {
    Write-Host " FAILED (.NET SDK not found in PATH)" -ForegroundColor Red
}

# 2. Check Docker
Write-Host "[2/4] Checking Docker & Docker Compose..." -NoNewline
$hasDocker = $false
try {
    $dockerVer = (docker --version)
    $composeVer = (docker compose version)
    Write-Host " OK ($dockerVer, $composeVer)" -ForegroundColor Green
    $hasDocker = $true
} catch {
    Write-Host " NOT DETECTED (Docker or Docker Compose not in PATH)" -ForegroundColor Yellow
}

# 3. Setup .env file
Write-Host "[3/4] Checking deploy/.env configuration..." -NoNewline
if (-not (Test-Path $EnvFile)) {
    Write-Host " Generating fresh .env..." -ForegroundColor Yellow
    if (Test-Path $EnvExample) {
        $content = Get-Content -Raw $EnvExample

        # Generate secrets (hex: safe in any .env parser and in the replacements below)
        # Works on Windows PowerShell 5.1 (.NET Framework) as well as PowerShell 7.
        function New-HexSecret([int]$bytes) {
            $buffer = New-Object byte[] $bytes
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
            return -join ($buffer | ForEach-Object { $_.ToString('x2') })
        }
        $masterSecret = New-HexSecret 32
        $edgeSecret = New-HexSecret 32
        $pgPassword = New-HexSecret 18
        $valkeyPassword = New-HexSecret 18

        # Replace required secrets & defaults (anchored to the start of a line)
        $content = $content -replace '(?m)^POSTGRES_PASSWORD=.*$', "POSTGRES_PASSWORD=$pgPassword"
        $content = $content -replace '(?m)^VALKEY_PASSWORD=.*$', "VALKEY_PASSWORD=$valkeyPassword"
        $content = $content -replace '(?m)^DLE_MASTER_SECRET=.*$', "DLE_MASTER_SECRET=$masterSecret"
        $content = $content -replace '(?m)^DLE_EDGE_SECRET=.*$', "DLE_EDGE_SECRET=$edgeSecret"
        $content = $content -replace '(?m)^DLE_DOMAIN=.*$', 'DLE_DOMAIN=localhost'
        $content = $content -replace '(?m)^DLE_TLS=.*$', 'DLE_TLS=internal'

        Set-Content -Path $EnvFile -Value $content -NoNewline
        Write-Host "      Created $EnvFile with generated POSTGRES_PASSWORD, VALKEY_PASSWORD, DLE_MASTER_SECRET and DLE_EDGE_SECRET." -ForegroundColor Green
    } else {
        Write-Host " FAILED ($EnvExample template not found)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host " OK ($EnvFile exists)" -ForegroundColor Green
}

# 4. Service launch / instructions
Write-Host "[4/4] Environment Ready!" -ForegroundColor Cyan
Write-Host ""
Write-Host "Endpoints for Local Development (using docker-compose.override.yml):" -ForegroundColor White
Write-Host "  * Edge (Data Plane, Redirects, QR): http://localhost:8080" -ForegroundColor Gray
Write-Host "  * Control API & OpenAPI (Scalar):   http://localhost:8081/scalar/v1" -ForegroundColor Gray
Write-Host "  * Administration SPA:               http://localhost:8081/admin/" -ForegroundColor Gray
Write-Host "  * Edge Health Check:                http://localhost:8080/healthz" -ForegroundColor Gray
Write-Host "  * Control Health Check:             http://localhost:8081/healthz" -ForegroundColor Gray
Write-Host ""
Write-Host "Automatic Bootstrap:" -ForegroundColor Yellow
Write-Host "  When the database is empty on first start, Dle.Control automatically provisions" -ForegroundColor Gray
Write-Host "  an initial tenant ('default') and an owner API key, printed once. Find it with:" -ForegroundColor Gray
Write-Host "    docker compose logs dle-control | Select-String -Context 0,4 'DLE BOOTSTRAP OWNER KEY'" -ForegroundColor Cyan
Write-Host ""

if ($CheckOnly) {
    Write-Host "Check completed successfully. Exiting (-CheckOnly)." -ForegroundColor Green
    return
}

if ($Aspire) {
    Write-Host "Starting local topology via .NET Aspire AppHost..." -ForegroundColor Cyan
    Push-Location (Join-Path $RootDir "deploy\aspire\Dle.AppHost")
    try {
        dotnet run
    } finally {
        Pop-Location
    }
    return
}

if ($Build -or $Up) {
    if (-not $hasDocker) {
        Write-Host "ERROR: Cannot start Docker stack because Docker is not available in PATH." -ForegroundColor Red
        exit 1
    }
    Push-Location $ScriptDir
    try {
        if ($Build) {
            Write-Host "Building images and launching compose stack..." -ForegroundColor Cyan
            docker compose up -d --build
        } else {
            Write-Host "Starting compose stack..." -ForegroundColor Cyan
            docker compose up -d
        }
        Write-Host "Stack started! Check status with: docker compose ps -a" -ForegroundColor Green
    } finally {
        Pop-Location
    }
} else {
    Write-Host "To start the local stack now, run:" -ForegroundColor White
    Write-Host "  cd deploy; docker compose up -d --build" -ForegroundColor Cyan
    Write-Host "or with this script:" -ForegroundColor White
    Write-Host "  .\setup-dev.ps1 -Build" -ForegroundColor Cyan
}
