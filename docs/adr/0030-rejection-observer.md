# ADR 0030 - Rejection observer: inform the consumer of a dead-lettered record

- Status: Accepted
- Date: 2026-10-12
- Issue: #71
- Amends: ADR 0020 D7

## Context

A consumer seals each audit record with its own `IAuditPayloadEncryptor` before the record is
spooled (ADR 0017 D3); the spool holds sealed bytes only, and the plaintext is gone by the time the
drain worker posts it. When the receiver's key store is replaced while producers run, a producer
keeps sealing with the old key until its key source refreshes (userorg: Ogham `RefreshInterval`,
1 min) plus the receiver's own key document cache (the audit unsealing proxy memoises it for 30 s).
The receiver answers `422` with the problem code `sealing.unknown_key`, and ADR 0020 D7 dead-letters
the record. Every record sealed in that window is lost to the audit store: today up to 60 s + 30 s.

The library has no reference to the consumer's sealer and no way to tell it "the receiver does not
know your key". ADR 0020 D7 (#71 amendment) left reading a problem+json body out, because the status
code alone decides the outcome.

## Decision

**D1. An optional `IAuditRejectionObserver` informs; it never decides.** Each registered observer
gets `OnRejectedAsync(AuditRejection(StatusCode, ProblemCode), ct)` for a record the endpoint
rejected on its own. The outcome is the same with or without observers: the status code is still the
whole contract of ADR 0020 D7, and a rejected record is dead-lettered. A "retry or dead-letter"
handler was rejected: the rejected envelope is sealed with the key the receiver does not know, so a
retry can never store it.

**D2. Dead-letter first, then notify.** The record is moved to `dead-letter/`, logged at Critical and
counted before any observer runs, so an observer that hangs or throws cannot keep the record in the
spool or block the records behind it. The Critical line names the problem code when one was read
(", problem code sealing.unknown_key").

**D3. No re-seal.** The spool never holds plaintext, by design (ADR 0017), so nothing can re-seal the
rejected envelope with a refreshed key. The observer stops further damage: a consumer that refreshes
its key on `sealing.unknown_key` makes the records sealed after the refresh deliverable.

**D4. The body is read only when someone listens.** The problem code is read only for a single-record
permanent rejection with at least one observer registered, and only from an
`application/problem+json` body (media type compared case-insensitively) of at most 16 KiB. Only the
top-level string `code` counts; anything else is `null`, never a failure. A stored record, a
transient answer and a rejected batch never read the body, and with no observer the hot path is
unchanged and allocation-free. A rejected batch is redelivered record by record as before, so the
observer hears of the record rejected alone, never of the batch.

**D5. Every permanent status is reported; the consumer filters.** The observer is told about `400`,
`409`, `413`, `415` and `422` alike, with whatever code was read. Which code means "refresh the key"
is the consumer's contract with its receiver, not this library's.

**D6. Each observer is bounded and isolated.** Observers run one after another, each bounded by
`DeliveryTimeout` (linked with the host's stopping token, so shutdown is not held up) through the
token it receives and a `WaitAsync` for an observer that ignores the token. An exception or a run past
the limit is logged at Warning, naming the observer type, and the cycle continues with the next
observer and the next record. Register observers as thread-safe singletons: the drain worker is a
hosted singleton, so a scoped one would be a captive dependency.

## Consequences

- The loss window after a key store replacement shrinks from up to 60 s + 30 s to about
  max(`DrainInterval` 5 s + one round trip, what remains of the receiver's 30 s key document cache):
  a refresh right after the first rejection may still fetch the receiver's stale document. Once the
  receiver stops serving a stale document after a store change, the window is about 5 s + one round
  trip.
- The records rejected inside that window stay dead-lettered; an operator still handles them.
- Public surface grows by `IAuditRejectionObserver` and `AuditRejection`; with no observer
  registered, behaviour is that of 5.3.0.
