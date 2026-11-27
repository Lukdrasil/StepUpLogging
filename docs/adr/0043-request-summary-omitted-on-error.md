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

**D3. The summary is omitted only when the completion event is exported.** The decision is made in the completion
event's `EnrichDiagnosticContext` callback, which Serilog.AspNetCore calls only for an event the root logger accepts,
right before it writes it. There the module asks `CategoryPolicy.Exports`, the rule `StepUpSink` applies, for the
source `Serilog.AspNetCore.RequestLoggingMiddleware` at the live switch level. When the answer is yes, the module also
marks the event `IsImmediate`, so it exports through the immediate route and a step-down between the decision and
`StepUpSink` cannot drop it after its summary was left out. When the answer is no (for example `BaseLevel` `Error` and a 4xx `Warning`), the summary is written as
under `Keep`, so no request ends with zero lines. An event held back into the pre-error buffer counts as not exported.
The level and status are noted in `GetLevel`, because an exception's 500 is not on the response yet when the event
is written. A
test pins the source name, so a Serilog.AspNetCore upgrade that renames it fails a test.

**D4. The completion event carries the summary's fields.** The module builds the inbound summary at
`RequestSummaryDetail`, with its redaction (ADR 0032 D5), and sets on the completion event `ClientKind`, `UserAgent`
and every optional field under the key the summary writes it with: the long name, or in `Minimal` the short key of
ADR 0033, so a query written against the summary finds the same field on the completion event. The summary fields
the completion event sets itself (`ClientIp`, `Jti`, `QueryString`, `RouteParameters`) are not copied, so no value
appears twice under two keys. `Direction`, `Method`, `Path`, `StatusCode` and
`ElapsedMs` are not copied: `RequestMethod`, `RequestPath`, `StatusCode` and `Elapsed` carry them. The summary's
`ElapsedMs` is measured around the whole inner pipeline, the completion event's `Elapsed` inside it, so the two differ
by the time of the middleware between them. A failure while building the fields leaves the summary to be written.

## Consequences

- With `Omit`, a failed request writes one event at `Warning` or `Error` instead of an Information summary plus that
  event. A query for failed requests that reads `IsRequestSummary` events must read the completion event instead.
- The completion event of a failed request gains the summary's properties, under the summary's keys; with
  `RequestSummaryDetail` `Minimal` the event mixes its own long names with the summary's short keys.
- That event carries `IsImmediate=true` and exports through the bypass logger, like an `AlwaysExportCategories`
  event; it is not held in the pre-error buffer, which it would not have entered anyway.
- A root `Serilog:Filter` that drops the completion event after enrichment is not seen by D3; that request then has no
  line. The library adds no filter of its own.
