# Analysis

Where the logging and audit paths spend their time, ranked by how many events per second one thread gets
through them, from the numbers in [baseline.md](baseline.md) (commit `0705cdd`, ShortRun, one machine).
Nothing here is a claim a benchmark or a test has not measured, except where a line says **Code** (read
from the source) or **Hypothesis** (not measured, and to be measured before anyone builds on it).

How to read the figures: events per second is `1e9 / Mean(ns)`, for one thread. "Library cost" is a row
minus the plain Serilog row of the same shape (238 ns, 424 B, `PipelineBenchmarks.PlainSerilogToNullSink`).
That baseline has no enrichers at all, so the library cost is an upper bound on what any Serilog
application with enrichers would pay extra. The output sink behind every row only counts events: no
exporter is in these numbers.

## Summary

| # | Hot spot | Events/s (1 thread) | Per event | Verdict |
|---|---|---|---|---|
| 1 | Event dropped below the pre-error level (Debug at Warning) | 1.46 M | 685 ns, 1360 B | The common case, and 2.9x the time and 3.2x the bytes of plain Serilog. Worth an optimisation. |
| 1b | The same with redaction on (5 patterns) | 0.76 M | 1309 ns, 1512 B | Redaction adds 624 ns to an event that is then thrown away. |
| 2 | Event held in the pre-error buffer (Information at Warning) | 1.18 M | 844 ns, 1360 B | 160 ns over dropped. The buffer's global lock makes 8 threads slower than one. |
| 3 | Event exported (Warning at Warning) | 1.47 M | 682 ns, 1360 B | No measurable cost over dropped; the exporter is not in this baseline. |
| 4 | Error flush of a full 100-event buffer | 7.5 M events (hold + flush) | 13.3 us per flush, 824 B | Dominated by the holds; the flush alone is not resolved. |
| 5 | Request through the middleware, 4 / 32 headers | 79 k / 33 k requests | 12.7 us, 6.4 KB / 30.5 us, 15 KB | Per header 0.5 to 0.6 us. |
| 5b | The same with `AlwaysLogRequestSummary` | 42 k / 24 k requests | +8.5 to +12 us, +3.46 KB | The summary costs about as much as the request log itself. |
| 6 | Audit write before the disk | 0.18 M to 0.56 M records | 1.8 to 5.7 us, 1.9 to 3.6 KB | Microseconds against an fsync that costs milliseconds (ADR 0020), so not a hot spot. |
| 7 | fsync of an audit record | not measured here | | The Disk suite was not run for this baseline. |

The order is the order of the triage: volume first. Row 7 is the slowest per event by a large margin but
runs once per audit record, while rows 1 to 3 run once per log call.

## 1. Events dropped below the pre-error level

**Measured.** `PipelineBenchmarks.DebugDropped`, default configuration, no activity: 685 ns and 1360 B per
call against 238 ns and 424 B for the plain Serilog logger. The library adds 447 ns and 936 B to a Debug
call that exports nothing and holds nothing. With an `Activity` current: 736 ns, 1432 B. With three
category floors and three deny-list prefixes: 673 ns (no measurable change). With five redaction
patterns and `RedactLogEventProperties`: 1309 ns, 1512 B.

The sink's own decision is a small part of it. `StepUpSinkBenchmarks.EmitBelowSwitch` takes 15.7 ns
without category rules and 46.8 ns with the deny-list and floors, 0 B either way (the unit tests
`StepUpSink_DroppedBelowLevelSwitch_DoesNotAllocate` and `..._DroppedWithDenyListAndFloors_DoesNotAllocate`
pin that). So at least 90 % of the 685 ns happens before or beside `StepUpSink.Emit`.

**Code.** The root logger is `MinimumLevel.Verbose()` (`StepUpLoggingExtensions.cs:267`) so the buffer and
trigger sinks see every event. Serilog therefore builds the event and runs every root enricher
(`ApplyCommonEnrichers`, `StepUpLoggingExtensions.cs:471`: log context, the OpenTelemetry trace and span
ids, `ActivityContextEnricher`, `Application`, `Environment`, `MachineName`, and with patterns the
`PathPropertyRedactionEnricher` and `RedactionEnricher`) and offers it to all five root sinks
(`StepUpLoggingExtensions.cs:336-359`) before `StepUpSink.Emit` drops it. Only the second of the two
facts is measured, by the 2.9x ratio; which enricher or sink holds the 447 ns is **not** measured: no row
here removes them one at a time.

