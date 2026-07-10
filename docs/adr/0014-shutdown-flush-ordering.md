# ADR 0014 — Buffered events survive shutdown

- Status: Accepted
- Date: 2026-07-10

## Context
`PreErrorBufferSink.Dispose()` makes a best-effort flush of every still-buffered event into the
bypass logger. That bypass logger is owned and disposed by `StepUpLoggingController` (see
`SetSummaryLogger`'s ownership note). The sink is disposed when the root Serilog `Logger` is
disposed. Both are disposed by the DI container, in reverse registration order, from two
registration sites that no test pins. If the controller loses that race, the shutdown flush
writes into a disposed `Serilog.Core.Logger` — whose `Write` is a silent no-op, not a throw —
and the final buffered events of the process disappear with no diagnostic whatsoever. Reasoning
from registration order suggests the current ordering is correct (the controller is registered
before `AddSerilog`, so the Serilog provider is disposed first), but "suggests" is the wrong
standard for a silent-data-loss path.

## Decision
Pin the property with a characterization test rather than assume it. Build the real container
through `AddStepUpLogging`, emit sub-level events under a trace, dispose the `ServiceProvider`,
and assert the buffered events reached the bypass sink. The test is the deliverable and the
regression pin. If it passes, the ordering is verified and no production code changes. If it
fails, the minimal fix is to take the bypass logger's lifetime away from the controller —
register it as its own DI singleton so the container disposes it after the Serilog provider, and
reduce the controller's `Dispose` to the timer alone, updating the ownership contract on
`SetSummaryLogger`.

## Consequences
- An emergent, order-dependent property becomes an asserted one. Any future reordering of the
  registrations in `AddStepUpLoggingInternal` — an easy, innocent-looking edit — now fails a test
  instead of silently dropping the last events before a crash, which is precisely when those
  events matter most.
- Note that IF the fix branch is taken, `SetSummaryLogger`'s documented ownership transfer changes,
  and that is a public-surface documentation change.
