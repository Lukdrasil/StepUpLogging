# ADR 0041 - The spool is one module, and its head follows the drain's reads

- Status: Accepted
- Date: 2026-11-27

## Context

The spool directory was handled through five separate collaborators: `SpoolWriter`, `SpoolReader`,
`SpoolUsageTracker`, `DeadLetterBox` and `SpoolHead`. Each caller wired the protocols between them by hand. The
sink reserved room, wrote, then committed or abandoned. `DrainWorker` took a release token before every delete
or dead-letter move, released after it, and set `SpoolHead` per drain step and cleared it after a full pass. The
health check and the gauges each took two or three of the pieces. `DrainWorker` had nine constructor
collaborators, and the test harness rebuilt the whole graph by hand.

## Decision

**D1. One `Spool` module owns the spool directory.** It composes the writer, reader, capacity, usage tally,
dead-letter box and head, and its interface is the life of a record: `Reserve` and the reservation's `WriteAsync`
(admitted only, the warn-threshold verdict before the write), `ReadOldestFirst`, `Remove` (delivered),
`SetAside` (dead-lettered, returns the new path), plus `Usage`, `Measure`, `HoldsDeadLetters`,
`DeadLetterDirectory` and `OldestRecordAgeAt`. It is the one spool singleton the sink, `DrainWorker`, the health
check and the gauges share. `DrainWorker` keeps all delivery policy: batching, outcomes, retries, the dead-letter
routes and reasons, the unreadable retry count and the record traces.

**D2. The head is derived from reads and settlements.** The oldest record waited on is the oldest entry the read
in progress handed out that has not been removed or set aside. A read that reaches the end with every entry
settled clears it. A read stopped early (a failed delivery, an unreadable file within its limit, cancellation)
leaves it on the record the drain stopped on. Reading, removing and setting aside belong to one drain at a time.
`DrainWorker` runs one cycle at a time, except that the shutdown drain in `StopAsync` can overlap a cycle that
outlived the host's stop token, so the spool locks its read state: an overlap can leave the age approximate,
never the state corrupt.

**D3. Nothing observable changes except one edge, which is a fix.** File layout, wire format, crash recovery, the cap, the
recheck interval, the drop reporting, log text, metrics and health signals are unchanged. An unreadable file set
aside is still not released from the tally, because its size is unknown. The edge: while a rejected batch is
redelivered record by record, the head now moves past each record as it is delivered. It used to keep the batch's
first record, already delivered, until the next cycle read the spool again, so a redelivery that stalled on a later
record reported the older age through the whole backoff wait. The `CHANGELOG` lists it under Fixed.

Rejected: keeping `SpoolHead` set per drain step by `DrainWorker`, which leaves the head protocol in the worker
and the release-token protocol duplicated beside it.
