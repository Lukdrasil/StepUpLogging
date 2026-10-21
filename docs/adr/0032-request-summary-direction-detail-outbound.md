# ADR 0032 - Request summary direction, detail switch and outbound summary

- Status: Accepted
- Date: 2026-10-21
- Issue: #91

## Context

The request summary was inbound only, with the template `Request finished {Method} {Path} {StatusCode} {ElapsedMs}`.
A reader could not see where a request came from: the User-Agent was an optional property, absent from the
rendered message, and nothing labelled a health probe, a bot or a script. Calls the app makes through
`IHttpClientFactory` left no summary at all. Some consumers want the summary narrower than today's field set,
others want it wider.

## Decision

**D1. One template for both directions.** Every summary is written as
`{Direction} {Method} {Path} {StatusCode} {ElapsedMs} ms {ClientKind} {UserAgent}`, with `Direction` `Inbound` or
`Outbound`. `UserAgent` is always present: the redacted header, or `(none)` when it is missing or blank, so the
rendered line keeps its columns. A summary without a status code (a failed outbound call) leaves `StatusCode` out
and renders the literal token. The template text is the same in every detail mode. `RequestSummary` builds the
event once, template properties first, then the optional fields in the order they were added, then
`IsRequestSummary`; its field list has a fixed capacity so each optional field costs one entry, not a resize.

**D2. `ClientKind` is a label, not an identity.** It is derived from the User-Agent by an ordered,
case-insensitive substring table, first match wins: `HealthProbe`, `Bot`, `Tool`, `Library`, then `Browser`
(`Mozilla/`, checked last because bots send it too); `Unknown` for anything else, `None` for no header. The header
is client-supplied and spoofable; `ClientId` carries the identity of an authenticated caller. The raw User-Agent
is classified before it is redacted, so a pattern that masks the header does not turn `curl/8` into `Unknown`.
That rule is written once, in the `RequestSummary` constructor, which takes the raw header and the redaction
patterns. The public `EmitRequestSummary` receives an already redacted User-Agent and passes zero patterns.

**D3. `RequestSummaryDetail` chooses the fields, `Full` by default.** `Full` keeps every property the summary had
before this change, so an upgrade loses nothing. The public `EmitRequestSummary` follows the same option.

| Inbound field | Compact | Full |
|---|---|---|
| `Direction`, `Method`, `Path`, `StatusCode`, `ElapsedMs`, `ClientKind`, `UserAgent` | yes | yes |
| `Route` (`RoutePattern.RawText`), `Aborted` (only when `true`), `ClientId` (`azp`, else `client_id`), `ClientIp`, `Jti` | yes | yes |
| `Protocol`, `Scheme`, `Host`, `RequestContentLength`, `ResponseContentLength`, `IsAuthenticated`, `AuthScheme`, `EndpointName`, `QueryString`, `RouteParameters`, `ForwardedFor` | | yes |

| Outbound field | Compact | Full |
|---|---|---|
| `Direction`, `Method`, `Path` (no query), `StatusCode`, `ElapsedMs`, `ClientKind`, `UserAgent` | yes | yes |
| `HttpClientName` (`(default)` for the unnamed client), `Host` (`Uri.Authority`) | yes | yes |
| `Protocol`, `RequestContentLength`, `ResponseContentLength` (header values only), `ExceptionType` (on failure) | | yes |

Not logged: `Referer`, `sub`, email and name claims, cookies, `Authorization`, and `Jti` on outbound calls.
`RequestSummaryDetail` set to `Compact` while both summaries are off is reported as an unused option at startup.

**D4. The outbound summary is an `IHttpMessageHandlerBuilderFilter`.** With `AlwaysLogOutboundRequestSummary` on,
the filter runs the client's own builder actions first, then inserts `OutboundRequestSummaryHandler` at index 0,
outermost: a call retried by a resilience handler is summarized once, with its final status. A failed call writes
`ExceptionType` (in `Full`) and is rethrown unchanged; writing the summary never fails the call. Rejected:
`ConfigureHttpClientDefaults`, whose handler would sit inside handlers added later by a client's own
configuration. Excluded clients, so an export does not cause another export: the library's audit client
`Lukdrasil.StepUpLogging.Audit` and the OTLP exporter clients `OtlpTraceExporter`, `OtlpMetricExporter` and
`OtlpLogExporter`. The OTLP names are those OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3 creates in
`TryEnableIHttpClientFactoryIntegration`; the log name is in that assembly although its README names only trace
and metric. `OutboundRequestSummaryRegistrationTests.OtlpExporter_FactoryClientNames_AreAllExcluded` pins them, so
an exporter upgrade that renames a client fails a test. The option is read once at registration. An `HttpClient`
created with `new HttpClient()` is not covered.

**D5. Redaction per field.** `Path`, `UserAgent`, `Host` (both directions), `QueryString`, `RouteParameters` and
`ForwardedFor` go through `RedactionRegexes`; `ClientId` and `Jti` through `RedactProperty`, so
`RedactionExemptProperties` (ADR 0031) can keep them readable.

## Consequences

- The template text changes from `Request finished ...` to `{Direction} ...`; queries on the exact template text
  must change. Existing property names are kept; `UserAgent` is now always present.
- The property order of the summary changes: template properties, then `ClientIp`, `Jti`, `QueryString`,
  `RouteParameters`, `ForwardedFor`.
- Outbound summaries add one event per outgoing call at `RequestSummaryLevel`; the option is off by default.
