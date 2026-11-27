# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`Lukdrasil.StepUpLogging` is a NuGet library for ASP.NET Core (multi-targets `net10.0;net11.0`) that dynamically raises Serilog's minimum log level in response to errors and lowers it again after a configurable timeout. It is not an application — there is no runnable entry point in `src/`.

## Commands

```bash
# Build (treat warnings as errors, matches CI)
dotnet build src/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.csproj --configuration Release /p:TreatWarningsAsErrors=true

# Run all tests
dotnet test --project tests/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.Tests.csproj

# Run a single test or class (xunit v3 / Microsoft.Testing.Platform; --filter "FullyQualifiedName~..." runs 0 tests)
dotnet test --project tests/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.Tests.csproj --filter-method "*ClassName.MethodName"
dotnet test --project tests/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.Tests.csproj --filter-class "*.ClassName"

# Pack NuGet (output to ./nuget-packages/)
dotnet pack src/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.csproj --configuration Release --output ./nuget-packages
```

**Publish to NuGet** is automated via GitHub Actions on `v*` tag pushes (see `.github/workflows/publish.yml`). Do not push tags manually without the user's explicit instruction.

## Architecture

### Core components (`src/Lukdrasil.StepUpLogging/`)

| File | Role |
|---|---|
| `StepUpLoggingController` | Public, created only by `AddStepUpLogging` (constructors internal). Callers use `Trigger()`, `IsSteppedUp` and `CurrentLevel`. Thread-safe state machine owning the internal `LoggingLevelSwitch`, a `Timer` for step-down, OTel counters, and the bypass logger, which `StepUpPipeline` attaches through `SetSummaryLogger` and the controller disposes (ADR 0003); it writes its step Warnings there and lends the logger to `RequestSummaryWriter`. `Trigger()` raises the level; a timer callback lowers it after `DurationSeconds`. In `Mode=Diagnostic` the switch starts at `DiagnosticLevel` and a `TimeProvider` timer returns it to Auto at `BaseLevel` after `DiagnosticDurationMinutes`; `Trigger()` is a no-op until then. |
| `StepUpTriggerSink` | Internal Serilog sink. Observes `Error`/`Fatal` events, skips those `CategoryPolicy.NeverTriggers`, and enqueues to a bounded `Channel<bool>` processed by a background `Task` that calls `controller.Trigger()`. Non-blocking so it never stalls the logging pipeline. |
| `PreErrorBufferSink` | Internal Serilog sink. Maintains a per-context ring buffer keyed by W3C TraceId (`Activity.Current`), buffering only held-back events `StepUpSink` hands it through `Hold` (events it did not export and the `CategoryVerdict` does not keep out), at/above the resolved `StepUpLevel`. It reads no category option. Dropped without export on dispose. On `Error`/`Fatal`, flushes buffered events to the bypass logger and clears the buffer. LRU eviction bounds memory. |
| `CategoryPolicy` | Internal. The one runtime reader of every category option, built once by `StepUpSettings` from the options: it drops blank prefixes, clears the NeverStepUp pin and the framework defaults in AlwaysOn, and matches through `CategoryPrefix`, so no caller matches a prefix. `Resolve` returns one `CategoryVerdict` per event: `ExportMinimum = max(switch, NeverStepUp pin unless diagnostic, floor)`, where the `CategoryFloors` entries decide first (most specific key wins) and only when none matches do the `FrameworkCategoryDefaults` rules of `FrameworkCategoryDefaultTable` apply (ADR 0029); while diagnostic is active only sources under a `DiagnosticExemptCategories` prefix get a floor. `KeptOutOfBuffer` is true for a held event below `Warning` whose floor came from a default not kept in the buffer or whose source is in `PreErrorBufferExcludeCategories`. `NeverTriggers` and `AlwaysExports` answer the two other lists. No memo, allocation-free. |
| `RequestSummary` | Internal. One inbound or outbound summary event, template `{Direction} {Method} {Path} {StatusCode} {ElapsedMs} ms {ClientKind} {UserAgent}`; classifies the raw User-Agent (`ClientKind`) before redacting it. `InboundRequestSummary` builds it from `HttpContext` per `RequestSummaryDetail` (ADR 0032), `OutboundRequestSummary` from the `HttpRequestMessage` of one factory client call. |
| `RequestSummaryWriter` | Internal. The one writer of request summaries: `WriteInbound` (skips `ExcludedPaths`) and `WriteOutbound` build the summary at `RequestSummaryDetail` with the redaction patterns, then write it at `RequestSummaryLevel` through the controller's bypass logger, read on each write; `Write` takes a summary already built. Never throws, so a summary cannot fail or mask the request or call. Built by the request middleware and by `OutboundRequestSummaryFilter`. |
| `OutboundRequestSummaryFilter` | Internal `IHttpMessageHandlerBuilderFilter`, always registered; it reads `AlwaysLogOutboundRequestSummary` from `StepUpSettings` and, when set, puts `OutboundRequestSummaryHandler` outermost in every factory client except the audit and OTLP exporter clients (`ExcludedClients`). |
| `BypassSink` | Internal Serilog sink, two instances: `RequestSummaries` forwards events tagged `IsRequestSummary=true` and `Immediate` forwards events tagged `IsImmediate=true` to the bypass logger, so they export independently of the step-up `LevelSwitch`. |
| `ActivityContextEnricher` | Adds `ParentSpanId`, `TraceFlags`, `TraceState` from `Activity.Current` — fields not provided by `Serilog.Enrichers.OpenTelemetry`. |
| `StepUpLoggingExtensions` | Public static class. Exposes `AddStepUpLogging()`, `AddAuditLogging()`, `UseStepUpRequestLogging()`, `AddStepUpLoggingMeters()` and the three activity source names (`*ActivitySourceName`). Registers the options pipeline and services; the Serilog wiring is in `StepUpPipeline`. |
| `StepUpSettings` | Internal. The options resolved once from `IOptions<StepUpLoggingOptions>` (typed levels, durations, the `CategoryPolicy`, `ExcludedPaths`, sensitive headers, request logging flags). The only runtime reader of the options: per-event and per-request modules read its typed members; its raw `Options` is read only while the logger is built (pipeline, output sinks, redaction patterns, startup report). |
| `StepUpPipeline` | Internal. Builds the root logger, its gated enrichers, the gated output logger, the bypass logger and the root sinks in their fixed order. The only place that sets that order. |
| `OutputSinks` | Internal static class. The OTLP, console and file sinks, each async-wrapped, written to both output loggers. |
| `OtlpExport` | Internal static class. Registers the OTel SDK trace and metric exporters and configures the Serilog OTLP sink from the `OTEL_*` environment variables. |
| `StepUpRequestLogging` | Internal. The inbound request middleware: request summary, early body buffering and the Serilog completion event with redacted fields. |
| `RequestClient` | Internal static class. The one client-address and User-Agent rule (ADR 0008), shared by request logging, the summary and the audit trail. |
| `ExcludedPaths` | Internal. Matches `ExcludePaths` entries: exact, or a `*` prefix on a segment boundary, ignoring case. |
| `StepUpLoggingActivitySources` | Internal static class. The `ActivitySource` instances, so only the library starts activities under its names. |
| `ThrowContextEnricher` | Internal root enricher, registered only when `CaptureScopesOnException` is on (ADR 0038). A process-wide `FirstChanceException` handler records `LogContext` and `ExecutionContext` per exception; an event carrying that exception, or one wrapping it, gets the throw site's scopes as absent properties, deepest throw site first. |
| `AlwaysExportEnricher` | Internal root enricher, registered only when `CategoryPolicy.HasAlwaysExport`. Sets `IsImmediate=true` on an event `CategoryPolicy.AlwaysExports`, so `BypassSink.Immediate` exports it once and `StepUpSink` and the pre-error buffer skip it. |
| `EnrichmentGate` | Internal. `Needs(LogEvent)` is true when the level is at or above `min(live LevelSwitch, StepUpLevel, Error)` or the event carries `IsImmediate` or `IsRequestSummary`; `For(...)` returns the predicate the root enrichers run behind (`Enrich.When`), or always-true when the `configure` hook is set or `Serilog:AuditTo` has an entry (ADR 0026). |
| `CategoryPrefix` | Internal static class. The one `SourceContext` prefix rule for every category option (ADR 0021 D3): ordinal equal, or starts with prefix + `.`. |
| `StepUpLoggingOptions` | All configuration knobs with defaults. Bound from the `"SerilogStepUp"` section through `AddOptions().Bind().Configure().PostConfigure().ValidateOnStart()`. Read at runtime only through `StepUpSettings`; `EnableOtlpExporter` and `SetStaticLogger` are also read once at registration, because they decide which services and which `AddSerilog` call are registered. |
| `StepUpLoggingOptionsValidator` | Internal `IValidateOptions`. One failure message per invalid option, naming the key and its value; checks enums, level names, ranges, floors, Diagnostic exemptions and that each `RedactionRegexes` entry parses. |
| `StartupReport` | Internal. Written once through the bypass logger when the logger is built: an Information summary of the effective configuration, a Warning per unknown key under the options section, and a Warning per option set to a value with no effect. |
| `SerilogConfigurationAssemblies` | Internal static class. The single `ReadFrom.Configuration` entry point (`ReadSerilogSection`) for all three call sites. Under JIT the call is unchanged; when `RuntimeFeature.IsDynamicCodeSupported` is false it hands the reader explicit assemblies: `ConfigurationLibraryAssemblies` plus each non-blank `Serilog:Using` entry (ADR 0035 D1). Every Serilog assembly the library references is either in that table or in `DeliberateExclusions` (with a reason), guarded by a test. |

