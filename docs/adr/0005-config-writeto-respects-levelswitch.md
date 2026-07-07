# ADR 0005 — Configuration WriteTo sinks respect the LevelSwitch

- Status: Accepted
- Date: 2026-07-07

## Context
The root logger is `ReadFrom.Configuration(...)` then `MinimumLevel.Verbose()`. If a user
declares `"Serilog": { "WriteTo": [...] }` in appsettings, that sink attaches to the **root**
and therefore exports **everything at Verbose, permanently**, bypassing the step-up
`LevelSwitch` entirely — the opposite of the library's purpose. A surprising footgun.

## Decision
Restructure pipeline wiring so configuration-driven `WriteTo` sinks sit **inside the gated
sub-logger** (behind the `LevelSwitch`), not on the Verbose root. From configuration the root
reads only `MinimumLevel.Override` and enrichers; `WriteTo` entries are applied to the
step-up-gated inner logger. Bypass-routed events (`IsImmediate`, `IsRequestSummary`) continue
to export independently as today.

This is a **behavior change** for anyone currently relying on root `WriteTo` exporting at
Verbose. It is called out in the migration notes and covered by ADR 0006 (v2).

## Consequences
- Config sinks now honor step-up gating — consistent, least-surprise semantics.
- Migration note required: users who *wanted* always-verbose export use the immediate/summary
  bypass or `AlwaysOn` mode instead.
- Tests: a config-declared sink must NOT receive sub-step-up events when not stepped up, and
  MUST receive them once stepped up.
