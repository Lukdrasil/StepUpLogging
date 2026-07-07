# ADR 0006 — v2.0.0 breaking API cleanup

- Status: Accepted
- Date: 2026-07-07

## Context
Several findings can only be fixed by changing the public surface. The developer approved a
major version bump, so these are bundled into **v2.0.0** rather than deferred.

## Decision
Ship the following breaking changes together as v2.0.0:

1. **Unify console-logging toggle.** Remove the `enableConsoleLogging` parameter from the
   `AddStepUpLogging(...)` overloads; console logging is driven solely by
   `StepUpLoggingOptions.EnableConsoleLogging`. One way to do one thing.
2. **Fix the `configure` callback signature.** The current overload synthesizes a fake
   `HostBuilderContext` with null `Configuration`/`HostingEnvironment` (NRE waiting to
   happen). Change the delegate to `Action<IServiceProvider, LoggerConfiguration>` (drop the
   unusable context), or pass a real context. Chosen: `Action<IServiceProvider, LoggerConfiguration>`.
3. Pipeline restructure from ADR 0005 (config WriteTo behavior) is a v2 behavior change and
   travels in the same major bump.

## Consequences
- Existing callers passing `enableConsoleLogging:` or using the `HostBuilderContext` callback
  must update — documented in `MIGRATION.md` / release notes.
- `<Version>` → `2.0.0`; `PackageReleaseNotes` enumerate the breaks.
- Non-breaking fixes (ADR 0001–0004, options validation, LRU, dead-code removal) do not
  *require* the major bump but ship in the same release.

## Deferred (recorded, not done now)
- **Multi-targeting** `net8.0;net9.0;net10.0` — large, independent change (CI matrix, per-TFM
  API availability, per-TFM test runs). Own PR after v2. See task `audit-fixes` finding 11.
