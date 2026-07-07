# ADR 0004 — Request body capture via early buffering middleware

- Status: Accepted
- Date: 2026-07-07

## Context
`CaptureRequestBody` calls `httpContext.Request.EnableBuffering()` inside
`UseSerilogRequestLogging`'s `EnrichDiagnosticContext`, which runs **after** the MVC pipeline
has already read the body from a non-buffered stream. The re-read returns 0 bytes, or throws
under Kestrel's `AllowSynchronousIO=false`. Requests without a `Content-Length` (chunked)
always record `[EMPTY]`. Existing tests miss it because the TestServer handler never consumes
the body.

## Decision
When `CaptureRequestBody` is enabled, register a thin middleware **before** Serilog request
logging that calls `EnableBuffering()` prior to `await next()`, so the body stays replayable.
The enricher then rewinds (`Body.Position = 0`) and reads **asynchronously** up to
`MaxBodyCaptureBytes`, redacts, and records it. Chunked bodies are read up to the cap rather
than gated on `Content-Length`.

## Consequences
- Body capture works with real handlers that consume the request body.
- Sync-IO exceptions under Kestrel are avoided (async read).
- A regression test must use a handler that actually reads the body and assert the captured,
  redacted content — the gap the current suite has.
