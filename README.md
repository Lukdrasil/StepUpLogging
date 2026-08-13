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
✅ **Sensitive data redaction** - Regex-based redaction for query strings and request bodies, with an opt-in sweep of application log properties too  
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

#### Which requests count as errors

The request-completion event's level is what trips the step-up and flushes the pre-error buffer,
so it is deliberately narrow:

| Outcome | Level |
|---|---|
| Excluded path (`ExcludePaths`) | Verbose |
| Client aborted the connection (closed tab, reload, dropped connection) | Information |
| Unhandled exception | Error |
| Status >= 500, no exception | Error, or Warning when `TreatServerErrorStatusAsError` is `false` |
| Status >= 400 | Warning |
| Otherwise | Information |

An aborted request is logged at Information rather than Error because a disconnect is not an
application failure — otherwise any caller, on an anonymous route included, could flush the buffer
and step the instance up just by disconnecting. A genuine exception on a request the client also
aborted is still Error.

Behind a reverse proxy or BFF, set `TreatServerErrorStatusAsError` to `false`: a 5xx relayed from a
backend then logs at Warning and stays out of the trigger path, while your own unhandled exceptions
still log at Error.

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

1. **Buffering phase**: Non-error logs at or above the resolved `StepUpLevel` are stored in a ring buffer per OpenTelemetry trace ID (one buffer per request); events below `StepUpLevel` are never buffered. Set `StepUpLevel` to `"Verbose"` to buffer everything.
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

## Audit Logging

Audit trails answer "who did what, to what, and with what outcome?" in response to regulatory investigations and security incidents. StepUpLogging provides facilities to emit audit records to your own durable store, guaranteeing they are never dropped by step-up gating.

### Setup

