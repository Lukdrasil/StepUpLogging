# ADR 0023: Static logger preserved by default

- Status: Accepted
- Date: 2026-09-30
- Issue: #31

## Context

`AddStepUpLoggingInternal` called `AddSerilog` without `preserveStaticLogger`, so
Serilog.Extensions.Hosting assigned the built logger to the process-global `Log.Logger` when
`ILoggerFactory` resolved (inside `Build()`), and called `Log.CloseAndFlush()` when the host was
disposed. With more than one host in a process, the last host built owned every `Log.*` call, and
disposing any host closed the static logger for the others.

Two Serilog 10.0.0 facts constrain the fix:

- With `preserveStaticLogger: true`, DI still resolves `Serilog.ILogger` (a `ForContext` wrapper
  over the callback logger), and `DiagnosticContext`/`IDiagnosticContext` are still registered.
- `RequestLoggingMiddleware` writes to `RequestLoggingOptions.Logger` when it is set, else to
  `Log.ForContext` per request. Preserving the static logger alone would send request completion
  events to whatever `Log.Logger` holds, by default a silent logger.

## Decision

1. **`Log.Logger` is left unchanged by default.** `AddSerilog` is called with
   `preserveStaticLogger: !SetStaticLogger`.

2. **`StepUpLoggingOptions.SetStaticLogger` (default `false`) opts back in** to the pre-5.0.0
   behavior. It is read once at registration from its own snapshot: the configuration section
   bound, then `configureOptions` applied. The existing `configSnapshot` and its
   `EnableOtlpExporter` check are left unchanged. Rejected: applying `configureOptions` to the
   shared `configSnapshot`, which would also change OTLP exporter registration, a second behavior
   change that belongs to its own issue. Rejected: always preserving and assigning `Log.Logger`
   from a hosted service at start, which assigns later than today and adds a type.

3. **`UseStepUpRequestLogging` always sets `RequestLoggingOptions.Logger` to
   `GetService<Serilog.ILogger>()`.** Request completion events reach the DI logger whether or not
   `Log.Logger` was assigned. `null` keeps Serilog's static fallback. Rejected: setting it only when
   `SetStaticLogger` is false; with the option on, both loggers are the same pipeline, so the
   branch has no effect.

4. **The step-up and step-down warnings go to the controller's bypass logger.**
   `StepUpLoggingController` writes the "Logging step up..." and "Logging step down..." warnings
   through `_summaryLogger ?? Log.Logger` instead of the static `Log.Warning`, in both modes.
   With the static logger preserved, `Log.Warning` reaches Serilog's silent logger and the lines
   would be lost.

## Consequences

- Breaking for consumers who log through static `Log.*`, rely on a bootstrap `ReloadableLogger`
  being reloaded by the host, or call `Log.CloseAndFlush()` to close the host's logger. MIGRATION.md
  "Migrating to v5.0.0" lists the steps.
- Hosts in one process are isolated: each logs to its own pipeline and survives the disposal of
  the others.
- The step-up and step-down warnings no longer pass through the root pipeline. Sinks added in the
  configure hook and root `Serilog:Filter`, `Enrich` and `Properties` do not see them, and neither
  do `PreErrorBufferSink` or the trigger sink. The step-down warning now exports even when
  `BaseLevel` is above Warning; before, it was written after the level was lowered and dropped.
- `SetStaticLogger` is not reloadable. Changing it after registration has no effect.
