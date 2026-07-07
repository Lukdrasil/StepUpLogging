# ADR 0001 — Redaction fails closed

- Status: Accepted
- Date: 2026-07-07

## Context
`CompiledRedactionPatterns.Redact()` wraps the whole loop in try/catch and, on any
exception (notably `RegexMatchTimeoutException`, 100 ms limit), returns the **original,
unredacted** input. For a component whose entire job is to keep secrets out of logs, this
is backwards: a slow/catastrophic pattern leaks the very value it should mask.

## Decision
Redaction fails **closed**. On a redaction error the value is replaced with a sentinel
`"[REDACTION-ERROR]"` rather than the raw input. Catch is per-pattern so one pathological
regex does not disable the remaining patterns, and a partially-redacted string is never
downgraded to the raw value.

## Consequences
- A regex timeout now masks the field instead of leaking it (safer default).
- Users who had a broken regex will see `[REDACTION-ERROR]` instead of silent pass-through —
  a visible signal, which is desirable.
- Test: a deliberately catastrophic pattern over a long input must yield the sentinel, never
  the secret substring.
