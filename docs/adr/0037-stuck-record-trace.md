# ADR 0037 - One trace per stuck record, in memory, with a fallback activity

- Status: Accepted
- Date: 2026-11-19
- Issue: #104

## Context

When a spooled audit record fails delivery, `DrainWorker` logs a Warning per attempt. When the record is finally
delivered, nothing was logged, so an operator saw warnings that just stop. The drain worker runs in a
`BackgroundService` with no ambient trace, so those warnings carried no TraceId that could tie them together.

## Decision

**D1. A stuck record owns one record trace.** The first failed attempt of a record starts a trace; every later
Warning for it, and its closing entry, is logged inside that trace: an Information entry when it is delivered
("delivered after {FailedAttempts} failed attempts"), or the existing Critical when it is dead-lettered. A record
delivered on the first attempt logs nothing new.

**D2. In memory only.** The per-record count and trace live in the drain worker's process. A restart starts a
fresh trace. The trace is not written into the spool envelope, which is a wire format.

**D3. A fallback activity when nothing listens.** Spans come from the `Lukdrasil.StepUpLogging.Audit.Delivery`
source, which the library does not register with OpenTelemetry. When `StartActivity` returns null (no listener,
or not sampled) a plain `Activity` with the same parent is started instead, so the log entries share a TraceId
whether or not the consumer exports the spans. The ambient `Activity.Current` is restored after each entry.

**D4. Batches.** With `DeliveryBatchSize` above 1, every record of a failed batch counts the failed attempt. A
record with no trace yet joins the trace of the batch head, which the Warning names. Each record that had failed
attempts logs its own closing entry.

Rejected: tracking only the batch head, which leaves the other records of the batch without a closing entry.
Rejected: bare activities with no source, which keep the API unchanged but can never be exported as spans.
