# Lukdrasil.StepUpLogging

[![NuGet](https://img.shields.io/nuget/v/Lukdrasil.StepUpLogging.svg)](https://www.nuget.org/packages/Lukdrasil.StepUpLogging/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Dynamic step-up logging for ASP.NET Core with Serilog** - automatically increase log verbosity when errors occur, with minimal performance overhead.

## Features

✅ **Flexible step-up modes** - `Auto` (production), `AlwaysOn` (dev), `Disabled` (minimal)  
✅ **Automatic step-up on errors** - Triggers detailed logging when `Error` level logs are detected  
✅ **Pre-error buffering** - In-memory per-request log buffering, flushed when errors occur  
✅ **OpenTelemetry-first** - Primary export via OTLP with optional console/file sinks  
✅ **OpenTelemetry Activities** - Built-in distributed tracing with 6+ instrumentation points (default-enabled)  
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

    "AlwaysLogRequestSummary": true,
    "RequestSummaryLevel": "Information",

    "EnableOtlpExporter": true,
    "EnableConsoleLogging": false,
    "CaptureRequestBody": true,
    "MaxBodyCaptureBytes": 16384,
    "ExcludePaths": ["/health", "/metrics"],
    "RedactionRegexes": [
      "password=[^&]*",
      "authorization:.*"
    ],

    "EnablePreErrorBuffering": true,
    "PreErrorBufferSize": 100,
    "PreErrorMaxContexts": 1024,

    "EnrichWithExceptionDetails": true,
    "EnrichWithThreadId": true,
    "EnrichWithProcessId": true,
    "EnrichWithMachineName": true
  }
}
```

### Production configuration via environment variables

Every `SerilogStepUp` key auto-binds from an environment variable using .NET's default
configuration provider, which maps configuration key-path segments to environment variable
names by joining them with a double underscore (`__`). No code change is required — the
`AddOptions<StepUpLoggingOptions>().Bind(...)` call inside `AddStepUpLogging` picks these up
automatically, exactly like the `OTEL_*` variables documented below.

This is the standard way to configure the library in Docker / Kubernetes, where you would
otherwise not ship an `appsettings.json`:

```bash
SerilogStepUp__DurationSeconds=300
SerilogStepUp__StepUpLevel=Debug
SerilogStepUp__EnableConsoleLogging=true
SerilogStepUp__CaptureRequestBody=true
```

Nested keys use the same convention (each path segment separated by `__`), e.g.
`SerilogStepUp__ExcludePaths__0=/health`.

### Request Summary behaviour

When "AlwaysLogRequestSummary" is enabled, the middleware emits a single structured summary event at the configured "RequestSummaryLevel" for every completed HTTP request. The summary contains:

- **HTTP method** - GET, POST, etc.
- **Request path** - URL path (trailing slashes normalized)
- **Response status code** - 200, 404, 500, etc.
- **Elapsed milliseconds** - Request duration
- **Trace ID** - Optional trace/correlation id
- **UserAgent** - Client User-Agent header (for client identification), redacted via `RedactionRegexes`
- **ClientIp** - Client IP address from `Connection.RemoteIpAddress` by default (see [Security](#security)); when `TrustForwardedHeaders` is enabled, the first `X-Forwarded-For` entry instead
- **ForwardedFor** - The raw `X-Forwarded-For` header value (redacted), present only when the header is sent

Summary events are marked with the "IsRequestSummary" property and are processed by the library's SummarySink so they are exported independently of the StepUp level switch (base Warning) and the normal step-up flow.

#### Example Request Summary Log

```json
{
  "Timestamp": "2025-01-08T12:34:56.789Z",
  "Level": "Information",
  "MessageTemplate": "Request finished {Method} {Path} {StatusCode} {ElapsedMs}",
  "Method": "POST",
  "Path": "/api/users",
  "StatusCode": 201,
  "ElapsedMs": 45.23,
  "UserAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
  "ClientIp": "198.51.100.100",
  "ForwardedFor": "203.0.113.42, 198.51.100.100",
  "IsRequestSummary": true
}
```

#### IP Address Detection

By default the library takes `ClientIp` from `HttpContext.Connection.RemoteIpAddress` and
**does not** trust the `X-Forwarded-For` (XFF) header, because that header is client-supplied
and spoofable. When XFF is present, its raw value is still logged separately as `ForwardedFor`
(redacted) for diagnostics.

1. **Default (`TrustForwardedHeaders: false`)** - `ClientIp` = `Connection.RemoteIpAddress`; XFF is ignored for `ClientIp` but surfaced as `ForwardedFor`.
2. **`TrustForwardedHeaders: true`** - `ClientIp` = first `X-Forwarded-For` entry when present (v2 behavior). Only enable this behind a reverse proxy you control **and** with `ForwardedHeadersMiddleware` configured. See [Security](#security).
3. **Graceful degradation** - Omits `ClientIp` if detection fails.

To customise where summaries are written, provide a dedicated summary logger in DI when calling AddStepUpLogging, or configure the default sinks; the library enforces a single DI-managed summary logger to avoid unmanaged CreateLogger instances.


### OpenTelemetry Configuration

StepUpLogging uses standard OpenTelemetry environment variables for OTLP configuration. OTLP endpoint and protocol are configured exclusively via environment variables and cannot be overridden programmatically:

| Environment Variable | Purpose | Default |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP collector endpoint | `http://localhost:4317` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Protocol: `grpc` or `http/protobuf` | `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers (format: `key1=value1,key2=value2`) | (none) |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (format: `key1=value1,key2=value2`) | (none) |

