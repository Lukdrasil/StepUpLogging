# 0015 — Pre-error buffer respects StepUpLevel as its level floor

## Status

Accepted (2026-07-10). Fixes issue #13.

## Context

The root Serilog logger is deliberately `Verbose` so the internal sinks see every
event (ADR 0003/0005 context). `PreErrorBufferSink` therefore buffers **all**
levels — including `Verbose` and `Debug` — and `FlushTo` replays the whole buffer
to the bypass logger on `Error`/`Fatal`. With `BaseLevel = Warning` and
`StepUpLevel = Information`, an error retroactively exports `Verbose`/`Debug`
events that no configured level would ever have exported. The retroactive flush
leaks below the most detailed level the user opted into.

## Decision

The pre-error buffer's semantic contract is: *"replay recent context as if the
level had already been stepped up when those events occurred."* Therefore the
buffer applies a **level floor equal to the resolved `StepUpLevel`**, enforced at
**buffering time** (`Emit`), not at flush time:

- `PreErrorBufferSink` gains a `LogEventLevel minimumLevel` constructor parameter.
- Events below `minimumLevel` are dropped before buffering (never enqueued, never
  counted in `buffer_events_total`).
- The wiring in `AddStepUpLoggingInternal` passes the parsed `StepUpLevel`
  (same parse-with-fallback the controller uses: invalid string → `Information`,
  unreachable in practice because options validation rejects invalid levels).

## Alternatives considered

- **Filter at flush time** — keeps sub-floor events in memory for no benefit;
  `StepUpLevel` is fixed at startup, so the floor cannot change after wiring.
- **New `PreErrorBufferMinimumLevel` option** — speculative configurability; no
  use case asks for a buffer floor different from the step-up level. Can be added
  later without breaking anything (the ctor is internal).
- **Floor = `BaseLevel`** — would make the buffer useless (everything at/above
  `BaseLevel` already exports normally); the buffer exists precisely to recover
  the `StepUpLevel..BaseLevel` band.

## Consequences

- Behaviour change vs ≤3.1.0: `Verbose`/`Debug` events no longer flush on error
  unless `StepUpLevel` is set that low. Users who want the old firehose set
  `StepUpLevel = "Verbose"`.
- Memory use of the ring buffers drops (sub-floor events are never stored).
- `NeverStepUpCategories` remains orthogonal: it exempts categories from the
  *live* step-up window and still does not filter the buffer (ADR 0008 remark
  unchanged) — the buffer floor is by level only.
