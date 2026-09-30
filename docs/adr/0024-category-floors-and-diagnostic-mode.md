# ADR 0024 - Category floors, Diagnostic mode, always-export and never-trigger categories

- Status: Accepted
- Date: 2026-09-30
- Issue: #35
- Amends: ADR 0021 D4

## Context

Consumers need per-category control that `NeverStepUpCategories` (ADR 0021) does not give: a
minimum level for noisy categories, a time-boxed diagnostic window entered at startup, categories
exported at any level (for example `Microsoft.Hosting.Lifetime`), and categories whose errors must
not trigger step-up. Since ADR 0023 D1, `StepUpSink` is the single export decision point, so every
new rule has to fit there or stay out of the sinks entirely.

## Decision

**D1. A floor only raises the minimum.** `CategoryFloors` maps a `SourceContext` prefix to a
level, and a matching category's minimum becomes `max(switch, floor)`, the ADR 0021 D2 invariant.
A floor never makes a category more verbose. A floor value above `Warning` fails startup
validation, so a floor can never hide an `Error`. An absolute per-category level was rejected: it
is a level override, not a floor, and D4 covers the `Lifetime` case.

**D2. An event rejected by a floor is a held-back event.** It goes to `PreErrorBufferSink.Hold`,
as ADR 0023 D4 does for `NeverStepUpCategories`. Dropping it before the buffer, like a root
`MinimumLevel:Override`, was rejected: it loses the context before an error and adds a second
decision point.

**D3. During Diagnostic every floor is lifted except exempt categories.** An entry of
`DiagnosticExemptCategories` keeps its floor; an exempt entry no floor key matches fails
validation. Floors in every mode (loses the diagnostic lift) and a separate diagnostic floor map
(two maps to keep in sync) were rejected.

**D4. Always-export is a root enricher.** It sets `IsImmediate=true` on a matching
`SourceContext`, so `ImmediateSink` exports the event once and `StepUpSink` drops it, with no sink
change. Category checks inside `ImmediateSink` and `StepUpSink` were rejected: two sinks evaluating
one predicate is what ADR 0023 D1 rules out.

**D5. Overlapping floor prefixes resolve to the most specific one**, Serilog `LevelOverrideMap`
semantics. An exempt entry may be narrower than its floor key. The highest matching level was
rejected: it cannot express a sub-category floor below its parent.

**D6. One prefix rule.** Every category option matches through `CategoryPrefix`, the ADR 0021 D3
rule moved out of `StepUpSink` unchanged.

**D7. `StepUpSink` reads a controller-owned diagnostic flag, never `StepUpMode`.** This amends
ADR 0021 D4: the sink stays free of any mode dependency, and the controller decides whether
Diagnostic is active.

**D8. Diagnostic is a startup-only, time-boxed window.** The switch starts at `DiagnosticLevel`,
and when `DiagnosticDurationMinutes` elapse the controller runs as `Auto` at `BaseLevel`. A
`DiagnosticReturnMode` option was rejected: it has no user. While the window is active,
`Trigger()` is a no-op and `IsSteppedUp` is `true`, and nothing carries over after expiry. The
window is timed by the `TimeProvider` from DI, falling back to `TimeProvider.System`, for the
diagnostic timer only; step-up keeps its `Func<long>` clock and `Timer`, since a full migration
belongs in its own task. The start and end Warnings go through the bypass logger, and the
UpDownCounter `stepup_diagnostic_active` on meter `StepUpLogging` reports the window.

## Consequences

- `StepUpMode.Diagnostic` and the six options (`CategoryFloors`, `DiagnosticExemptCategories`,
  `DiagnosticLevel`, `DiagnosticDurationMinutes`, `AlwaysExportCategories`,
  `NeverTriggerCategories`) are validated at startup in every mode (ADR 0007). Blank list entries
  pass validation and are filtered at wiring.
- With `CaptureRequestBody=true`, request bodies are captured (still redacted) for the whole
  Diagnostic window, because body capture follows `IsSteppedUp`.
- All additions are additive: this change breaks nothing of its own.