Keys and values in `OTEL_EXPORTER_OTLP_HEADERS` and `OTEL_RESOURCE_ATTRIBUTES` are
**percent-decoded** per the OTel specification, so an encoded value such as
`Authorization=Basic%20QWxhZGRpbg%3D%3D` reaches the collector as `Basic QWxhZGRpbg==`. A
literal comma inside a value is not representable in this format (it delimits pairs).

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

## Request Logging

The `UseStepUpRequestLogging()` middleware enriches each request with detailed context:

### Captured Information

- **RequestPath** - Normalized request path (trailing slashes removed)
- **QueryString** - Query parameters (redacted based on `RedactionRegexes`)
- **RouteParameters** - Route parameter values (e.g., `{id}`, `{role}`)
- **Headers** - HTTP request headers with automatic redaction of sensitive headers
- **RequestBody** - POST/PUT/PATCH bodies (when `CaptureRequestBody` is enabled and logging is stepped-up)

### Example Log Output

```json
{
  "Timestamp": "2025-01-08T12:34:56.789Z",
  "Level": "Information",
  "MessageTemplate": "HTTP {Method} {Path} responded {StatusCode} in {Elapsed:0.00}ms",
  "RequestPath": "/api/users/123",
  "RouteParameters": {
    "id": "123"
  },
  "QueryString": "?filter=active&sort=name",
  "Headers": {
    "content-type": "application/json",
    "user-agent": "Mozilla/5.0",
    "authorization": "[REDACTED]",
    "cookie": "[REDACTED]"
  },
  "RequestBody": "{\"name\": \"John Doe\", \"password\": \"[REDACTED]\"}"
}
```

### Sensitive Header Redaction

Built-in redacted headers:
- `Authorization`
- `Cookie`
- `X-API-Key`
- `X-Auth-Token`
- `X-Access-Token`
- `Authorization-Token`
- `Proxy-Authorization`
- `WWW-Authenticate`
- `Sec-WebSocket-Key`

Add custom sensitive headers via configuration:

**appsettings.json:**
```json
{
  "SerilogStepUp": {
    "AdditionalSensitiveHeaders": [
      "X-Custom-Secret",
      "X-Internal-Token",
      "X-Database-Password"
    ]
  }
}
```

**Programmatically:**
```csharp
builder.AddStepUpLogging(opts =>
{
    opts.AdditionalSensitiveHeaders = new[]
    {
        "X-Custom-Secret",
        "X-Internal-Token"
    };
});
```

## OpenTelemetry Activities & Instrumentation

StepUpLogging automatically instruments your requests with OpenTelemetry `Activity` objects for distributed tracing. This feature is **enabled by default** but can be disabled if needed.

### Activity Instrumentation Points

The library creates activities at these key points:

| Activity | Type | Description | Triggered |
|----------|------|-------------|----------|
| `LogRequest` | `Server` | Request processing span | Every HTTP request |
| `TriggerStepUp` | `Internal` | Step-up event triggered | When error detected |
| `PerformStepDown` | `Internal` | Step-down event executed | When duration expires |
| `ApplyRedaction` | `Internal` | Sensitive data redaction | Per redaction pattern |
| `CaptureRequestBody` | `Internal` | Request body capture | When step-up active |
| `FlushBufferedEvents` | `Internal` | Pre-error buffer flush | When error occurs |

### Enable/Disable Activities

**Enabled by default** - Activities are created automatically when OTEL is registered:

```json
{
  "SerilogStepUp": {
    "EnableActivityInstrumentation": true
  }
}
```

**Disable if needed** (opt-out):

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.EnableActivityInstrumentation = false;  // Disable activity creation
});
```

### Zero Overhead When OTEL Not Registered

When OpenTelemetry is not registered in the service container:
- Activities are created but not propagated
- No performance overhead (activities are internal)
- Enable/disable flag has no effect

### Example with Aspire Observability

```csharp
var builder = WebApplication.CreateBuilder(args);

