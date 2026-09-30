# ADR 0023 - Pre-error buffer exports each event at most once and drops held-back events on dispose

- Status: Accepted
- Date: 2026-09-30
- Supersedes: ADR 0014

## Context

`StepUpSink` and `PreErrorBufferSink` were wired as sibling root sinks, each deciding on its own
what to do with an event. The buffer kept every non-error event at or above `StepUpLevel`,
including events `StepUpSink` had already exported. An `Error` in the same trace then flushed
those events to the bypass logger a second time, and `PreErrorBufferSink.Dispose()` flushed
whatever was still buffered at shutdown, so an exported `Warning` reached the output twice and a
held-back `Information` was exported at shutdown although `BaseLevel` never allowed it and no
error occurred (issue #29).

## Decision

**D1. The export decision is made once, in `StepUpSink`.** Bypass-marked events
(`IsImmediate`, `IsRequestSummary`) are dropped first. The pin-aware minimum (the switch, with
`NeverStepUpCategories` pinned to `BaseLevel` per ADR 0021) then decides: an exported event goes to
the inner logger, a rejected one goes to `PreErrorBufferSink.Hold`. A shared predicate evaluated by
both sibling sinks was rejected, because a switch flip between the two `Emit` calls would duplicate
or lose one event.

**D2. The buffer holds only held-back events.** `Hold` keeps the `StepUpLevel` floor (ADR 0015),
excludes bypass-marked events and ignores `Error`/`Fatal`. `PreErrorBufferSink` stays a root sink
after `StepUpSink` only for the flush: its `Emit` flushes the trace's buffer on `Error`/`Fatal`
and buffers nothing. Serilog calls sinks in order on one thread, so an event is held before a
later `Error` in the same trace flushes it.

**D3. Dispose drops held-back events without export.** A held-back event is below the level the
service chose to export, and without an error it carries no reason to leave the process. Keeping
the shutdown flush was rejected: it exported events `BaseLevel` never allowed, with no `Error`.

**D4. `NeverStepUpCategories` events are still buffered.** A listed category rejected by its pin
is a held-back event like any other and is exported exactly once by an `Error` flush (ADR 0021
stands).

## Consequences

- Every event reaches the output at most once: exported by `StepUpSink`, or flushed by an `Error`.
- The last held-back events of a process with no error are lost at shutdown, by design. ADR 0014's
  disposal-order property no longer protects a buffer flush; `ShutdownFlushOrderingTests` now pins
  the drop.
- No public-surface change: `StepUpSink` gains an optional internal constructor parameter and
  `PreErrorBufferSink` an internal `Hold` method.
