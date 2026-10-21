# Lukdrasil.StepUpLogging

[![NuGet](https://img.shields.io/nuget/v/Lukdrasil.StepUpLogging.svg)](https://www.nuget.org/packages/Lukdrasil.StepUpLogging/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Dynamic step-up logging for ASP.NET Core with Serilog** - automatically increase log verbosity when errors occur, with minimal performance overhead.

## Features

✅ **Step-up on errors** - Exports `Warning` and above normally; an `Error` raises the level to `Information` for 3 seconds  
✅ **Pre-error buffering** - The events of a failing request that were not exported are flushed with its error  
✅ **Quiet framework logs** - ASP.NET Core, HttpClient, Polly, EF Core and Npgsql progress lines stay at `Warning` by default  
✅ **OpenTelemetry-first** - OTLP export when a collector is configured, optional console (JSON or one-line text) and file sinks  
✅ **OpenTelemetry Activities and metrics** - Spans and counters for step-up, buffering and request logging  
✅ **Request logging** - Request completion events, optional per-request summary and request body capture  
✅ **Sensitive data redaction** - Sensitive headers always, plus regex redaction of paths, query strings, route values and bodies, and optionally of application log properties  
✅ **Fails fast on bad configuration** - Invalid values stop the host with the key named; unknown keys are warned at startup  
✅ **Audit logging** - Optional encrypted, durable audit spool  
✅ **.NET 10.0** - Built with modern C# 14 features

## Getting started

### 1. Install

```bash
dotnet add package Lukdrasil.StepUpLogging
```

### 2. Register

```csharp
using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);
builder.AddStepUpLogging();

var app = builder.Build();
app.UseStepUpRequestLogging();
// UseStaticFiles, UseRouting, authentication, endpoints...
app.Run();
```

`AddStepUpLogging()` replaces the host's logging with Serilog and reads the `SerilogStepUp` configuration
section. `UseStepUpRequestLogging()` writes one completion event per request; call it right after
`builder.Build()`, before `UseStaticFiles`, `UseRouting`, authentication and anything that reads the request
body or ends the request early, because it sees only the requests that reach it. Log through `ILogger<T>`
as usual.

### 3. Set what your deployment needs

Nothing is required. What you set depends on where the logs go:

| Deployment | Set | Why |
|---|---|---|
| Logs go to an OpenTelemetry collector | `OTEL_EXPORTER_OTLP_ENDPOINT`, and `OTEL_SERVICE_NAME` | The endpoint turns OTLP export on; the service name identifies the app. Aspire sets both. Add `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` for an HTTP collector and `OTEL_EXPORTER_OTLP_HEADERS` for authentication |
| Logs are read from the console (container stdout) | `SerilogStepUp:EnableConsoleLogging` = `true` | No output is on by default without a collector. Add `ConsoleFormat` = `Text` for one readable line per event instead of JSON |
| The app runs behind a reverse proxy you control | `SerilogStepUp:TrustForwardedHeaders` = `true`, with `ForwardedHeadersMiddleware` | Otherwise `ClientIp` is the proxy's address |
| The app is a reverse proxy or BFF | `SerilogStepUp:TreatServerErrorStatusAsError` = `false` | A 5xx relayed from a backend then does not step the app up |

At startup the library writes one line with what it ended up using, for example
`StepUp logging: Mode Auto, BaseLevel Warning, StepUpLevel Information for 3 s; outputs OTLP http://collector:4317; framework defaults Recommended; request summary off`.
Check it after the first deployment. `outputs none` means no log leaves the process.

### What you get by default

| Behaviour | Default |
|---|---|
| Normal export | `Warning`, `Error` and `Fatal` from every category |
| On an `Error` or `Fatal` | Every category exports `Information` and above for 3 s. Each further error restarts the 3 s, with no upper limit |
| Events before an error | Each request's events at `Information` and above that were not exported are held in memory (100 per request, 1024 requests) and exported with the request's error; otherwise dropped |
| Framework categories | ASP.NET Core hosting, routing, MVC, HTTP results and static files, HttpClient, Polly, EF Core commands and infrastructure, and Npgsql commands export at `Warning` and above even during a step-up, and are not flushed by an error. CORS and the YARP forwarder export at `Warning` but are flushed |
| OTLP export | On when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, off otherwise |
| Console and file output | Off |
| Request completion event | One per request at `Information`, `Warning` for 4xx, `Error` for 5xx and unhandled exceptions; exported like any other event, so normally only from `Warning` |
| Per-request summary | Off |
| Request body capture | Off |
| Excluded paths | `/healthz`, `/metrics`, `/health` |
| Redaction | `Authorization`, `Proxy-Authorization`, `Cookie`, `X-API-Key`, `X-Auth-Token`, `X-Access-Token` and other credential headers always; no regex patterns |
| Client IP | The connection's remote address; `X-Forwarded-For` is logged as `ForwardedFor` but not trusted |
| Enrichment | `Application`, `Environment`, `MachineName`, trace and span ids, structured exception details |
| Invalid configuration | The host does not start; the error names each failing key and its value. Unknown keys under `SerilogStepUp` are logged as warnings |

### Typical setups

**Production with a collector.** No `SerilogStepUp` settings:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_SERVICE_NAME=orders-api
OTEL_RESOURCE_ATTRIBUTES=deployment.environment=production
```

**Container that logs to stdout only:**

```json
{ "SerilogStepUp": { "EnableConsoleLogging": true, "ConsoleFormat": "Text" } }
```

**Local development, everything visible:**

```json
{
  "SerilogStepUp": {
    "Mode": "AlwaysOn",
    "StepUpLevel": "Debug",
    "EnableConsoleLogging": true,
    "ConsoleFormat": "Text"
  }
}
```

Put it in `appsettings.Development.json` so production keeps the defaults.

**Aspire.** Call `AddStepUpLogging()` from `AddServiceDefaults()` after `ConfigureOpenTelemetry()`, and do not also
call `AddSerilog`. When the OpenTelemetry SDK is already registered, the library leaves the exporter setup to it.

```csharp
public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
{
    builder.ConfigureOpenTelemetry();
    builder.AddDefaultHealthChecks();
    builder.Services.AddServiceDiscovery();
    builder.AddStepUpLogging();
    return builder;
}
```

## Changing the defaults

### Where options are set

Options bind from the `SerilogStepUp` section, so `appsettings.json`, `appsettings.{Environment}.json` and
environment variables all work. An environment variable joins the path with `__`
(`SerilogStepUp__DurationSeconds=300`, `SerilogStepUp__ExcludePaths__0=/health`) and wins over the JSON files.
Values set in code win over configuration:

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.Mode = builder.Environment.IsDevelopment() ? StepUpMode.AlwaysOn : StepUpMode.Auto;
});
```

A second callback extends the Serilog configuration. It turns off the optimisation that skips enriching events
nobody exports, so use it only when you need it:

```csharp
builder.AddStepUpLogging(
    opts => opts.StepUpLevel = "Debug",
    (services, lc) => lc.Enrich.WithProperty("Region", "eu-west"));
```

Levels are written as names: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. A list option you
configure replaces its default list, and `[]` empties it.

These settings have no effect:

- `Logging:LogLevel`: Serilog replaces the `Microsoft.Extensions.Logging` providers and their filters. Use
  `BaseLevel`, `CategoryFloors` and `FrameworkCategoryDefaults`.
- `Serilog:MinimumLevel:Default`: the root logger always runs at `Verbose` so the buffer and the trigger see every
  event. `Serilog:MinimumLevel:Override` entries still apply, and drop events before the buffer sees them.
- `OtlpEndpoint` and other OTLP keys under `SerilogStepUp`: the OTLP settings are the `OTEL_*` environment variables.

### What a change means in production

**Step-up**

| Option | Default | Effect of changing it in production |
|---|---|---|
| `Mode` | `Auto` | `Disabled` never steps up: you keep only `Warning` and above plus the events flushed by an error, at the lowest volume. `AlwaysOn` exports `StepUpLevel` all the time and ignores the framework defaults and `NeverStepUpCategories`: volume grows with traffic, so keep it for development. `Diagnostic` exports `DiagnosticLevel` (`Debug`) for `DiagnosticDurationMinutes` (30, at most 120) after each start, then runs as `Auto`; use it for one deployment while investigating |
| `BaseLevel` | `Warning` | `Information` exports every application `Information` event all the time, a large and steady volume increase. `Error` hides warnings until something fails |
| `StepUpLevel` | `Information` | `Debug` exports `Debug` from every category during a step-up window, and the buffer then holds `Debug` too, so an incident produces much more data and the buffer uses more memory |
| `DurationSeconds` | `3` | The default keeps the step-up to the requests in flight when the error happens; the failing request's own earlier events come from the pre-error buffer. Longer, such as `180`, also exports `Information` from every request for that long after an error, so the requests that follow an incident have full detail, at a volume cost that grows with traffic. A sustained failure keeps restarting the window either way |
| `MaxContinuousStepUpSeconds` | `0` (no cap) | Set it, at least `DurationSeconds`, when a sustained failure must not keep the detail on for hours. After the cap, triggers are ignored for `StepUpCooldownSeconds` (`300`) |
| `NeverTriggerCategories` | none | Errors from listed categories still export and flush their request, but do not raise the level for everyone. Use it for a dependency that fails often and loudly |

**Noise control**

| Option | Default | Effect of changing it in production |
|---|---|---|
| `FrameworkCategoryDefaults` | `Recommended` | `None` brings back framework `Information` lines (request started and finished, endpoint executing, every HttpClient and SQL call) in every step-up window and in every error flush, typically several lines per request. Dashboards built on those lines need it |
| `CategoryFloors` | none | `"MyApp.Chatty": "Warning"` keeps a category at `Warning` even during a step-up. A floor never goes above `Warning`, so no error is hidden. A key also overrides the framework default it matches |
| `PreErrorBufferExcludeCategories` | none | Listed categories are never flushed by an error, only exported live |
| `AlwaysExportCategories` | none | Listed categories export at every level all the time; volume grows with their traffic |

See [Category Control](#category-control) for how the category options combine.

**Outputs**

| Option | Default | Effect of changing it in production |
|---|---|---|
| `EnableOtlpExporter` | On when `OTEL_EXPORTER_OTLP_ENDPOINT` is set | `true` without the variable exports to `http://localhost:4317`, for a collector sidecar. `false` stops OTLP export even when the variable is set |
| `EnableConsoleLogging` | `false` | `true` writes every exported event to stdout as well. With OTLP on and a platform that also ships stdout, each event is stored twice |
| `ConsoleFormat` | `Json` | `Text` writes one readable line per event with the keyed trace id; log processors that parse JSON from stdout lose the structured properties. OTLP and file output stay structured |
| `ConsoleTraceFormat` | `TraceId` | `Traceparent` writes the `Text` line's trace token as a W3C `traceparent=00-<trace id>-<span id>-<flags>` instead of `TraceId=<trace id>`, for pasting into a trace lookup |
| `ConsoleTextProperties` | `["Jti"]` | Properties written after the message in `Text` format, so identity stays on the console without OTLP |

The file sink is enabled by the `logFilePath` parameter of `AddStepUpLogging`: daily files, 30 kept.

**Requests**

| Option | Default | Effect of changing it in production |
|---|---|---|
| `AlwaysLogRequestSummary` | `false` | `true` exports one summary per request at `RequestSummaryLevel` whatever the level: an access log whose volume equals your traffic |
| `CaptureRequestBody` | `false` | `true` adds POST, PUT and PATCH bodies, up to `MaxBodyCaptureBytes` characters (16384), while stepped up or when the response is 5xx. Bodies can carry personal data: add `RedactionRegexes` for it. Each request body is buffered in memory |
| `ExcludePaths` | `/healthz`, `/metrics`, `/health` | A configured list replaces these, so list them again if you still want them excluded, or set `AdditionalExcludePaths` to add paths and keep the defaults |
| `AdditionalExcludePaths` | none | Added to `ExcludePaths` (to the defaults when `ExcludePaths` is not configured), so `SerilogStepUp__AdditionalExcludePaths__0=/.well-known/*` adds one path without restating the defaults |
| `TrustForwardedHeaders` | `false` | `true` takes `ClientIp` from `X-Forwarded-For`, which any client can forge unless your proxy overwrites it |
| `TreatServerErrorStatusAsError` | `true` | `false` logs a 5xx without an exception at `Warning`, so it no longer steps up or flushes. Unhandled exceptions are still `Error` |

**Buffer, redaction and enrichment**

| Option | Default | Effect of changing it in production |
|---|---|---|
| `EnablePreErrorBuffering` | `true` | `false` saves the buffer memory but an error then arrives without the events that led to it |
| `PreErrorBufferSize`, `PreErrorMaxContexts` | `100`, `1024` | The memory bound: at most size × contexts held events. Too small loses the oldest events of a long request or the requests of a busy instance |
| `RedactionRegexes` | none | Each pattern is applied to request paths, query strings, route values, headers and bodies, at a CPU cost per request |
| `RedactLogEventProperties` | `false` | `true` also applies the patterns to the string properties of every exported application event, at a CPU cost per event |
| `RedactionExemptProperties` | none | Listed property names, such as `Jti`, skip the patterns on every event and on the request summary. A secret logged under a listed name is exported in clear; request paths, query strings, headers, route values and bodies are always redacted |
| `ServiceVersion`, `ServiceInstanceId` | none | Added to every event when set |
| `EnrichWithThreadId`, `EnrichWithProcessId`, `EnrichWithCallStack` | `false` | More properties on every event; call stacks are expensive |

Every option, with its default, is listed in [Configuration Options](#configuration-options).

### OpenTelemetry environment variables

| Environment variable | Purpose | Default |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector endpoint; setting it turns OTLP export on | none (OTLP export off) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` or `http/protobuf` | `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers, `key1=value1,key2=value2` | none |
| `OTEL_SERVICE_NAME` | Service name | `unknown_service:<process name>` |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes, `key1=value1,key2=value2` | none |

Keys and values in `OTEL_EXPORTER_OTLP_HEADERS` and `OTEL_RESOURCE_ATTRIBUTES` are **percent-decoded** per the
OpenTelemetry specification, so `Authorization=Basic%20QWxhZGRpbg%3D%3D` reaches the collector as
`Basic QWxhZGRpbg==`. A literal comma inside a value cannot be written in this format.

### Startup report and validation

When the logger is built, the library writes through its own output, whatever the current level:

- One `Information` line with the effective configuration. The OTLP endpoint is shown as scheme, host and port only.
- A `Warning` for each key under `SerilogStepUp` that matches no option, such as a misspelled `BaseLvel` or
  `OtlpEndpoint`. The key is ignored.
- A `Warning` for an option set to a value that has no effect: `ConsoleFormat` with `EnableConsoleLogging`
  `false`, or `RedactLogEventProperties` with no `RedactionRegexes`.

An invalid value stops the host at startup with an `OptionsValidationException` that names each failing key and
the value it got, for example
`SerilogStepUp:BaseLevel must be a Serilog level name (Verbose, Debug, Information, Warning, Error, Fatal), got 'Warnign'.`

## Request Logging

The `UseStepUpRequestLogging()` middleware enriches each request with detailed context:

### Captured Information

- **RequestPath** - Normalized request path (trailing slashes removed, redacted based on `RedactionRegexes`)
- **QueryString** - Query parameters (redacted based on `RedactionRegexes`)
- **RouteParameters** - Route parameter values (e.g., `{id}`, `{role}`), redacted based on `RedactionRegexes`; a value whose text sits inside a redacted part of the path becomes `[REDACTED]`
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

### Request summary

When "AlwaysLogRequestSummary" is enabled, the middleware emits a single structured summary event at the configured "RequestSummaryLevel" for every completed HTTP request. The summary contains:

- **HTTP method** - GET, POST, etc.
- **Request path** - URL path (trailing slashes normalized, redacted based on `RedactionRegexes`)
- **Response status code** - 200, 404, 500, etc.
- **Elapsed milliseconds** - Request duration
- **Trace ID** - Optional trace/correlation id
- **UserAgent** - Client User-Agent header (for client identification), redacted via `RedactionRegexes`
- **ClientIp** - Client IP address from `Connection.RemoteIpAddress` by default (see [Security](#security)); when `TrustForwardedHeaders` is enabled, the first `X-Forwarded-For` entry instead
- **ForwardedFor** - The raw `X-Forwarded-For` header value (redacted), present only when the header is sent

Summary events are marked with the "IsRequestSummary" property and are processed by the library's SummarySink so they are exported independently of the StepUp level switch (base Warning) and the normal step-up flow.

### Example Request Summary Log

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

### Which requests count as errors

The request-completion event's level is what trips the step-up and flushes the pre-error buffer,
so it is deliberately narrow:

| Outcome | Level |
|---|---|
| Excluded path (`ExcludePaths`, `AdditionalExcludePaths`) | Verbose |
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

### IP Address Detection

By default the library takes `ClientIp` from `HttpContext.Connection.RemoteIpAddress` and
**does not** trust the `X-Forwarded-For` (XFF) header, because that header is client-supplied
and spoofable. When XFF is present, its raw value is still logged separately as `ForwardedFor`
(redacted) for diagnostics.

1. **Default (`TrustForwardedHeaders: false`)** - `ClientIp` = `Connection.RemoteIpAddress`; XFF is ignored for `ClientIp` but surfaced as `ForwardedFor`.
2. **`TrustForwardedHeaders: true`** - `ClientIp` = first `X-Forwarded-For` entry when present (v2 behavior). Only enable this behind a reverse proxy you control **and** with `ForwardedHeadersMiddleware` configured. See [Security](#security).
3. **Graceful degradation** - Omits `ClientIp` if detection fails.

To customise where summaries are written, provide a dedicated summary logger in DI when calling AddStepUpLogging, or configure the default sinks; the library enforces a single DI-managed summary logger to avoid unmanaged CreateLogger instances.


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
4. **Exactly once**: Only held-back events are buffered, meaning events the step-up gate did not export. An event is exported at most once: either immediately at the current level, or by the flush when an error follows. Events from `NeverStepUpCategories` are still held back and flushed on an error.
5. **Shutdown**: Held-back events still in the buffer when the application shuts down without an error are dropped, not exported.
6. **Excluded categories**: Events below `Warning` from a `PreErrorBufferExcludeCategories` prefix are never held, so an error does not export them. Other categories of the same trace still flush.
7. **Framework categories**: With `FrameworkCategoryDefaults` `Recommended` (the default), framework categories such as Hosting, Routing, `HttpClient` and EF Core commands are not held below `Warning` either (see [Framework category defaults](#framework-category-defaults)).

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
| `PreErrorBufferExcludeCategories` | `[]` | `SourceContext` prefixes whose events below `Warning` are never buffered (see below) |

**Keep framework categories out of the flush:**

`PreErrorBufferExcludeCategories` lists `SourceContext` prefixes whose events below `Warning` the
buffer never holds, so an error in their trace does not export them. Use it to keep framework
categories at `Warning` in `Auto` mode even when a trace fails. Matching follows the same prefix rule
as `NeverStepUpCategories`, and blank entries are ignored.

```json
{
  "SerilogStepUp": {
    "PreErrorBufferExcludeCategories": [ "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Hosting", "Polly" ]
  }
}
```

- Only the buffer is filtered. An event the current level or a `CategoryFloors` floor lets through
  is still exported, so Diagnostic mode still exports these categories at `DiagnosticLevel`.
- A listed category's `Warning` events are still held and flushed, for example at `BaseLevel` `Error`.
- Application categories in the same trace still flush.

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
spool, and delivered to a receiver you configure, one `POST` per record by default (or a batch of
them, see `DeliveryBatchSize`). A crash between "the
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

The drain worker is the only retrying layer on the delivery path. `AddEncryptedSpoolAuditSink`
removes every resilience handler registered on the delivery `HttpClient` before it, including one
inherited from `ConfigureHttpClientDefaults(h => h.AddStandardResilienceHandler())`, so the audit
POST is never retried behind the worker's back and `DeliveryTimeout` is the only timeout. To add a
custom handler to that client, register it after `AddEncryptedSpoolAuditSink`.

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
| `SpoolFullRecheckInterval` | 1 s | How often a spool found full is measured against the disk again (a refused write costs no directory scan in between), and the window in which dropped records are logged as one Critical summary instead of one line each. Must be greater than zero. |
| `DrainInterval` | 5 s | How often the drain worker looks for records, and the first wait it backs off from. |
| `MaxDrainBackoff` | 5 min | Ceiling the wait doubles up to while the endpoint keeps failing. |
| `DeliveryBatchSize` | 1 | Most records delivered in one request, 1 to 1024. At 1 each record goes to `{EndpointBaseUrl}/audit`. Above 1, a run of records goes as one JSON array to `{EndpointBaseUrl}/audit/batch`, which your receiver must implement first (see [Delivery contract](#delivery-contract)). |
| `DeliveryTimeout` | 30 s | Timeout for one delivery attempt — a whole batch, when batching. A timeout is transient — retried, never dead-lettered. |
| `ShutdownDrainTimeout` | 5 s | How long a stopping host waits for the drain worker. Nothing is lost when it runs out; the next start delivers. |
| `UnreadableRetryLimit` | 10 | Drain cycles a spool file may fail to even be opened before it is dead-lettered as unreadable. |
| `OldestRecordMaxAge` | none (signal off) | How old the oldest spooled record may get before the health check reports `OldestRecordStatus`. Must be greater than zero when set. See [Health check](#health-check). |
| `OldestRecordStatus` | `Degraded` | The status reported once the oldest record is older than `OldestRecordMaxAge`. |
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

The drain worker posts the spooled bytes unchanged as `application/json; charset=utf-8`: one envelope
per request to `{EndpointBaseUrl}/audit`, or, with `DeliveryBatchSize` above 1, up to that many as one
JSON array to `{EndpointBaseUrl}/audit/batch` (a run of one still goes to `/audit`). It reads the
response:

| Response | Meaning | Outcome |
|---|---|---|
| `2xx` | The receiver durably stored the record (for a batch: every record in it) | Deleted from the spool |
| `400`, `409`, `413`, `415`, `422` | The record itself is defective — malformed, conflicting, oversized, wrongly typed, or unprocessable | Moved to `dead-letter/`, logged Critical, never retried. For a batch, it says one record is defective, not which: the batch is redelivered record by record to `/audit`, and only the record rejected on its own is dead-lettered |
| Anything else (`3xx`, `401`/`403`/`404`/`405`/`407`, `408`, `429`, `5xx`, a network failure, a timeout) | The receiver could not take the record right now, or the failure says nothing about the record itself | Retried with backoff |

The status code is the whole contract: the worker reads nothing else from the response, not even a
problem+json body. So when a record names a key the receiver does not know, the status code is how the
receiver says which kind of unknown it is: answer `503` while the keys are not loaded yet (a cold start,
a key store that is still syncing), and the record waits and is retried; answer `422` when the key is
gone for good, and the record is dead-lettered. A `404` or a `401` for an unknown key would be retried
for ever, and a `400` would dead-letter a record that a key loaded a minute later could have stored.

A batch is one delivery: the receiver must store it whole or reject it whole, so that a `2xx` is true
of every record in it, and must still deduplicate on `EventId`. Anything transient resends the whole
batch.

A `413` (or any other permanent rejection) on a batch makes the worker fall back to per-record delivery
for that batch, every cycle it is retried, so keep `DeliveryBatchSize` small enough that a full batch
fits the receiver's request body limit.

`dead-letter/` is a sibling directory of the spool, and nothing in this package ever deletes from
it: it is the permanent evidence that a record never reached the audit store, and clearing it is a
manual, investigated action. A record reaches it by three routes, not only the table above: a
permanent rejection from the receiver, a spool file that cannot even be parsed as an envelope, or
one that still cannot be read from disk after `UnreadableRetryLimit` attempts — the last two never
contact the endpoint at all. All three flip the health check to `Unhealthy`, and the
`audit_spool_dead_lettered_total` counter tells them apart by its `reason` tag (see [Metrics](#metrics)).

### Health check

One combined health check (registered under the `audit` tag) reports the worst of five conditions:

| Condition | Status | Configurable via |
|---|---|---|
| Spool at ≥ 50% of cap (writes still succeed) | `SpoolWarnStatus` (default `Unhealthy`) | `EncryptedSpoolOptions.SpoolWarnStatus` |
| Spool at 100% of cap (new records are being dropped) | `SpoolFullStatus` (default `Unhealthy`) | `EncryptedSpoolOptions.SpoolFullStatus` |
| `dead-letter/` holds any record | `Unhealthy`, not configurable | — |
| Three delivery attempts in a row have failed (the run ends on the first that gets through) | `UnreachableStatus` (default `Degraded`) | `EncryptedSpoolOptions.UnreachableStatus` — the count of three is fixed |
| The oldest spooled record has waited longer than `OldestRecordMaxAge` (delivery is stuck behind it, whatever the cause; off unless the option is set) | `OldestRecordStatus` (default `Degraded`) | `EncryptedSpoolOptions.OldestRecordMaxAge`, `EncryptedSpoolOptions.OldestRecordStatus` |

### Metrics

Under the same `StepUpLogging.Audit` meter as the rest of audit logging:

- `audit_spool_depth` — records currently held in the spool.
- `audit_spool_bytes` — bytes currently held in the spool.
- `audit_spool_oldest_age_seconds` — age in whole seconds of the oldest record the drain worker is waiting on, 0 when the spool is empty. It keeps growing while delivery is stuck behind one record, however empty the spool still is: alert on it rather than on depth.
- `audit_spool_rejected_full_total` — records dropped because the spool was at its cap.
- `audit_spool_drained_total` — records the endpoint confirmed it stored.
- `audit_spool_drain_failures_total` — delivery attempts, unreadable spool files, and drain-cycle faults that did not get a record through (retried, not lost); it can move while the endpoint itself is perfectly healthy, e.g. one spool file the worker cannot yet open.
- `audit_spool_dead_lettered_total` — records moved to `dead-letter/`, tagged `reason`: `rejected` (the receiver refused it), `corrupt` (the spool file does not parse) or `unreadable` (the file could not be read within `UnreadableRetryLimit`).
- `audit_encryption_failures_total` — `IAuditPayloadEncryptor` calls that threw (the exception still propagates; this only counts that it happened).

### The fsync cost

Every audit write through this sink costs one `fsync`: the record is flushed to the device, not
just the OS page cache, before `WriteAsync` returns, so a power loss right after cannot lose a
record the caller was already told was durable. That costs single-digit to tens of milliseconds on
ordinary SSDs, more on network storage, and it lands **on the business path** — the caller of
`AuditAsync`, not a background thread. Audit volume is usually small relative to overall traffic
(mutating operations, not every request), but account for it: nobody will guess that audit is the
source of the latency until they measure it.

## Performance

Benchmark results (k6 load test, 50 VUs, 3 minutes):

| Metric | Standard Serilog | StepUpLogging | Improvement |
|--------|------------------|---------------|-------------|
| Avg Latency | 1.19 ms | 0.98 ms | **-18%** ⚡ |
| P95 Latency | 2.12 ms | 1.64 ms | **-23%** ⚡ |
| Throughput | 165.77 req/s | 166.21 req/s | **+0.3%** |

See full [performance test results](tests/k6/performance_test_results.md).

Per-component BenchmarkDotNet benchmarks (log pipeline, sinks, enrichers, redaction, request logging, audit CPU and the encrypted spool), a recorded baseline and a ranked list of hot spots are in [docs/performance](docs/performance/README.md).

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
- `ConsoleFormat` selects the format: `Json` (default, CompactJSON with every property) or `Text`, one
  readable line per event. Only the console changes: OTLP and the file sink stay structured.

```text
[15:54:07.218 WRN] TraceId=c4fed5a2d96d1640c013ae014f5d162d PersonalTokens.DataAccess.KeycloakUserTokenIssuer: The PAT user token mint was refused with HTTP 400 and OAuth error invalid_scope: Invalid scopes: audience.
[15:54:07.221 ERR] TraceId=c4fed5a2d96d1640c013ae014f5d162d Serilog.AspNetCore.RequestLoggingMiddleware: HTTP POST /api/pat/exchange responded 502 in 36.1778 ms Jti=4ebece0627d94b5f9f37c969bb17d27f
```

`Text` writes the time, level, trace id as `TraceId=<trace id>`, `SourceContext` and the rendered message, then each
`ConsoleTextProperties` entry the event has as `Name=value`, with an exception on the following
lines. Other properties that are not in the message template are not written to the console. Line
breaks inside the message and the values are written as `\r` and `\n`, so a logged value cannot
forge a console line.

`ConsoleTraceFormat: Traceparent` writes the trace token as the W3C
`traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-01` instead, so a line can be pasted
into a trace lookup. The flags come from the event's `TraceFlags` and are `00` when it has none; an
event without a span id keeps `TraceId=`, and an event without a trace id has no trace token.

`ConsoleTextProperties` defaults to `["Jti"]`, the token id the request summary carries, so the
caller's identity stays on the console when OTLP is not available. Add your own identity
properties; a configured list replaces the default, so keep `Jti` in it if you still want it.

```json
{
  "SerilogStepUp": {
    "EnableConsoleLogging": true,
    "ConsoleFormat": "Text",
    "ConsoleTextProperties": [ "Jti", "UserId", "ClientId" ]
  }
}
```

**Optional: File Sink** (Archival/Compliance)
- Daily rolling files with 30-day retention
- Enable via `logFilePath` parameter in `AddStepUpLogging()`

**Config-Declared Sinks** — User-defined `Serilog:WriteTo` sinks in configuration now receive bypass-routed events (immediate logs, request summaries, pre-error buffer flushes) in addition to gated logs. If using a config File sink, set `shared: true` to prevent file lock contention between the two loggers writing to the same file (ADR 0003).

## Configuration Options

| Option | Default | Environment Variable | Description |
|---|---|---|---|
| **Step-Up Behavior** |
| `Mode` | `Auto` | - | Step-up mode: `Auto`, `AlwaysOn`, `Disabled`, `Diagnostic` |
| `BaseLevel` | `"Warning"` | - | Normal log level |
| `StepUpLevel` | `"Information"` | - | Elevated log level during step-up |
| `DurationSeconds` | `3` | - | How long step-up remains active (Auto mode) |
| `MaxContinuousStepUpSeconds` | `0` (disabled) | - | Upper bound on a single continuous step-up window; forces a step-down and opens a cooldown when exceeded. `0` disables the cap. Must be `0` or `>= DurationSeconds`. |
| `StepUpCooldownSeconds` | `300` | - | Seconds triggers are ignored after the cap forces a step-down; ignored when the cap is disabled |
| `DiagnosticLevel` | `"Debug"` | - | Level the switch sits at while Diagnostic mode is active |
| `DiagnosticDurationMinutes` | `30` | - | How long Diagnostic mode lasts after startup, from 1 to 120 minutes |
| `NeverStepUpCategories` | `["Microsoft.EntityFrameworkCore.Database.Command"]` (applied only when not configured) | - | `SourceContext` prefixes the step-up never raises above `BaseLevel` (see below). A configured list replaces the default, `[]` pins nothing |
| `CategoryFloors` | `{}` | - | `SourceContext` prefix to minimum level; a matching category exports at `max(switch, floor)`. Floors are capped at `Warning` (see Category floors) |
| `FrameworkCategoryDefaults` | `Recommended` | `SerilogStepUp__FrameworkCategoryDefaults` | Built-in `Warning` floors for framework categories, applied when no `CategoryFloors` key matches; `None` turns them off (see Framework category defaults) |
| `DiagnosticExemptCategories` | `[]` | - | Floor prefixes that keep their floor during Diagnostic mode; each must be matched by a `CategoryFloors` key or, with `FrameworkCategoryDefaults` `Recommended`, a framework default prefix |
| `AlwaysExportCategories` | `[]` | - | `SourceContext` prefixes whose events are exported once, at any level, through the immediate path (see Always-export categories) |
| `NeverTriggerCategories` | `[]` | - | `SourceContext` prefixes whose `Error`/`Fatal` events never trigger step-up; they still export and flush their trace buffer (see Never-trigger categories) |
| **Pre-Error Buffering** |
| `EnablePreErrorBuffering` | `true` | - | Enable/disable pre-error buffering |
| `PreErrorBufferSize` | `100` | - | Max events per request before oldest are dropped |
| `PreErrorMaxContexts` | `1024` | - | Max concurrent request contexts; older ones are evicted |
| `PreErrorBufferExcludeCategories` | `[]` | - | `SourceContext` prefixes whose events below `Warning` are never buffered, so an error flush does not export them (see Pre-Error Buffering) |
| **OpenTelemetry Instrumentation** |
| `EnableActivityInstrumentation` | `true` | - | Enable/disable Activity creation (default-enabled, opt-out) |
| **OpenTelemetry** |
| `EnableOtlpExporter` | `true` when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, else `false` | - | Export logs to the OTLP endpoint; `true` without the variable exports to `http://localhost:4317` |
| (Endpoint/Protocol) | (env only) | `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP configuration (environment variables only) |
| **Additional Sinks** |
| `EnableConsoleLogging` | `false` | - | Enable console output (dev scenarios) |
| `ConsoleFormat` | `Json` | `SerilogStepUp__ConsoleFormat` | Console format: `Json` (CompactJSON) or `Text` (one line with time, level, keyed trace id, `SourceContext` and message); OTLP and file stay structured |
| `ConsoleTraceFormat` | `TraceId` | `SerilogStepUp__ConsoleTraceFormat` | Trace token of the `Text` console line: `TraceId` (`TraceId=<trace id>`) or `Traceparent` (W3C `traceparent=00-<trace id>-<span id>-<flags>`) |
| `ConsoleTextProperties` | `["Jti"]` | - | Properties written after the message in the `Text` console format as `Name=value` when the event has them; a configured list replaces the default |
| **Enrichers** |
| `EnrichWithExceptionDetails` | `true` | - | Enrich logs with structured exception details |
| `StructuredExceptionDetails` | `true` | - | Second switch for the same enricher; exception details are added only when both are `true` |
| `EnrichWithThreadId` | `false` | - | Include thread ID in log events |
| `EnrichWithProcessId` | `false` | - | Include process ID in log events |
| `EnrichWithMachineName` | `true` | - | Include machine name in log events |
| `EnrichWithCallStack` | `false` | - | Include call-stack information using Serilog.Enrichers.CallStack (https://github.com/hokagedami/serilog-stacktrace-enricher) |
| `EnrichWithEnvironment` | `true` | - | Include environment name (Development/Production) |
| **Request Logging** |
| `AlwaysLogRequestSummary` | `false` | - | Export one summary per request at `RequestSummaryLevel`, whatever the current level |
| `RequestSummaryLevel` | `"Information"` | - | Level of the request summary when `AlwaysLogRequestSummary` is on |
| `CaptureRequestBody` | `false` | - | Capture POST/PUT/PATCH bodies during step-up |
| `MaxBodyCaptureBytes` | `16384` | - | Max bytes to capture from request body |
| `ExcludePaths` | `["/healthz", "/metrics", "/health"]` (applied only when not configured) | - | Paths to exclude from logging. A configured list replaces the defaults, `[]` excludes nothing |
| `AdditionalExcludePaths` | `[]` | `SerilogStepUp__AdditionalExcludePaths__0` | Paths appended to the effective `ExcludePaths`, same matching; the defaults stay unless `ExcludePaths` is configured |
| `RedactionRegexes` | `[]` | - | Regex patterns for redacting sensitive data. Always applied to request metadata and bodies; also applied to application log properties when `RedactLogEventProperties` is set — see [Security](#security) |
| `RedactionExemptProperties` | `[]` | `SerilogStepUp__RedactionExemptProperties__0` | Property names (ordinal, case-sensitive) whose values are never redacted: the `RedactLogEventProperties` sweep skips them and `Jti` on the request summary keeps its value when listed. Request inputs are always redacted. See [Security](#security) |
| `RedactLogEventProperties` | `false` | - | Opt-in: also sweep the string-valued and URI-like (`PathString`, `QueryString`, `HostString`, `Uri`) scalar properties of application log events with `RedactionRegexes`, not just request metadata. See [Security](#security) |
| `AdditionalSensitiveHeaders` | `[]` | - | Custom header names to redact in request logging |
| `TrustForwardedHeaders` | `false` | - | When `true`, `ClientIp` is taken from the first `X-Forwarded-For` entry (v2 behavior). Only enable behind a proxy you control with `ForwardedHeadersMiddleware`. See [Security](#security). |
| `TreatServerErrorStatusAsError` | `true` | - | When `false`, a request completing with status >= 500 but **no** exception is logged at Warning instead of Error, so it does not trigger step-up. Set this in a reverse proxy / BFF where most 5xx are relayed from a backend. An unhandled exception is still Error. |
| **Hosting** |
| `SetStaticLogger` | `false` | - | When `true`, the built logger is assigned to Serilog's static `Log.Logger` and host dispose calls `Log.CloseAndFlush()` (pre-5.0.0 behavior). By default `Log.Logger` is left unchanged: log through DI `ILogger<T>`. Read once at registration. See [MIGRATION.md](MIGRATION.md#migrating-to-v500). |
| **Service Identification** |
| `ServiceVersion` | `null` | - | `ServiceVersion` property on every event; not added when unset |
| `ServiceInstanceId` | `null` | - | `ServiceInstanceId` property on every event; not added when unset |

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

`StepUpMode.Diagnostic` also lifts the pin for its whole window. With the default list, EF
`Database.Command` SQL (and its parameters when `EnableSensitiveDataLogging` is on) is exported
for up to 120 minutes. To keep it quiet during the window, add a `CategoryFloors` entry for the
category and a `DiagnosticExemptCategories` entry for it (see [Diagnostic mode](#diagnostic-mode)).

One caveat: the deny-list gates the export path only. The pre-error buffer is deliberately **not**
filtered by it. With `FrameworkCategoryDefaults` `Recommended` (the default) the EF command category is
kept out of the buffer anyway (see [Framework category defaults](#framework-category-defaults)). With
`None`, when the buffer flushes on an error it still carries the SQL that led up to that error, and
unless you set `RedactLogEventProperties` it carries it **unredacted**: the SQL command text is a
property of an ordinary log event, not request metadata, so only that flag brings it in scope (see
[Security](#security)). Treat EF as a channel that can leak secrets: do not log sensitive values
through it.

A configured list replaces the default entry. To restore the pre-3.1.0 behaviour (step-up raises
every category, including EF SQL), set it empty:

```json
{ "SerilogStepUp": { "NeverStepUpCategories": [] } }
```

## Category Control

Each category option takes `SourceContext` prefixes and does one thing:

| You want a category to | Option |
|---|---|
| export at `Warning` and above even during a step-up | `CategoryFloors` (`"Category": "Warning"`) |
| keep its `BaseLevel` during a step-up, but still flush before an error | `NeverStepUpCategories` |
| never be flushed by an error in its trace (below `Warning`) | `PreErrorBufferExcludeCategories` |
| always export, at any level, whatever the switch | `AlwaysExportCategories` |
| log errors that do not step the level up | `NeverTriggerCategories` |
| keep its floor during Diagnostic mode | `DiagnosticExemptCategories` |
| stay at the library's defaults for framework noise | `FrameworkCategoryDefaults` (`Recommended`, the default) |

### Category floors

`CategoryFloors` maps a `SourceContext` prefix to a minimum level. A matching category exports at
`max(switch, floor)`, so a floor only ever hides events, never adds them: a floor below the current
switch level changes nothing. Use it to keep a noisy category quiet while the rest of the app is
stepped up.

```json
{
  "SerilogStepUp": {
    "CategoryFloors": {
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Information"
    }
  }
}
```

- A prefix matches a category equal to it, or one that begins with it followed by a `.`, ordinal and
  case-sensitive, the same rule as `NeverStepUpCategories`.
- When several keys match, the most specific (longest) one decides, so a sub-category can sit below
  its parent's floor.
- Floors apply in every `Mode`, `AlwaysOn` and `Disabled` included. When both a floor and a
  `NeverStepUpCategories` entry apply, the higher minimum wins.
- A floor above `Warning` fails startup, so a floor never hides an `Error`.
- An event a floor rejects is a held-back event: with `EnablePreErrorBuffering` on, an `Error` in the
  same trace exports it exactly once if it is at or above `StepUpLevel`. Below `StepUpLevel` it is
  dropped, for example an exempt floored `Debug` event during Diagnostic mode.
- During Diagnostic mode every floor is lifted except for categories under a
  `DiagnosticExemptCategories` prefix, which keep the floor of their most specific matching key.
  An exempt prefix that no `CategoryFloors` key matches fails startup.

### Framework category defaults

`FrameworkCategoryDefaults` (default `Recommended`) applies built-in floors to framework categories whose
`Information` events repeat the request summary or a span. Their events below `Warning` are neither
exported during a step-up window nor flushed by an `Error`, so a failing request exports the
application's events and the request summary instead of the framework's progress lines.

| Prefix | Floor | Flushed on error |
|---|---|---|
| `Microsoft.AspNetCore.Hosting.Diagnostics` | `Warning` | no |
| `Microsoft.AspNetCore.Routing` | `Warning` | no |
| `Microsoft.AspNetCore.Mvc` | `Warning` | no |
| `Microsoft.AspNetCore.Http.HttpResults` | `Warning` | no |
| `Microsoft.AspNetCore.StaticFiles` | `Warning` | no |
| `Microsoft.AspNetCore.Cors` | `Warning` | yes |
| `System.Net.Http.HttpClient` | `Warning` | no |
| `Polly` | `Warning` | no |
| `Microsoft.EntityFrameworkCore.Database.Command` | `Warning` | no |
| `Microsoft.EntityFrameworkCore.Infrastructure` | `Warning` | no |
| `Npgsql.Command` | `Warning` | no |
| `Yarp.ReverseProxy.Forwarder` | `Warning` | yes |

- `Warning`, `Error` and `Fatal` events of these categories are exported as before.
- CORS and the YARP forwarder are still flushed on an error, because a failed CORS check or a
  forwarding error explains the failure.
- Authentication, Authorization, Antiforgery, Kestrel, DataProtection, health checks and
  `Microsoft.Hosting.Lifetime` are not listed.
- A `CategoryFloors` entry overrides the defaults for every category it matches, even when a default
  prefix is longer, and is flushed on an error like any floor. Add it to
  `PreErrorBufferExcludeCategories` to keep it out of the flush. A broad key such as
  `"Microsoft": "Warning"` therefore brings the flush back for every `Microsoft.*` default.
- `AlwaysOn` does not apply the defaults. Diagnostic mode lifts them unless a
  `DiagnosticExemptCategories` entry keeps them; such an entry may name a default prefix.

Bring one category back, or turn the defaults off:

```json
{
  "SerilogStepUp": {
    "CategoryFloors": {
      "System.Net.Http.HttpClient.Keycloak": "Information"
    }
  }
}
```

```json
{
  "SerilogStepUp": {
    "FrameworkCategoryDefaults": "None"
  }
}
```

```csharp
builder.AddStepUpLogging(o => o.FrameworkCategoryDefaults = FrameworkCategoryPreset.None);
```

The setting replaces seeded defaults on purpose: the configuration binder cannot remove a key from a
dictionary or an entry from an array, so a default stored in `CategoryFloors` could not be cleared
from appsettings. See docs/adr/0029-framework-category-defaults.md.

### Always-export categories

`AlwaysExportCategories` lists `SourceContext` prefixes whose events are exported once, at any
level, whatever the current step-up state. A matching event is marked `IsImmediate=true` and takes
the same path as `LogImmediate`: the step-up gate skips it and the pre-error buffer never holds it,
so a later error in the same trace does not export it a second time. Matching uses the same prefix
rule as `NeverStepUpCategories`. With `RedactLogEventProperties` on, these events are redacted like
any other. Blank entries are ignored. Default: empty. A root `Serilog:MinimumLevel:Override` or a
`Logging:LogLevel` filter set above the listed category's level still drops the event before the
enricher sees it, so the list cannot bring such an event back.

```json
{
  "SerilogStepUp": {
    "BaseLevel": "Warning",
    "AlwaysExportCategories": [ "Microsoft.Hosting.Lifetime" ]
  }
}
```

With this configuration the host's "Application started" Information is exported although
`BaseLevel` is Warning.

### Never-trigger categories

`NeverTriggerCategories` (default `[]`) lists `SourceContext` prefixes whose `Error` and `Fatal`
events never trigger step-up. Use it for a category that logs errors it recovers from itself,
such as a retry library, so every transient failure does not raise the level for the whole
service.

Only the trigger is skipped. The error itself is exported, and it still flushes the held-back
events of its trace from the pre-error buffer. Matching follows the same prefix rule as
`NeverStepUpCategories`: the category equals the prefix, or starts with it followed by a `.`.
Blank entries are ignored.

```json
{
  "SerilogStepUp": {
    "NeverTriggerCategories": ["Polly"]
  }
}
```

### Diagnostic mode

`Mode: "Diagnostic"` raises verbosity for a fixed window after startup, for example to watch a new
deployment. It is entered only at startup; there is no runtime API.

```json
{
  "SerilogStepUp": {
    "Mode": "Diagnostic",
    "DiagnosticLevel": "Debug",
    "DiagnosticDurationMinutes": 30
  }
}
```

- The switch starts at `DiagnosticLevel` and stays there for `DiagnosticDurationMinutes` (1 to 120).
  When the window ends, the controller runs as `Auto` at `BaseLevel`.
- While Diagnostic is active, `Trigger()` does nothing and `IsSteppedUp` is `true`. Nothing carries
  over: an error during the window does not start a step-up after it ends.
- Every floor is lifted during the window except for `DiagnosticExemptCategories` prefixes (see
  [Category floors](#category-floors)).
- The `NeverStepUpCategories` pin is lifted during the window. With the default list, EF
  `Database.Command` SQL (and its parameters when `EnableSensitiveDataLogging` is on) is exported
  for the whole window, up to 120 minutes. To keep it quiet, add a `CategoryFloors` entry for
  `Microsoft.EntityFrameworkCore.Database.Command` (for example `"Warning"`) and list the same
  prefix in `DiagnosticExemptCategories`.
- The window is timed by the `TimeProvider` registered in DI, or `TimeProvider.System` when none is.
- A start Warning is written at startup and an end Warning when the window ends, both through the
  bypass logger: `StepUp Diagnostic mode active at {DiagnosticLevel} for {DurationMinutes} min until {ExpiresAt}`
  and `StepUp Diagnostic mode ended, running as Auto at {BaseLevel}`. Disposing the host before the
  window ends writes no end Warning.
- A startup Warning is written when `DiagnosticLevel` is not more verbose than `BaseLevel`, since the
  mode then cannot increase verbosity.
- `stepup_diagnostic_active` reads 1 during the window and 0 after it; `stepup_active` stays 0.
- With `CaptureRequestBody=true`, request bodies are captured (still redacted) for the whole window,
  because body capture follows `IsSteppedUp`.

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

`RedactionRegexes` is always applied to the request path (summary `Path`, the "HTTP ..." event's
`RequestPath` and the `LogRequest` span's `http.target`), query strings, route values, headers,
and request bodies. It also always covers the path-bearing properties of every exported event,
the request summary and the pre-error buffer flush included: the `RequestPath` that ASP.NET Core's
hosting log scope attaches to every event logged during a request, and the `Path` of
`Microsoft.AspNetCore.Hosting.Diagnostics` events. `ExcludePaths` still matches the raw path. On its own it does **not** scan the rendered text of arbitrary log messages — a secret
passed as a message-template argument (`logger.LogInformation("token={T}", secret)`) is left
alone.

Set `RedactLogEventProperties: true` to also sweep the string-valued scalar properties of
application log events with the same `RedactionRegexes`, so the example above **is** redacted
once the flag is on. URI-like values (`PathString`, `QueryString`, `HostString`, `Uri`) are swept
too: each is matched on its `ToString()` form, for `Uri` the decoded form the exporter renders,
and exported as the redacted string when a pattern matched. Other non-string scalars (numbers,
GUIDs, dates) are left as they are. The sweep reaches properties and nothing else. It does not touch
message-template text or exception messages: an interpolated `logger.LogInformation($"token={t}")`
bakes the value into the template and produces no property, so it is logged verbatim whatever the
flag says. It does not recurse into structures (`{@user}`), sequences or dictionaries. It skips
the twelve properties the library stamps itself (`TraceId`, `SourceContext`,
`ServiceInstanceId` and nine more — the `RedactLogEventProperties` XML doc lists them and the
reason for each). And it never sees the four events the library writes straight to the bypass
logger — the request summary, the startup level-ordering warning, and the step-up and step-down
warnings — which do not pass root
enrichment. Do not log secrets in interpolated message templates.

`RedactionExemptProperties` lists property names whose values the patterns never touch, for an
identifier that a broad pattern would otherwise mask: with a UUID pattern, Keycloak's
`jti` (`onrtac:<uuid>`) would come out as `onrtac:[REDACTED]`. Listing `Jti` keeps it on the
request summary, the request completion event and, with `RedactLogEventProperties` on, in the
sweep; the same UUID in the request path or in any other property is still redacted. Names match
ordinal and case-sensitive, by name only: a secret you log under a listed name is exported in
clear. The request path, query string, headers, route values and body are always redacted,
whatever the list holds.

```json
{
  "SerilogStepUp": {
    "RedactionRegexes": [ "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" ],
    "RedactionExemptProperties": [ "Jti" ]
  }
}
```

The flag has a running cost worth sizing before you enable it. The sweep sits on the root pipeline,
behind the same enrichment gate as the library's other root enrichers, so it runs on an event some sink
can use: one at or above `min(live level switch, StepUpLevel, Error)`, or marked `IsImmediate` or
`IsRequestSummary`. A `Debug` event the level switch drops and nothing else reads is not swept. (Events
filtered out by a `Serilog:MinimumLevel:Override` are dropped before enrichment and are never swept
either.) The gate is off when you set the `configure` hook or declare `Serilog:AuditTo`, because a sink
on the Verbose root then receives every event; the sweep then runs on every event that reaches the root.
Per swept event the work is one regex replace per configured pattern per non-excluded string-valued
scalar property, each scaling with the length of the value; when every pattern is linear-time and they
share their options, one scan for the whole list replaces those replaces on a value that matches none of
them. No figure is published here: a measurement taken against this README's sample patterns would not
transfer to yours. The lever is the pattern list — keep `RedactionRegexes` short and each pattern
narrow rather than open-ended. With the flag off, or with no patterns configured, the enricher is not
registered at all and there is no per-event cost.

One consequence of the gate: a root `Serilog:Filter` sees the properties of a skipped event unredacted.
A filter only decides and exports nothing, so this matters only if your filter logs or forwards the
properties of the events it inspects. The library's own sinks refuse a skipped event; the one exception is the narrow race of ADR 0026 D3, where
a log call made by a root filter or a `SelfLog` handler between the gate and `StepUpSink` can let one
event through unenriched and unredacted, and then only if the switch was also lowered by hand in that window.

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
- `stepup_diagnostic_active` - Whether Diagnostic mode is currently active (0 or 1); `stepup_active` stays 0 during Diagnostic
- `stepup_duration_seconds` - Duration histogram of step-up windows
- `request_body_captured_total` - Number of requests with captured body
- `request_redaction_applied_total` - Number of requests with redaction applied
- `buffer_events_total` - Total events buffered by pre-error buffer
- `buffer_flushed_events_total` - Events flushed due to error
- `buffer_flush_total` - Number of buffer flush operations
- `buffer_evicted_contexts_total` - Contexts evicted due to LRU pressure

## License

MIT © Lukdrasil
