# Lukdrasil.StepUpLogging

[![NuGet](https://img.shields.io/nuget/v/Lukdrasil.StepUpLogging.svg)](https://www.nuget.org/packages/Lukdrasil.StepUpLogging/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Dynamic step-up logging for ASP.NET Core with Serilog** - automatically increase log verbosity when errors occur, with minimal performance overhead.

## Features

✅ **Flexible step-up modes** - `Auto` (production), `AlwaysOn` (dev), `Disabled` (minimal)  
✅ **Automatic step-up on errors** - Triggers detailed logging when `Error` level logs are detected  
✅ **OpenTelemetry-first** - Primary export via OTLP with optional console/file sinks  
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

**Option 1: Configuration from appsettings.json (recommended)**

```csharp
using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);

// Automatically loads configuration from appsettings.json "SerilogStepUp" section
builder.AddStepUpLogging();

var app = builder.Build();
app.UseStepUpRequestLogging();
app.Run();
```

**Option 2: Programmatic configuration**

```csharp
using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);

builder.AddStepUpLogging(opts =>
{
    opts.Mode = StepUpMode.Auto;
    opts.BaseLevel = "Warning";
    opts.StepUpLevel = "Debug";
    opts.EnableConsoleLogging = builder.Environment.IsDevelopment();
});

var app = builder.Build();
app.UseStepUpRequestLogging();
app.Run();
```

**Option 3: Mixed (appsettings.json + programmatic override)**

```csharp
// appsettings.json is loaded first, then overridden by code
builder.AddStepUpLogging(opts =>
{
    // Override only specific settings
    if (builder.Environment.IsProduction())
    {
        opts.Mode = StepUpMode.Auto;
        opts.EnableConsoleLogging = false;
    }
    else
    {
        opts.Mode = StepUpMode.AlwaysOn;
        opts.EnableConsoleLogging = true;
    }
});
```

**Option 4: Aspire ServiceDefaults Integration**

When using Aspire ServiceDefaults which already configures Serilog, use the `UseStepUpLogging()` extension method on `LoggerConfiguration`:

```csharp
// In ServiceDefaults/Extensions.cs
public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) 
    where TBuilder : IHostApplicationBuilder
{
    builder.ConfigureOpenTelemetry();
    builder.AddDefaultHealthChecks();
    builder.Services.AddServiceDiscovery();
    
    // Configure Serilog with StepUp logging
    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
          .UseStepUpLogging(builder, opts =>
          {
              opts.Mode = builder.Environment.IsDevelopment() 
                  ? StepUpMode.AlwaysOn 
                  : StepUpMode.Auto;
          });
    });
    
    return builder;
}

// In your API Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(); // Includes StepUp logging

var app = builder.Build();
app.UseStepUpRequestLogging(); // Add request logging middleware
app.Run();
```

### Configuration (appsettings.json)

```json
{
  "SerilogStepUp": {
    "Mode": "Auto",
    "BaseLevel": "Warning",
    "StepUpLevel": "Information",
    "DurationSeconds": 180,
    
    "EnableOtlpExporter": true,
    "EnableConsoleLogging": false,
    "CaptureRequestBody": true,
    "MaxBodyCaptureBytes": 16384,
    "ExcludePaths": ["/health", "/metrics"],
    "RedactionRegexes": [
      "password=[^&]*",
      "authorization:.*"
    ],
    
    "EnrichWithExceptionDetails": true,
    "EnrichWithThreadId": true,
    "EnrichWithProcessId": true,
    "EnrichWithMachineName": true
  }
}
```

### OpenTelemetry Configuration

StepUpLogging uses standard OpenTelemetry environment variables for OTLP configuration. OTLP endpoint and protocol are configured exclusively via environment variables and cannot be overridden programmatically:

| Environment Variable | Purpose | Default |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP collector endpoint | `http://localhost:4317` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Protocol: `grpc` or `http/protobuf` | `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers (format: `key1=value1,key2=value2`) | (none) |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (format: `key1=value1,key2=value2`) | (none) |

**Example with environment variables:**

```bash
# Docker or Kubernetes deployment
export OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer xyz,X-Custom=value"
export OTEL_RESOURCE_ATTRIBUTES="service.name=MyApi,deployment.environment=production"

dotnet MyApp.dll
```

For other OTLP options (like additional headers or resource attributes), use environment variables or configure via `appsettings.json`.

### Step-Up Modes

**`Auto` (default - Production)**
- Logs at `BaseLevel` (Warning) during normal operation
- Automatically steps up to `StepUpLevel` (Information) when errors occur
- Returns to `BaseLevel` after configured duration

**`AlwaysOn` (Development)**
- Always logs at `StepUpLevel` (Information)
- Useful for local development to see all detailed logs
- Step-up triggers are ignored (already at max verbosity)

**`Disabled` (Minimal Logging)**
- Always logs at `BaseLevel` (Warning)
- Step-up mechanism is completely disabled
- Error triggers are ignored

```json
// Development configuration example
{
  "SerilogStepUp": {
    "Mode": "AlwaysOn",
    "StepUpLevel": "Debug",
    "EnableConsoleLogging": true
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

## Common Scenarios

### Production with Auto Step-Up

```json
{
  "SerilogStepUp": {
    "Mode": "Auto",
    "BaseLevel": "Warning",
    "StepUpLevel": "Information",
    "DurationSeconds": 300,
    "EnableOtlpExporter": true,
    "OtlpEndpoint": "http://otel-collector:4317",
    "OtlpResourceAttributes": {
      "service.name": "ProductionAPI",
      "deployment.environment": "production"
    }
  }
}
```

### Local Development (Always Verbose)

```json
{
  "SerilogStepUp": {
    "Mode": "AlwaysOn",
    "StepUpLevel": "Debug",
    "EnableConsoleLogging": true,
    "EnableOtlpExporter": false,
    "CaptureRequestBody": true
  }
}
```

### Environment-Specific Configuration

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.Mode = builder.Environment.IsDevelopment() 
        ? StepUpMode.AlwaysOn 
        : StepUpMode.Auto;
    
    opts.StepUpLevel = builder.Environment.IsDevelopment() ? "Debug" : "Information";
    opts.EnableConsoleLogging = builder.Environment.IsDevelopment();
    
    // Production: use OTLP, Development: use console
    opts.EnableOtlpExporter = !builder.Environment.IsDevelopment();
    
    if (builder.Environment.IsProduction())
    {
        opts.OtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_ENDPOINT") 
            ?? "http://otel-collector:4317";
        opts.OtlpHeaders["Authorization"] = "Bearer " + 
            Environment.GetEnvironmentVariable("OTEL_TOKEN");
    }
});
```

### With Authentication Headers

```bash
# Use environment variables for OTLP authentication
export OTEL_EXPORTER_OTLP_ENDPOINT=http://secure-collector:4317
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer xyz,X-API-Key=secret"
export OTEL_RESOURCE_ATTRIBUTES="service.name=MyAPI,service.version=1.2.3,deployment.environment=production"

dotnet MyApp.dll
```

### Multiple Resource Attributes

```bash
# Use environment variable for multiple resource attributes
export OTEL_RESOURCE_ATTRIBUTES="service.name=payment-service,service.version=2.1.0,service.namespace=ecommerce,deployment.environment=production,cloud.provider=azure,cloud.region=westeurope,k8s.cluster.name=prod-cluster,k8s.namespace.name=payment"

dotnet MyApp.dll
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

### Export Architecture

**Primary: OpenTelemetry OTLP** (Production-ready)
- Logs exported to OTLP collector (default: `localhost:4317`)
- Supports both gRPC and HTTP protocols
- Structured logging with full trace context correlation
- Resource attributes for service identification

**Fallback: Console Logging** (Development/Legacy)
- Enable via `EnableConsoleLogging: true` in configuration
- Useful for local development or direct log collection
- Outputs CompactJSON format

**Optional: File Sink** (Archival/Compliance)
- Daily rolling files with 30-day retention
- Enable via `logFilePath` parameter in `AddStepUpLogging()`

## Configuration Options

| Option | Default | Environment Variable | Description |
|---|---|---|---|
| **Step-Up Behavior** |
| `Mode` | `Auto` | - | Step-up mode: `Auto`, `AlwaysOn`, `Disabled` |
| `BaseLevel` | `"Warning"` | - | Normal log level |
| `StepUpLevel` | `"Information"` | - | Elevated log level during step-up |
| `DurationSeconds` | `180` | - | How long step-up remains active (Auto mode) |
| **OpenTelemetry** |
| `EnableOtlpExporter` | `true` | - | Export logs to OTLP endpoint |
| (Endpoint/Protocol) | (env only) | `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP configuration (environment variables only) |
| **Additional Sinks** |
| `EnableConsoleLogging` | `false` | - | Enable console output (dev scenarios) |
| **Enrichers** |
| `EnrichWithExceptionDetails` | `true` | - | Enrich logs with structured exception details |
| `EnrichWithThreadId` | `false` | - | Include thread ID in log events |
| `EnrichWithProcessId` | `false` | - | Include process ID in log events |
| `EnrichWithMachineName` | `true` | - | Include machine name in log events |
| `EnrichWithEnvironment` | `true` | - | Include environment name (Development/Production) |
| **Request Logging** |
| `CaptureRequestBody` | `false` | - | Capture POST/PUT/PATCH bodies during step-up |
| `MaxBodyCaptureBytes` | `16384` | - | Max bytes to capture from request body |
| `ExcludePaths` | `["/health", "/metrics"]` | - | Paths to exclude from logging |
| `RedactionRegexes` | `[]` | - | Regex patterns for redacting sensitive data |
| **Service Identification** |
| `ServiceVersion` | `null` | `APP_VERSION` | Service version for enrichment |

## OpenTelemetry Metrics

Exposed metrics for monitoring:

- `stepup_trigger_total` - Total number of step-up triggers
- `stepup_active` - Whether step-up is currently active (0 or 1)
- `stepup_duration_seconds` - Duration histogram of step-up windows
- `request_body_captured_total` - Number of requests with captured body
- `request_redaction_applied_total` - Number of requests with redaction applied

## License

MIT © Lukdrasil
