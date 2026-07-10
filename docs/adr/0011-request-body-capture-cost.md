# ADR 0011 — Request-body capture: cost, and preserving the triggering trace

- Status: Accepted
- Date: 2026-07-10

## Context
With `CaptureRequestBody = true`, `EnableBuffering()` runs on every POST/PUT/PATCH so the
enricher can rewind the body at request completion (ADR 0004). But the body is only ever *read*
when `IsSteppedUp`. So the buffering — memory up to the 30 KB threshold, disk spooling beyond
it, on every write request — is paid permanently for a feature used only inside a step-up
window. Worse, analysis showed the payoff is usually missed entirely: `StepUpTriggerSink` calls
`controller.Trigger()` from a background task draining a channel, so when a handler logs an
`Error` mid-request, `IsSteppedUp` is still `false` when the enricher runs at completion. The
request that *caused* the step-up logs no body; its successors do. The exact inverse of the
intent.

## Decision
(a) Keep `EnableBuffering()` unconditional. Gating it on `IsSteppedUp` would save the IO but
guarantee the triggering request's body is never available — the developer chose completeness
over the tax.
(b) Fix the missed payoff: the capture condition becomes `IsSteppedUp || Response.StatusCode
>= 500`, which is knowable synchronously at enricher time and needs no new state. A failing
request now logs its own body.
(c) Record, but do not yet build, the upgrade path that removes the tax without losing (b):
replace `EnableBuffering()` with a tee-ing wrapper stream that copies the first
`MaxBodyCaptureBytes + margin` characters into a pooled buffer as the endpoint reads the body,
discarding the remainder. Bounded memory per in-flight request, zero disk spooling, body always
available. It subsumes the `ArrayPool<char>` idea (the pooled buffer *is* the tee target) and
removes the `AllowSynchronousIO` opt-in and the `Position = 0` rewind. Deferred because it
replaces a framework primitive with our own stream and deserves its own block with its own
tests.

The symmetry that motivates all of this: for *log events*, `PreErrorBufferSink` already
preserves the preceding context of the trace that fails — a per-`TraceId` ring buffer flushed to
the bypass logger the instant an `Error`/`Fatal` lands. Part (b) gives the request body the same
property. Both answer the same question: when something breaks, what led up to it must already
be in hand.

## Consequences
- 5xx requests now emit `RequestBody` where they previously did not — a visible behavior change,
  listed in MIGRATION.md.
- The buffering tax remains, consciously; the tee-stream is tracked as follow-up work.
- Record only (no change): `PreErrorBufferSink.TrackLruFor` takes one global lock on the logging
  hot path for its move-to-front. Known ceiling; approximate-LRU (a timestamp on `Buffer` plus
  periodic sweep) is the upgrade path if contention ever shows in a profile.
