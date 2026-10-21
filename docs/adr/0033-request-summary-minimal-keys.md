# ADR 0033 - Request summary minimal keys

- Status: Accepted
- Date: 2026-10-21
- Amends: ADR 0032 D3

## Context

The request summary is written on every request. `Compact` (ADR 0032 D3) cut the fields, but each event still
repeats long property names such as `HttpClientName` and `ElapsedMs`. Serilog's CLEF shortens its own envelope
keys (`@t`, `@mt`, `@l`) for the same reason.

## Decision

**D1. `RequestSummaryDetail.Minimal` writes the `Compact` fields under short keys.** The fields and their values
are those of `Compact`; only the keys change:

| Field | Key |
|---|---|
| `Direction`, `Method`, `Path`, `StatusCode`, `ElapsedMs`, `ClientKind`, `UserAgent` | `dir`, `mth`, `path`, `sc`, `ms`, `ck`, `ua` |
| `Route`, `Aborted`, `ClientId`, `ClientIp`, `Jti` (inbound) | `rt`, `ab`, `cid`, `ip`, `jti` |
| `HttpClientName`, `Host` (outbound) | `hc`, `host` |

The template is `{dir} {mth} {path} {sc} {ms} ms {ck} {ua}`, so the rendered message is the same as in the other
modes. `IsRequestSummary` keeps its name: `SummarySink` routes on it. The keys are not `@`-prefixed: CLEF escapes a
property starting with `@` to `@@`.

**D2. The renaming is in `RequestSummary`.** The builders add fields under their long names; `RequestSummary` maps
each to its key when it builds the event, so a field added to `Compact` later fails a test until it has a key.

## Consequences

- `ConsoleTextProperties` and `RedactionExemptProperties` match by key: a `Minimal` consumer lists `jti`, not `Jti`.
  `Jti` and `ClientId` are still redacted through `RedactProperty` under their long names before the rename.
- Queries on the summary differ by mode; switching modes changes the keys.
