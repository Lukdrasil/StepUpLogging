# ADR 0010 — Step-up duration has an optional hard cap with cooldown

- Status: Accepted
- Date: 2026-07-10

## Context
`Trigger()` extends the step-up window on every error, rate-limited only by a 5 s
`_minTriggerInterval` that bounds timer churn — not total duration. A caller who can repeatedly
provoke an `Error` (an endpoint that reliably 500s, an unauthenticated path that logs errors)
can hold the process at `StepUpLevel` indefinitely. Verbose export to OTLP is metered, so this
is a cost- and volume-amplification vector reachable by anyone who can make the service fail.

## Decision
Add `MaxContinuousStepUpSeconds` (default `0` = disabled, so v2 configs are unaffected) and
`StepUpCooldownSeconds` (default `300`). Once step-up has been continuously active longer than
the cap, force a step-down and ignore triggers for the cooldown. Validation rejects a cap
smaller than `DurationSeconds`, which would fire before a single window could complete. Two new
counters on the `StepUpLogging` meter: `stepup_cap_reached_total` and
`stepup_trigger_suppressed_total`. The forced step-down shares the normal step-down body so the
metric surface stays coherent; its activity is tagged `stepup.forced_by_cap`.

## Consequences
- Opt-in: nobody's behavior changes on upgrade.
- Operators who enable it trade diagnostic depth during a sustained incident for a bounded
  telemetry bill; the cooldown is the knob.
- Blind spot: an attacker pacing triggers to just outside the cooldown still gets sustained
  verbosity at a reduced duty cycle — collector-side sampling remains the backstop, and the
  README says so.
- Record only (no change): `ArmTimer` allocates a fresh `Timer` per trigger where
  `Timer.Change()` would do. The 5 s rate limit makes the allocation immaterial, so it stays
  for the clarity of the generation-guard pattern (ADR 0002).
