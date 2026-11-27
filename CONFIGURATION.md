# Configuration reference

This page lists every setting of `Lukdrasil.StepUpLogging`. Each entry says what the setting does, its default, and when you might change it.

A quick reminder of the idea behind the library. Normally your app logs only a few things (warnings and errors). When an error happens, the library "steps up" for a short time: it logs more detail. After a timeout it "steps down" again. You get detail when something breaks and quiet logs the rest of the time.

## Where you set options

Most options live in the `SerilogStepUp` section of `appsettings.json`:

```json
{
  "SerilogStepUp": {
    "BaseLevel": "Warning",
    "StepUpLevel": "Information",
    "DurationSeconds": 3
  }
}
```

You can set the same options with environment variables. Replace each `:` with a double underscore `__`:

```bash
SerilogStepUp__BaseLevel=Warning
SerilogStepUp__ClientIpLogging=Prefix
SerilogStepUp__ExcludePaths__0=/healthz      # list entries use an index
SerilogStepUp__CategoryFloors__MyApp.Chatty=Warning
```

You can also set them in code. `AddStepUpLogging` takes an optional delegate that runs after the configuration section is read, so a value set there wins:

```csharp
builder.AddStepUpLogging(options =>
{
    options.EnableConsoleLogging = true;
    options.ConsoleFormat = ConsoleLogFormat.Text;
});
```

A few things to know:

- **Names are case-insensitive.** `"baselevel"` works like `"BaseLevel"`. Level names are the Serilog ones: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.
- **Invalid values stop the app.** The host refuses to start and the error names each bad key and the value it got (ADR 0007). A typo in a level name, a duration of 0 or a broken regex is caught at startup, not hours later.
- **Options are read once at startup.** A change needs a restart.
- **The startup report.** When the logger is built, the library writes one Information line with the effective configuration (mode, levels, outputs, request summary). It also writes a Warning for each key under `SerilogStepUp` that matches no option (often a typo), and a Warning for an option that has no effect. Examples: `ConsoleFormat` set while `EnableConsoleLogging` is false, `RedactLogEventProperties` set with no `RedactionRegexes`, or `RequestSummaryDetail` changed while no request summary is on, or `RequestSummaryOnError` set to `Omit` while `AlwaysLogRequestSummary` is false. It also warns when `StepUpLevel` or `DiagnosticLevel` is not more verbose than `BaseLevel`, because then stepping up changes nothing.

