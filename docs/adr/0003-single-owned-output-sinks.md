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