What the enrichers cost individually is measured on a fresh event (`EnricherBenchmarks`, baseline
`NewEvent` 734 ns and 1.77 KB for 4 string properties):

| Enricher | Added time | Added bytes |
|---|---|---|
| `ActivityContextEnricher`, no activity | none measurable | none |
| `ActivityContextEnricher`, in an activity | +146 ns | +0.53 KB (4 properties); none measurable at 16 |
| `AlwaysExportEnricher`, category listed | +190 ns (noisy, see baseline.md) | +0.51 KB (4 properties); none at 16 |
| `RedactionEnricher`, 5 patterns, no match | +2.6 us (6 strings), +7.9 us (18 strings) | +0.13 KB, +0.32 KB |

The +0.5 KB that adding one property costs at 4 properties and not at 16 is the size of one resize of the
event's property dictionary, **Hypothesis**: the dictionary Serilog creates is sized for the properties
the event arrived with, and the first property an enricher adds beyond that capacity pays for a resize.
If it holds, the extra 936 B of the pipeline are mostly resizes and property objects, and the way to
shrink them is fewer properties added per event, not faster enrichers. The `Redaction` row of the
pipeline (+624 ns over `Default`, +152 B) is consistent with the measured redaction cost of 5 patterns on
the one non-excluded string property the benchmark event carries, which `RedactShortNoMatch` puts at
352 ns for 5 patterns. The other 270 ns or so (the enricher's property snapshot,
`RedactionEnricher.cs:44`, its lookups, and `PathPropertyRedactionEnricher`) are not separated.

**Candidates.**

- *Redact only what can leave.* A dropped event is never exported, so redacting it is wasted work, but
  the pre-error buffer holds events already redacted (ADR 0022 D5 puts the enricher before every sink)
  and flushes them to the bypass logger. Redacting at flush time for held events and not at all for
  dropped ones removes the 624 ns from 1309 ns (48 %) for dropped events when redaction is on. It moves
  the point at which fail-closed redaction (ADR 0001) is applied, so it needs an explicit decision.
  ADRs: 0022 (D4, D5, D7), 0023, 0001.
- *Do not enrich events no sink will use.* Raise the root minimum level to the lowest level any consumer
  wants (`StepUpLevel`, and `DiagnosticLevel` in Diagnostic mode), with `MinimumLevel.Override` entries
  for `AlwaysExportCategories` so those still reach `ImmediateSink` at any level (ADR 0024). The ceiling
  of the win is the whole 685 ns and 1360 B for a Debug event below that level; the realistic win is
  **unmeasured** and needs a row for a logger whose root minimum is raised. Today the line at
  `StepUpLoggingExtensions.cs:267` sets `Verbose` after `ReadFrom.Configuration`, so a consumer's own
  `Serilog:MinimumLevel:Default` is overridden (their `Override` entries still apply, ADR 0022 D7); any
  change has to settle that too. ADRs: 0015, 0024, 0005, 0021.
- *Measure first.* The breakdown benchmark in follow-up 1 comes before either.

## 2. Events held in the pre-error buffer

**Measured.** `PipelineBenchmarks.InformationHeld`: 844 ns, 1360 B (same bytes as dropped; the 160 ns
over `DebugDropped` is the hold). `PreErrorBufferSinkBenchmarks.Hold` alone:

| Traces | Threads | Per hold | Holds/s (all threads) | Allocated |
|---|---|---|---|---|
| 1 | 1 | 127 ns | 7.9 M | 0 |
| 1 | 8 | 237 ns | 4.2 M | 0 |
| 4096 | 1 | 468 ns | 2.1 M | 1032 B |
| 4096 | 8 | 772 ns | 1.3 M | 1031 B |

Two findings.

