# ADR 0018 — Core 4.0.0: the `AuditEvent` contract, and one sink or none

- Status: Accepted
- Date: 2026-08-13
- Issue: #22 (Part A)

## Context

Three changes to `AuditEvent` are needed before any spooling sink can work correctly. One is
additive; one is a defect in the current contract; one is forced by at-least-once delivery.

**`ActorType` defaults to `"user"`.** An event composed without setting it does not record a
missing value — it *asserts that the actor was a human*. In an append-only audit store that
assertion is permanent. This has already produced two real defects in the first consumer:
anonymous and service-token authorization denials recorded as authenticated users, and a mapper
that still hardcodes `"user"` for callers that may be API keys. Both were found by review rather
than by a test, because there is nothing to test against — the wrong answer is the default.

**There is no event identifier.** A spooling sink retries, and a crash between "receiver accepted"
and "spool entry deleted" produces a duplicate. At-least-once delivery is inherent to the design
and can only be deduplicated, which requires a producer-generated identifier.

**There is nowhere to record before/after state.** Every audit schema worth the name has that
pair; today a call site recording a role change has to flatten it into two ad-hoc `Data` keys that
every consumer then agrees on by convention.

## Decision

1. **`ActorType` becomes `required`.** Every call site states the actor kind once, at the only
   place that knows it, and the compiler walks the developer through every site.

2. **The factory methods take it as a third parameter:**
   `Success/Failure/Denied(action, actorId, actorType)`. This is forced — with `required` the
   existing two-argument factories would not compile inside the library itself.

3. **`EventId` is library-owned**, stamped in `AuditLogger.Enrich` alongside `TimestampUtc`,
   `TraceId`, `SpanId`, `SourceIp` and `UserAgent`; a caller-set value is overwritten. The value
   is a UUIDv7 (`Guid.CreateVersion7()`), which is time-ordered and so also gives the receiver a
   stable sort — arrival order cannot provide one once a spool drains out of order after an outage.

4. **`OldValues` and `NewValues` are added** as `IReadOnlyDictionary<string, object?>?`.
   Caller-supplied, therefore **not redacted** — the same category as `Data` under ADR 0016 D5.

5. **`AddAuditLogging` refuses a second sink registration.** It registers the sink with `Add`, not
   `TryAdd` (`StepUpLoggingExtensions.cs:129`), while `AuditLogger<T>` resolves a single
   `IAuditEventSink` (`AuditLogger.cs:23`) — so registering two sinks silently keeps only the last.
   A consumer following the README's own example and *also* calling a package's registration
   method would lose one sink without a word. `AddAuditLogging` now throws when an
   `IAuditEventSink` is already registered, naming both. The check belongs in core, where the
   registration happens, so it holds in either call order and protects consumers who never call
   `AddEncryptedSpoolAuditSink`.

6. **`IAuditEventSink.WriteAsync` returns `ValueTask<AuditWriteResult>`** — `Stored` or `Dropped`.
   Today the interface can express only "stored" (normal return) or "failed" (exception), and a
   sink that deliberately discards a record under back-pressure (ADR 0020 D6) has no third thing to
   say. Left as-is, that record is counted in `audit_events_total`, which ADR 0016 defines as
   *records written*, and it still gets a companion log — breaking ADR 0016 D7's invariant that a
   companion log implies an audit record, and making the README's
   `rate(audit_events_total[1h]) == 0` alarm read healthy exactly while audit is being lost.
   With the result, `AuditLogger` counts a `Dropped` record on `audit_events_dropped_total`, leaves
   `audit_events_total` alone, and **writes no companion log**.

   `AuditWriteResult` declares `Stored = 1` and `Dropped = 2` — **no zero member**. `default` and
   `default(ValueTask<AuditWriteResult>)` therefore produce a value matching nothing, and
   `AuditLogger` throws on it, naming the sink type. A zero member would be a default that asserts
   something no sink said: `Stored = 0` would make every mechanically-ported sink silently claim
   success (the README's own `return default;` at `README.md:748` compiles straight through the
   signature change), and `Dropped = 0` would make a correct sink silently report all its records
   gone. This ADR exists to remove exactly that kind of default; it does not get to introduce one.
   Explicit numeric values follow `AuditOutcome`; its zero member and its tolerance of unknown
   values deliberately do not. `AuditOutcome` is caller-supplied descriptive data, and
   `AuditLogger.OutcomeTag` forgiving an unrecognised value into `"Unknown"`
   (`AuditLogger.cs:106-112`) costs one imprecise metric tag. `AuditWriteResult` is a **control
   signal**: it decides whether a companion log is written and which counter moves. Forgiving an
   unrecognised control signal means silently choosing one of those behaviours on the sink's
   behalf, and either choice is a lie about whether the record exists. The throw is an
   `InvalidOperationException` naming the sink type, and it is deliberately **not** counted in
   `audit_write_failures_total` — that counter means the sink threw while writing, and here the
   write may well have succeeded. This is a contract violation by the sink and surfaces as one.

   **This amends ADR 0016 D2.** Not its propagation guarantee — nothing is thrown here, so
   exceptions still propagate unchanged — but its other half, *"no log and continue"*. Core now has
   a sanctioned way for a sink to say "I did not store this", available to every sink author, not
   an exception carved out for one package. The rationale D2 gives still holds and is preserved:
   what it forbids is a failure that is **silent**, and `Dropped` is the opposite of silent — it
   suppresses the companion log, keeps the success counter honest, and lands on a counter of its
   own.

