# ADR 0003 — Output sinks built once, single-owned, disposed

- Status: Accepted
- Date: 2026-07-07

## Context
Two problems in the same area:
1. `ConfigureOutputSinks` runs **twice** with the same `logFilePath` (once for the
   `StepUpSink` inner logger, once for the bypass logger). The Serilog File sink takes an
   exclusive lock, so the second logger silently drops to SelfLog; likewise two independent
   OTLP exporters open (two gRPC channels, double buffering).
2. The bypass logger (async + OTLP) is **never disposed**, so its buffers are not flushed on
   shutdown — losing exactly the highest-value events (errors, flushed buffers, summaries).
   The `SummarySink` comment claiming the target is "DI-managed" is false; nobody owns it.

## Decision
Build the output sink chain **once** and share it, with a single explicit owner responsible
for disposal. The bypass logger is registered so that application shutdown flushes and
disposes it (owner = the composition that created it; the last sink in the pipeline disposes
it, or it is registered as a disposable singleton in DI). File sinks that must be opened twice
use `shared: true` as a defensive fallback. No sink claims ownership of a logger it does not
create.

## Consequences
- One OTLP exporter, one File handle; no silently-dropped bypass output.
- Bypass buffers flush on graceful shutdown; error/summary logs are not lost.
- Disposal order matters: the bypass logger must outlive the sinks that write to it and be
  disposed after them. Tests assert a single exporter/handle and that dispose flushes.

## Addendum (2026-07-07) — chosen implementation: minimal, low-risk

The "share physical sink instances / one shared logger" option was weighed against the
developer's overriding priority: **no unexpected change to log content**. Collapsing to one
shared logger risks the enrichment double-apply trap (inner logger has no enrichers; bypass
does). We therefore implement the **minimal** fix, which resolves the two concrete defects
without touching enrichment or logger structure:

1. **Dispose the bypass logger via its existing owner, the controller.** `StepUpLoggingController`
   already holds the bypass logger (`_summaryLogger`, set by `SetSummaryLogger`), is already
   `sealed IDisposable` (it owns the step-down `Timer`), and is already disposed by the DI
   container at shutdown. Its `Dispose` gains one line: `(_summaryLogger as IDisposable)?.Dispose()`.
   Because the controller singleton is *resolved inside the `AddSerilog` factory callback* (to set the
   bypass logger on it), it is always created before the root Serilog logger; MS DI disposes singletons
   in reverse *creation* order, so the container disposes the controller *after* the root logger — so the
   buffer/summary/immediate sinks (and
   `PreErrorBufferSink.Dispose`'s best-effort flush *to* the bypass logger) all run first, then the
   bypass logger is disposed, flushing its async OTLP/File buffers. This ordering is exactly what
   the "disposal order matters" consequence requires, achieved with no new types and no
   `IHostApplicationLifetime` wiring. (Preferred over an `ApplicationStopped` hook, which would
   dispose the bypass logger *before* the buffer sink's dispose-time flush and lose those events.)
2. **File sink `shared: true`.** The bypass and step-up-inner loggers may both open the same
   file path; `shared: true` prevents the exclusive-lock self-log drop. (OTLP's two exporters are
   left as-is — harmless duplication, and deduping them would require the risky shared-logger
   restructure this addendum rejects.)

Not done here (rejected as behavior-risky): collapsing to a single shared output logger, or
deduping the OTLP exporter. Tests assert the bypass logger is disposed on host shutdown and that
a File path shared by both loggers does not throw / drop.
