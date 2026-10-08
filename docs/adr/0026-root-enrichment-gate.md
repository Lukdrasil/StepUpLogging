# ADR 0026 - Root enrichment gate

- Status: Accepted
- Date: 2026-10-08
- Amends: ADR 0005, ADR 0015, ADR 0022 D5, ADR 0024 D4 (notes only; their decisions stand)

## Context

The root Serilog logger is `MinimumLevel.Verbose()` so the pre-error buffer and the trigger sink see
every event they need (ADR 0003, 0005, 0015). Serilog therefore builds each event and runs every root
enricher before any sink decides whether the event goes anywhere. Most events do not: at `BaseLevel`
Warning and `StepUpLevel` Information, a Debug event is dropped by `StepUpSink` and ignored by the
buffer, the trigger sink and both bypass sinks. `docs/performance/analysis.md` section 1 measured what
that costs: a dropped Debug event took 668 ns and 1360 B against 241 ns and 424 B for plain Serilog, and
the ladder in `docs/performance/results-root-gate.md` puts all 936 B and about 360 of the 401 ns the
pipeline adds in the enrichers that add properties.

Raising the root minimum level would remove the cost, and more: Serilog would not build the event. It is
rejected here (D6) because it changes what `ILogger.IsEnabled(Debug)` answers through
`Microsoft.Extensions.Logging`, which is published behaviour.

## Decision

**D1. The root enrichers run only on events some sink can use.** `EnrichmentGate.Needs` is true when the
event's level is at or above `min(live switch level, StepUpLevel, Error)`, or the event carries
`IsImmediate=true` or `IsRequestSummary=true`. Each term is the lowest level a consumer of the root
reads:

- the live `LevelSwitch`: `StepUpSink` exports at `max(switch, NeverStepUp pin, category floor)`, never
  below the switch;
- `StepUpLevel`: `PreErrorBufferSink` holds the events `StepUpSink` did not export, from `StepUpLevel` up
  (ADR 0015), and replays them on an `Error`;
- `Error`: `StepUpTriggerSink` reads `Error` and `Fatal`, and the buffer flush keys on them; the floor is
  capped there, so an `Error` is always enriched even when `StepUpLevel` is `Fatal`;
- the two markers: `ImmediateSink` and `SummarySink` export those events at any level.

The gate reads the live switch (`StepUpLoggingController.LevelSwitch` is public), so a switch lowered by
Diagnostic mode, by `Trigger()` or by a consumer lowers the floor with it. The floor always includes
`StepUpLevel`, never the switch alone. Without it, an `Information` event logged just before a step-up
would be judged against a switch still at Warning, skipped by the enrichers, and then exported by
`StepUpSink` after the switch rose: an exported event without `Application`, trace ids or
`ServiceVersion`. With `StepUpLevel` in the floor, the switch cannot rise to a level the gate had
skipped. The remaining window is a consumer lowering the switch below `StepUpLevel` by hand while an event
is in flight; that event is exported without the library's enrichers, once.

**D2. The gate is off when a consumer sink sees the Verbose root.** `EnrichmentGate.For` returns an
always-true predicate when the `configure` hook is set or `Serilog:AuditTo` has an entry, because a
consumer sink added there, or an audit sink, receives every event at Verbose and expects the same
properties it always had. The decision is made once, when the logger is built. Rejected: a stage logger
that strips enrichment before a configure-hook sink, which changes what the hook receives; and leaving the
gate on with a documented limitation, which silently removes properties from a sink that did not ask.

**D3. `AlwaysExportEnricher` runs before the gate and is not gated.** It reads only `SourceContext` and
sets `IsImmediate=true` (ADR 0024 D4); D1 lets that marker through, so the event is enriched and exported
once by `ImmediateSink`. `RedactionEnricher` stays last and ungated (ADR 0022 D5; its own gating is
separate work). The bypass logger runs the same enrichers ungated: everything that reaches it is exported.

**D4. Config-declared root settings are not gated.** `Serilog:Enrich`, `Serilog:Properties`,
`Serilog:Using` and `Serilog:MinimumLevel:Override` are read by the root before the library's enrichers
and behave as before. `Serilog:MinimumLevel:Default` stays ignored: the root is Verbose after the
configuration is read (ADR 0022 D7).

**D5. A root `Serilog:Filter` sees the events the gate skips without the library's enrichers.** Filters
run after enrichment. For a sub-floor event they see no `Application`, `Environment`, trace ids or other
library property, and the `RequestPath` the ASP.NET Core hosting scope attaches is not redacted, because
`PathPropertyRedactionEnricher` is among the gated enrichers. A filter only decides; it does not export.
A filter that logs or forwards a property of such an event can read the raw path. Such a filter is the one
consumer sink the gate does not detect (D2): it sits on the root, in configuration, and declares no sink.

**D6. The root stays Verbose.** A raised root minimum with `MinimumLevel.Override` entries for
`AlwaysExportCategories` would drop the event before it is built, but `IsEnabled(Debug)` would change from
`true` to `false` for the dropped levels. It is deferred as an opt-in that needs its own decision
(`docs/performance/analysis.md` section 1).

**D7. How the enrichers are applied.** `ApplyCommonEnrichers` takes a `LoggerEnrichmentConfiguration` and
makes one call per enricher on it: inside `Enrich.When`, a chained `.Enrich.` call reaches the logger
configuration and escapes the condition. The optional enrichers are a table of (flag, add) rows filtered
by the flag, which also takes the method from cyclomatic complexity 12 to 3. `ApplyRootEnrichers` wires
the always-export enricher and then `Enrich.When(gate, ...)`; `CreateBypassLogger` applies
`ApplyCommonEnrichers(cfg.Enrich, ...)` directly.

## Consequences

- Measured in `docs/performance/results-root-gate.md`: in the default configuration a dropped Debug event
  allocates 424 B, the bytes of plain Serilog (1360 B before), at 369 ns against 668 ns. With a consumer
  root sink it stays at 1360 B and about 690 ns. Held and exported events are unchanged.
- Nothing exported changes: every exported or held event is at or above the floor, or carries a marker,
  so it is enriched as before. The characterisation tests in `EnrichmentGateHostTests` pin this for manual
  switch changes, Diagnostic mode, a step-up, the pre-error flush, immediate and summary events, the
  configure hook and `Serilog:AuditTo`.
- A `SourceContext` supplied only by the log context (`LogContext.PushProperty`) is no longer seen by
  `AlwaysExportEnricher`, which now runs before `FromLogContext`; a `SourceContext` from `ForContext` or
  from `Microsoft.Extensions.Logging` is unaffected.
- A consumer who wants the gate and a root sink has no switch; D2 trades that for not changing what the
  sink receives.
- Enrichers a consumer registers in the `configure` hook, or in `Serilog:Enrich`, are not gated: D2 turns
  the gate off in the first case and D4 leaves the second alone.
- A change that adds a library enricher belongs in `ApplyCommonEnrichers` (the gated set) or in
  `ApplyRootEnrichers` ahead of the gate, and states which.
