# ADR 0002 — Step-down correctness via generation counter

- Status: Accepted
- Date: 2026-07-07

## Context
`StepUpLoggingController` arms a `Timer` for step-down. A fired callback blocks on `_gate`
while a concurrent `Trigger()` holds it and extends the window (`_timer.Change(_duration…)`).
The stale callback then acquires the lock and steps down **immediately**, despite the window
having just been extended; the freshly-armed timer fires again `_duration` later, producing a
second step-down with no matching step-up. Result: premature loss of verbosity, a negative
`stepup_active` gauge, and a bogus duration histogram sample. The callback also never checks
that the switch is actually stepped up.

## Decision
Introduce a monotonically increasing `_generation` counter. Every arm/extend in `Trigger()`
increments it and captures the value into the timer state. `StepDownCallback` compares the
captured generation to the current one **inside the lock**; if they differ, the callback is
stale and returns without touching state. Additionally guard
`if (LevelSwitch.MinimumLevel != _stepUpLevel) return;`.

Interval/duration math moves off wall-clock `DateTime.UtcNow` to a monotonic source
(`Stopwatch.GetTimestamp()` / `Environment.TickCount64`) so an NTP jump cannot corrupt the
rate-limit window or the duration histogram (also covers the low-severity clock finding).

## Consequences
- No spurious step-down after a window extension; gauge and histogram stay consistent.
- Deterministic to test with an injectable clock/time source rather than real timers.
- `Trigger()` and `StepDownCallback` must share the generation under the same lock.
