# ADR 0008 — Categories the step-up must never raise

- Status: Accepted
- Date: 2026-07-10

## Context

`AddStepUpLogging` raises the shared `LoggingLevelSwitch` from `BaseLevel` (default
`Warning`) to `StepUpLevel` (default `Information`) for `DurationSeconds` (default 180)
whenever an `Error`/`Fatal` event is observed. Every event at or above the switch is
exported by `StepUpSink` to the configured output sinks (OTLP / Console / File and any
`Serilog:WriteTo` sink the consumer declared — see ADR 0005).

EF Core writes every executed SQL command under the category
`Microsoft.EntityFrameworkCore.Database.Command` at level `Information`. With the stock
defaults, therefore, the first error in a DB-backed service exports the application's
entire SQL traffic for the next 180 seconds. Two consequences:

1. **Volume and cost.** In a DB-heavy service `Database.Command` is typically the
   dominant category. The step-up window coincides with an incident, i.e. exactly when
   traffic and telemetry cost are already elevated.
2. **Unredacted content.** `CompiledRedactionPatterns.Redact()` covers request metadata
   only — query string, route values, headers, request body. It does not scan the
   rendered text of arbitrary log events (documented in `StepUpLoggingOptions`). The SQL
   command text, and with `EnableSensitiveDataLogging` also the parameter values, leave
   the process unredacted.

A consumer can already write
`Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command = Warning`.
`SplitSerilogConfiguration` keeps `MinimumLevel` (including `Override`) on the root
reader, and the subsequent `.MinimumLevel.Verbose()` only replaces the default minimum,
so the override survives. But its semantics differ: the root override drops the event
**before any sink sees it**, so it never reaches `PreErrorBufferSink` and the pre-error
forensic buffer loses the very SQL that led to the error. It is also a manual per-consumer
opt-in, not a safe default of the library.

This repository has no EF Core dependency; the change protects downstream consumers.

## Decision

Add `StepUpLoggingOptions.NeverStepUpCategories` — a `string[]` of Serilog `SourceContext`
prefixes, defaulting to `["Microsoft.EntityFrameworkCore.Database.Command"]`.

**1. The gate lives in `StepUpSink.Emit`, and nowhere else.** `StepUpSink` is the single
choke point through which every exported event passes, including the consumer's own
config-declared `WriteTo` sinks attached to the gated inner logger. One check covers all
outputs. `PreErrorBufferSink` is deliberately **not** filtered: its flush is bounded
(`PreErrorBufferSize` events per trace, once, on error) and the SQL leading up to an error
is the most valuable content the buffer holds. The flood problem is the 180-second window,
not the one bounded flush. (Consequence: that flush still carries unredacted SQL through
the bypass logger — pre-existing behaviour, unchanged by this ADR, documented in the README.)

**2. A listed category is pinned to `BaseLevel`, not silenced.** It behaves as though
step-up did not exist: EF `Warning`/`Error` still export, `Information` SQL never does.
Formally the effective minimum for a listed category is
`max(BaseLevel, LevelSwitch.MinimumLevel)` — the `max` makes the invariant literal ("the
deny-list never makes a category more verbose than it already is") and is a no-op for any
sane `BaseLevel` ≤ `StepUpLevel` configuration. When the switch sits at `BaseLevel`
(not stepped up) the expression collapses to today's behaviour, so nothing changes outside
the step-up window.

**3. Matching follows Serilog's override semantics**: a `SourceContext` matches a prefix
when it is ordinal-equal to it, or when it starts with `prefix + "."`. Comparison is
`Ordinal` (Serilog's `LevelOverrideMap` is ordinal), so `…Database.CommandBuilder` does
**not** match `…Database.Command`. An event carrying no `SourceContext` never matches.

**4. The deny-list does not apply in `StepUpMode.AlwaysOn`.** In that mode the switch
starts at `StepUpLevel` and `Trigger()` no-ops — no step-up ever happens, so there is
nothing to suppress, and a developer running `AlwaysOn` locally wants to see the SQL. This
is enforced at the wiring site by passing an empty deny-list to the sink, not by a mode
check inside the sink: `StepUpSink` stays free of any `StepUpMode` dependency. In
`Disabled` and `Auto` the list applies (in `Disabled` it is a no-op, since the switch never
leaves `BaseLevel`).

**5. Blank entries are filtered out at wiring time**, mirroring how `RedactionRegexes`
filters empty patterns. A blank entry is a configuration typo: an empty-string prefix
matches dot-rooted `SourceContext` values (those starting with `.`) and costs a comparison
on every event, so blanks are dropped as defensive hygiene rather than passed to the sink.

The resolved `BaseLevel` is read from a new `internal LogEventLevel BaseLevel` accessor on
`StepUpLoggingController` rather than re-parsed at the wiring site, so the sink and the
switch can never disagree about what the base level is.

## Consequences

- A non-empty default is an observable behaviour change for existing consumers: EF SQL
  disappears from stepped-up exports. This is the point of the change. It ships as a
  **minor** version (2.0.0 → 2.1.0) with release notes and a README note showing how to
  restore the old behaviour (`"NeverStepUpCategories": []`).
- The library reads `SourceContext` for the first time. Events produced without it (raw
  `Serilog.Log.Information(...)` outside `ILogger<T>`) are unaffected.
- `StepUpSink` gains two constructor parameters (`baseLevel`, `neverStepUpCategories`). It
  is `internal`, so this is not a public API break.
- Extending to per-category levels later (`Dictionary<string, string>`) remains possible
  as an additive second option; `string[]` covers every known use case today.
- The default list holds exactly one entry. Candidates such as
  `Microsoft.AspNetCore.Hosting.Diagnostics` or `System.Net.Http.HttpClient` are
  deliberately excluded — that is precisely the diagnostic signal step-up exists to reveal.
