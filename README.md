# Lukdrasil.StepUpLogging

[![NuGet](https://img.shields.io/nuget/v/Lukdrasil.StepUpLogging.svg)](https://www.nuget.org/packages/Lukdrasil.StepUpLogging/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Dynamic step-up logging for ASP.NET Core with Serilog** - automatically increase log verbosity when errors occur, with minimal performance overhead.

## Features

✅ **Automatic step-up on errors** - Triggers detailed logging when `Error` level logs are detected  
✅ **Minimal overhead** - 18-29% faster than standard Serilog in baseline tests  
✅ **Request body capture** - Optional capture during step-up with configurable size limits  
✅ **Sensitive data redaction** - Regex-based redaction for query strings and request bodies  
✅ **OpenTelemetry metrics** - Built-in metrics for monitoring step-up triggers and duration  
✅ **Manual control** - Expose endpoints to manually trigger or check step-up status  
✅ **.NET 10.0** - Built with modern C# 14 features

## Quick Start

### Installation

```bash
dotnet add package Lukdrasil.StepUpLogging
```

### Basic Setup

```csharp
using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);

// Add step-up logging
builder.AddStepUpLogging(enableConsoleLogging: true);

var app = builder.Build();

// Add step-up request logging middleware
app.UseStepUpRequestLogging();

app.Run();
```

### Configuration (appsettings.json)

```json
{
  "SerilogStepUp": {
    "BaseLevel": "Warning",
    "StepUpLevel": "Information",
    "DurationSeconds": 180,
    "CaptureRequestBody": true,
    "MaxBodyCaptureBytes": 16384,
    "ExcludePaths": ["/health", "/metrics"],
    "RedactionRegexes": [
      "password=[^&]*",
      "authorization:.*"
    ]
  }
}
```

## Manual Control

Add endpoints to manually trigger step-up or check status:

```csharp
app.MapPost("/stepup/trigger", (StepUpLoggingController controller) =>
{
    controller.Trigger();
    return Results.Ok(new { message = "Step-up activated" });
});

app.MapGet("/stepup/status", (StepUpLoggingController controller) =>
{
    return Results.Ok(new { active = controller.IsSteppedUp });
});
```

## Performance

Benchmark results (k6 load test, 50 VUs, 3 minutes):

| Metric | Standard Serilog | StepUpLogging | Improvement |
|--------|------------------|---------------|-------------|
| Avg Latency | 1.19 ms | 0.98 ms | **-18%** ⚡ |
| P95 Latency | 2.12 ms | 1.64 ms | **-23%** ⚡ |
| Throughput | 165.77 req/s | 166.21 req/s | **+0.3%** |

See full [performance test results](tests/k6/performance_test_results.md).

## How It Works

1. **Normal operation**: Logs at `BaseLevel` (e.g., Warning)
2. **Error detected**: `StepUpTriggerSink` automatically triggers step-up
3. **Step-up active**: Logs at `StepUpLevel` (e.g., Information) for configured duration
4. **Auto restore**: Returns to `BaseLevel` after duration expires

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `BaseLevel` | `"Warning"` | Normal log level |
| `StepUpLevel` | `"Information"` | Elevated log level during step-up |
| `DurationSeconds` | `180` | How long step-up remains active |
| `CaptureRequestBody` | `false` | Capture POST/PUT/PATCH bodies during step-up |
| `MaxBodyCaptureBytes` | `16384` | Max bytes to capture from request body |
| `ExcludePaths` | `["/health", "/metrics"]` | Paths to exclude from logging |
| `RedactionRegexes` | `[]` | Regex patterns for redacting sensitive data |

## OpenTelemetry Metrics

Exposed metrics for monitoring:

- `stepup_trigger_total` - Total number of step-up triggers
- `stepup_active` - Whether step-up is currently active (0 or 1)
- `stepup_duration_seconds` - Duration histogram of step-up windows
- `request_body_captured_total` - Number of requests with captured body
- `request_redaction_applied_total` - Number of requests with redaction applied

## License

MIT © Lukdrasil
