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
regression pin.

**Outcome (verified 2026-07-10): the ordering is correct, and no production change was required.**
`ShutdownFlushOrderingTests.BufferedEvents_SurviveServiceProviderDisposal_ReachBypassLogger`
builds the real container with `EnablePreErrorBuffering = true`, emits sub-`BaseLevel` events
under a W3C `Activity` (buffered, never exported, no `Error` emitted), disposes the host, and
confirms the buffered events reach the bypass logger's file sink. It passes against unchanged
production code: the controller is registered *before* `AddSerilog`, so the container disposes
the Serilog provider (and with it `PreErrorBufferSink`) *first*, while the controller-owned
bypass logger is still alive to receive the shutdown flush. The bypass logger's ownership
remains with `StepUpLoggingController` as documented on `SetSummaryLogger`; no fix branch was
taken.

## Consequences
- An emergent, order-dependent property is now an asserted one. Any future reordering of the
  registrations in `AddStepUpLoggingInternal` — an easy, innocent-looking edit — now fails a test
  instead of silently dropping the last events before a crash, which is precisely when those
  events matter most.
- No public-surface change: `SetSummaryLogger`'s documented ownership transfer stands. The fix
  branch (registering the bypass `Logger` as its own DI singleton and reducing the controller's
  `Dispose` to the timer alone) was considered but is unnecessary given the verified ordering.