The encrypted spool audit sink is the one exception. Its options are set only in code. See [Audit and the encrypted spool](#audit-and-the-encrypted-spool).

## Contents

1. [Step-up levels and timing](#step-up-levels-and-timing)
2. [Modes](#modes)
3. [Categories](#categories)
4. [Pre-error buffer](#pre-error-buffer)
5. [Request logging and summaries](#request-logging-and-summaries)
6. [Redaction and sensitive headers](#redaction-and-sensitive-headers)
7. [Client IP](#client-ip)
8. [Outputs: OTLP, console, file](#outputs-otlp-console-file)
9. [Enrichment and tracing](#enrichment-and-tracing)
10. [Startup and the static logger](#startup-and-the-static-logger)
11. [Audit and the encrypted spool](#audit-and-the-encrypted-spool)
12. [OpenTelemetry environment variables](#opentelemetry-environment-variables)
13. [The Serilog section](#the-serilog-section)
14. [Parameters of AddStepUpLogging](#parameters-of-addstepuplogging)

---

## Step-up levels and timing

A **log level** says how important an event is. From least to most important: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. A minimum level of `Warning` means "export `Warning`, `Error` and `Fatal`, drop the rest".

| Option | Type | Default | What it does |
|---|---|---|---|
| `BaseLevel` | string (level) | `"Warning"` | The minimum level in normal times. `Information` makes logs much bigger all the time. `Error` hides warnings until something fails. |
| `StepUpLevel` | string (level) | `"Information"` | The minimum level while stepped up, after an error. `Debug` gives more detail per incident and makes the pre-error buffer hold `Debug` events too. |
| `DurationSeconds` | int | `3` | How long a step-up lasts. Each new error restarts the timer. Must be greater than 0. A longer value, such as `180`, also gives full detail for the requests that come right after an incident, at a volume cost. |
| `MaxContinuousStepUpSeconds` | int | `0` (no cap) | The longest a step-up may stay on without a break. When it is reached, the level goes back to `BaseLevel` and new errors are ignored for `StepUpCooldownSeconds` (ADR 0010). Use it so a failure that lasts hours does not keep detailed logging on for hours. Must be `0` or at least `DurationSeconds`. |
| `StepUpCooldownSeconds` | int | `300` | How long new errors are ignored after the cap above forced a step-down. Has no effect while the cap is `0`. Must be `0` or more. |

Example: step up to `Debug` for one minute, but never for longer than ten minutes in a row.

```json
{
  "SerilogStepUp": {
    "StepUpLevel": "Debug",
    "DurationSeconds": 60,
    "MaxContinuousStepUpSeconds": 600,
    "StepUpCooldownSeconds": 300
  }
}
```

## Modes

| Option | Type | Default | What it does |
|---|---|---|---|
| `Mode` | enum `StepUpMode` | `Auto` | How the step-up behaves. See the list below. |
| `DiagnosticLevel` | string (level) | `"Debug"` | The level used while `Diagnostic` mode is active. |
| `DiagnosticDurationMinutes` | int | `30` | How long `Diagnostic` mode lasts after each start, from 1 to 120 minutes. |
| `DiagnosticExemptCategories` | string[] | `[]` | Category prefixes that keep their floor during `Diagnostic` mode. See [Categories](#categories). Each entry must match a `CategoryFloors` key or, with `FrameworkCategoryDefaults` set to `Recommended`, a built-in framework prefix. |

The four modes:

- **`Auto`** (default). Log at `BaseLevel`. On an `Error` or `Fatal`, step up to `StepUpLevel` for `DurationSeconds`. Use this in production.
- **`AlwaysOn`**. Always log at `StepUpLevel`. Errors change nothing. The framework defaults and `NeverStepUpCategories` are not applied. Good for local development. Too noisy for production.
- **`Disabled`**. Always log at `BaseLevel`. Errors never step up. Events flushed by the pre-error buffer are still exported. The lowest volume.
- **`Diagnostic`**. For investigating a problem in one deployment. After each start the level sits at `DiagnosticLevel` for `DiagnosticDurationMinutes`. During that window errors do not step up, and category floors are lifted unless a `DiagnosticExemptCategories` entry keeps them. When the window ends, the app runs as `Auto`. The startup report writes a Warning saying when the window ends.

Example: debug logs for the first 15 minutes after a deployment, while still keeping Entity Framework SQL quiet.

```json
{
  "SerilogStepUp": {
    "Mode": "Diagnostic",
    "DiagnosticLevel": "Debug",
    "DiagnosticDurationMinutes": 15,
    "DiagnosticExemptCategories": [ "Microsoft.EntityFrameworkCore.Database.Command" ]
  }
}
```

## Categories

A **category** is the name of the logger that wrote an event, stored as `SourceContext`. With `ILogger<OrderService>` it is the full class name, for example `MyApp.Orders.OrderService`.

All category options use **prefixes** with one rule (ADR 0021): an entry matches a category that is equal to it, or that starts with it followed by a dot. So `MyApp.Orders` matches `MyApp.Orders` and `MyApp.Orders.OrderService`, but not `MyApp.OrdersArchive`. Matching is case-sensitive. Blank entries are ignored.

| Option | Type | Default | What it does |
|---|---|---|---|
| `FrameworkCategoryDefaults` | enum `FrameworkCategoryPreset` | `Recommended` | Built-in floors for chatty framework categories (ADR 0029). See below. `None` turns them off, so framework categories behave like your own code. |
| `CategoryFloors` | map prefix to level | `{}` | A floor sets a lowest level for a category, even during a step-up. `"MyApp.Chatty": "Warning"` keeps that category at `Warning` and above. A floor can only make a category quieter, never more verbose, and the most specific prefix wins. Values must be `Warning` or lower (`Verbose`, `Debug`, `Information`, `Warning`), so a floor can never hide an error. A key here also overrides any built-in framework rule it matches. |
| `NeverStepUpCategories` | string[] or not set | not set, which means `["Microsoft.EntityFrameworkCore.Database.Command"]` | Categories that a step-up never raises above `BaseLevel`. By default this keeps Entity Framework SQL commands out of a step-up, because they are many and carry raw SQL. A list you set replaces the default. `[]` pins nothing. Has no effect in `AlwaysOn` mode and is lifted in `Diagnostic` mode. With `FrameworkCategoryDefaults` `Recommended`, the EF command category is already floored at `Warning`, so this matters only with `None` or a `BaseLevel` above `Warning`. |
| `AlwaysExportCategories` | string[] | `[]` | Categories exported at every level, all the time, once each, by a path that ignores the step-up. They also skip the pre-error buffer. Volume grows with their traffic. |
| `NeverTriggerCategories` | string[] | `[]` | Categories whose `Error` and `Fatal` events never start a step-up. Those events are still exported and still flush their request's buffer. Use it for a dependency that fails often and loudly. |

What `Recommended` does: these categories export only at `Warning` and above, even during a step-up. Most are also kept out of the pre-error buffer below `Warning`, because their Information events only repeat the request summary or a trace span.

| Prefix | Kept in the buffer |
|---|---|
| `Microsoft.AspNetCore.Hosting.Diagnostics` | no |
| `Microsoft.AspNetCore.Routing` | no |
| `Microsoft.AspNetCore.Mvc` | no |
| `Microsoft.AspNetCore.Http.HttpResults` | no |
| `Microsoft.AspNetCore.StaticFiles` | no |
| `Microsoft.AspNetCore.Cors` | yes |
| `System.Net.Http.HttpClient` | no |
| `Polly` | no |
| `Microsoft.EntityFrameworkCore.Database.Command` | no |
| `Microsoft.EntityFrameworkCore.Infrastructure` | no |
| `Npgsql.Command` | no |
| `Yarp.ReverseProxy.Forwarder` | yes |

CORS and the YARP forwarder stay in the buffer because they often explain a failure.

Example:

```json
{
  "SerilogStepUp": {
    "CategoryFloors": {
      "MyApp.Chatty": "Warning",
      "Microsoft.AspNetCore.Routing": "Information"
    },
    "NeverTriggerCategories": [ "MyApp.Integrations.FlakyPartner" ],
    "AlwaysExportCategories": [ "MyApp.Billing" ]
  }
}
```

The second floor overrides the built-in `Warning` rule for routing, so routing exports `Information` again during a step-up.

## Pre-error buffer

When an error happens, you usually want to see what led up to it. The **pre-error buffer** keeps recent events of each request in memory, even when they were not exported. If that request then logs an `Error` or `Fatal`, the buffered events are exported with it. If not, they are dropped. Requests are told apart by their W3C trace id (the id that ties together all events and spans of one request).

Only events at or above `StepUpLevel` are buffered (ADR 0015). Set `StepUpLevel` to `Verbose` to buffer everything.

| Option | Type | Default | What it does |
|---|---|---|---|
| `EnablePreErrorBuffering` | bool | `true` | Turns the buffer on or off. Off saves memory, but errors then arrive without their history. |
| `PreErrorBufferSize` | int | `100` | Most events kept per request. The oldest are dropped first. Must be greater than 0. |
| `PreErrorMaxContexts` | int | `1024` | Most requests tracked at once. When full, the least recently used request is dropped (ADR 0027). Must be greater than 0. Memory is bounded by about size times contexts events. |
| `PreErrorBufferExcludeCategories` | string[] | `[]` | Category prefixes whose events below `Warning` are never buffered. Their live export is not changed, and other categories of the same request still flush (ADR 0028). |

```json
{
  "SerilogStepUp": {
    "PreErrorBufferSize": 200,
    "PreErrorMaxContexts": 2048,
    "PreErrorBufferExcludeCategories": [ "MyApp.Metrics" ]
  }
}
```

## Request logging and summaries

`app.UseStepUpRequestLogging()` adds a middleware that writes one **completion event** per request. Its level is `Error` for an unhandled exception or a 5xx, `Warning` for a 4xx, and `Information` otherwise. It is exported like any other event, so with the default `BaseLevel` you see only the warnings and errors.

A **request summary** is a separate, short access-log line. It is written by a path that ignores the step-up, so you get one per request whatever the level. It is off by default.

| Option | Type | Default | What it does |
|---|---|---|---|
| `AlwaysLogRequestSummary` | bool | `false` | Writes one summary per inbound request. This is an access log, so its volume equals your traffic. Needs `UseStepUpRequestLogging`. |
| `AlwaysLogOutboundRequestSummary` | bool | `false` | Writes one summary per outgoing call made through an `IHttpClientFactory` client. A retried call is summarized once. The library's own audit client and the OTLP exporter clients are left out. An `HttpClient` made with `new HttpClient()` is not covered. |
| `RequestSummaryLevel` | string (level) | `"Information"` | The level the summaries are written at. |
| `RequestSummaryDetail` | enum `RequestSummaryDetail` | `Full` | Which fields a summary carries (ADR 0032, ADR 0033). `Full` has every field. `Compact` keeps the message fields plus the ones that identify the caller (route, client id, client IP, token id). `Minimal` writes the `Compact` fields under short keys such as `mth`, `sc`, `ip`, to save space. The rendered message text is the same in every mode. |
| `RequestSummaryOnError` | enum `RequestSummaryOnErrorMode` | `Keep` | `Omit` writes one line per failed request (status 400 or above, or an exception) instead of two (ADR 0043). The inbound summary is left out when the completion event is exported, and the completion event carries the summary's fields under the summary's keys (short keys in `Minimal`). When the completion event is not exported (for example a 4xx `Warning` under `BaseLevel` `Error`), the summary is still written. A 2xx always writes its summary. Needs `AlwaysLogRequestSummary`. |
| `ExcludePaths` | string[] or not set | not set, which means `["/healthz", "/metrics", "/health"]` | Request paths that get no request logging. Case-insensitive. An entry ending in `*` matches a prefix on a path segment, for example `/internal/*`. A list you set replaces the defaults. `[]` excludes nothing. |
| `AdditionalExcludePaths` | string[] | not set (adds nothing) | Paths added to `ExcludePaths`. Use it to add a path and keep the defaults without repeating them. |
| `TreatServerErrorStatusAsError` | bool | `true` | When `true`, a 5xx without an exception is logged at `Error`, which also steps up and flushes the buffer. Set `false` in a reverse proxy or BFF (a backend-for-frontend), where most 5xx come from a backend. Those are then logged at `Warning`. An unhandled exception is `Error` either way. |
| `CaptureRequestBody` | bool | `false` | Adds the body of POST, PUT and PATCH requests to the completion event, while stepped up or when the response is 5xx (ADR 0004, ADR 0011). The body is redacted first. Bodies can hold personal data, so add `RedactionRegexes` for it. Each body is buffered in memory. |
| `MaxBodyCaptureBytes` | int | `16384` (16 KB) | The most body text kept. Despite the name it counts characters of the decoded text, not raw bytes. Must be greater than 0. |

The client IP settings for request logging are in [Client IP](#client-ip).

Example: an access log in the short format, without the health and readiness probes.

```json
{
  "SerilogStepUp": {
    "AlwaysLogRequestSummary": true,
    "RequestSummaryDetail": "Minimal",
    "AdditionalExcludePaths": [ "/ready", "/.well-known/*" ]
  }
}
```

## Redaction and sensitive headers

**Redaction** means replacing a secret with `[REDACTED]` before it is logged.

| Option | Type | Default | What it does |
|---|---|---|---|
| `RedactionRegexes` | string[] | `[]` | Regular expressions (text patterns) to redact. They are applied, ignoring case, to the request path, query string, headers, route values and request body. Each entry must be a valid regex. Keep the list short and each pattern narrow, because every pattern costs CPU on every request. |
| `RedactLogEventProperties` | bool | `false` | Also applies `RedactionRegexes` to the string properties of your own log events, for example `logger.LogInformation("token={Token}", t)` (ADR 0022). It does not touch message text, so `$"token={t}"` (string interpolation) is never redacted. Do not put secrets in interpolated messages. Has no effect while `RedactionRegexes` is empty. |
| `RedactionExemptProperties` | string[] | `[]` | Property names that the patterns skip, for ids like `Jti` that a broad pattern (for example a bare UUID) would hide. Case-sensitive. The request inputs (path, query, headers, route values, User-Agent, `X-Forwarded-For`, body) are always redacted whatever this list says (ADR 0031). |
| `AdditionalSensitiveHeaders` | string[] | `[]` | Extra header names whose values are always hidden in the logged headers. Case-insensitive. |

These headers are always hidden, whatever you configure:
`Authorization`, `Cookie`, `X-API-Key`, `X-Auth-Token`, `X-Access-Token`, `Authorization-Token`, `Proxy-Authorization`, `WWW-Authenticate`, `Sec-WebSocket-Key`.

Example: hide e-mail addresses and an API key header of your own.

```json
{
  "SerilogStepUp": {
    "RedactionRegexes": [ "[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}" ],
    "RedactLogEventProperties": true,
    "RedactionExemptProperties": [ "Jti" ],
    "AdditionalSensitiveHeaders": [ "X-Partner-Key" ]
  }
}
```

## Client IP

A full IP address is personal data under GDPR. From 9.0.0 the library writes no client IP by default. Logs and the audit trail choose separately how much of it to write (ADR 0042).

| Option | Type | Default | What it does |
|---|---|---|---|
| `ClientIpLogging` | enum `ClientIpMode` | `Off` | How much of the client IP goes into the logs. |
| `ClientIpAudit` | enum `ClientIpMode` | `Off` | How much of the client IP goes into `AuditEvent.SourceIp` of audit records. |
| `TrustForwardedHeaders` | bool | `false` | Where the client IP comes from (ADR 0008). See below. |

`ClientIpMode` has three values:

- **`Off`** leaves the field out. It does not write a marker value.
- **`Prefix`** writes only the network part in CIDR form. That is `/24` for IPv4 and `/48` for IPv6, for example `203.0.113.0/24` and `2001:db8:1234::/48`. The `/24` suffix shows the reader it is a prefix and not a real address. The prefix lengths are fixed.
- **`Full`** writes the whole address. This is what 8.x always did.

What each mode writes:

| Output | Governed by | `Off` | `Prefix` | `Full` |
|---|---|---|---|---|
| `ClientIp` in the request summary and in the completion event | `ClientIpLogging` | left out | network prefix, for example `203.0.113.0/24` | full address |
| `ForwardedFor` (only in the `Full` request summary) | `ClientIpLogging` | left out | each entry masked to its prefix, entries that do not parse left out | full value, redacted |
| `X-Forwarded-For`, `Forwarded`, `X-Real-IP`, `X-Original-For` in the logged `Headers` | `ClientIpLogging` | redacted | redacted | logged as received |
| `AuditEvent.SourceIp` | `ClientIpAudit` | left out | network prefix | full address |

Details worth knowing:

- **Where the address comes from.** With `TrustForwardedHeaders` set to `false`, the address is the connection's remote address. Behind a proxy that is the proxy's address. With `true`, the address is the first entry of the `X-Forwarded-For` header. Any client can fake that header, so only turn it on behind a reverse proxy you control, together with ASP.NET Core's `ForwardedHeadersMiddleware`. The same rule feeds both the logs and `AuditEvent.SourceIp`. The two mode options only decide how much of that address is written.
- **Prefix is pseudonymisation, not anonymisation.** A `/24` or `/48` still narrows a client down to a small network. These options reduce what is written. They do not by themselves make a deployment GDPR-compliant.
- **IPv4 behind a dual-mode socket.** Kestrel can report an IPv4 client as `::ffff:203.0.113.7`. Such an address is masked as IPv4, so it becomes `203.0.113.0/24`.
- **Only a value that parses is masked.** The address is redacted first and then parsed. A value that does not parse as an address, including one a redaction regex changed, is left out instead of written as raw text.
- **Field names do not change.** `ClientIp` stays `ClientIp`, and the `Minimal` short key stays `ip`.
- **The library never changes the request's headers.** Redaction applies only to the copy that is logged, so forwarding the headers to a backend still works.
- **Other IP headers are yours to add.** A header such as `CF-Connecting-IP` is not covered by these options. Add it to `AdditionalSensitiveHeaders`.
- **Audit endpoints.** An audit endpoint that checks `SourceIp` is a valid IP address rejects records under `Prefix`.
- **Not covered.** `client.address` on trace spans, and any address your own code puts into log events or into an audit record's `Data`.

Upgrading from 8.x: to keep the old behaviour, set both options to `Full`.

```json
{
  "SerilogStepUp": {
    "ClientIpLogging": "Full",
    "ClientIpAudit": "Full"
  }
}
```

Example: audit the IP but do not log it.

```json
{
  "SerilogStepUp": {
    "ClientIpLogging": "Off",
    "ClientIpAudit": "Full",
    "TrustForwardedHeaders": true
  }
}
```

Here audit records get the full address taken from `X-Forwarded-For` (set by your own proxy), while the logs carry no client IP and the four IP headers are redacted.

## Outputs: OTLP, console, file

No output is on by default unless an OpenTelemetry collector address is set.

| Option | Type | Default | What it does |
|---|---|---|---|
| `EnableOtlpExporter` | bool | `true` when the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable is set, else `false` | Sends logs over OTLP (the OpenTelemetry protocol) to a collector. Unless your app registered the OpenTelemetry SDK itself first, it also registers the SDK's OTLP exporter for traces and metrics. Set `true` without the variable to send to the default `localhost:4317`, for a collector sidecar. Set `false` to stop OTLP even when the variable is set. The address and protocol come only from [environment variables](#opentelemetry-environment-variables). |
| `EnableConsoleLogging` | bool | `false` | Writes every exported event to the console (stdout). If your platform also ships stdout while OTLP is on, each event is stored twice. |
| `ConsoleFormat` | enum `ConsoleLogFormat` | `Json` | `Json` writes compact JSON, one event per line. `Text` writes one readable `Key=value` line per event. Only the console changes, OTLP and the file stay structured. |
| `ConsoleTraceFormat` | enum `ConsoleTraceFormat` | `TraceId` | How a `Text` line shows the trace. `TraceId` writes `TraceId=<32 hex digits>`. `Traceparent` writes the W3C `traceparent=00-<trace id>-<span id>-<flags>`, which you can paste into a trace search. |
| `ConsoleTextProperties` | string[] or not set | not set, which means `["Jti", "Method", "Path", "StatusCode", "ElapsedMs"]`, or `["jti", "mth", "path", "sc", "ms"]` with `RequestSummaryDetail` `Minimal` | Extra properties written after the message on a `Text` line, when the event has them. A list you set replaces the default. |

The file output is not an option. It is the `logFilePath` parameter of `AddStepUpLogging`. See [Parameters of AddStepUpLogging](#parameters-of-addstepuplogging).

Example: readable console lines for a container.

```json
{
  "SerilogStepUp": {
    "EnableConsoleLogging": true,
    "ConsoleFormat": "Text",
    "ConsoleTraceFormat": "Traceparent"
  }
}
```

## Enrichment and tracing

**Enrichment** means adding extra properties to every event, such as the machine name. Every event also gets `Application` (the host's application name), `TraceId`, `SpanId`, `ParentSpanId`, `TraceFlags` and `TraceState`. Those are always on.

| Option | Type | Default | What it does |
|---|---|---|---|
| `EnrichWithEnvironment` | bool | `true` | Adds `Environment`, for example `Production`. |
| `EnrichWithMachineName` | bool | `true` | Adds `MachineName`. |
| `EnrichWithExceptionDetails` | bool | `true` | Adds structured exception details (type, stack trace, inner exceptions, extra fields) as properties. Works only when `StructuredExceptionDetails` is also `true`. |
| `StructuredExceptionDetails` | bool | `true` | Second switch for the same exception details. Both this and `EnrichWithExceptionDetails` must be `true`. Setting either one to `false` turns the details off. |
| `EnrichWithThreadId` | bool | `false` | Adds `ThreadId`. |
| `EnrichWithProcessId` | bool | `false` | Adds `ProcessId`. |
| `EnrichWithCallStack` | bool | `false` | Adds the call stack. Expensive, so keep it for debugging. |
| `ServiceVersion` | string or not set | not set | When set, adds `ServiceVersion` to every event. |
| `ServiceInstanceId` | string or not set | not set | When set, adds `ServiceInstanceId` to every event. When not set, nothing is added. |
| `CaptureScopesOnException` | bool | `true` | When an exception is thrown, remembers the log scopes (`BeginScope`, `LogContext`) that were open at that point. An event that later logs this exception gets those scopes, even if it is written outside them (ADR 0038). Set `false` in code that throws exceptions as normal control flow at a high rate. |
| `EnableActivityInstrumentation` | bool | `true` | Lets the library start its own tracing activities (spans) for request logging and for step-up and step-down. The pre-error buffer flush activity is not affected. They are exported only if you add the library's activity sources to your OpenTelemetry setup. Set `false` to skip creating them. |

```json
{
  "SerilogStepUp": {
    "ServiceVersion": "2.4.1",
    "EnrichWithThreadId": true,
    "EnrichWithMachineName": false
  }
}
```

## Startup and the static logger

| Option | Type | Default | What it does |
|---|---|---|---|
| `WriteStartupSummary` | bool | `true` | Writes the one Information line of the startup report. Set `false` in a command-line tool, where that line would appear on every run. The report's Warnings are written either way. |
| `SetStaticLogger` | bool | `false` | When `true`, the library also sets Serilog's static `Log.Logger`, and stopping the host calls `Log.CloseAndFlush()`, like before 5.0.0 (ADR 0025). The default leaves `Log.Logger` alone. Use `ILogger<T>` from dependency injection instead. |

## Audit and the encrypted spool

Audit records do not go through Serilog. You write them with `IAuditLogger<T>.AuditAsync()`, and they go to one **audit sink** (ADR 0016). There are two ways to register one:

- `builder.AddAuditLogging<TSink>(sinkLifetime)` registers your own sink. `sinkLifetime` defaults to `Scoped`, so the sink can share the request's `DbContext`. It needs `AddStepUpLogging` too, in any order.
- `builder.AddEncryptedSpoolAuditSink(options => ...)` registers the built-in encrypted spool sink. Do not also call `AddAuditLogging` for it. There is no on/off flag: not calling the method is how it stays off.

The client IP written into audit records is set by `ClientIpAudit`. See [Client IP](#client-ip).

### How the encrypted spool works

The sink encrypts each record through your `IAuditPayloadEncryptor`, then writes it to a file on disk (the **spool**) before it reports success. A background worker then sends the spooled records to your audit endpoint, oldest first, and deletes each one once the endpoint has accepted it. If the endpoint is down, records wait on disk and are retried. A record the endpoint rejects for good is moved to a `dead-letter/` folder, never deleted (ADR 0020).

**These options are set only in code**, in the `AddEncryptedSpoolAuditSink` delegate. They are not read from `appsettings.json`. Durations are `TimeSpan` values and statuses are `HealthStatus` values. Invalid values stop the app at startup, naming each one.

### Required options

| Option | Type | What it does |
|---|---|---|
| `SpoolDirectory` | string | The folder for spooled records, one file per record. Use storage that outlives the process, such as a mounted volume. A container's own filesystem is wiped on restart and would take unsent records with it. The app fails to start if the folder cannot be created or written to. |
| `EndpointBaseUrl` | string | The base URL of the audit endpoint. Must be an absolute `http` or `https` URL with no query string or fragment. Records go to `{EndpointBaseUrl}/audit`. |
| `ModuleName` | string | The name of the module producing the records. It travels inside the encrypted payload. |
| `Version` | string | The version of that module, also inside the payload. |

### Size limits

| Option | Type | Default | What it does |
|---|---|---|---|
| `MaxPayloadBytes` | int | `65536` (64 KB) | The largest record accepted. A bigger record fails at the call that wrote it, before anything is encrypted or spooled. Must be greater than 0. |
| `SpoolMaxBytes` | long | `268435456` (256 MB) | Size cap of the spool. When the spool reaches this or `SpoolMaxEntries`, new records are dropped and each drop is logged at Critical. Must be greater than 0. |
| `SpoolMaxEntries` | int | `100000` | Record count cap of the spool. Must be greater than 0. |
| `SpoolFullRecheckInterval` | TimeSpan | 1 second | How often a full spool is measured on disk again, so room freed by the worker is noticed. Also the window in which dropped records are logged as one Critical summary line instead of one line each. Must be greater than 0. |

### Delivery

| Option | Type | Default | What it does |
|---|---|---|---|
| `DrainInterval` | TimeSpan | 5 seconds | How often the worker looks for records to send. Also the first wait after a failed attempt. Kept short, because records still on disk are lost if the host is destroyed. Must be greater than 0. |
| `MaxDrainBackoff` | TimeSpan | 5 minutes | The longest wait between attempts while the endpoint keeps failing. The wait doubles after each failure and stops growing here. Must be greater than 0. |
| `DeliveryTimeout` | TimeSpan | 30 seconds | How long one delivery attempt may take (a whole batch, when batching). A timeout is treated as temporary and retried. Must be greater than 0, or `Timeout.InfiniteTimeSpan`. |
| `DeliveryBatchSize` | int | `1` | The most records sent in one request, from 1 to 1024. At 1 each record goes on its own to `/audit`. Above 1, a run of records goes as one JSON array to `/audit/batch`, which your receiver must support first. It must then store a batch whole or reject it whole. |
| `ShutdownDrainTimeout` | TimeSpan | 5 seconds | How long a stopping host waits for the worker to send what is left. Keep it well inside the host's own shutdown timeout. Nothing is lost when it runs out, the next start sends the rest. Must be greater than 0. |
| `UnreadableRetryLimit` | int | `10` | How many drain cycles in a row a spool file may fail to open before it is moved to `dead-letter/` as unreadable. Without a limit, one stuck file would block every record behind it. Must be greater than 0. |
| `ConfigureProducerCredentials` | `Action<HttpClient>` or not set | not set | Sets the credentials the endpoint needs (an `Authorization` header, a client certificate) on the worker's `HttpClient`. This authenticates the HTTP request only. It is not an encryption key. |

### Health check

The sink registers a health check named `encrypted-spool-audit` with the tag `audit`. It reports the worst of these signals.

| Option | Type | Default | What it does |
|---|---|---|---|
| `SpoolWarnStatus` | HealthStatus | `Unhealthy` | Reported from half the cap on, while writes still succeed. A spool that stopped draining is on its way to losing records, so the default takes the instance out of rotation. |
| `SpoolFullStatus` | HealthStatus | `Unhealthy` | Reported once the spool is full and records are being dropped. |
| `UnreachableStatus` | HealthStatus | `Degraded` | Reported once three delivery attempts in a row have failed, until one gets through. Milder, because no record is lost yet. |
| `OldestRecordMaxAge` | TimeSpan or not set | not set (signal off) | How old the oldest spooled record may get before the check reports `OldestRecordStatus`. It catches a worker stuck on one record while the spool is far from full. Must be greater than 0 when set. The same age is always available as the `audit_spool_oldest_age_seconds` gauge. |
| `OldestRecordStatus` | HealthStatus | `Degraded` | Reported once the oldest record is older than `OldestRecordMaxAge`. |

Example:

```csharp
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

builder.Services.AddSingleton<IAuditPayloadEncryptor, MyEncryptor>();

builder.AddEncryptedSpoolAuditSink(options =>
{
    options.SpoolDirectory = "/var/lib/myapp/audit-spool";
    options.EndpointBaseUrl = "https://audit.internal.example";
    options.ModuleName = "orders";
    options.Version = "2.4.1";
    options.DeliveryBatchSize = 50;
    options.OldestRecordMaxAge = TimeSpan.FromMinutes(30);
    options.ConfigureProducerCredentials = client =>
        client.DefaultRequestHeaders.Authorization = new("Bearer", GetToken());
});
```

Register `IAuditPayloadEncryptor` as a thread-safe singleton. It owns all key handling. To hear about records the endpoint rejected, register any number of `IAuditRejectionObserver` singletons (ADR 0030).

## OpenTelemetry environment variables

The OTLP address and protocol are read **only from environment variables**, never from `SerilogStepUp` (ADR 0013).

| Variable | What it does | Default |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | The collector address. Setting it also turns `EnableOtlpExporter` on by default. | not set (OTLP off) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc`, or `http/protobuf` (also accepted as `http`) for an HTTP collector. Any other value falls back to `grpc`. | `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers sent with each export, usually for authentication, as `key1=value1,key2=value2`. Values may be percent-encoded. | not set |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | The longest wait for one log export, in milliseconds (ADR 0036). A missing, invalid or non-positive value means 10 seconds. | `10000` |
| `OTEL_RESOURCE_ATTRIBUTES` | Attributes that describe the service, as `key1=value1,key2=value2`, added to exported logs. | not set |
| `OTEL_SERVICE_NAME` | The service name that identifies your app. The library does not read it itself. The Serilog OTLP sink reads it for logs, and the OpenTelemetry SDK for traces and metrics. | `unknown_service:<process name>` |

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=x-api-key=abc123
OTEL_SERVICE_NAME=orders-api
```

## The Serilog section

The library also reads the standard `Serilog` section with Serilog's own configuration reader. A few keys behave in a special way:

- **`Serilog:WriteTo`**. Sinks declared here are attached behind the step-up gate, so they get the same events as the library's own outputs (ADR 0005).
- **`Serilog:AuditTo`**. Sinks declared here are attached to the root logger and see every event, at every level. Having any entry here also makes the library enrich every event, instead of only the ones that will be exported (ADR 0026).
- **`Serilog:Using`**. Names extra Serilog assemblies. In a Native AOT app, any config-only sink or enricher outside the library's own set must be listed here (ADR 0035).
- **`Serilog:MinimumLevel:Default`** is replaced. The root always runs at `Verbose`, so the buffer and the trigger see every event. Use `BaseLevel` instead. `Serilog:MinimumLevel:Override` entries still apply, and still reload when the file changes (ADR 0012). An event an override drops never reaches the buffer.
- `Enrich`, `Properties`, `Filter` and `Destructure` work as usual on the root.

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": { "MyApp.VeryChatty": "Error" }
    },
    "WriteTo": [
      { "Name": "File", "Args": { "path": "logs/app.log" } }
    ]
  }
}
```

## Parameters of AddStepUpLogging

These are code-only settings, passed to `builder.AddStepUpLogging(...)`.

| Parameter | Type | Default | What it does |
|---|---|---|---|
| `configureOptions` | `Action<StepUpLoggingOptions>` | none | Sets options in code. Runs after the configuration section is read, so it wins. |
| `configure` | `Action<IServiceProvider, LoggerConfiguration>` | none | Extra Serilog setup in code, for example a sink of your own. It runs after the library's sinks. Setting it makes the library enrich every event, like `Serilog:AuditTo`. |
| `configSectionName` | string | `"SerilogStepUp"` | The configuration section the options are read from. |
| `logFilePath` | string | none | When set, also writes exported events to this file. |

```csharp
builder.AddStepUpLogging(
    options => options.EnableConsoleLogging = true,
    configSectionName: "Logging:StepUp",
    logFilePath: "logs/app.log");
```
