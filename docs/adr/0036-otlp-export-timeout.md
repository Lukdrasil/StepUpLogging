# ADR 0036 - OTLP export timeout from OTEL_EXPORTER_OTLP_TIMEOUT

- Status: Accepted
- Date: 2026-11-11

## Context

A process whose collector accepts the connection and never answers (a blackhole address, a stalled proxy) waits
200 s in `host.Dispose()`, even when it logged nothing but the startup report. Measured with
`OtlpExportTimeoutTests`, for both `grpc` and `http/protobuf`.

The cause is the Serilog OTLP sink (Serilog.Sinks.OpenTelemetry 4.2.0). It sends through an `HttpClient` it
creates without a timeout setting (`HttpExporter`, and the `HttpClient` it hands `GrpcChannel` in `GrpcExporter`), so
one export waits the 100 s `HttpClient` default. `ConfigureOutputSinks` attaches the sink to two loggers, the gated
output logger and the bypass logger (ADR 0003 addendum keeps both). Each flushes its last batch once on dispose, one
after the other: 2 x 100 s. With a 2 s bound the dispose took 4.0 s, which confirms one export per logger.

The OpenTelemetry SDK exporter that `AddStepUpLogging` registers with `UseOtlpExporter()` already honors
`OTEL_EXPORTER_OTLP_TIMEOUT`: in a started host against the same collector its shutdown took 10 s by default and
1 s with the variable at 500 ms (logs and metrics, one export each). It needs no change.

## Decision

**D1. The Serilog OTLP sink waits at most `OTEL_EXPORTER_OTLP_TIMEOUT` milliseconds for one export.**
`ConfigureOutputSinks` sets `HttpMessageHandler` to `OtlpExportTimeoutHandler`, a `DelegatingHandler` that cancels
the request after the timeout. Its inner handler is the sink's own default
(`SocketsHttpHandler { ActivityHeadersPropagator = null }`), so nothing else about the request changes. The bound
applies to every export, not only at dispose, as the variable means in the OpenTelemetry specification.

**D2. The value follows the specification.** Milliseconds, default 10000. A missing, unparseable or non-positive
value falls back to the default. Only the generic variable is read, like the endpoint, protocol and headers; the
signal-specific `OTEL_EXPORTER_OTLP_LOGS_TIMEOUT` is not.

Rejected: a separate, shorter bound that applies only at dispose. Serilog's `Async` and batching sinks offer no
bounded dispose, so it needs a wrapper that abandons the flush thread, and a second setting no other OTLP exporter
has. Rejected: one OTLP sink shared by both loggers, which halves the worst case but is the restructure the
ADR 0003 addendum turned down.

## Consequences

- A process exits within about twice `OTEL_EXPORTER_OTLP_TIMEOUT` (20 s by default) when the collector does not
  answer, instead of 200 s. A command-line tool sets the variable lower, for example `1000`.
- A collector that answers within the timeout sees no change. An export slower than the timeout, which before
  succeeded within 100 s, now fails and its batch is dropped (reported to Serilog `SelfLog`); raise the variable.
- Ceiling: the bound covers the request until the response headers arrive. A collector that sends headers and then
  stalls the body (HTTP) or the trailers (gRPC) is still bounded only by the 100 s `HttpClient` default.
- `OtlpExportTimeoutTests` pins the bound under JIT for both protocols, and the generic-host consumer in
  `PackageConsumerBuildTests` pins it in a Native AOT binary.