// ConfigureOpenTelemetry() automatically registers traces
builder.AddServiceDefaults();

builder.AddStepUpLogging(opts =>
{
    opts.EnableActivityInstrumentation = true;  // Default
});

var app = builder.Build();
app.UseStepUpRequestLogging();
app.Run();

// Activities now visible in:
// - Grafana Tempo / Jaeger
// - Application Insights
// - Custom OTEL collectors
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

## Pre-Error Buffering

**Pre-error buffering** automatically captures recent log events in memory per request/activity and flushes them when an error occurs. This provides context around the error without increasing log verbosity in normal operation.

### How It Works

1. **Buffering phase**: All non-error logs are stored in a ring buffer per OpenTelemetry trace ID (one buffer per request)
2. **Flush trigger**: When an `Error` or `Fatal` log is emitted, the buffer flushes all captured events from that request to the output
3. **Memory management**: Uses LRU eviction to prevent unbounded memory growth (configurable limits on buffer size and active contexts)

### Configuration

**Enable via appsettings.json:**

```json
{
  "SerilogStepUp": {
    "EnablePreErrorBuffering": true,
    "PreErrorBufferSize": 100,
    "PreErrorMaxContexts": 1024
  }
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `EnablePreErrorBuffering` | `true` | Enable/disable pre-error buffering |
| `PreErrorBufferSize` | `100` | Max events to retain per request before oldest are dropped |
| `PreErrorMaxContexts` | `1024` | Max concurrent request contexts to track; older ones are evicted |

**Enable programmatically:**

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.EnablePreErrorBuffering = true;
    opts.PreErrorBufferSize = 200;      // Capture more events per request
    opts.PreErrorMaxContexts = 512;     // Fewer concurrent requests in memory
});
```

### Benefits

- **Diagnostics**: See what happened before an error (request headers, SQL queries, business logic) without enabling debug logging for all requests
- **Production-safe**: Buffering is per-request; no global state that could consume unbounded memory
- **Configurable**: Tune buffer size and context limits based on your memory budget and traffic patterns
- **Automatic**: No code changes needed; works transparently in the logging pipeline

### Example Scenario

Without buffering:
```
[Warning] Request started: GET /api/users/123
[Error] User not found (id=123)
```

With buffering:
```
[Information] Request started: GET /api/users/123
[Debug] Querying user database: SELECT * FROM Users WHERE Id = @id
[Debug] Query parameters: @id = 123
[Debug] Database response: no rows
[Error] User not found (id=123)
```

All `Debug`/`Information` events are buffered internally; when the error occurs, they are flushed to provide diagnostic context.

## Immediate Logging

Sometimes you need a log event to **always export** regardless of the current step-up level — for example, a security audit entry, a billing event, or a key lifecycle marker. Use the `LogImmediate*` extension methods or `BeginImmediateScope` for this.

Immediate events bypass the step-up `LoggingLevelSwitch` and go directly to the configured sinks. They are also excluded from the pre-error ring buffer, so they are never duplicated when a buffer flush occurs.

### Extension Methods

```csharp
using Lukdrasil.StepUpLogging;

// Single-event helpers
logger.LogImmediateInformation("Payment processed: {OrderId}", orderId);
logger.LogImmediateWarning("Rate limit approaching for {ClientId}", clientId);
logger.LogImmediateError("Checkout failed: {Reason}", reason);
logger.LogImmediateError(ex, "Unhandled exception during {Operation}", op);

// Generic level variant
logger.LogImmediate(LogLevel.Information, "Audit: {Action} by {User}", action, user);
logger.LogImmediate(LogLevel.Warning, ex, "Retrying {Operation}", op);
```

### Scope Variant

Use `BeginImmediateScope` when multiple log events inside a block should all export immediately:

```csharp
using (logger.BeginImmediateScope())
{
    logger.LogInformation("Step 1 complete");
    logger.LogInformation("Step 2 complete");
    logger.LogWarning("Step 3 skipped");
}
```

### When to Use

| Scenario | Recommended approach |
|---|---|
| Single critical event | `LogImmediateError` / `LogImmediateWarning` |
| Block of related events that must all export | `BeginImmediateScope` |
| Normal diagnostic logs (visible only when stepped up) | Standard `logger.Log*` |

### Metrics

Immediate-routed events are tracked by the `StepUpLogging.Immediate` meter:

