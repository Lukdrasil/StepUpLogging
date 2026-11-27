# ADR 0043 - A failed request can write one line instead of two

- Status: Accepted
- Date: 2026-11-27
- Amends: ADR 0032

## Context

With `AlwaysLogRequestSummary` on, every inbound request writes its summary through the bypass logger, and a failed
request also writes the request completion event of `UseSerilogRequestLogging` at `Warning` (4xx) or `Error` (5xx,
exception), which the default `BaseLevel` exports. Under a flood of refused requests, for example a client hitting a
rate limit with 429, that doubles the log volume for the same information. The summary carries fields the completion
event lacks: `ClientKind`, `UserAgent`, `Route`, `Aborted`, `ClientId` and, in `Full`, `Protocol`, `Scheme`, `Host`,
the content lengths, `IsAuthenticated`, `AuthScheme`, `EndpointName` and `ForwardedFor`. Several services use the
library, so the change must be opt-in.

## Decision

**D1. `RequestSummaryOnError: Keep | Omit`, default `Keep`.** `Keep` is the earlier behaviour exactly. `Omit` acts only
with `AlwaysLogRequestSummary`; set without it, the startup report warns that it has no effect and names it in the
summary line (`omitted on error`). Outbound summaries are unchanged.

**D2. A request is failed when it ends with status 400 or above or with an unhandled exception.** A 2xx or 3xx always
writes its summary, even when its Information completion event is exported (BaseLevel `Information`, stepped up,
Diagnostic). A client abort is not a failure by itself: its completion event is Information, and its summary is
omitted only when it also ends with status 400 or above or an exception, and Information is exported.

**D3. The summary is omitted only when the completion event was exported, and the sinks that export it say so.**
Under `Omit` the summary middleware issues a `CompletionReceipt` per request and stores it in an `AsyncLocal`. That
channel works because Serilog.AspNetCore writes the completion event inside the middleware's `await next()`, and the
root sinks run synchronously within that write. Three things then happen on the same execution context:
1. The completion event's `EnrichDiagnosticContext` callback sets the summary's fields on a failed request and marks
   the receipt carried.
2. `StepUpSink`, right before it hands an event to the output logger, and `BypassSink`, right after it writes one to
   the bypass logger, call `CompletionReceipt.NoteExported`. That call marks the receipt exported when the event is
   the completion event, recognised by the source `Serilog.AspNetCore.RequestLoggingMiddleware`.
3. Once `next()` returns, the middleware writes the summary unless the receipt is both carried and exported.

So the decision follows the export instead of predicting it. In every case below the event is not exported, so the
summary is written as under `Keep` and no request ends with zero lines:
- A completion event below the export minimum, for example a 4xx `Warning` under `BaseLevel` `Error`.
- An event held back into the pre-error buffer, including one the buffer later flushes.
- An event dropped by a root `Serilog:Filter`.
- An event a step-down leaves behind between enrichment and `StepUpSink`.
- An event written to a consumer-supplied completion logger that does not write to the library's root sinks.

An exception is noted in `GetLevel`, which runs before the callback, because its 500 is not on the response yet when
the event is written. The same note gives the body capture of ADR 0009 its 500. A test pins the source name, so a
Serilog.AspNetCore upgrade that renames it fails a test.

The channel is hidden from the sink interfaces. **A sink added later that hands events to an output logger must call
`CompletionReceipt.NoteExported` too.** If it does not, its completion events leave the summary in place, which costs
a line but never loses one.

**D4. The completion event carries the summary's fields.** The module builds the inbound summary at
`RequestSummaryDetail`, with its redaction (ADR 0032 D5), and sets on the completion event `ClientKind`, `UserAgent`
and every optional field under the key the summary writes it with: the long name, or in `Minimal` the short key of
ADR 0033, so a query written against the summary finds the same field on the completion event. The summary fields
the completion event sets itself (`ClientIp`, `Jti`, `QueryString`, `RouteParameters`) are not copied, so no value
appears twice under two keys. `InboundRequestSummary` owns that list next to the fields it builds. `Direction`, `Method`, `Path`, `StatusCode` and
`ElapsedMs` are not copied: `RequestMethod`, `RequestPath`, `StatusCode` and `Elapsed` carry them. The summary's
`ElapsedMs` is measured around the whole inner pipeline, the completion event's `Elapsed` inside it, so the two differ
by the time of the middleware between them. A failure while building the fields leaves the summary to be written.

## Consequences

- With `Omit`, a failed request writes one event at `Warning` or `Error` instead of an Information summary plus that
  event. A query for failed requests that reads `IsRequestSummary` events must read the completion event instead.
- The completion event of a failed request gains the summary's properties, under the summary's keys; with
  `RequestSummaryDetail` `Minimal` the event mixes its own long names with the summary's short keys.
- The completion event is exported through its normal route. Under `Omit` it gains no marker, and the step-up switch,
  the category options and every filter apply to it as under `Keep`.
- The completion event of a failed request carries the summary's fields whenever a receipt is issued, also when it is
  then held back. A buffered completion event that is flushed later is therefore written next to the summary.
- Every exported event costs one `AsyncLocal` read in `StepUpSink` or `BypassSink`. It costs more only inside a
  request whose completion event carries the summary, and there only a `SourceContext` comparison.