*Eight threads hold slower in aggregate than one.* The same events take 237 ns each with eight threads
against 127 ns with one (`Hold` reports wall time per hold, so aggregate throughput halves), and 772 ns
against 468 ns with many traces. **Code:** `BufferEvent` holds `_lruGate` across the buffer lookup, the LRU
move-to-front and the enqueue (`PreErrorBufferSink.cs:163-173`), and the enqueue takes a second lock
inside the buffer (`:75`). The code comment at `:156-160` and ADR 0011 already name it as the known
ceiling, with approximate LRU as the upgrade path; this is the first measurement of it. It is a worst
case: eight threads that do nothing but hold. A held event costs 844 ns end to end and the hold is 127 ns
of it, so contention shows only when many cores log held events at their maximum rate.

*A trace's first held event allocates about 1 KB.* With more traces than the buffer keeps (every hold is
a new trace, and evicts the oldest), `Hold` allocates 1032 B per call, against 0 B when the trace already
has its buffer. **Code:** `Buffer` pre-sizes its queue to the configured capacity
(`new Queue<LogEvent>(_capacity)`, `PreErrorBufferSink.cs:69`, 100 events by default, so an 800 B array
plus its header), **Hypothesis:** that array is most of the 1032 B. A request that holds three events
still pays for a hundred slots.

The zero in the first rows is the same fact the unit test `Hold_RepeatedInOneW3CTrace_DoesNotAllocate`
pins: `GetContextKey` reads `activity.TraceId.ToString()` (`:214`), which does not allocate per hold
(the test measures 0 bytes over 10 000 holds in one trace; the benchmark rows use the `TraceId` property
path and also show 0 B). It is not a cost to remove.

**Candidates.**

- *Approximate LRU instead of a global lock* (a timestamp on `Buffer` and a periodic sweep, as ADR 0011
  proposes). Ceiling of the win: eight threads at the one-thread rate, 237 to 127 ns for one trace and
  772 to 468 ns with churn; whether it is reachable is **unmeasured**. It must keep the property the
  comment at `:155` protects: a concurrent eviction must not orphan an event between the touch and the
  enqueue. ADRs: 0011 (the recorded ceiling), 0023, 0015.
- *Size a trace's queue on demand* (grow from small). Ceiling: the 824 B array on the first hold of each
  trace; the saving is real only for traces that hold fewer events than the capacity, which needs a row
  with 3, 10 and 100 held events per trace to size. ADR: 0015.

## 3. Events exported

**Measured.** `PipelineBenchmarks.WarningExported`: 682 ns, 1360 B, equal to `DebugDropped` in time and
bytes. `StepUpSinkBenchmarks.EmitExported` is 18.5 ns without category rules and 50.9 ns with, against
15.7 ns and 46.8 ns for a drop: writing to the inner logger and a counting sink adds 3 to 4 ns.
`RequestLoggingBenchmarks` shows the same: stepped up or not, 6.4 KB and 12.7 to 13.6 us (4 headers, no
summary), 6.4 KB and 29.9 to 30.5 us (32 headers).

So the library's own cost of an export is the cost of every event, not of the export. What a real
exporter adds (OTLP serialisation and batching, console or file formatting) is not in this baseline, and
it is where an exported event will cost more than a dropped one. Follow-up 8 adds it.

Stepping up does not make the library slower per event: the pipeline spends the same time on the event
whichever level it ends at. It makes more events reach the exporter, which is a cost the exporter owns.

## 4. Error flush

**Measured.** `FillAndFlushOnError`: 13.3 to 14.3 us and 824 B per cycle in every parameter combination (the
threads and traces parameters do not apply to it, since a cycle is one trace on one thread). A cycle is 100 holds plus
one Error that flushes them, so 7.5 M events per second through hold and flush together. At 127 ns a hold
the 100 holds are about 12.7 us of the 13.3 us; the flush itself is inside the noise of that estimate and
is **not** resolved by this row. The flush writes the events to the bypass logger one by one on the
thread of the failing request (`PreErrorBufferSink.cs:101-106`), so what that costs with a real exporter
behind it is **not** measured.

