# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`Lukdrasil.StepUpLogging` is a NuGet library for ASP.NET Core (.NET 10 / C# 14) that dynamically raises Serilog's minimum log level in response to errors and lowers it again after a configurable timeout. It is not an application — there is no runnable entry point in `src/`.

## Commands

```bash
# Build (treat warnings as errors, matches CI)
dotnet build src/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.csproj --configuration Release /p:TreatWarningsAsErrors=true

# Run all tests
dotnet test --project tests/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.Tests.csproj

# Run a single test (xunit v3 / Microsoft.Testing.Platform filter syntax)
dotnet test --project tests/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"

# Pack NuGet (output to ./nuget-packages/)
dotnet pack src/Lukdrasil.StepUpLogging/Lukdrasil.StepUpLogging.csproj --configuration Release --output ./nuget-packages
```

**Publish to NuGet** is automated via GitHub Actions on `v*` tag pushes (see `.github/workflows/publish.yml`). Do not push tags manually without the user's explicit instruction.

## Architecture

### Core components (`src/Lukdrasil.StepUpLogging/`)

| File | Role |
|---|---|
| `StepUpLoggingController` | Public. Thread-safe state machine owning the `LoggingLevelSwitch`, a `Timer` for step-down, OTel counters, and a reference to the bypass logger. `Trigger()` raises the level; a timer callback lowers it after `DurationSeconds`. |
| `StepUpTriggerSink` | Internal Serilog sink. Observes `Error`/`Fatal` events and enqueues to a bounded `Channel<bool>` processed by a background `Task` that calls `controller.Trigger()`. Non-blocking so it never stalls the logging pipeline. |
| `PreErrorBufferSink` | Internal Serilog sink. Maintains a per-context ring buffer keyed by W3C TraceId (`Activity.Current`). On `Error`/`Fatal`, flushes buffered events to the bypass logger and clears the buffer. LRU eviction bounds memory. |
| `SummarySink` | Internal Serilog sink. Forwards any event tagged `IsRequestSummary=true` to the bypass logger, so request summaries export at their own level independently of the step-up `LevelSwitch`. |
| `ActivityContextEnricher` | Adds `ParentSpanId`, `TraceFlags`, `TraceState` from `Activity.Current` — fields not provided by `Serilog.Enrichers.OpenTelemetry`. |
| `StepUpLoggingExtensions` | Public static class. Exposes `AddStepUpLogging()` and `UseStepUpRequestLogging()`, all three `ActivitySource` instances, `AddStepUpLoggingMeters()`, and `CompiledRedactionPatterns`. All Serilog pipeline wiring happens here. |
| `StepUpLoggingOptions` | All configuration knobs with defaults. Bound from `"SerilogStepUp"` appsettings section. |

### Serilog pipeline (wired in `AddStepUpLoggingInternal`)

The root logger is deliberately set to `MinimumLevel.Verbose()` so the buffer and trigger sinks see every event regardless of the current level. Actual export is gated inside sub-loggers:

```
Root (Verbose)
 ├─ Sub-logger: gated by LevelSwitch (the step-up switch), IsRequestSummary filtered out → OTLP / Console / File
 ├─ PreErrorBufferSink (Verbose) → bypass logger (on Error)
 ├─ StepUpTriggerSink → StepUpLoggingController.Trigger()
 └─ SummarySink → bypass logger (for IsRequestSummary=true events)
```

**Bypass logger**: created directly inside the `AddSerilog` callback (not via DI) to avoid a circular deadlock. `AddSerilog` registers `Serilog.ILogger` as a factory that depends on `ILoggerFactory`, which in turn depends on this very callback, so calling `GetRequiredService<Serilog.ILogger>()` inside the callback deadlocks.

### OTLP configuration

OTLP endpoint, protocol, and headers are read **from environment variables only** (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES`). They are not configurable via `StepUpLoggingOptions`.

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
