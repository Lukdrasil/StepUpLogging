# Documentation Summary for B01

## XML Documentation
- `ResolveOtlpProtocol`: Already present with accurate summary (protocol resolution logic)
- `IsExcludedPath`: Already present with accurate summary (path matching and boundary logic)  
- `CreateBypassLogger`: Already present with clear remarks caveat about `shared:true` requirement for config File sinks (ADR 0003)

All three internal helpers have comprehensive XML documentation in place; no changes needed.

## README Changes
Added user-facing note in "Export Architecture" section explaining that config-declared `Serilog:WriteTo` sinks now receive bypass-routed events (immediate logs, request summaries, pre-error buffer flushes) alongside gated logs. Explicitly documented that config File sinks must set `shared: true` to prevent file lock contention (ADR 0003).

## Out of Scope
- ADR documentation (bypass-wiring behavior change noted separately)
- Code or test modifications (documentation only)