**Code.** The 824 B per cycle is the size of a 100-reference array, which fits `_queue.ToArray()` at `:95`.
**Hypothesis**, not measured separately.

No candidate yet: the row does not show a flush cost to remove. Follow-up 8 puts a real sink behind it.

## 5. Request logging

**Measured.** `RequestLoggingBenchmarks`, five redaction patterns configured, a request with a path,
three route values, a query string, a user agent and a forwarded-for header:

| Headers | Summary | Per request | Requests/s | Allocated |
|---|---|---|---|---|
| 4 | off | 12.7 us (error wider than mean) | 79 k | 6.4 KB |
| 4 | on | 23.8 us | 42 k | 9.86 KB |
| 32 | off | 30.5 us (error wider than mean) | 33 k | 15.0 KB |
| 32 | on | 42.4 us | 24 k | 18.5 KB |

Between 4 and 32 headers a request gets 17.8 us and 8.6 KB more expensive: 0.64 us and 0.31 KB per header.
The request summary adds 8.5 to 12 us and 3.46 KB, whatever the header count and whether stepped up.

**Code.** Per header, the enricher joins the values through a LINQ `Where` and `string.Join`, runs
`Redact` and stores the result in a dictionary (`StepUpLoggingExtensions.cs:725-745`). The per-header
time matches one `Redact` over five patterns of a short value (352 ns in `RedactionPatternBenchmarks`)
plus the join and the dictionary, **Hypothesis** that redaction is over half of it. The summary builds
its event with up to seven chained `ForContext` calls (`StepUpLoggingController.cs:177-207`) and redacts
the query string, path, route values, user agent and forwarded-for in the middleware
(`StepUpLoggingExtensions.cs:618-634`); the benchmark does not separate those.

**Candidates.**

- *Fewer allocations per header:* skip the LINQ and the join for the common single-value header. Ceiling
  unmeasured; the measured fact is 0.31 KB per header. ADR: 0009 (what must be redacted), 0008 for the
  client-address rule it must keep.
- *Build the summary's properties once,* not through a chain of `ForContext`. Ceiling: part of the 8.5 to
  12 us; **unmeasured** which part. ADRs: 0008, 0009.
- Both depend on the cost of `Redact` itself (section 8).

## 6. Audit write before the disk

**Measured.** `AuditBenchmarks`, five redaction patterns, a sink that stores nothing:

| Step | Request | Data entries | Time | Allocated |
|---|---|---|---|---|
| `AuditLogger.AuditAsync` | none | 0 | 495 ns | 176 B |
| `AuditLogger.AuditAsync` | present | 0 / 32 | 2.11 us | 352 B |
| `SerializeForSpool` (payload + envelope JSON) | none | 0 | 1.30 us | 1728 B |
| `SerializeForSpool` | none | 32 | 3.42 us | 3144 B |
| `SerializeForSpool` | present | 0 / 32 | 1.42 us / 3.57 us | 1968 B / 3384 B |

The CPU before the fsync is 1.8 us (no request, no data) to 5.7 us (request, 32 data entries) per record:
176 k to 556 k records per second per thread. A request context adds 1.6 us: `ReadRequestContext` runs
`ExtractClientAddresses` and redacts the user agent (`AuditLogger.cs:112-127`), three `Redact` calls over
five patterns, which fits the measured redaction cost; 32 data entries add 2.1 us and 1.4 KB to the
serialisation.

ADR 0020 puts the fsync of one record at "single-digit to tens of milliseconds on ordinary SSDs". That
figure is the ADR's, not a measurement of this baseline (the Disk suite was not run). If it holds, the
CPU above is under a tenth of a percent of an audit write, and no change to it can matter until the
fsync is the smaller part.

**Code.** The record is serialised twice: the payload to JSON bytes (`SpoolPayload.cs:28`), which the
encryptor turns into ciphertext, and then the envelope with those bytes as a base64 string
(`SpoolWriter.cs:50`). The envelope format is the contract with the receiving service (ADR 0020,
ADR 0016), so a change to it is a contract change, not an optimisation.