- `immediate_processed_total` — number of events forwarded via `ImmediateSink`

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
| `MaxContinuousStepUpSeconds` | `0` (disabled) | - | Upper bound on a single continuous step-up window; forces a step-down and opens a cooldown when exceeded. `0` disables the cap. Must be `0` or `>= DurationSeconds`. |
| `StepUpCooldownSeconds` | `300` | - | Seconds triggers are ignored after the cap forces a step-down; ignored when the cap is disabled |
| **Pre-Error Buffering** |
| `EnablePreErrorBuffering` | `true` | - | Enable/disable pre-error buffering |
| `PreErrorBufferSize` | `100` | - | Max events per request before oldest are dropped |
| `PreErrorMaxContexts` | `1024` | - | Max concurrent request contexts; older ones are evicted |
| **OpenTelemetry Instrumentation** |
| `EnableActivityInstrumentation` | `true` | - | Enable/disable Activity creation (default-enabled, opt-out) |
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
| `EnrichWithCallStack` | `false` | - | Include call-stack information using Serilog.Enrichers.CallStack (https://github.com/hokagedami/serilog-stacktrace-enricher) |
| `EnrichWithEnvironment` | `true` | - | Include environment name (Development/Production) |
| **Request Logging** |
| `CaptureRequestBody` | `false` | - | Capture POST/PUT/PATCH bodies during step-up |
| `MaxBodyCaptureBytes` | `16384` | - | Max bytes to capture from request body |
| `ExcludePaths` | `["/health", "/metrics"]` | - | Paths to exclude from logging |
| `RedactionRegexes` | `[]` | - | Regex patterns for redacting sensitive data (request metadata and bodies only — see [Security](#security)) |
| `AdditionalSensitiveHeaders` | `[]` | - | Custom header names to redact in request logging |
| `TrustForwardedHeaders` | `false` | - | When `true`, `ClientIp` is taken from the first `X-Forwarded-For` entry (v2 behavior). Only enable behind a proxy you control with `ForwardedHeadersMiddleware`. See [Security](#security). |
| **Service Identification** |
| `ServiceVersion` | `null` | `APP_VERSION` | Service version for enrichment |

## Security

Three security properties are worth understanding before you deploy:

### Client IP is only as trustworthy as your proxy configuration

By default `ClientIp` comes from `HttpContext.Connection.RemoteIpAddress`, and the library
**does not** trust the `X-Forwarded-For` header — it is client-supplied and spoofable. The
library will not guess your proxy topology. If you run behind a reverse proxy and need the
real client address, configure ASP.NET Core's
[`ForwardedHeadersMiddleware`](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
with your known proxies so `Connection.RemoteIpAddress` reflects the true client, and leave
`TrustForwardedHeaders` at `false`. Setting `TrustForwardedHeaders: true` blindly trusts the
first XFF entry and lets any caller forge the logged IP; use it only behind a proxy you fully
control. The raw header is always logged (redacted) as `ForwardedFor` for diagnostics.

### Redaction covers request metadata and bodies, not message-template arguments

`RedactionRegexes` is applied to query strings, route values, headers, and request bodies.
It does **not** scan the rendered text of arbitrary log messages — a secret passed as a
message-template argument (`logger.LogInformation("token={T}", secret)`) is **not** redacted.
Do not log secrets in message templates.

### Sustained-error cost amplification

Step-up raises verbosity on errors, which raises telemetry cost. `MaxContinuousStepUpSeconds`
bounds a *single continuous* step-up window and then opens a cooldown, but an attacker pacing
triggers to fire just outside the cooldown still obtains sustained verbosity at a reduced duty
cycle. This is by design (see ADR 0010): the library cannot distinguish malicious from
legitimate error bursts. Collector-side sampling / quota / rate limiting remains the backstop
for uncapped cost, especially when the cap is disabled (`MaxContinuousStepUpSeconds = 0`, the
default).

## OpenTelemetry Activities & Metrics

### Activities

When `EnableActivityInstrumentation` is enabled (default), these activities are created:
- **Request-level**: `LogRequest` (server-side span) tracking entire HTTP request
- **Operation-level**: `TriggerStepUp`, `PerformStepDown`, `FlushBufferedEvents` (internal operations)
- **Sub-operation**: `ApplyRedaction`, `CaptureRequestBody` (child spans of LogRequest)

Activities include W3C trace context tags and semantic conventions:
- `http.scheme` - Protocol (http/https)
- `http.host` - Host header value
- `security.redaction_applied` - Whether redaction was performed

### Metrics

Exposed metrics for monitoring:

- `stepup_trigger_total` - Total number of step-up triggers
- `stepup_active` - Whether step-up is currently active (0 or 1)
- `stepup_duration_seconds` - Duration histogram of step-up windows
- `request_body_captured_total` - Number of requests with captured body
- `request_redaction_applied_total` - Number of requests with redaction applied
- `buffer_events_total` - Total events buffered by pre-error buffer
- `buffer_flushed_events_total` - Events flushed due to error
- `buffer_flush_total` - Number of buffer flush operations
- `buffer_evicted_contexts_total` - Contexts evicted due to LRU pressure

## License

MIT © Lukdrasil