7. **A third counter, `audit_events_dropped_total`**, joins the two ADR 0016 enumerates.
   **This supersedes ADR 0016's Metrics section**, which states there are two — an amendment note
   is recorded in `docs/adr/0016-audit-logging.md` itself, because a metric name is a contract that
   consumers alert on, exactly as `AuditEvent.cs:3-7` argues for the outcome enum's numeric values.
   The README's operator guidance changes with it: `rate(audit_events_total[1h]) == 0` remains the
   "audit stopped working" alarm and is now trustworthy, and `audit_events_dropped_total > 0`
   becomes an alarm in its own right.

8. **The core package goes to `4.0.0`.** Decisions 1, 2, 5 and 6 are breaking.

## Consequences

- Upgrading from 3.x is a deliberate act: the build fails at every `AuditEvent` construction until
  the actor kind is supplied. That is the intent — a silent upgrade would leave the false `"user"`
  assertions in place.
- One `AuditAsync` call is one event with one identity, even if the caller passes the same instance
  twice. The receiver can deduplicate on `EventId` without coordinating with producers.
- A caller who wants idempotency on their own terms (a retry of the same business operation must
  not produce a second record) cannot express it. That is accepted: making `EventId`
  caller-settable would mean a repeated `AuditAsync` on one instance is **silently discarded** by
  the receiver as a duplicate, which is exactly the silent audit loss ADR 0016 exists to prevent.
- `ActorType` remains a `string`, so consumers can use their own actor categories.
- An application that today registers two audit sinks by accident and runs will stop starting after
  the upgrade. That is the point — it was only ever auditing through one of them — but it is a
  behaviour break beyond the type changes, and it belongs in the release notes.
- The README's audit section and `AuditEventTests` both encode the old contract (examples that no
  longer compile, `ActorType` listed as optional, an assertion on the `"user"` default). Updating
  them is part of this work, not a follow-up.
- The README's testing recipe inverts: it teaches substituting a test sink via
  `AddAuditLogging<RecordingAuditSink>` in `ConfigureTestServices`, which now throws once
  `Program.cs` has already registered one. The recipe becomes `RemoveAll<IAuditEventSink>()` and
  then register — and the guard's message says so, because that is where the developer is standing
  when they hit it.
- Decision 6 makes every existing sink implementation change signature. That is cheap now and
  expensive later: ADR 0016 D3, before its ADR 0017 amendment, meant the package shipped none, so
  the implementations in existence at the time of this decision are the ones consumers wrote, and
  4.0.0 is the only release that can absorb this.
- Decision 6's own hazard, named so it is designed against: a sink author can return `Stored`
  reflexively without thinking, which is the same class of silent wrong answer as the `"user"`
  default this ADR removes. The XML docs must say plainly that `Dropped` means *the record is gone*
  and that returning `Stored` for a discarded record erases the operator's only signal.
- The guard is partial by construction: a consumer who bypasses `AddAuditLogging` and calls
  `services.AddSingleton<IAuditEventSink, …>()` directly still silently last-wins. Closing that
  would mean core policing registrations it does not own. Documented rather than chased.

## Rejected alternatives

- **Default `ActorType` to `"unknown"`.** A quieter upgrade, and the value at least stops lying.
  But `"unknown"` then spreads silently through production and nobody learns they should have
  written the real value — the wrong answer is still the default, merely less harmful.
- **A third parameter with a default value** (`actorType = "unknown"`). Compiles everywhere,
  changes nothing, and reintroduces the exact defect under a different string.
- **An enum instead of `string`.** Removes typos and inconsistent values across call sites, but is
  a larger break and takes extensibility away from consumers with their own actor categories.
- **Dropping the factory methods.** Object initializers with `required` would make omissions
  impossible and remove the risk of three positional strings being transposed. Rejected as a wider
  break than necessary; the transposition risk is instead called out in the XML docs.
