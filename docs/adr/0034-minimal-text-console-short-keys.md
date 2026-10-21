# ADR 0034 - Minimal text console short keys

- Status: Accepted
- Date: 2026-10-21
- Amends: ADR 0033

## Context

`RequestSummaryDetail.Minimal` (ADR 0033) shortens the request summary keys, mainly for the `Text` console line.
The line itself still writes long fixed keys (`Timestamp=`, `Level=`, `TraceId=`, `SourceContext=`, `Message=`,
`Exception=`), and the default `ConsoleTextProperties` (`Jti`, `Method`, `Path`, `StatusCode`, `ElapsedMs`) name
none of the `Minimal` keys, so nothing is written after the message.

## Decision

**D1. Under `Minimal` the `Text` line writes short line keys.**

| Item | Long key | Short key |
|---|---|---|
| Timestamp | `Timestamp` | `t` |
| Level | `Level` | `l` |
| Trace id | `TraceId` | `tid` |
| W3C trace context | `traceparent` | `tp` |
| `SourceContext` | `SourceContext` | `src` |
| Rendered message | `Message` | `msg` |
| Exception | `Exception` | `ex` |

None of them is an ADR 0033 key, so a line key never collides with a summary key: `sc` stays `StatusCode`, so
`SourceContext` is `src`. The values, their order and their quoting are unchanged.

**D2. Under `Minimal` the default `ConsoleTextProperties` is the short keys of the default list:** `jti`, `mth`,
`path`, `sc`, `ms`, mapped through `RequestSummary.MinimalKeys`. A configured list is used as given.

**D3. `Full` and `Compact` keep the long keys byte for byte.** JSON console, OTLP and the file sink are unchanged in
every mode.

## Consequences

- Switching to or from `Minimal` changes the `Text` line keys as well as the summary keys; a parser or query on the
  console line must follow.
- A `Minimal` consumer with a configured `ConsoleTextProperties` lists the short keys for summary fields
  (`jti`, not `Jti`), as ADR 0033 already required.
