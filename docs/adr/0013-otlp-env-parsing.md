# ADR 0013 — OTLP env-var parsing follows the OTel spec

- Status: Accepted
- Date: 2026-07-10

## Context
`ParseOtlpHeaders` and `ParseResourceAttributes` split `OTEL_EXPORTER_OTLP_HEADERS` /
`OTEL_RESOURCE_ATTRIBUTES` on `,` and `=` and use the raw substrings. The OpenTelemetry
specification defines these as W3C Baggage-style lists whose keys and values are percent-encoded.
The common real-world case is an auth header —
`OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic%20dXNlcjpwYXNz` — which we forward with a literal
`%20`, so the collector rejects the credentials and the export fails with no clear signal. Any
value containing `=`, `,`, or a space is affected.

## Decision
Percent-decode both key and value with `Uri.UnescapeDataString` after trimming; skip malformed
pairs (no `=`) and empty keys. Do not invent an escape mechanism for literal commas in values —
the env-var format genuinely cannot represent them, and inventing one would diverge from every
other OTel SDK reading the same variable.

## Consequences
- Credentials containing percent-encoded characters now reach the collector intact.
- A user who was compensating by pre-decoding their env var (writing a literal space) sees no
  change, because `UnescapeDataString` is a no-op on unescaped input — unless their value contains
  a literal `%`, which was already ambiguous and is now interpreted per spec.
- Tests cover `%20`, `%2C`, an unescaped value, and a malformed pair.
