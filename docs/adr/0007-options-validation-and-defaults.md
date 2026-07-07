# ADR 0007 — Options validation and reconciled defaults

- Status: Accepted
- Date: 2026-07-07

## Context
- `DurationSeconds` default is **180** in `StepUpLoggingOptions` but the controller's `<= 0`
  fallback is **300** — two different "defaults" depending on the path taken.
- A typo'd level (`"Warnign"`) silently falls back to a hardcoded default; the user never
  learns their config is broken. An inverted config (StepUpLevel less verbose than BaseLevel)
  also passes unnoticed.
- `IOptions<StepUpLoggingOptions>` is registered by hand outside the standard options
  pipeline, so `Validate`/`ValidateOnStart` are not available.

## Decision
Move to `AddOptions<StepUpLoggingOptions>().Bind(section).ValidateDataAnnotations().Validate(...).ValidateOnStart()`.
Validation rules:
- `DurationSeconds > 0` (reconcile: single canonical default — keep **180**, remove the 300
  fallback; invalid values fail fast rather than silently becoming 300).
- `BaseLevel` / `StepUpLevel` / `RequestSummaryLevel` parse to a valid `LogEventLevel`
  (reject typos at startup instead of silent fallback).
- Warn (not fail) when `StepUpLevel` is not more verbose than `BaseLevel` — likely a mistake.

## Consequences
- Misconfiguration surfaces at startup (`ValidateOnStart`) instead of silently degrading.
- The hand-rolled `IOptions` registration is removed in favor of the framework pipeline.
- One canonical default for `DurationSeconds` (180) everywhere.
- Tests: invalid level string and `DurationSeconds<=0` must throw `OptionsValidationException`
  on start; valid config must not.
