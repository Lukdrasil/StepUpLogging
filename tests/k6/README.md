# k6 Performance Tests

Performance comparison tests between **SimpleApi** (standard Serilog logging) and **StepUpApi** (with step-up logging capability).

## Purpose

Measure and compare:
- **SimpleApi Baseline**: Standard Serilog logging without step-up capability
- **StepUpApi Active**: Step-up logging that automatically activates on error logs (capturing request bodies, applying redaction, etc.)

> **Note**: StepUpApi automatically triggers step-up logging when Error-level logs occur (via `StepUpTriggerSink`), so a "baseline inactive" test is not meaningful.

## Prerequisites

1. **k6 CLI installed** – Download from [k6.io](https://k6.io/docs/get-started/installation/)

2. **Both APIs running**:
   ```powershell
   # Terminal 1: Start SimpleApi (port 5001)
   dotnet run --project example/ExampleApp/SimpleApi/SimpleApi.csproj

   # Terminal 2: Start StepUpApi (port 5002)
   dotnet run --project example/ExampleApp/StepUpApi/StepUpApi.csproj
   ```

3. **Verify endpoints are accessible**:
   - SimpleApi: `http://localhost:5001/demo/logs`
   - StepUpApi: `http://localhost:5002/demo/logs`

## Running Tests

### Option 1: Manual Run (All Platforms)
```bash
# Test each scenario individually
k6 run --env TEST_TYPE=baseline_simple tests/k6/performance_test.js
k6 run --env TEST_TYPE=stepup_active tests/k6/performance_test.js
```

### Option 2: Automated Run (Linux/macOS)
```bash
bash tests/k6/run_all_tests.sh
```

### Option 3: Automated Run (Windows - Batch)
```cmd
tests\k6\run_all_tests.bat
```

### Option 4: Automated Run (Windows - PowerShell) ⭐ Recommended
```powershell
# PowerShell
.\tests\k6\run_all_tests.ps1

# Or if running from PowerShell ISE or with execution policy:
powershell -ExecutionPolicy Bypass -File .\tests\k6\run_all_tests.ps1
```

## Test Scenarios

### 1. SimpleApi Baseline (Standard Serilog)
```bash
k6 run --env TEST_TYPE=baseline_simple tests/k6/performance_test.js
```

**What it measures:**
- Standard Serilog request logging (no step-up capability)
- Endpoints: `/weatherforecast`, `/demo/logs`, `/demo/echo`, `/stepup/status`
- Load: Ramps up to 50 VUs over 30s, holds for 2 min, ramps down
- Represents typical production logging without dynamic level switching

### 2. StepUpApi with Active Step-Up
```bash
k6 run --env TEST_TYPE=stepup_active tests/k6/performance_test.js
```

**What it measures:**
- StepUpApi with automatic step-up triggering on error logs
- Error logs in `/demo/logs` endpoint automatically activate step-up via `StepUpTriggerSink`
- Request body capture is enabled (POST/PUT/PATCH)
- Redaction patterns are applied
- Higher verbosity level (Information logs are captured)
- Shows real-world behavior: step-up activates automatically when errors occur

## Interpreting Results

### Key Metrics

| Metric | What It Means |
|--------|--------------|
| `demo_logs_latency` (p95) | Response time for GET /demo/logs (95th percentile) |
| `demo_echo_latency` (p95) | Response time for POST /demo/echo (95th percentile) |
| `weatherforecast_latency` (p95) | Response time for GET /weatherforecast (95th percentile) |
| `http_req_duration` | Total HTTP request/response time |
| `http_reqs` | Total number of requests completed |
| `http_err_rate` | Percentage of failed requests |
| `errors` | Total error count |
| `success` | Total successful requests |

### Example Output Interpretation

```
✓ GET /demo/logs
  ✓ status is 200
  ✓ response time < 500ms
✓ POST /demo/echo
  ✓ status is 200
  ✓ response time < 1000ms

Metrics:
  demo_logs_latency..............: avg=45.2ms   min=8.1ms    med=41.3ms   max=234.5ms  p(95)=89.2ms   p(99)=156.3ms
  demo_echo_latency..............: avg=52.1ms   min=10.5ms   med=48.2ms   max=312.1ms  p(95)=112.5ms  p(99)=201.2ms
  weatherforecast_latency........: avg=28.3ms   min=5.2ms    med=26.1ms   max=145.2ms  p(95)=61.2ms   p(99)=98.5ms
  http_reqs......................: 7500 reqs
  http_err_rate..................: 0%
```

### Comparison Analysis

**Compare SimpleApi vs StepUpApi:**
```
Difference % = ((StepUpApi_p95 - SimpleApi_p95) / SimpleApi_p95) * 100

Example:
  SimpleApi p95:   45ms
  StepUpApi p95:   42ms
  Difference: ((42 - 45) / 45) * 100 = -6.7% (StepUpApi is faster!)
```

**Why StepUpApi can be faster:**
- Default log level is Warning/Error (logs less in normal operation)
- SimpleApi may log more at Information level by default
- Step-up only activates when needed (on errors)

## Thresholds

The test includes predefined performance thresholds:

- `demo_logs_latency` p95 < 500ms
- `demo_echo_latency` p95 < 1000ms
- `weatherforecast_latency` p95 < 200ms
- Total errors < 100

If any threshold is breached, k6 will exit with code 99 (threshold exceeded).

## Advanced: Custom Load Profiles

Edit `performance_test.js` to change load stages:

```javascript
// Current: 50 VUs max
stages: [
  { duration: '30s', target: 50 },   // Ramp to 50 VUs
  { duration: '2m', target: 50 },    // Hold at 50 VUs
  { duration: '30s', target: 0 },    // Ramp down
]

// Heavy load test: 200 VUs
stages: [
  { duration: '1m', target: 200 },
  { duration: '5m', target: 200 },
  { duration: '1m', target: 0 },
]
```

## Troubleshooting

### Connection refused
- Ensure both APIs are running on the correct ports (5001, 5002)
- Check firewall settings

### High error rate
- Verify API endpoints are responding (use Scalar UI at `/scalar`)
- Check API logs for errors: `dotnet run --project ...`
- Increase CPU/memory if system is saturated

### Inconsistent results
- Ensure no other heavy processes are running
- Run each test multiple times and compare averages
- Warm up the APIs first (make a few requests manually)

## Example Workflow

```bash
# Start APIs first (using Aspire)
cd example/ExampleApp/ExampleApp.AppHost
dotnet run

# Or run the automated test script
cd tests/k6
.\run_all_tests.ps1  # Windows PowerShell

# Tests will run:
# 1. SimpleApi Baseline (standard Serilog)
# 2. StepUpApi Active (with automatic error-triggered step-up)

# Compare p95 latencies in the summary output
```

### Or use automated scripts:

**PowerShell (Windows):**
```powershell
.\tests\k6\run_all_tests.ps1
```

**Bash (Linux/macOS):**
```bash
bash tests/k6/run_all_tests.sh
```

**Batch (Windows):**
```cmd
tests\k6\run_all_tests.bat
```

## Notes

- **Body Capture Overhead**: Only active when step-up is triggered and request body capture is enabled
- **Redaction Overhead**: Regex patterns are applied to query strings and request bodies when step-up is active
- **Metrics Export**: Use k6 CloudCollector or InfluxDB for persistent metrics storage (see k6 docs)