**Candidate.** None worth building before the Disk baseline exists: see follow-up 8.

## 7. fsync

Not measured in this baseline. `DurableWriteBenchmarks` (1 and 8 writers), `FullSpoolDropBenchmarks` and
`SpoolDrainBenchmarks` exist (issue #69) and are marked `Disk`. Run them on the target volume with
`STEPUP_BENCH_SPOOL_ROOT` set (see [README.md](README.md)) and record the result next to this one.

## 8. The cost under everything: `Redact`

Redaction runs per pattern per string, in the pipeline (1b), the request logging (5) and the audit write
(6), so its cost scales every row above. **Measured** (`RedactionPatternBenchmarks`):

| Input | 1 pattern | 5 patterns |
|---|---|---|
| Short value, no match | 68 ns, 0 B | 352 ns, 0 B |
| Header of about 1000 characters, no match | 168 ns, 0 B | 2900 ns, 0 B |
| Path that matches the first pattern | 221 ns, 88 B | 602 ns, 88 B |

The cost is linear in the number of patterns for a short value (about 71 ns per pattern) and worse for
a long one: the four patterns added after the first cost 2.7 us together on a header, about 0.7 us each,
so the cost depends on the pattern as well as on the count (which of the four is the expensive one is
not measured). A value no pattern matches comes back as the same string
(`Redact_ReturnsSameInstance_WhenNoPatternMatches`), and the no-match rows allocate nothing, so the cost
is time, not garbage. ADR 0022 D7 said what a consumer pays "is described rather than measured"; these
are the measured numbers for the sample patterns, and they do not transfer to a consumer's patterns.

**Candidates.** Reduce passes per value: skip values too short to match any pattern, or test several
patterns in one pass. Combining patterns changes what a later pattern sees after an earlier one wrote
`[REDACTED]`, so equivalence has to be shown, not assumed. ADRs: 0001 (fails closed), 0022 D7, 0009.

## Follow-up tasks, ranked

Rank is the measured ceiling times how often the path runs. Every optimisation task starts by adding the
benchmark row it needs, and states its result against [baseline.md](baseline.md).

1. **Break down the dropped-path cost (benchmark only).** Add pipeline rows with the root enrichers and
   root sinks turned off one at a time, so the 447 ns and 936 B of section 1 are attributed. Nothing in
   `src/`. Every task below that touches the pipeline waits for it.
2. **Do not enrich events no sink will use** (section 1). Ceiling 685 ns and 1360 B per event below the
   buffer's level. ADRs 0015, 0024, 0005, 0021 to amend; includes the `Serilog:MinimumLevel:Default`
   question.
3. **Redact only what can leave** (section 1). Ceiling 624 ns per dropped event with redaction on. ADRs
   0022 D4, D5, D7, 0023, 0001 to amend; a decision on when fail-closed redaction applies.
4. **`PreErrorBufferSink` without a global lock** (section 2). Ceiling: eight threads from 237 ns to the
   one-thread 127 ns per hold. ADR 0011's recorded ceiling, 0023, 0015.
5. **Cheaper `Redact`** (section 8). Linear in patterns today; scales rows 1b, 5 and 6. ADRs 0001,
   0022 D7, 0009.
6. **Request logging allocations** (section 5): per-header join, the summary's `ForContext` chain.
   Ceiling: 0.31 KB per header, 3.46 KB per summary. ADRs 0008, 0009.
7. **Size a trace's buffer on demand** (section 2). Ceiling 824 B on the first held event of each trace.
   ADR 0015.
8. **Complete the baseline (benchmark only):** run the Disk suite and record it; add a row with a real
   output sink (file, then OTLP to a local collector) behind the pipeline, the Error flush and a stepped-up
   request; add rows for `StepUpTriggerSink`, `SummarySink`, `ImmediateSink` and
   `StepUpLoggingController.EmitRequestSummary`, which no benchmark covers.

Not recommended on this evidence: any change to the audit CPU path (section 6) or to the spool envelope
(a contract with the receiving service, ADR 0020), and any change to `StepUpSink.Emit` itself, which is
15 to 51 ns and allocation-free by test.