Implement `IAuditEventSink` over your durable store — see [Example Production Sink](#example-production-sink) for a database-backed one — and wire it via `AddAuditLogging`:

```csharp
using Lukdrasil.StepUpLogging;

// In Program.cs — DbAuditSink is the sink from "Example Production Sink" below
builder.AddStepUpLogging();  // Required: audit uses its client-IP rules
builder.AddAuditLogging<DbAuditSink>();
```

The sink must write somewhere the records survive: audit records that go to memory or to nowhere are worse than no audit at all, because they are relied upon. The package ships no `IAuditEventSink` implementation for exactly that reason.

**`AddAuditLogging` requires `AddStepUpLogging`** — audit logging derives client IP via the same `TrustForwardedHeaders` policy as request logging. Both calls are registration-only, so their order does not matter; what matters is that `AddStepUpLogging` is called at all. If it is not, **the host refuses to start** with a message naming both methods — the app never serves a request that should have been audited.

To disable audit logging in an environment (e.g., development), simply do not call `AddAuditLogging`. There is no configuration flag:

```csharp
if (!builder.Environment.IsDevelopment())
{
    builder.AddAuditLogging<MyProductionAuditSink>();
}
```

### Recording Audit Events

Inject `IAuditLogger<T>` (scoped) and call `AuditAsync`:

```csharp
public sealed class OrderService(IAuditLogger<OrderService> audit, IOrderRepository db)
{
    private readonly IOrderRepository _db = db;

    public async Task CancelOrderAsync(string orderId, string userId)
    {
        try
        {
            // Business logic
            await _db.CancelOrderAsync(orderId);
            
            // Record success with companion log
            var evt = AuditEvent.Success("order.cancel", userId, "user") with
            {
                TargetType = "order",
                TargetId = orderId,
                Data = new Dictionary<string, object?> { { "reason", "customer requested" } }
            };
            
            await audit.AuditAsync(evt, log =>
                log.LogInformation("Order {OrderId} cancelled by {UserId}", orderId, userId)
            );
        }
        catch (NotFoundException)
        {
            await audit.AuditAsync(AuditEvent.Denied("order.cancel", userId, "user") with
            {
                TargetType = "order",
                TargetId = orderId,
                Reason = "order not found"
            });
            throw;
        }
    }
}
```

### Understanding the Companion Log

The optional `log` parameter lets you emit a log event alongside the audit record:

- **The companion log is written exactly as given** — its message-template text is never touched, and its property values are redacted only if you set `RedactLogEventProperties`, the same as every other application log the library emits. Whatever redaction you see on the audit record's library-filled fields comes from the library's extraction logic, not from redacting the template itself.
- **Only use the log for a summary.** Put identifiers (order ID, user ID) and structured context (outcome, reason) in the audit `Action`, required fields, and `Data`. Put the human-readable narrative in the log. Example:
  ```csharp
  var evt = AuditEvent.Failure("user.login", userId, "user") with
  {
      Data = new Dictionary<string, object?> { { "attempt", 3 } }
  };
  
  await audit.AuditAsync(evt, log =>
      log.LogWarning("User login failed after {Attempts} attempts", 3)
  );
  ```
  The audit record carries the count (3) as structured data in the consumer's store; the log carries the narrative (failed after N attempts) in telemetry where it helps operators understand the incident.

### What the Sink Receives

Every `AuditEvent` has:

- **Caller-supplied, required:** `Action` (string, e.g., `"order.cancel"`), `ActorId`, `ActorType` (the kind of actor `ActorId` identifies, e.g. `"user"`, `"service"`, `"api-key"` — required, with no default, so a call site cannot pass off a guessed actor kind as fact), `Outcome` (Success/Failure/Denied).
- **Caller-supplied, optional:** `TargetType`, `TargetId`, `Reason`, `Data`, `OldValues`, `NewValues` (state before/after the action, like `Data` never redacted — supply only the fields that changed, not a whole entity), `OnBehalfOfId`, `TenantId`.
- **Library-filled:** `EventId` (a UUIDv7 stamped on every call; a caller-set value is overwritten — this is the field a retrying or spooling sink deduplicates on), `TimestampUtc` (UTC), `TraceId`, `SpanId` (from OpenTelemetry), `SourceIp` (redacted on only one of its two branches — see below), `UserAgent` (always redacted; see [Security](#security)).

`SourceIp` is redacted asymmetrically, by where the address came from: an address read from `X-Forwarded-For` (only when `TrustForwardedHeaders = true`) is client-supplied and goes through redaction, while an address read from the connection is supplied by the network layer, cannot be forged, and reaches your sink bare. This is the same client-IP rule request logging uses (ADR 0008), reused rather than restated.

The library does **not** copy the `Data` dictionary — if your sink buffers the event and your code mutates the dictionary afterwards, the audit record sees the mutations. Pass a snapshot if you need to mutate it: `Data = new Dictionary<string, object?>(myDict)`.

### Metrics and Alerting

The audit meter `StepUpLogging.Audit` provides:
- `audit_events_total{outcome}` — count of records written, grouped by outcome (Success, Failure, Denied). Moves only for a `Stored` write.
- `audit_events_dropped_total{outcome}` — count of records a sink deliberately discarded (returned `AuditWriteResult.Dropped`), e.g. under back-pressure. No companion log is written for these, and `audit_events_total` does not move.
- `audit_write_failures_total` — count of sink writes that threw.

**Zero audit events over a period is itself an alarm** — it indicates your audit stopped working. Query for the *rate* of `audit_events_total` over a rolling window:

```promql
# Alert if no audit events in the last hour
rate(audit_events_total[1h]) == 0
```

**A nonzero drop rate is an alarm in its own right, separate from the one above** — it means a sink is discarding records on purpose, and `audit_events_total` never moves for a dropped write, so the alarm above reads healthy the whole time it happens:

```promql
# Alert if any audit events are being deliberately dropped
audit_events_dropped_total > 0
```

### Example Production Sink

This example writes to Entity Framework Core with transactional consistency:

```csharp
public sealed class DbAuditSink(MyDbContext db) : IAuditEventSink
{
    public async ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
    {
        db.AuditLogs.Add(new AuditLogEntity
        {
            Action = auditEvent.Action,
            ActorId = auditEvent.ActorId,
            ActorType = auditEvent.ActorType,
            TargetType = auditEvent.TargetType,
            TargetId = auditEvent.TargetId,
            Outcome = auditEvent.Outcome,
            Reason = auditEvent.Reason,
            SourceIp = auditEvent.SourceIp,
            UserAgent = auditEvent.UserAgent,
            TraceId = auditEvent.TraceId,
            TimestampUtc = auditEvent.TimestampUtc,
            Data = JsonSerializer.Serialize(auditEvent.Data)
        });
        
        // Writes in the same transaction as the business operation
        await db.SaveChangesAsync();
        return AuditWriteResult.Stored;
    }
}

// In Program.cs — AddAuditLogging registers the sink itself; a separate
// AddScoped<DbAuditSink>() would just be a second, unused descriptor.
builder.AddAuditLogging<DbAuditSink>(ServiceLifetime.Scoped);
```

**Note:** This is a working example, not a shipped type. The package deliberately ships no `IAuditEventSink` implementation — the one you implement is yours, suited to your store and transaction model.

### Testing Your Audit Trail

Because nothing in the package writes audit records for you, the assertion that they *are* written is yours to make. In tests, substitute an in-memory test double for the production sink and assert on what it recorded:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection.Extensions; // RemoveAll

// Test double only — never wire this in Program.cs; it discards every record on shutdown
public sealed class RecordingAuditSink : IAuditEventSink
{
    // Concurrent, not List<T>: as a singleton this instance is shared by every request the test
    // drives, and each writes on its own thread
    public ConcurrentQueue<AuditEvent> Records { get; } = new();

    public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
    {
        Records.Enqueue(auditEvent);
        return ValueTask.FromResult(AuditWriteResult.Stored);
    }
}

builder.AddStepUpLogging();
// Defensive, not required here since nothing has registered a sink yet: if this recipe instead
// runs against a builder that already carries Program.cs's own AddAuditLogging<DbAuditSink>() call
// (e.g. a test that shares Program.cs's startup code before substituting a sink), a second
// AddAuditLogging call throws, naming both sink types — RemoveAll first avoids that either way.
builder.Services.RemoveAll<IAuditEventSink>();
// Singleton so one instance outlives the request scope the assertions run outside of
builder.AddAuditLogging<RecordingAuditSink>(ServiceLifetime.Singleton);

// ... exercise the operation, then:
var sink = (RecordingAuditSink)host.Services.GetRequiredService<IAuditEventSink>();
var recorded = Assert.Single(sink.Records);
Assert.Equal("order.cancel", recorded.Action);
Assert.Equal(AuditOutcome.Success, recorded.Outcome);
```

Skip the `RemoveAll` and `AddAuditLogging<RecordingAuditSink>` throws `InvalidOperationException`,
naming both sinks: *"AddAuditLogging\<RecordingAuditSink\> failed: DbAuditSink is already
registered as the IAuditEventSink... call services.RemoveAll\<IAuditEventSink\>() before
registering the test sink."*

Cover the failure path too: a sink that throws must abort the business operation (exceptions propagate unchanged), and an operation that was denied must still leave a `Denied` record.

## Encrypted Spool Audit Sink

`EncryptedSpoolAuditSink` ships inside this same package — no separate install. It is a durable,
at-least-once `IAuditEventSink`: every audited write is encrypted, written to a local write-ahead
spool, and delivered to a receiver you configure, one `POST` per record. A crash between "the
receiver accepted it" and "the spool file was deleted" resends the record on the next drain cycle,
so **your receiver must deduplicate on `EventId`** — delivery is at-least-once, not exactly-once.

### Setup

```csharp
using Lukdrasil.StepUpLogging;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

builder.AddStepUpLogging();
builder.AddEncryptedSpoolAuditSink(options =>
{
    // A mounted volume that outlives the process — never a path inside the deployment artifact,
    // which is replaced on the next restart or rollout and would take spooled records with it.
    options.SpoolDirectory = "/var/lib/myapp/audit-spool";
    options.EndpointBaseUrl = "https://audit-receiver.internal";
    options.ModuleName = "order-service";
    options.Version = "1.4.0";
});

// The consumer's own IAuditPayloadEncryptor — see below — registered separately.
builder.Services.AddSingleton<IAuditPayloadEncryptor, MyAeadEncryptor>();
```

Do not call `AddAuditLogging` yourself for this sink — `AddEncryptedSpoolAuditSink` does that
internally, alongside registering the drain worker as a hosted service and a combined health
check. There is no `Enabled` flag: not calling this method is how the sink stays off. A required
option left unset is a host that refuses to start, naming it. Whatever credentials the endpoint
needs (a bearer token, a client certificate) go on the delivery `HttpClient` via
`options.ConfigureProducerCredentials`, not on the port and not in `EncryptedSpoolOptions` as a
key — the drain worker sends what that client is set up to send and never inspects it.

### Options

`EncryptedSpoolOptions` is set in the `AddEncryptedSpoolAuditSink` lambda; unlike
`StepUpLoggingOptions`, it is not bound from `appsettings.json`.

| Option | Default | What it does |
|---|---|---|
| `SpoolDirectory` | *required* | Where spooled records are kept, one file per record. Must outlive the process. |
| `EndpointBaseUrl` | *required* | Absolute base URL of the audit endpoint; records are posted to `{EndpointBaseUrl}/audit`. |
| `ModuleName` | *required* | Name of the producing module, carried inside the encrypted payload. |
| `Version` | *required* | Version of the producing module, alongside `ModuleName`. |
| `MaxPayloadBytes` | 64 KB | Largest serialized record accepted. Over it, `AuditAsync` throws at the call site — nothing is encrypted or spooled. |
| `SpoolMaxBytes` | 256 MB | Spool size cap. Reaching either this or `SpoolMaxEntries` starts dropping new records. |
| `SpoolMaxEntries` | 100 000 | Spool record-count cap. |
| `DrainInterval` | 5 s | How often the drain worker looks for records, and the first wait it backs off from. |
| `MaxDrainBackoff` | 5 min | Ceiling the wait doubles up to while the endpoint keeps failing. |
| `DeliveryTimeout` | 30 s | Timeout for one delivery attempt. A timeout is transient — retried, never dead-lettered. |
| `ShutdownDrainTimeout` | 5 s | How long a stopping host waits for the drain worker. Nothing is lost when it runs out; the next start delivers. |
| `UnreadableRetryLimit` | 10 | Drain cycles a spool file may fail to even be opened before it is dead-lettered as unreadable. |
| `SpoolWarnStatus`, `SpoolFullStatus`, `UnreachableStatus` | see [Health check](#health-check) | The statuses the health check reports. |
| `ConfigureProducerCredentials` | none | Sets credentials on the delivery `HttpClient`, as above. |

### The encryption port

The sink never sees a key. It hands the serialized record to `IAuditPayloadEncryptor`, which your
application implements and registers:

```csharp
public interface IAuditPayloadEncryptor
{
    ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload);
}
```

Your implementation owns **all** cryptography — algorithm, key material, and key custody
(acquisition, caching, rotation). The sink treats the returned bytes as opaque: it spools and
delivers them as-is, never interpreting either side of the call. Register it as a **singleton**,
and make it **thread-safe** — the sink that calls it is itself a singleton, calling `EncryptAsync`
concurrently from every in-flight audit write; unsynchronized shared state (a cached key, a
counter-derived nonce) breaks confidentiality for the whole archive, not just the affected
records. An exception thrown here propagates unchanged out of `AuditAsync` — it is never turned
into a dropped record.

### Delivery contract

The drain worker posts one spooled envelope per request to `{EndpointBaseUrl}/audit` and reads the
response:

| Response | Meaning | Outcome |
|---|---|---|
| `2xx` | The receiver durably stored the record | Deleted from the spool |
| `400`, `409`, `413`, `415`, `422` | The record itself is defective — malformed, conflicting, oversized, wrongly typed, or unprocessable | Moved to `dead-letter/`, logged Critical, never retried |
| Anything else (`3xx`, `401`/`403`/`404`/`405`/`407`, `408`, `429`, `5xx`, a network failure, a timeout) | The receiver could not take the record right now, or the failure says nothing about the record itself | Retried with backoff |

`dead-letter/` is a sibling directory of the spool, and nothing in this package ever deletes from
it: it is the permanent evidence that a record never reached the audit store, and clearing it is a
manual, investigated action. A record reaches it by three routes, not only the table above: a
permanent rejection from the receiver, a spool file that cannot even be parsed as an envelope, or
one that still cannot be read from disk after `UnreadableRetryLimit` attempts — the last two never
contact the endpoint at all. All three flip the health check to `Unhealthy`.

### Health check

One combined health check (registered under the `audit` tag) reports the worst of four conditions:

| Condition | Status | Configurable via |
|---|---|---|
| Spool at ≥ 50% of cap (writes still succeed) | `SpoolWarnStatus` (default `Unhealthy`) | `EncryptedSpoolOptions.SpoolWarnStatus` |
| Spool at 100% of cap (new records are being dropped) | `SpoolFullStatus` (default `Unhealthy`) | `EncryptedSpoolOptions.SpoolFullStatus` |
| `dead-letter/` holds any record | `Unhealthy`, not configurable | — |
| Three delivery attempts in a row have failed (the run ends on the first that gets through) | `UnreachableStatus` (default `Degraded`) | `EncryptedSpoolOptions.UnreachableStatus` — the count of three is fixed |

### Metrics

Under the same `StepUpLogging.Audit` meter as the rest of audit logging:

- `audit_spool_depth` — records currently held in the spool.
- `audit_spool_bytes` — bytes currently held in the spool.
- `audit_spool_rejected_full_total` — records dropped because the spool was at its cap.
- `audit_spool_drained_total` — records the endpoint confirmed it stored.
- `audit_spool_drain_failures_total` — delivery attempts, unreadable spool files, and drain-cycle faults that did not get a record through (retried, not lost); it can move while the endpoint itself is perfectly healthy, e.g. one spool file the worker cannot yet open.
- `audit_spool_dead_lettered_total` — records moved to `dead-letter/`.
- `audit_encryption_failures_total` — `IAuditPayloadEncryptor` calls that threw (the exception still propagates; this only counts that it happened).

### The fsync cost

Every audit write through this sink costs one `fsync`: the record is flushed to the device, not
just the OS page cache, before `WriteAsync` returns, so a power loss right after cannot lose a
record the caller was already told was durable. That costs single-digit to tens of milliseconds on
ordinary SSDs, more on network storage, and it lands **on the business path** — the caller of
`AuditAsync`, not a background thread. Audit volume is usually small relative to overall traffic
(mutating operations, not every request), but account for it: nobody will guess that audit is the
source of the latency until they measure it.

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

**Config-Declared Sinks** — User-defined `Serilog:WriteTo` sinks in configuration now receive bypass-routed events (immediate logs, request summaries, pre-error buffer flushes) in addition to gated logs. If using a config File sink, set `shared: true` to prevent file lock contention between the two loggers writing to the same file (ADR 0003).

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
| `NeverStepUpCategories` | `["Microsoft.EntityFrameworkCore.Database.Command"]` | - | `SourceContext` prefixes the step-up never raises above `BaseLevel` (see below) |
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
| `RedactionRegexes` | `[]` | - | Regex patterns for redacting sensitive data. Always applied to request metadata and bodies; also applied to application log properties when `RedactLogEventProperties` is set — see [Security](#security) |
| `RedactLogEventProperties` | `false` | - | Opt-in: also sweep the string-valued scalar properties of application log events with `RedactionRegexes`, not just request metadata. See [Security](#security) |
| `AdditionalSensitiveHeaders` | `[]` | - | Custom header names to redact in request logging |
| `TrustForwardedHeaders` | `false` | - | When `true`, `ClientIp` is taken from the first `X-Forwarded-For` entry (v2 behavior). Only enable behind a proxy you control with `ForwardedHeadersMiddleware`. See [Security](#security). |
| `TreatServerErrorStatusAsError` | `true` | - | When `false`, a request completing with status >= 500 but **no** exception is logged at Warning instead of Error, so it does not trigger step-up. Set this in a reverse proxy / BFF where most 5xx are relayed from a backend. An unhandled exception is still Error. |
| **Service Identification** |
| `ServiceVersion` | `null` | `APP_VERSION` | Service version for enrichment |

### Categories the step-up never raises (`NeverStepUpCategories`)

When an error trips the step-up, every category is exported at `StepUpLevel` for the whole
window. That is usually what you want, but a few high-volume categories turn the window into a
flood. EF Core is the canonical case: it logs every executed SQL command under
`Microsoft.EntityFrameworkCore.Database.Command` at `Information`, so the first error in a
DB-backed service would export the application's entire SQL traffic for `DurationSeconds` — a
volume and cost spike that lands exactly during an incident, and one that carries the raw SQL
(with `EnableSensitiveDataLogging`, the parameter values too).

`NeverStepUpCategories` is a list of Serilog `SourceContext` prefixes that are pinned to
`BaseLevel` even while the switch is raised. A listed category is not silenced — its `Warning`
and `Error` events still export — only the step-up's extra verbosity is withheld from it.
Matching is ordinal: a `SourceContext` matches a prefix when it is equal to it, or when it
continues with a `.` (so `…Database.Command` also covers `…Database.Command.Internal`, but not
`…Database.CommandBuilder`). The default holds exactly one entry, the EF command category.

The list has no effect in `StepUpMode.AlwaysOn`: that mode never steps up, so there is nothing
to suppress, and a developer running it locally wants to see the SQL.

One caveat: the deny-list gates the export path only. The pre-error buffer is deliberately **not**
filtered — when it flushes on an error it still carries the SQL that led up to that error, and
unless you set `RedactLogEventProperties` it carries it **unredacted**: the SQL command text is a
property of an ordinary log event, not request metadata, so only that flag brings it in scope (see
[Security](#security)). Treat EF as a channel that can leak secrets: do not log sensitive values
through it.

To restore the pre-3.1.0 behaviour (step-up raises every category, including EF SQL), set the
list empty:

```json
{
  "SerilogStepUp": {
    "NeverStepUpCategories": []
  }
}
```

## Security

Three security properties are worth understanding before you deploy:

### Client IP is only as trustworthy as your proxy configuration

`X-Forwarded-For` is client-supplied and trivially spoofable. Setting `TrustForwardedHeaders:
true` blindly trusts its first entry as `ClientIp`, letting any caller forge the logged IP —
only enable it behind a proxy you fully control. By default the library does not trust XFF:
`ClientIp` comes from `HttpContext.Connection.RemoteIpAddress`. If you run behind a reverse
proxy and need the real client address, configure ASP.NET Core's
[`ForwardedHeadersMiddleware`](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
with your known proxies so `Connection.RemoteIpAddress` reflects the true client, and leave
`TrustForwardedHeaders` at `false`. The raw header is always logged (redacted) as
`ForwardedFor` for diagnostics.

### Redaction: always-on for request data, opt-in for application logs

`RedactionRegexes` is always applied to query strings, route values, headers, and request
bodies. On its own it does **not** scan the rendered text of arbitrary log messages — a secret
passed as a message-template argument (`logger.LogInformation("token={T}", secret)`) is left
alone.

Set `RedactLogEventProperties: true` to also sweep the string-valued scalar properties of
application log events with the same `RedactionRegexes`, so the example above **is** redacted
once the flag is on. The sweep reaches properties and nothing else. It does not touch
message-template text or exception messages: an interpolated `logger.LogInformation($"token={t}")`
bakes the value into the template and produces no property, so it is logged verbatim whatever the
flag says. It does not recurse into structures (`{@user}`), sequences or dictionaries. It skips
the twelve properties the library stamps itself (`TraceId`, `SourceContext`,
`ServiceInstanceId` and nine more — the `RedactLogEventProperties` XML doc lists them and the
reason for each). And it never sees the two events the library writes straight to the bypass
logger — the request summary and the startup level-ordering warning — which do not pass root
enrichment. Do not log secrets in interpolated message templates.

The flag has a running cost worth sizing before you enable it. The sweep is registered on the
root logger, and the root deliberately runs at `Verbose` so the pre-error buffer and the trigger
sink see every event — so the sweep runs on every event your application **emits**, not on the
smaller set that is actually exported. A `Debug` event that the level switch drops and that never
leaves the process is swept on the way in all the same. Per event the work is one regex replace per
configured pattern per string-valued scalar property, so it grows with the number of patterns,
the number of string properties on the event, and the length of their values. No figure is
published here: a measurement taken against the sample patterns in this README would not transfer
to yours. The levers are the ones you control — keep `RedactionRegexes` short, and each pattern
narrow and anchored rather than open-ended. With the flag off, or on with no patterns configured,
the enricher is not registered at all and there is no per-event cost.

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
