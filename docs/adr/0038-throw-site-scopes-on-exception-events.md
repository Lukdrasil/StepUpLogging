# ADR 0038 - Throw-site scopes on exception events

- Status: Accepted
- Date: 2026-11-27
- Amends: ADR 0026 D3 and D7 (one more enricher before the gate)

## Context

An event that logs an exception lost every `BeginScope` and `LogContext` property, because the scope was
disposed while the exception unwound: the request completion event of `RequestLoggingMiddleware`, the
`ExceptionHandlerMiddleware` event and a `LogError(ex, ...)` in a `catch` outside the `using` block. Serilog and
`Serilog.Extensions.Logging` have no feature for this (serilog/serilog#895). A MEL provider such as
`Nito.Logging`'s `AddExceptionLoggingScopes()` does not help: `AddSerilog(writeToProviders: false)` ignores DI
logger providers, and the request completion event never passes through MEL.

## Decision

**D1. Record the scopes at the throw site.** `ThrowContextEnricher`, adapted from Serilog.ThrowContext (MIT, see
`THIRD-PARTY-NOTICES.md`), subscribes to `AppDomain.FirstChanceException` and stores `LogContext.Clone()` and
`ExecutionContext.Capture()` per exception in a `ConditionalWeakTable`. Replaying the execution context restores
the `AsyncLocal` scope of `SerilogLoggerProvider`, so `BeginScope` scopes come back with the `LogContext` ones.

**D2. Replay on the root, before the gate, after `FromLogContext`.** The enricher runs ungated between
`FromLogContext` and `AlwaysExportEnricher`. It adds only absent properties, so the scope open where the event is
logged wins over the throw site's. Root-only is enough: the buffer flush and both bypass routes forward events the
root already enriched. An event without an exception costs one null check.

**D3. The deepest throw site wins.** Inner exceptions are replayed before the exception that wraps them, every
entry of an `AggregateException` included, and an exception's first throw before its rethrows.

**D4. On by default, one switch.** `CaptureScopesOnException` (default `true`). With `false` the enricher is never
constructed, so nothing subscribes. Once subscribed, the handler stays for the process lifetime: it is static and
the captures die with their exceptions.

## Consequences

- Every thrown exception in the process, handled or not, pays one `ExecutionContext.Capture()`, one
  `LogContext.Clone()` and a table entry. Code that throws as control flow at a high rate should set
  `CaptureScopesOnException` to `false`.
- The captured execution context lives as long as the exception object, which keeps its `AsyncLocal` values alive.
- When the event already has a `Scope` property, the throw site's `Scope` entries are not merged into it.

Rejected: a MEL `ILoggerProvider` wrapper, which never sees the request completion event. Rejected: capturing only
in the library's middleware, which misses a `catch` in application code.