### Serilog pipeline (wired in `StepUpPipeline`)

The root logger is deliberately set to `MinimumLevel.Verbose()` so the buffer and trigger sinks see every event regardless of the current level. Actual export is gated inside sub-loggers. The root's library enrichers (`ApplyCommonEnrichers`) run behind the `EnrichmentGate`, so a Debug event nobody will export is not enriched; `ThrowContextEnricher` and `AlwaysExportEnricher` run before the gate (ADR 0038), `RedactionEnricher` stays last and ungated, and the bypass logger enriches ungated. Inside `Enrich.When` make one call per enricher on the `LoggerEnrichmentConfiguration`; a chained `.Enrich.` escapes the gate (ADR 0026):

```
Root (Verbose)
 ├─ Sub-logger: gated by LevelSwitch (the step-up switch), IsRequestSummary filtered out → OTLP / Console / File
 ├─ PreErrorBufferSink (held-back events from StepUpSink, floored at StepUpLevel) → bypass logger (on Error)
 ├─ StepUpTriggerSink → StepUpLoggingController.Trigger()
 ├─ BypassSink.RequestSummaries → bypass logger (for IsRequestSummary=true events)
 └─ BypassSink.Immediate → bypass logger (for IsImmediate=true events)
```

**Bypass logger**: created directly inside the `AddSerilog` callback (not via DI) to avoid a circular deadlock. `AddSerilog` registers `Serilog.ILogger` as a factory that depends on `ILoggerFactory`, which in turn depends on this very callback, so calling `GetRequiredService<Serilog.ILogger>()` inside the callback deadlocks.

