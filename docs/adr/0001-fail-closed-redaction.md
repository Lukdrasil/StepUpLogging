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

## Amendment (2026-10-09, performance)
`Redact` now tests one union of the patterns first, `(?:p1)|(?:p2)|...`, and returns the input when the
union cannot match it; only an input the union matches goes through the per-pattern loop above, whose
fail-closed catch is unchanged. The union is built only when it is exact: two or more patterns, all compiled
with the same options including `NonBacktracking`, the same timeout, and no `#` in any pattern (an inline
`(?x)` comment would swallow the branches after it). Anything else, including a pattern that fell back to
`Compiled` and a union that fails to build, keeps the plain loop. `NonBacktracking` has no backreferences, conditionals or lookarounds, so each branch matches the same text
inside the union as alone. A union that matches nothing proves no pattern matches the input, so the first
pattern leaves it unchanged, and so does each later one. A union that matches sends the input through the loop,
where a later pattern still sees an earlier pattern's `[REDACTED]`. The output is byte-identical to the loop, with one
accepted divergence: a timeout. If the union finishes inside the timeout and finds no match, the input is
returned as is, where the loop would have returned the sentinel had one of the patterns run out of time on
that input. Nothing leaks, because no pattern matches the value; the value just no longer turns into the
sentinel on that path. If the union itself times out, `Redact` falls back to the loop, and the sentinel
behaviour above holds. See `RedactionShortcutPrefilterTests` and `docs/performance/results-redaction.md`.
