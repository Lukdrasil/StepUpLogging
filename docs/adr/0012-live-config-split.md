# ADR 0012 — Serilog configuration is split live, not snapshotted

- Status: Accepted
- Date: 2026-07-10

## Context
ADR 0005 moved config-declared `Serilog:WriteTo` sinks behind the step-up `LevelSwitch` by
partitioning the app configuration into a "root" view (everything except `WriteTo`) and a
"gated" view (only `WriteTo` plus `Using`). The implementation built both views with
`new ConfigurationBuilder().AddInMemoryCollection(pairs).Build()` — a materialized snapshot
taken once at startup. That silently discards the live configuration's reload semantics:
`ReadFrom.Configuration(...)` normally honors `reloadOnChange`, so `Serilog:MinimumLevel:Override`
can be retuned at runtime by editing appsettings. Against a snapshot, nothing reloads, and
nothing says so.

## Decision
Keep `SplitSerilogConfiguration`'s name, signature, and `internal` visibility — its tests pin
those — but return a pair of `PrefixFilteredConfiguration` wrappers instead of snapshots. The
wrapper implements `IConfiguration` over the live instance, delegating `GetSection`,
`GetChildren`, `GetReloadToken`, and the indexer, and hiding keys its filter rejects. The root
filter hides everything under `Serilog:WriteTo:`; the gated filter exposes only `Serilog:WriteTo:`
and `Serilog:Using:` plus their ancestor sections, so `GetSection("Serilog")` still resolves.
Delegating `GetReloadToken` is the entire point: Serilog's configuration reader re-reads on
change, and `MinimumLevel:Override` keeps working.

## Consequences
- Runtime level overrides work again for config-declared Serilog settings, matching the behavior
  users get from plain `ReadFrom.Configuration`.
- The wrapper is a small amount of delegation code that must be tested for the ancestor-resolution
  edge case: `GetSection("Serilog").GetSection("WriteTo")` must resolve through both filters
  correctly.
- No public API change. ADR 0005's decision stands; only its implementation changes.
