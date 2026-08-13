# ADR 0020 — Write-ahead spool: fsync per record, and no failure mode that loses audit quietly

- Status: Accepted
- Date: 2026-08-13
- Issue: #22 (Part B)

## Context

Every audit record is written to disk encrypted before it is sent, and deleted only once the
receiving service that holds the private key (app2) confirms it is durably stored. That ordering
is the point of the package: a record that exists only in memory is lost on a crash, and a spool
path that runs only on failure never runs in normal operation and therefore rots unnoticed.

The design's failure modes all share one property — each has a cheap variant that keeps the
application running by losing audit silently. This ADR rejects every one of them.

## Decision

1. **`WriteAsync` returns only once the record is durably on disk** — flushed to the device, not
   merely to the OS page cache. One fsync per audit write, synchronously on the business path.

2. **Records are written to a `.tmp` file and atomically renamed** into place. The drain worker
   sees only complete files; a `.tmp` left by a crash is handled at start-up. A `.tmp` that still parses as a whole envelope is instead promoted to its `.env` name — the directory entry for its rename is not fsynced, so a power loss can revert the rename alone and leave a complete record behind under its old name, and that record was already durable. An unparseable `.tmp` is deleted. Without this, a crash
   mid-write leaves truncated content that the drain worker would treat as a poisoned record.

3. **One file per record**, named `{createdUtc:yyyyMMddTHHmmssfffffffZ}-{eventId}.env`, so
   lexicographic order is chronological order and two records in the same tick cannot collide.

4. **Serialization happens first, inside the sink's `WriteAsync`**, before encryption and before
   touching the disk, and a configurable cap bounds the serialized payload. A value that cannot be
   serialized, or a runaway `Data` dictionary, fails there — attributed to the call site rather
   than surfacing later as an encryption or disk error. The check is *not* in core's `AuditAsync`:
   ADR 0016 D3 does not let core assume its sink serializes to JSON, and promoting the check to
   core later is additive whereas removing it would be breaking.

5. **Spool caps**, per issue #22 B5: at ≥ 50 % of cap the health check reports the configured
   `SpoolWarnStatus` (default `Unhealthy`, overridable) including the overall status, and logs
   ERROR. Already-spooled records are never dropped or rotated out.

6. **At 100 % of cap the new record is dropped**, logged at Critical and counted — `WriteAsync`
   returns `AuditWriteResult.Dropped` (ADR 0018 D6) rather than throwing, so core neither counts it
   as written nor emits a companion log for it, and ADR 0016 D7's invariant survives intact.
   This package is not carving out an exception — ADR 0018 D6 made "I did not store this" part of
   the core contract, and this is the one condition under which this sink uses it.
   **Why dropping is the right answer here rather than throwing:** throwing would mean a long app2
   outage fills the spool and from that moment *every audited operation in the application fails*
   — the service outage that issue #22's cap section exists to prevent. The trade it describes is
   "bounded, loud, visible loss" of records, not loss of the service. The loss is not silent: the
   health check has already been Unhealthy since 50 %, every drop logs Critical, and a counter
   tracks it.

7. **A permanently rejected record is dead-lettered, loudly.** The delivery contract is ours to
   define (app2 conforms to it): `POST {base}/audit` with one envelope per request; **2xx** means
   the record is durably stored; **exactly `400`, `409`, `413`, `415`, `422` are a permanent
   rejection** — each indicts the record itself (malformed body, a conflicting version, an
   oversized payload, an unsupported media type, an unprocessable value) and retrying cannot fix
   it; **every other response — 3xx, `401`/`403`/`407`, `404`/`405`, `408`, `429`, 5xx, network
   faults, and timeouts — is transient** and stays in the spool for retry with backoff, even
   though some of those look permanent in the moment (an expired credential, a receiver
   mid-rollout answering `404`): under the old rule one expired credential would walk the entire
   spool into dead-letter at machine speed; a config outage must never consume the records
   produced during it. On a permanent failure the record is moved to `dead-letter/` beside the
   spool — never deleted — logged at **Critical**, and counted. The queue then continues. A
   non-empty `dead-letter/` makes the health check **Unhealthy, including the overall status**.
   This is a receiver verdict about a record it was actually sent; a record that cannot be read
   from disk after bounded retries is dead-lettered as unreadable — distinct from receiver
   rejection: the endpoint is never contacted, so it never counts against the endpoint's own
   reachability signal, only against the drain worker's own failure counter.

8. **Deletion of spool files by a compromised host is an accepted risk.** Encryption does nothing
   against it. The mitigation is to shorten the window: a short `DrainInterval`, so records sit on
   disk for seconds rather than minutes, and monitoring on app2 for a producer that has gone quiet.

## Consequences

- Audit writes cost one fsync each — single-digit to tens of milliseconds on ordinary SSDs, more on
  network storage. Audit volume is small relative to ordinary traffic (mutating operations, not
  every request), but the cost lands on the business path and must be documented and measured,
  because nobody will guess that audit is the source of the latency.
- Dropping at 100 % is deliberate, bounded, loud, visible loss. A ring buffer was never an option:
  it would let an attacker push out the record of their own action by generating noise.
- **`Dropped` is returned for exactly one condition in this sink: the spool is at 100 % of cap.**
  That fence matters more than it looks. `Dropped` is the one thing a sink can say that makes the
  companion log disappear and the success counter stay flat — which is precisely why it must never
  become the convenient answer to an awkward failure. Every other failure here — spool write,
  serialization, encryption, a key that cannot be obtained — still throws and propagates under
  ADR 0016 D2. The exception-propagation guarantee itself has no exceptions.
- One malformed record can no longer take down auditing for the whole application, because the
  queue moves past it — but it does take the instance out of rotation until someone clears
  `dead-letter/` by hand. That is the intended trade: a record that never reached the audit store
  must not be something an operator can fail to notice.
- `dead-letter/` is deliberately not self-cleaning. It is evidence.

## Rejected alternatives

- **fsync configurable, or off by default.** Turning it off silently voids the guarantee that
  write-ahead exists for, and nothing in review would show it.
- **Group commit** across a few-millisecond window. Keeps the guarantee and cuts fsync count under
  load, but is markedly harder to write and to test, and solves a problem nobody has measured.
- **Retrying a permanently rejected record forever.** What issue #22 originally described. The
  drain runs oldest-first, so one bad record blocks the queue, the spool fills, and at 100 % the
  package starts rejecting new events — a single corrupt record disables auditing everywhere.
- **Dead-lettering after N attempts regardless of status code.** Simpler and independent of which
  codes app2 actually returns, but a long app2 outage would dead-letter records that were perfectly
  deliverable.
- **Skipping a bad record and retrying it later.** Nothing is ever set aside, but the spool slowly
  fills with undeliverable records until it hits the cap.
- **Throwing when the spool is full.** Strictly consistent with ADR 0016 D2 and with every other
  failure path here, and it guarantees no operation ever completes unaudited — but it converts a
  sustained app2 outage into a total application outage.
- **Falling back to `ToString()` for values that will not serialize.** The business operation would
  never fail because of audit, at the price of an audit record that has quietly lost its structure
  — and someone will rely on that record during an investigation.
