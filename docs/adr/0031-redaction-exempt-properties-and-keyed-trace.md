# ADR 0031 - Redaction exempt properties and a keyed trace id in the Text console

- Status: Accepted
- Date: 2026-10-20
- Issue: #87
- Amends: ADR 0022 D5, D6

## Context

A consumer configures a UUID pattern in `RedactionRegexes` to catch secrets that are bare UUIDs.
Keycloak issues `jti` as `<prefix>:<uuid>` (`onrtac:`, `ofrtac:`, ...), so the `Jti` the request
summary carries and the `Text` console prints by default comes out as `onrtac:[REDACTED]`. The jti is
what matches a request to a user, so the console loses its identity. User ids and other non-secret
UUIDs are masked the same way. The only workaround was a negative lookbehind in every pattern, which
couples each pattern to each identifier format.

The `Text` console line wrote the trace id as a bare 32-hex token after the level. A reader, or a log
processor that parses `Name=value` pairs, could not tell what it was; every other value after the
message is written as `Name=value`.

## Decision

**D1. `RedactionExemptProperties` lists property names whose values skip `RedactionRegexes`.** Names
are matched ordinal and case-sensitive, blank entries are dropped, the same way as the library's own
exclusion set (ADR 0022 D6). The list lives on `CompiledRedactionPatterns`, so it applies wherever
redaction runs on a named property: the `RedactLogEventProperties` sweep, which unions it with the
fixed exclusion set into one lookup, and `Jti` on the request summary and on the request completion
event. The request inputs (path, query string, headers, route values, `User-Agent`,
`X-Forwarded-For`, body) are always redacted, whatever the list holds: they are not named
properties a consumer chooses, and an exemption there would leak the very values the patterns exist
for. Default: empty, so redaction output is unchanged on upgrade. Rejected: a value-level allow-list
(`RedactionExemptRegexes`, a value that fully matches is kept); identifiers that matter sit in their
own property, and a second regex list doubles the cost and the ways to get it wrong. Revisit only if
a property-name list proves insufficient.

**D2. The `Text` line writes the trace id with a key, in the form `ConsoleTraceFormat` selects.**
`TraceId` (default) writes `TraceId=<32 hex>`; `Traceparent` writes the W3C
`traceparent=00-<32 hex>-<16 hex>-<2 hex>`, so a line can be pasted into a trace lookup. The token stays
right after the level. The flags are the `TraceFlags` property (stamped by `ActivityContextEnricher`)
as two hex digits, `00` when it is absent or not a number. An event with a trace id but no span id
falls back to `TraceId=`, since a traceparent needs both; an event without a trace id gets no trace
token, as before. The default is written only on the option: the formatter and its factory take the
trace format as a required parameter, so no second default can drift from it. Rejected: a `Bare`
value keeping the unkeyed token; the layout change is noted in the changelog instead, and a parser of
the old column needs one prefix strip.

## Consequences

- With a UUID pattern and `RedactionExemptProperties = ["Jti"]`, `Jti=onrtac:<uuid>` reaches the
  console and OTLP unchanged, while the same UUID in the request path or another property is still
  `[REDACTED]`.
- A listed name is exempt on every event, whoever stamped the property; it is a second hole by name
  next to the fixed set of ADR 0022 D6, and the `RedactLogEventProperties` doc names it.
- The `Text` console line layout changes: the trace token now carries a key. JSON console, OTLP and
  file output are unchanged.
- Public surface grows by `RedactionExemptProperties`, `ConsoleTraceFormat` and the
  `ConsoleTraceFormat` enum; an undefined enum number fails options validation on start.