### Audit pipeline

Audit writes bypass Serilog entirely (ADR 0016 D1): `IAuditLogger<T>.AuditAsync()` goes through the internal `AuditTrail`, which enriches the record and calls `IAuditEventSink.WriteAsync()` directly. `EncryptedSpoolAuditSink` → spool (write-ahead, fsync, atomic rename) → `DrainWorker` → configured endpoint; the delivery contract (which status codes are durable, permanent, or transient) is defined in ADR 0020 D7.

### Encrypted spool sink (`src/Lukdrasil.StepUpLogging/EncryptedSpool/`)

| File | Role |
|---|---|
| `IAuditPayloadEncryptor` | Public port. Owns all crypto and key handling — algorithm, acquisition, caching, rotation. The sink calls `EncryptAsync` and never sees a key; the consumer implements and registers it. |
| `IAuditRejectionObserver` / `AuditRejection` | Public, optional (ADR 0030). Each registered observer is told the status and top-level problem+json `code` of a record the endpoint rejected on its own, after it is dead-lettered; it informs only (e.g. refresh a sealing key on `422 sealing.unknown_key`), cannot retry or re-seal, and is bounded by `DeliveryTimeout`; a throw or timeout is logged at Warning and the cycle continues. None registered = no body read, behaviour unchanged. Register as thread-safe singletons. |
| `EncryptedSpoolAuditSink` | Internal `IAuditEventSink`. Serializes each record, encrypts it via the port, and spools it durably before returning `Stored`. A write reserves room in `SpoolUsageTracker` (a tally of disk plus writers in flight) before it writes, so concurrent fsyncs overlap and the cap still holds; a full spool refuses from the tally and re-measures the disk at most once per `SpoolFullRecheckInterval`. A full spool is the only case that returns `Dropped`, reported at Critical by `DroppedRecordLog` at a bounded rate (first drop of a window by name, the rest as one summary); every other failure (encryptor throws, serialization fails) propagates unchanged. |
| `SpoolWriter` / `SpoolReader` | Internal. `SpoolWriter` writes each envelope to `.tmp`, fsyncs, then atomically renames it to `{createdUtc}-{eventId}.env`; a `.tmp` surviving a crash is promoted to `.env` (not deleted) if it still parses whole, deleted only if truncated. `SpoolReader` reads the spool directory oldest-first, skipping in-flight `.tmp` files. |
| `SpoolJsonContext` | Internal. Source-generated JSON for the spool (ADR 0035 D2, D3): `SpoolEnvelopeJsonContext` (no naming policy) behind `SpoolEnvelope.ToUtf8Bytes`/`FromUtf8Bytes`, which own the envelope wire format, and the camelCase `SpoolPayloadJsonContext` that declares the allowed `Data` value types. `SpoolPayloadJson` adds a reflection fallback for undeclared types only while `JsonSerializer.IsReflectionEnabledByDefault`; the wire bytes are unchanged. |
| `DrainWorker` | Internal `BackgroundService`. Delivers spooled records oldest-first to the configured endpoint, posting the stored bytes unchanged, and deletes each on 2xx (and releases it from `SpoolUsageTracker`). `DeliveryBatchSize` above 1 posts a run as one JSON array to `/audit/batch`; a rejected batch is redelivered record by record to `/audit`. Three routes to `dead-letter/`: a permanent rejection (`400`/`409`/`413`/`415`/`422`), a corrupt (unparseable) spool file, or a file unreadable past `UnreadableRetryLimit`. **Everything else** (3xx, 401/403/407, 404/405, 408, 429, 5xx, network faults, timeouts, an unreadable file within the limit) is transient and retried with backoff, so one expired credential cannot walk the spool into `dead-letter/`. A record with a failed attempt gets a record trace (`StuckRecordTraces`, ADR 0037) that its retry Warnings, dead-letter Critical and closing Information entry (logged when it is finally delivered) share. |
| `EncryptedSpoolHealthCheck` | Internal `IHealthCheck`. Reports the worst of three signals: spool fill (warn/full thresholds), a non-empty `dead-letter/`, and endpoint reachability. |
| `StepUpLoggingEncryptedSpoolExtensions` | Public static class. Exposes `AddEncryptedSpoolAuditSink()`, which registers the sink, `DrainWorker`, and health check, and calls `AddAuditLogging<EncryptedSpoolAuditSink>` internally — do not also call `AddAuditLogging` directly for this sink. |

### OTLP configuration

OTLP endpoint, protocol, and headers are read **from environment variables only** (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES`). They are not configurable via `StepUpLoggingOptions`. `EnableOtlpExporter` defaults to whether `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### Testing

Tests use **xunit v3** with `Microsoft.Testing.Platform`. The library's `InternalsVisibleTo` attribute (in the csproj) exposes internal types to the test project without needing `[assembly:]` attributes.

Do not add "Arrange / Act / Assert" comments in test methods. Follow the naming style used in nearby test files.

## Coding Conventions

- C# 14 features are preferred (file-scoped namespaces, primary constructors, pattern matching, switch expressions).
- `is null` / `is not null` — never `== null`.
- XML doc comments on all `public` members.
- `sealed` on internal implementation classes unless inheritance is needed.
- Sinks must implement `IDisposable` (and `IAsyncDisposable` when they own background tasks).
- Redaction is applied to query strings, route values, headers, and request bodies via `CompiledRedactionPatterns.Redact()` — never bypass it for user-supplied values.
