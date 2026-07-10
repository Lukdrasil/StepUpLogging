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

## Amendment (2026-07-10)
Redaction patterns are now compiled with `RegexOptions.NonBacktracking`, which guarantees
linear-time matching and makes catastrophic backtracking structurally impossible rather than
merely time-bounded. Patterns that use lookaround or backreferences are unsupported by that
engine and throw `NotSupportedException` at construction; those fall back to
`RegexOptions.Compiled`, exactly as before. The 100 ms timeout stays in both cases as a
backstop, and the fail-closed sentinel above remains the behavior when it fires.
`NonBacktracking` cannot be combined with `Compiled`, so the fallback swaps the option rather
than adding to it.
