# ADR 0009 — Redaction covers every request-derived value, before truncation

- Status: Accepted
- Date: 2026-07-10

## Context
Three inconsistencies in the redaction path. (a) The request body was truncated to
`MaxBodyCaptureBytes` and only *then* redacted, so a secret straddling the cut no longer matched
its pattern and its surviving prefix was logged verbatim. (b) `ExtractUserAgent` fed the request
summary without passing through `CompiledRedactionPatterns.Redact()`, while the detailed
enricher path redacted every header — the same class of value, treated two different ways.
(c) An `ApplyRedaction` Activity was started per redacted header per request, so trace noise
grew in proportion to header count.

## Decision
(a) Read a 256-character margin beyond `MaxBodyCaptureBytes`, redact the margin-extended text,
then truncate the redacted result. Truncation is now strictly downstream of redaction.
(b) Every request-derived string — query string, route values, headers, body, User-Agent,
`ForwardedFor` — goes through `Redact()`. No exceptions.
(c) One `ApplyRedaction` span per request, started lazily on the first redaction and tagged
`security.redaction_count` and `security.redaction_targets`; the per-field `RedactionCounter`
metric is unchanged.

## Consequences
- A body whose redacted form exceeds the limit is cut after masking, so a partially-visible
  secret cannot survive the cut.
- Redaction cost on the body rises by the bounded margin (256 chars).
- Trace volume drops. The `security.redaction_type` tag is gone, replaced by the aggregated
  `security.redaction_targets`.
