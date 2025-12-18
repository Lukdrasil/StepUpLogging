# Quick script to run all k6 performance tests and compare results (PowerShell)

Write-Host "======================================"
Write-Host "k6 Performance Test Suite" -ForegroundColor Cyan
Write-Host "======================================"
Write-Host ""

# Check if k6 is installed
try {
    $k6Version = k6 version 2>$null
    Write-Host "✓ k6 installed: $k6Version" -ForegroundColor Green
} catch {
    Write-Host "✗ k6 is not installed. Please install from https://k6.io/docs/get-started/installation/" -ForegroundColor Red
    exit 1
}

# Check if both APIs are running
Write-Host "⏳ Checking if APIs are running..." -ForegroundColor Yellow
Write-Host ""

$simpleApiRunning = $false
$stepupApiRunning = $false
$simpleApiUrl = ""
$stepupApiUrl = ""

# Try multiple URL combinations for SimpleApi
$simpleApiUrls = @(
    "http://localhost:7152/health",
    "https://localhost:7152/health",
    "http://localhost:5014/health"
)

foreach ($url in $simpleApiUrls) {
    try {
        $response = Invoke-WebRequest -Uri $url -SkipCertificateCheck -TimeoutSec 2 -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            $simpleApiRunning = $true
            $simpleApiUrl = $url -replace "/health", ""
            Write-Host "✓ SimpleApi is running at: $simpleApiUrl" -ForegroundColor Green
            break
        }
    } catch {
        # Continue to next URL
    }
}

if (-not $simpleApiRunning) {
    Write-Host "⚠ SimpleApi not found on any expected port" -ForegroundColor Yellow
}

# Try multiple URL combinations for StepUpApi
$stepupApiUrls = @(
    "http://localhost:7189/health",
    "https://localhost:7189/health",
    "http://localhost:5079/health"
)

foreach ($url in $stepupApiUrls) {
    try {
        $response = Invoke-WebRequest -Uri $url -SkipCertificateCheck -TimeoutSec 2 -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            $stepupApiRunning = $true
            $stepupApiUrl = $url -replace "/health", ""
            Write-Host "✓ StepUpApi is running at: $stepupApiUrl" -ForegroundColor Green
            break
        }
    } catch {
        # Continue to next URL
    }
}

if (-not $stepupApiRunning) {
    Write-Host "⚠ StepUpApi not found on any expected port" -ForegroundColor Yellow
}

if (-not $simpleApiRunning -or -not $stepupApiRunning) {
    Write-Host ""
    Write-Host "⚠ Warning: Not all APIs are running. Tests may fail." -ForegroundColor Yellow
    Write-Host ""
    Read-Host "Press Enter to continue anyway"
}

# Export API URLs pro k6 testy
$env:SIMPLE_API_BASE = $simpleApiUrl
$env:STEPUP_API_BASE = $stepupApiUrl

Write-Host ""
Write-Host "Running tests..." -ForegroundColor Cyan
Write-Host "  SimpleApi:  $simpleApiUrl"
Write-Host "  StepUpApi:  $stepupApiUrl"
Write-Host ""

$logDir = $env:TEMP
$simpleBaselineLog = Join-Path $logDir "simple_baseline.log"
$stepupActiveLog = Join-Path $logDir "stepup_active.log"

# Test 1: SimpleApi Baseline (standard Serilog without step-up)
Write-Host "1️⃣  Testing SimpleApi Baseline (standard Serilog)..." -ForegroundColor Cyan
k6 run --env TEST_TYPE=baseline_simple ./performance_test.js > $simpleBaselineLog 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ SimpleApi Baseline test passed" -ForegroundColor Green
} else {
    Write-Host "✗ SimpleApi Baseline test failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
}

Write-Host ""

# Test 2: StepUpApi with Active Step-up (triggered by errors)
Write-Host "2️⃣  Testing StepUpApi with Active Step-up..." -ForegroundColor Cyan
k6 run --env TEST_TYPE=stepup_active ./performance_test.js > $stepupActiveLog 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ StepUpApi Active Step-up test passed" -ForegroundColor Green
} else {
    Write-Host "✗ StepUpApi Active Step-up test failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
}

Write-Host ""
Write-Host "======================================"
Write-Host "Test Results Summary" -ForegroundColor Cyan
Write-Host "======================================"
Write-Host ""
Write-Host "📊 Detailed logs:" -ForegroundColor Yellow
Write-Host "  SimpleApi Baseline:    $simpleBaselineLog"
Write-Host "  StepUpApi Active:      $stepupActiveLog"
Write-Host ""
Write-Host "Compare the p95/p99 latencies to calculate overhead:" -ForegroundColor Yellow
Write-Host "  Overhead % = ((Active - Baseline) / Baseline) * 100"
Write-Host ""

# Optional: Extract and display key metrics from logs
Write-Host "📈 Attempting to extract key metrics..." -ForegroundColor Yellow
Write-Host ""

function Extract-Metrics {
    param(
        [string]$LogFile,
        [string]$Label
    )
    
    if (Test-Path $LogFile) {
        $content = Get-Content $LogFile -Raw
        
        # Try to extract demo_logs_latency p95
        if ($content -match 'demo_logs_latency.*?p\(95\)=([0-9.]+)') {
            $p95 = $matches[1]
            Write-Host "$Label - demo_logs_latency p95: $p95" -ForegroundColor Gray
        }
    }
}

Extract-Metrics -LogFile $simpleBaselineLog -Label "SimpleApi"
Extract-Metrics -LogFile $stepupActiveLog -Label "StepUpApi (active)"

Write-Host ""
Write-Host "✓ All tests completed!" -ForegroundColor Green
Write-Host ""
Write-Host "Tips:" -ForegroundColor Yellow
Write-Host "  • Open log files to see full results: notepad $simpleBaselineLog"
Write-Host "  • Check test outputs above for threshold failures"
Write-Host "  • Run individual tests with: k6 run --env TEST_TYPE=<type> tests/k6/performance_test.js"
