# Results: the baseline against the final commit

Every follow-up of [analysis.md](analysis.md) measured together: the baseline of [baseline.md](baseline.md)
(commit `0705cdd`) against the end of this series of branches. Follow-ups 2 to 5 and 7 have their own
documents ([results-root-gate.md](results-root-gate.md), [results-redaction.md](results-redaction.md),
[results-prebuffer.md](results-prebuffer.md)); this one adds follow-up 6 (request logging allocations) and
follow-up 8 (the rows the baseline lacked: the side sinks, `EmitRequestSummary`, a real exporter and the Disk
suite), and puts the whole delta in one place.

- Before: `0705cdd`, the baseline. A detached worktree under `/tmp` held `0705cdd` with the final benchmark
  project copied over it and `src/` untouched, so the before rows are the old library measured by the new
  benchmarks. `RedactionPerPatternBenchmarks` does not compile against the old `src/`; it was excluded with
  `<Compile Remove>` and its before cells read `new`.
- Parent: `490a54d`, the branch this one is stacked on (follow-ups 2 to 5 and 7), run the same way with the
  final benchmark project over it. It isolates follow-up 6: the only `src/` difference between parent and final
  is `RequestHeaderRedaction.cs`, `StepUpLoggingController.cs` and `StepUpLoggingExtensions.cs`.
- After: Logging and Audit at `c26c284` and Exporter at `c26c284` (one run) and `b1a9f80` (two runs). `430d958`
  adds a YAML file and `b1a9f80` one Exporter row; `src/` is the same in all three.
- Date: 2026-10-09, all runs on one machine, one after another, never two at once.
- Machine state: laptop on AC power, CPU governor `powersave`, desktop session running, load average 1.3 to
  2.2 before each run; the same state as [baseline.md](baseline.md). The governor was not pinned.

```
BenchmarkDotNet v0.15.8, Linux Omarchy
12th Gen Intel Core i7-12700H 0.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3
```

Commands, from the repository root (the before and parent runs from their worktrees):

```bash
# Logging and Audit: final, baseline, baseline, final (145 and 135 rows)
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Logging Audit --job short --exporters github json

# Exporter: three runs per commit, with the collector of otlp-collector.yaml running
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Exporter --job short --exporters github json

# Disk: the default job, at the final commit
STEPUP_BENCH_SPOOL_ROOT=/home/lukdrasil/.cache/stepup-bench-spool dotnet run -c Release \
  --project tests/Lukdrasil.StepUpLogging.Benchmarks -- --anyCategories Disk --exporters github
```

Wall time of the four Logging and Audit runs: 1135 s (final), 1059 s (baseline), 1049 s (baseline), 1081 s
(final). The Disk run took 378 s, an Exporter run 16 to 22 s.

## How much to trust these

`--job short` is one launch, three warmup and three measured iterations. The order final, baseline, baseline,
final puts a slow drift of the machine on both sides equally, and the two runs of one commit measure how far
two runs of the same code differ:

- Drift is `|run 1 - run 2| / mean` of a row. Across the 144 rows of the final commit with a mean over 5 ns
  the median drift is 2.0 %, the 90th percentile 8.9 %, the largest 33 %, and one row is over 25 %. For the
  baseline commit: 2.2 %, 6.6 %, 55 %, one row over 25 %.
- A change is called **resolved** only when `|delta|` is above the larger of 25 % and the drift of either
  pair. Everything else is **unresolved**, whatever its sign. The drift columns below give each row's noise.
- The allocation column nearly always agrees between runs, but it is not fully deterministic: 7 of the 280
  row pairs (135 baseline, 145 final) differ between the two runs of one commit, by 5 to 32 B: `SerializeForSpool`
  with a request and no data 1952 B and 1968 B (final), `Hold` with 4096 traces and 8 threads 1030 B and 1025 B
  (baseline), `InformationHeld` with redaction in an activity 1520 B and 1488 B (final), and four baseline rows
  32 B apart (`DroppedPathBenchmarks` `ActivityContext`, `Properties` and `ExceptionDetails` in an activity,
  `MelDebugDropped` with floors in an activity). A byte difference of 32 B or more is a fact where a nanosecond
  difference under 25 % is not; a smaller one is not claimed anywhere below.
- A row below 2 ns (`TriggerOnBelowError`) is call overhead of the benchmark, not a cost.

The after of a row is the mean of its two runs, as is its before.

## What moved, and which follow-up moved it

Headline rows; every row of every class is in [All rows](#all-rows).

| Row | Before | After | Delta | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|
| Debug dropped, default configuration | 680 ns | 320 ns | -53 % (resolved) | 1.33 KB | 424 B | 2, 3 (gate, ADR 0026) |
| Debug dropped, redaction on | 1.28 us | 344 ns | -73 % (resolved) | 1.48 KB | 424 B | 3 (`RedactionEnricher` gated) |
| Debug dropped through `Microsoft.Extensions.Logging`, default | 691 ns | 367 ns | -47 % (resolved) | 1.31 KB | 408 B | 2, 3 |
| Warning exported, redaction on | 1.36 us | 953 ns | -30 % (resolved) | 1.48 KB | 1.38 KB | 3, 5 |
| `RedactProperties`, 16 properties | 10.4 us | 5.06 us | -51 % (resolved) | 6.20 KB | 5.93 KB | 5 |
| Hold, 4096 traces, 8 threads | 744 ns | 223 ns | -70 % (resolved) | 1.01 KB | 224 B | 4, 7 |
| Hold, 256 traces, 8 threads | 273 ns | 104 ns | -62 % (resolved) | 0 B | 0 B | 4 |
| First hold of a new trace (4096 traces, 1 thread) | 481 ns | 374 ns | -22 % (unresolved) | 1.01 KB | 224 B | 7 |
| Short value, 5 patterns, no match | 383 ns | 91 ns | -76 % (resolved) | 0 B | 0 B | 5 |
| Request, 32 headers, summary off | 30.5 us | 17.3 us | -43 % (resolved) | 15.03 KB | 9.98 KB | 3, 5, 6 |
| Request, 32 headers, summary on | 40.9 us | 24.4 us | -40 % (resolved) | 18.49 KB | 12.78 KB | 3, 5, 6 |
| Request, 4 headers, summary off | 14.7 us | 11.6 us | -21 % (unresolved) | 6.40 KB | 5.39 KB | 3, 5, 6 |
| Request, 4 headers, summary on | 23.5 us | 17.9 us | -24 % (unresolved) | 9.86 KB | 8.19 KB | 3, 5, 6 |
| `EmitRequestSummary`, every optional field | 1.42 us | 723 ns | -49 % (resolved) | 2.77 KB | 2.00 KB | 6 |
| Audit write, with a request context | 2.10 us | 1.22 us | -42 % (resolved) | 352 B | 352 B | 5 |

The request rows are the sum of three follow-ups and are split next. The two non-stepped 4-header rows moved
21 % and 24 %, inside the rule; their bytes are certain (6.40 KB to 5.39 KB, 9.86 KB to 8.19 KB).

### Follow-up 6 isolated: the parent against the final commit

`490a54d` and the final commit, `RequestLoggingBenchmarks`, `RequestSummaryBenchmarks` and
`RequestPathRedactionBenchmarks`, run in the order parent, final, parent, final:

| Row | Parent | Final | Delta | Alloc parent | Alloc final |
|---|---|---|---|---|---|
| 32 headers, summary off, not stepped up | 20.8 us | 18.0 us | -13 % | 14.85 KB | 9.98 KB |
| 32 headers, summary on, not stepped up | 28.8 us | 24.4 us | -15 % | 18.31 KB | 12.78 KB |
| 32 headers, summary off, stepped up | 18.9 us | 17.2 us | -9 % | 14.85 KB | 9.98 KB |
| 32 headers, summary on, stepped up | 25.5 us | 22.4 us | -12 % | 18.31 KB | 12.78 KB |
| 4 headers, summary off, not stepped up | 12.5 us | 11.4 us | -9 % | 6.23 KB | 5.39 KB |
| 4 headers, summary on, not stepped up | 19.2 us | 18.4 us | -4 % | 9.69 KB | 8.19 KB |
| 4 headers, summary off, stepped up | 11.7 us | 11.1 us | -5 % | 6.23 KB | 5.39 KB |
| 4 headers, summary on, stepped up | 18.6 us | 17.0 us | -9 % | 9.69 KB | 8.19 KB |
| `EmitRequestSummary`, no optional field | 387 ns | 379 ns | -2 % | 856 B | 944 B |
| `EmitRequestSummary`, every optional field | 1.48 us | 725 ns | -51 % | 2.77 KB | 2.00 KB |
| `EnrichRequestPath` | 967 ns | 952 ns | -2 % | 128 B | 128 B |
| `RedactPathAndRouteValues` | 1.28 us | 1.22 us | -5 % | 376 B | 376 B |

What follow-up 6 did, and how much of it is measured:

- **Allocations, resolved.** A request with 32 headers allocates 4.87 KB less (14.85 KB to 9.98 KB, 33 %), with
  4 headers 0.84 KB less (6.23 KB to 5.39 KB). The cost of one more header falls from 0.31 KB to 0.16 KB, by
  the design: a single-valued header is no longer joined through a `Where` iterator and `string.Join`, and the
  dictionary is sized to the header count. The summary on top of a request is 2.80 KB at 4 and at 32 headers,
  against 3.46 KB, and `EmitRequestSummary` with every optional field is 0.77 KB (28 %) lighter.
- **Time, partly resolved.** The request rows are 4 % to 15 % faster, all inside the 25 % rule, so the time of
  a request is **not** a resolved result of follow-up 6 on its own, though in all eight request rows both runs
  of the final are below both runs of the parent. The summary with every optional field halves, -51 % on 3 %
  drift in both pairs, which is the one resolved time result.
- **A regression in bytes.** `EmitRequestSummary` with no optional field allocates 944 B against 856 B, 88 B
  (10 %) more, and the time does not move. The cause is not measured. **Hypothesis**: the event is built with
  a property dictionary that grows once to hold the four template properties and `IsRequestSummary`, which
  the old chain of `ForContext` calls did not pay for a bare summary. The 0.77 KB saved at the other end of
  the same method is nine times larger, and a real request usually carries a query string or a user agent.

The request time is dominated by something other than what follow-up 6 touched. With 4 headers a request is
11 us where plain Serilog is 0.24 us; the rest is the middleware, the diagnostic context, `RedactionEnricher`
and the redaction of the path and route values, none of which this branch changes.

## Regressions and rows that did not move

Stated plainly, because a results page that lists only the wins is not one:

- **`RedactMatch`, 5 patterns: 586 ns to 801 ns, +37 % (resolved).** A value that matches pays for the union
  scan before the loop. [results-redaction.md](results-redaction.md) measured the same effect (575 ns to
  679 ns, "inside the noise") and this run puts it outside the rule. The trade of follow-up 5 is a short value
  that matches none, 383 ns to 91 ns, against a value that matches, 586 ns to 801 ns. Which side a consumer
  is on depends on how many of its values match a pattern. The header-sized no-match
  rows are unchanged (2.80 us and 2.64 us), as follow-up 5 recorded.
- **`EnrichRequestPath`: 698 ns to 956 ns, +37 % (resolved).** Not follow-up 6: the parent measures 967 ns,
  so the change came with an earlier branch. **Hypothesis**: the path matches a pattern and pays the union
  scan as `RedactMatch` above does; not measured per cause. The bytes (128 B) are unchanged.
- **`PreErrorBufferSinkBenchmarks.FillAndFlushOnError`: 13.3 to 14.2 us to 15.4 to 16.9 us, +15 % to +25 %.**
  All six rows move the same way on a drift of 1 % to 3 %, so it is probably real although each row is inside
  the 25 % rule. It is the 15 to 20 ns more per single-thread hold that [results-prebuffer.md](results-prebuffer.md)
  recorded, over the 100 holds of a cycle. Allocations unchanged at 824 B. The stripe is now chosen from the last 4 characters
  of the trace id, and the six rows measure 11.9 to 14.5 us ([results-hold-cost.md](results-hold-cost.md)).
- **`PreErrorBufferSinkBenchmarks.Hold`, one trace, 8 threads: 233.9 ns to 284.5 ns, +22 % (unresolved).**
  Both final runs (289.3 ns, 279.7 ns) are above both baseline runs (229.8 ns, 238.0 ns), on a drift of 3 % in
  each pair, the evidence used for `FillAndFlushOnError` above, so it is probably real although it is inside
  the 25 % rule. It is the one-trace, 8-thread row: all eight threads queue on that one trace's own buffer lock,
  which was out of scope of follow-up 4 ([results-prebuffer.md](results-prebuffer.md) measured 227 ns and 274 ns
  there and called it unchanged). **Hypothesis**: the 15 to 20 ns more per single-thread hold, spent inside that
  lock, is paid by the threads that wait for it; not measured per cause, and not measured at the parent
  `490a54d`. The follow-up 6 diff does not touch `PreErrorBufferSink`. Allocations unchanged at 0 B. The
  one-thread row of the same trace moves +11 % (124 ns to 138 ns) on a 21 % drift and is not claimed. After the
  stripe change the row measures 241.5 ns against 289.6 ns in a paired run, unresolved
  ([results-hold-cost.md](results-hold-cost.md)).
- **`InformationHeld`: +3 % to +16 % (unresolved)** on the six non-redacting scenarios, the same direction
  as the single-thread hold seen from the whole pipeline. **Hypothesis**: one more stripe lookup per held
  event; not measured separately. After the stripe change the eight rows measure 1 % to 10 % faster except one at
  +11 %, all unresolved ([results-hold-cost.md](results-hold-cost.md)).
- **`HoldTraces`, `HeldPerTrace=100` (a trace that fills its ring): 11.7 us to 14.2 us, +21 % (unresolved),
  1.01 KB to 2.05 KB allocated per trace.** A ring that grows from 4 slots to 100 allocates the steps on the
  way, so a trace that fills it costs twice the bytes of the old fixed ring; a trace that holds 3 or 10
  events allocates 224 B or 464 B instead of 1.01 KB. The ring now grows 4, 16, capacity: 1200 B at 100 events
  held (376 B at 10), at the price of 1200 B instead of 744 B at 17 to 32
  ([results-hold-cost.md](results-hold-cost.md)).
- **`StepUpSinkBenchmarks` rows move by +3 % to +44 % (unresolved)**; `EmitBelowSwitch` without rules is 13 ns
  to 19 ns on a drift of 55 % in the baseline pair. Nothing in `StepUpSink.Emit` changed on this branch; the
  rows are 0 B and 13 to 53 ns.
- **Unchanged, as expected:** the `ConsumerRootSink` scenario (the consumer's own root sink keeps the gate
  open, so a dropped event costs what it did: 684 ns to 698 ns), `PlainSerilogToNullSink`,
  `DroppedPathBenchmarks`, the long-header rows of `RedactionPatternBenchmarks`, `SerializeForSpool`.

## New rows

Rows the baseline did not have (follow-up 8), at the final commit. The side sinks and the summary also ran
against `0705cdd`, whose code for them is the same or older, so [All rows](#all-rows) has a before for them;
`RedactionPerPatternBenchmarks` did not compile there.

| Row | Time | Allocated | What it says |
|---|---|---|---|
| `SideSinkBenchmarks.TriggerOnError` | 60.5 ns | 0 B | `StepUpTriggerSink` enqueuing an Error: a channel write, no allocation. |
| `SideSinkBenchmarks.TriggerOnBelowError` | 0.5 ns | 0 B | Below the trigger level: a level compare, call overhead. |
| `SideSinkBenchmarks.ImmediateTagged` / `ImmediateUntagged` | 15.0 ns / 7.6 ns | 0 B | `ImmediateSink`, an event marked immediate or not. |
| `SideSinkBenchmarks.SummaryTagged` / `SummaryUntagged` | 16.1 ns / 8.6 ns | 0 B | `SummarySink`, a request summary or any other event. |
| `RequestSummaryBenchmarks.EmitRequestSummary` | 382 ns, 944 B bare; 723 ns, 2.00 KB with every field | | Section 5's "summary builds its event" row. |
| `RedactionPerPatternBenchmarks` (10 rows) | 64 to 86 ns short; 168 to 173 ns long, **2.19 us for pattern 4** | 0 B | `\b\d{16}\b` is 13 times the other four on a header of about 1000 characters; already in [results-redaction.md](results-redaction.md). |

The side sinks cost 8 to 70 ns and nothing in bytes, so none of them is a candidate for work. The union
prefilter, `StepUpSink` and the side sinks together are small next to the 11 us of a request and the 0.7 us of
an exported event.

## Exporter

`ExporterBenchmarks` (category `Exporter`): the pipeline with a real sink behind it, built outside the timing,
disposed inside it so the asynchronous sink has written or sent every event before the clock stops. The OTLP
rows ran against `otel/opentelemetry-collector-contrib:0.160.0` with [otlp-collector.yaml](otlp-collector.yaml)
in a container, which counted what it accepted; the setup throws when the collector accepted fewer log records
than the pipeline exported, so a row that ran has delivered every event. The file rows write a rolling file
on the same volume as the Disk run.

Per operation, `--job short`, the mean of three runs per commit (two for `EmptyHostDisposed`, which
`b1a9f80` added):

| Row | Sink | Before (`0705cdd`) | After | Alloc before | Alloc after |
|---|---|---|---|---|---|
| `WarningExported` | File | 18.3 us | 18.2 us | 1.82 KB | 1.82 KB |
| `WarningExported` | OTLP | 19.8 us | 15.2 us | 3.32 to 3.37 KB | 3.32 to 3.61 KB |
| `ErrorFlush` | File | 78.1 us | 66.6 us | 4.43 KB | 4.42 KB |
| `ErrorFlush` | OTLP | 59.7 us | 64.1 us | 9.59 to 9.70 KB | 9.72 to 9.90 KB |
| `SteppedUpRequest` | File | 75.0 us | 70.5 us | 10.45 KB | 9.00 KB |
| `SteppedUpRequest` | OTLP | 129 us | 96.8 us | 17.00 KB | 15.5 KB |
| `EmptyHostDisposed` | File | 402 us | 374 us | 1.88 KB | 1.82 to 1.88 KB |
| `EmptyHostDisposed` | OTLP | 537 us | 386 us | 3.32 KB | 3.26 KB |

How to read it:

- **Bytes are the result.** A stepped-up request allocates 1.45 KB (14 %) less with a file and about 1.5 KB
  (9 %) less with OTLP, in every run, which is follow-up 6 seen through a real exporter. The warning and the
  flush allocate what the sink allocates; the OTLP warning varies by 0.3 KB between runs (the exporter's own
  batching).
- **Time is mostly unresolved.** The `Error` column is as wide as the mean in most rows (an OTLP stepped-up
  request: 95 us, error 439 us), and the runs of one commit differ by up to 30 %. The -25 % on the OTLP request
  and -23 % on the OTLP warning are inside that; the file rows move 0 % to 15 %.
- **The exporter is the cost.** An exported warning is 18 us through a file and 15 to 20 us through OTLP,
  against 0.68 us through the pipeline into a counting sink: the sink is 20 to 30 times what the library
  adds, so the rows of the library above are a small share of what an application pays per exported event.
- **`EmptyHostDisposed` is part of every row:** a host with no event, built outside the timing and disposed
  inside it, costs 270 to 475 us (file) and 385 to 590 us (OTLP) per invocation, with errors larger than the
  means. Each row divides it by its events per invocation (1000 warnings, 100 flushes, 200 requests): about
  2 us per request and 4 us per flush, and under 0.6 us per warning.

## Disk

The Disk suite ran once, at the final commit, with the default job (not `--job short`), the spool on
`/home/lukdrasil/.cache/stepup-bench-spool`: btrfs on a device-mapper volume over an NVMe drive, not `/tmp`.
**It was not run at `0705cdd`**: `git diff 0705cdd..HEAD -- src/Lukdrasil.StepUpLogging/EncryptedSpool` is
empty, so the spool code, and with it every number below, is the baseline's code, and a before run would only
measure the machine. The directory was deleted afterwards.

| Benchmark | Writers / batch | Mean | StdDev | Allocated |
|---|---|---|---|---|
| `DurableWriteBenchmarks.WriteRecords`, per record | 1 | 2.16 ms (median 2.45 ms) | 0.58 ms | 7.48 KB |
| `DurableWriteBenchmarks.WriteRecords`, per record | 8 | 336 us (median 302 us) | 76 us | 7.50 KB |
| `FullSpoolDropBenchmarks.MeasureTheSpool` (100 000 files) | | 164 ms | 4.6 ms | 42 188 KB |
| `FullSpoolDropBenchmarks.DroppedWrite` | | 2.90 us | 0.05 us | 2.95 KB |
| `SpoolDrainBenchmarks.DrainSpool`, per record | 1 | 15.36 ms | 0.03 ms | 4.23 KB |
| `SpoolDrainBenchmarks.DrainSpool`, per record | 64 | 259 us | 2 us | 3.71 KB |

A durable write is one fsync: 2.2 ms with one writer, and 0.34 ms per record with eight, because their
fsyncs overlap (the reserve-before-write tally of ADR 0020, #69). A write refused by a full spool takes 2.9 us
and does not touch the disk. Draining with a batch of 64 is 59 times cheaper per record than one by one.

## Audit CPU against the measured fsync

[analysis.md](analysis.md) section 6 could only compare the audit CPU to ADR 0020's "single-digit to tens of
milliseconds". It can now compare it to a measurement. The CPU before the fsync, excluding the consumer's
encryptor, is 1.7 us (no request, no data) to 5.6 us (request, 32 data entries) per record in the baseline
(`AuditToNullSink` plus `SerializeForSpool`) and 1.8 us to 4.8 us at the final commit. Against 2.16 ms per
record with one writer that is 0.08 % to 0.26 % (1.72 us and 5.62 us of 2.16 ms); against 336 us per record
with eight overlapping writers, 0.5 % to 1.7 % (of 336 us), the largest share the measurements give, at most 1.7 %
of a write. Halving the audit CPU would return under 1 % of a write (0.84 % at most). So the audit path stays unchanged, and so does the spool envelope, a contract with the receiving service (ADR 0020). The one
audit row that moved, `AuditToNullSink` with a request context (2.10 us to 1.22 us, -42 %), moved with
follow-up 5's cheaper `Redact` on the user agent, with no change to `AuditLogger`.

What this does not show: the consumer's encryptor, which sits between the serialisation and the spool and is
not in any row, and a disk slower than this NVMe, where the fsync share only grows.

## All rows

Before is the mean of the two baseline runs and After the mean of the two final runs; Drift is
`|run 1 - run 2| / mean` of the before pair and the after pair. Follow-up is the follow-up whose change the row
measures; `new` means the row did not exist for the baseline (`new (8)`: follow-up 8 added it).

### PipelineBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| DebugDropped | Scenario=ConsumerRootSink, InActivity=False | 683.7 ns | 698.0 ns | +2 % (unresolved) | 0 % / 3 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| DebugDropped | Scenario=ConsumerRootSink, InActivity=True | 734.5 ns | 745.4 ns | +1 % (unresolved) | 1 % / 3 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| DebugDropped | Scenario=Default, InActivity=False | 679.6 ns | 319.5 ns | -53 % (resolved) | 3 % / 6 % | 1.33 KB | 424 B | 2, 3, 4, 5 |
| DebugDropped | Scenario=Default, InActivity=True | 739.3 ns | 326.6 ns | -56 % (resolved) | 1 % / 3 % | 1.40 KB | 424 B | 2, 3, 4, 5 |
| DebugDropped | Scenario=FloorsAndNeverStepUp, InActivity=False | 684.4 ns | 317.4 ns | -54 % (resolved) | 0 % / 1 % | 1.33 KB | 424 B | 2, 3, 4, 5 |
| DebugDropped | Scenario=FloorsAndNeverStepUp, InActivity=True | 749.2 ns | 298.6 ns | -60 % (resolved) | 1 % / 19 % | 1.40 KB | 424 B | 2, 3, 4, 5 |
| DebugDropped | Scenario=Redaction, InActivity=False | 1.28 us | 344.2 ns | -73 % (resolved) | 2 % / 1 % | 1.48 KB | 424 B | 2, 3, 4, 5 |
| DebugDropped | Scenario=Redaction, InActivity=True | 1.45 us | 355.3 ns | -76 % (resolved) | 3 % / 7 % | 1.56 KB | 424 B | 2, 3, 4, 5 |
| InformationHeld | Scenario=ConsumerRootSink, InActivity=False | 822.1 ns | 845.3 ns | +3 % (unresolved) | 0 % / 4 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=ConsumerRootSink, InActivity=True | 875.6 ns | 986.1 ns | +13 % (unresolved) | 4 % / 0 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=Default, InActivity=False | 820.1 ns | 874.2 ns | +7 % (unresolved) | 1 % / 4 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=Default, InActivity=True | 871.4 ns | 957.0 ns | +10 % (unresolved) | 2 % / 7 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=FloorsAndNeverStepUp, InActivity=False | 833.1 ns | 878.7 ns | +5 % (unresolved) | 4 % / 4 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=FloorsAndNeverStepUp, InActivity=True | 915.3 ns | 1.06 us | +16 % (unresolved) | 5 % / 8 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=Redaction, InActivity=False | 1.53 us | 1.16 us | -24 % (unresolved) | 2 % / 5 % | 1.48 KB | 1.38 KB | 2, 3, 4, 5 |
| InformationHeld | Scenario=Redaction, InActivity=True | 1.65 us | 1.27 us | -23 % (unresolved) | 2 % / 11 % | 1.56 KB | 1.48 KB | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=ConsumerRootSink, InActivity=False | 710.3 ns | 727.8 ns | +2 % (unresolved) | 0 % / 1 % | 1.31 KB | 1.31 KB | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=ConsumerRootSink, InActivity=True | 794.6 ns | 817.9 ns | +3 % (unresolved) | 2 % / 3 % | 1.38 KB | 1.38 KB | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=Default, InActivity=False | 691.2 ns | 366.9 ns | -47 % (resolved) | 6 % / 9 % | 1.31 KB | 408 B | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=Default, InActivity=True | 751.2 ns | 367.3 ns | -51 % (resolved) | 10 % / 4 % | 1.38 KB | 408 B | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=FloorsAndNeverStepUp, InActivity=False | 725.8 ns | 361.1 ns | -50 % (resolved) | 1 % / 2 % | 1.31 KB | 408 B | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=FloorsAndNeverStepUp, InActivity=True | 819.4 ns | 378.0 ns | -54 % (resolved) | 0 % / 4 % | 1.38 KB | 408 B | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=Redaction, InActivity=False | 1.30 us | 390.6 ns | -70 % (resolved) | 2 % / 0 % | 1.46 KB | 408 B | 2, 3, 4, 5 |
| MelDebugDropped | Scenario=Redaction, InActivity=True | 1.44 us | 398.7 ns | -72 % (resolved) | 3 % / 1 % | 1.55 KB | 408 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=ConsumerRootSink, InActivity=False | 228.2 ns | 239.5 ns | +5 % (unresolved) | 5 % / 1 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=ConsumerRootSink, InActivity=True | 250.4 ns | 249.1 ns | -1 % (unresolved) | 3 % / 0 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=Default, InActivity=False | 241.5 ns | 225.4 ns | -7 % (unresolved) | 7 % / 14 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=Default, InActivity=True | 252.7 ns | 221.9 ns | -12 % (unresolved) | 7 % / 21 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=FloorsAndNeverStepUp, InActivity=False | 242.8 ns | 234.7 ns | -3 % (unresolved) | 12 % / 10 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=FloorsAndNeverStepUp, InActivity=True | 245.3 ns | 247.5 ns | +1 % (unresolved) | 1 % / 1 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=Redaction, InActivity=False | 232.8 ns | 231.8 ns | -0 % (unresolved) | 0 % / 1 % | 424 B | 424 B | 2, 3, 4, 5 |
| PlainSerilogToNullSink | Scenario=Redaction, InActivity=True | 246.7 ns | 247.5 ns | +0 % (unresolved) | 2 % / 4 % | 424 B | 424 B | 2, 3, 4, 5 |
| WarningExported | Scenario=ConsumerRootSink, InActivity=False | 684.1 ns | 716.2 ns | +5 % (unresolved) | 5 % / 2 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=ConsumerRootSink, InActivity=True | 763.2 ns | 781.3 ns | +2 % (unresolved) | 1 % / 3 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=Default, InActivity=False | 669.2 ns | 678.6 ns | +1 % (unresolved) | 1 % / 1 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=Default, InActivity=True | 761.9 ns | 738.1 ns | -3 % (unresolved) | 2 % / 0 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=FloorsAndNeverStepUp, InActivity=False | 713.1 ns | 708.2 ns | -1 % (unresolved) | 3 % / 1 % | 1.33 KB | 1.33 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=FloorsAndNeverStepUp, InActivity=True | 797.4 ns | 799.1 ns | +0 % (unresolved) | 7 % / 1 % | 1.40 KB | 1.40 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=Redaction, InActivity=False | 1.36 us | 952.6 ns | -30 % (resolved) | 1 % / 0 % | 1.48 KB | 1.38 KB | 2, 3, 4, 5 |
| WarningExported | Scenario=Redaction, InActivity=True | 1.52 us | 1.09 us | -28 % (resolved) | 6 % / 4 % | 1.56 KB | 1.45 KB | 2, 3, 4, 5 |

### DroppedPathBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| ActivityContext | InActivity=False | 407.3 ns | 409.7 ns | +1 % (unresolved) | 0 % / 2 % | 784 B | 784 B | 2, 3 |
| ActivityContext | InActivity=True | 470.7 ns | 473.2 ns | +1 % (unresolved) | 6 % / 2 % | 856 B | 856 B | 2, 3 |
| ExceptionDetails | InActivity=False | 602.7 ns | 614.3 ns | +2 % (unresolved) | 2 % / 2 % | 1.33 KB | 1.33 KB | 2, 3 |
| ExceptionDetails | InActivity=True | 681.4 ns | 672.0 ns | -1 % (unresolved) | 0 % / 1 % | 1.40 KB | 1.40 KB | 2, 3 |
| Immediate | InActivity=False | 657.9 ns | 629.9 ns | -4 % (unresolved) | 2 % / 0 % | 1.33 KB | 1.33 KB | 2, 3 |
| Immediate | InActivity=True | 699.1 ns | 718.2 ns | +3 % (unresolved) | 3 % / 1 % | 1.40 KB | 1.40 KB | 2, 3 |
| LogContext | InActivity=False | 246.9 ns | 249.0 ns | +1 % (unresolved) | 5 % / 0 % | 424 B | 424 B | 2, 3 |
| LogContext | InActivity=True | 252.5 ns | 255.2 ns | +1 % (unresolved) | 2 % / 1 % | 424 B | 424 B | 2, 3 |
| Plain | InActivity=False | 240.0 ns | 238.4 ns | -1 % (unresolved) | 0 % / 1 % | 424 B | 424 B | 2, 3 |
| Plain | InActivity=True | 246.5 ns | 246.1 ns | -0 % (unresolved) | 7 % / 2 % | 424 B | 424 B | 2, 3 |
| PreErrorBuffer | InActivity=False | 629.9 ns | 638.8 ns | +1 % (unresolved) | 2 % / 4 % | 1.33 KB | 1.33 KB | 2, 3 |
| PreErrorBuffer | InActivity=True | 706.7 ns | 688.8 ns | -3 % (unresolved) | 1 % / 6 % | 1.40 KB | 1.40 KB | 2, 3 |
| Properties | InActivity=False | 604.2 ns | 588.4 ns | -3 % (unresolved) | 2 % / 3 % | 1.33 KB | 1.33 KB | 2, 3 |
| Properties | InActivity=True | 677.6 ns | 677.3 ns | -0 % (unresolved) | 4 % / 4 % | 1.40 KB | 1.40 KB | 2, 3 |
| StepUpSink | InActivity=False | 623.5 ns | 610.9 ns | -2 % (unresolved) | 1 % / 2 % | 1.33 KB | 1.33 KB | 2, 3 |
| StepUpSink | InActivity=True | 696.3 ns | 701.5 ns | +1 % (unresolved) | 1 % / 0 % | 1.40 KB | 1.40 KB | 2, 3 |
| Summary | InActivity=False | 648.5 ns | 647.9 ns | -0 % (unresolved) | 2 % / 2 % | 1.33 KB | 1.33 KB | 2, 3 |
| Summary | InActivity=True | 710.0 ns | 694.3 ns | -2 % (unresolved) | 1 % / 1 % | 1.40 KB | 1.40 KB | 2, 3 |
| TraceIds | InActivity=False | 403.6 ns | 399.7 ns | -1 % (unresolved) | 1 % / 1 % | 784 B | 784 B | 2, 3 |
| TraceIds | InActivity=True | 436.2 ns | 441.2 ns | +1 % (unresolved) | 2 % / 2 % | 832 B | 832 B | 2, 3 |
| Trigger | InActivity=False | 625.7 ns | 608.1 ns | -3 % (unresolved) | 0 % / 3 % | 1.33 KB | 1.33 KB | 2, 3 |
| Trigger | InActivity=True | 696.5 ns | 697.4 ns | +0 % (unresolved) | 2 % / 1 % | 1.40 KB | 1.40 KB | 2, 3 |

### EnricherBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| ActivityContext | InActivity=False, StringProperties=16 | 2.27 us | 2.37 us | +4 % (unresolved) | 2 % / 7 % | 5.88 KB | 5.88 KB | 2, 3 |
| ActivityContext | InActivity=False, StringProperties=4 | 749.3 ns | 761.1 ns | +2 % (unresolved) | 4 % / 4 % | 1.77 KB | 1.77 KB | 2, 3 |
| ActivityContext | InActivity=True, StringProperties=16 | 2.37 us | 2.42 us | +2 % (unresolved) | 3 % / 2 % | 5.90 KB | 5.90 KB | 2, 3 |
| ActivityContext | InActivity=True, StringProperties=4 | 851.6 ns | 826.8 ns | -3 % (unresolved) | 1 % / 5 % | 2.30 KB | 2.30 KB | 2, 3 |
| AlwaysExport | InActivity=False, StringProperties=16 | 2.37 us | 2.27 us | -4 % (unresolved) | 3 % / 5 % | 5.88 KB | 5.88 KB | 2, 3 |
| AlwaysExport | InActivity=False, StringProperties=4 | 877.8 ns | 854.1 ns | -3 % (unresolved) | 1 % / 5 % | 2.28 KB | 2.28 KB | 2, 3 |
| AlwaysExport | InActivity=True, StringProperties=16 | 2.31 us | 2.38 us | +3 % (unresolved) | 3 % / 3 % | 5.88 KB | 5.88 KB | 2, 3 |
| AlwaysExport | InActivity=True, StringProperties=4 | 891.9 ns | 860.4 ns | -4 % (unresolved) | 1 % / 1 % | 2.28 KB | 2.28 KB | 2, 3 |
| NewEvent | InActivity=False, StringProperties=16 | 2.37 us | 2.34 us | -1 % (unresolved) | 2 % / 1 % | 5.88 KB | 5.88 KB | 2, 3 |
| NewEvent | InActivity=False, StringProperties=4 | 753.4 ns | 745.8 ns | -1 % (unresolved) | 5 % / 1 % | 1.77 KB | 1.77 KB | 2, 3 |
| NewEvent | InActivity=True, StringProperties=16 | 2.35 us | 2.36 us | +1 % (unresolved) | 3 % / 7 % | 5.88 KB | 5.88 KB | 2, 3 |
| NewEvent | InActivity=True, StringProperties=4 | 770.8 ns | 735.7 ns | -5 % (unresolved) | 2 % / 9 % | 1.77 KB | 1.77 KB | 2, 3 |
| RedactProperties | InActivity=False, StringProperties=16 | 10.38 us | 5.06 us | -51 % (resolved) | 1 % / 0 % | 6.20 KB | 5.93 KB | 2, 3 |
| RedactProperties | InActivity=False, StringProperties=4 | 3.22 us | 1.52 us | -53 % (resolved) | 3 % / 5 % | 1.90 KB | 1.82 KB | 2, 3 |
| RedactProperties | InActivity=True, StringProperties=16 | 10.22 us | 4.90 us | -52 % (resolved) | 1 % / 10 % | 6.20 KB | 5.93 KB | 2, 3 |
| RedactProperties | InActivity=True, StringProperties=4 | 3.18 us | 1.58 us | -50 % (resolved) | 1 % / 0 % | 1.90 KB | 1.82 KB | 2, 3 |

### StepUpSinkBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| EmitBelowSwitch | WithCategoryRules=False | 13.2 ns | 19.0 ns | +44 % (unresolved) | 55 % / 6 % | 0 B | 0 B | - |
| EmitBelowSwitch | WithCategoryRules=True | 47.4 ns | 48.6 ns | +3 % (unresolved) | 1 % / 4 % | 0 B | 0 B | - |
| EmitExported | WithCategoryRules=False | 21.5 ns | 23.7 ns | +10 % (unresolved) | 0 % / 1 % | 0 B | 0 B | - |
| EmitExported | WithCategoryRules=True | 46.8 ns | 53.2 ns | +14 % (unresolved) | 13 % / 1 % | 0 B | 0 B | - |

### SideSinkBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| ImmediateTagged | - | 15.3 ns | 15.0 ns | -2 % (unresolved) | 3 % / 1 % | 0 B | 0 B | new (8) |
| ImmediateUntagged | - | 8.2 ns | 7.6 ns | -8 % (unresolved) | 14 % / 33 % | 0 B | 0 B | new (8) |
| SummaryTagged | - | 16.3 ns | 16.1 ns | -1 % (unresolved) | 1 % / 2 % | 0 B | 0 B | new (8) |
| SummaryUntagged | - | 8.7 ns | 8.6 ns | -2 % (unresolved) | 0 % / 2 % | 0 B | 0 B | new (8) |
| TriggerOnBelowError | - | 0.4 ns | 0.5 ns | +49 % (below 2 ns, call overhead) | 3 % / 19 % | 0 B | 0 B | new (8) |
| TriggerOnError | - | 69.8 ns | 60.5 ns | -13 % (unresolved) | 0 % / 0 % | 0 B | 0 B | new (8) |

### PreErrorBufferSinkBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| FillAndFlushOnError | Contexts=1, Threads=1 | 13.33 us | 15.38 us | +15 % (unresolved) | 2 % / 1 % | 824 B | 824 B | 4, 7 |
| FillAndFlushOnError | Contexts=1, Threads=8 | 13.21 us | 16.45 us | +25 % (unresolved) | 2 % / 3 % | 824 B | 824 B | 4, 7 |
| FillAndFlushOnError | Contexts=256, Threads=1 | 13.91 us | 16.75 us | +20 % (unresolved) | 2 % / 1 % | 824 B | 824 B | 4, 7 |
| FillAndFlushOnError | Contexts=256, Threads=8 | 14.07 us | 16.92 us | +20 % (unresolved) | 2 % / 3 % | 824 B | 824 B | 4, 7 |
| FillAndFlushOnError | Contexts=4096, Threads=1 | 14.17 us | 16.72 us | +18 % (unresolved) | 1 % / 1 % | 824 B | 824 B | 4, 7 |
| FillAndFlushOnError | Contexts=4096, Threads=8 | 13.95 us | 16.87 us | +21 % (unresolved) | 1 % / 3 % | 824 B | 824 B | 4, 7 |
| Hold | Contexts=1, Threads=1 | 123.9 ns | 137.7 ns | +11 % (unresolved) | 5 % / 21 % | 0 B | 0 B | 4, 7 |
| Hold | Contexts=1, Threads=8 | 233.9 ns | 284.5 ns | +22 % (unresolved) | 3 % / 3 % | 0 B | 0 B | 4, 7 |
| Hold | Contexts=256, Threads=1 | 172.1 ns | 177.3 ns | +3 % (unresolved) | 15 % / 2 % | 0 B | 0 B | 4, 7 |
| Hold | Contexts=256, Threads=8 | 272.8 ns | 104.0 ns | -62 % (resolved) | 1 % / 6 % | 0 B | 0 B | 4, 7 |
| Hold | Contexts=4096, Threads=1 | 480.8 ns | 373.5 ns | -22 % (unresolved) | 5 % / 2 % | 1.01 KB | 224 B | 4, 7 |
| Hold | Contexts=4096, Threads=8 | 744.2 ns | 222.9 ns | -70 % (resolved) | 13 % / 1 % | 1.01 KB | 224 B | 4, 7 |

### PreErrorBufferGrowthBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| HoldTraces | HeldPerTrace=10 | 1.51 us | 1.72 us | +14 % (unresolved) | 3 % / 1 % | 1.01 KB | 464 B | 7 |
| HoldTraces | HeldPerTrace=100 | 11.71 us | 14.23 us | +21 % (unresolved) | 7 % / 2 % | 1.01 KB | 2.05 KB | 7 |
| HoldTraces | HeldPerTrace=3 | 710.5 ns | 640.8 ns | -10 % (unresolved) | 0 % / 1 % | 1.01 KB | 224 B | 7 |

### RedactionPatternBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| LoopLongHeaderNoMatch | PatternCount=1 | 168.2 ns | 167.1 ns | -1 % (unresolved) | 5 % / 0 % | 0 B | 0 B | 5 |
| LoopLongHeaderNoMatch | PatternCount=5 | 2.85 us | 2.90 us | +2 % (unresolved) | 1 % / 1 % | 0 B | 0 B | 5 |
| LoopMatch | PatternCount=1 | 218.4 ns | 219.4 ns | +0 % (unresolved) | 2 % / 3 % | 88 B | 88 B | 5 |
| LoopMatch | PatternCount=5 | 592.9 ns | 596.1 ns | +1 % (unresolved) | 2 % / 1 % | 88 B | 88 B | 5 |
| LoopShortNoMatch | PatternCount=1 | 67.0 ns | 64.3 ns | -4 % (unresolved) | 4 % / 4 % | 0 B | 0 B | 5 |
| LoopShortNoMatch | PatternCount=5 | 350.3 ns | 355.7 ns | +2 % (unresolved) | 2 % / 1 % | 0 B | 0 B | 5 |
| RedactLongHeaderNoMatch | PatternCount=1 | 168.0 ns | 170.2 ns | +1 % (unresolved) | 5 % / 5 % | 0 B | 0 B | 5 |
| RedactLongHeaderNoMatch | PatternCount=5 | 2.80 us | 2.64 us | -5 % (unresolved) | 0 % / 0 % | 0 B | 0 B | 5 |
| RedactMatch | PatternCount=1 | 211.8 ns | 213.1 ns | +1 % (unresolved) | 3 % / 10 % | 88 B | 88 B | 5 |
| RedactMatch | PatternCount=5 | 586.2 ns | 800.9 ns | +37 % (resolved) | 8 % / 0 % | 88 B | 88 B | 5 |
| RedactShortNoMatch | PatternCount=1 | 64.7 ns | 64.1 ns | -1 % (unresolved) | 11 % / 14 % | 0 B | 0 B | 5 |
| RedactShortNoMatch | PatternCount=5 | 383.2 ns | 90.6 ns | -76 % (resolved) | 4 % / 0 % | 0 B | 0 B | 5 |

### RedactionPerPatternBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| LongHeaderNoMatch | PatternIndex=0 | new | 168.2 ns | new | - / 2 % | new | 0 B | new |
| LongHeaderNoMatch | PatternIndex=1 | new | 168.0 ns | new | - / 2 % | new | 0 B | new |
| LongHeaderNoMatch | PatternIndex=2 | new | 173.4 ns | new | - / 6 % | new | 0 B | new |
| LongHeaderNoMatch | PatternIndex=3 | new | 169.6 ns | new | - / 3 % | new | 0 B | new |
| LongHeaderNoMatch | PatternIndex=4 | new | 2.19 us | new | - / 0 % | new | 0 B | new |
| ShortNoMatch | PatternIndex=0 | new | 66.3 ns | new | - / 2 % | new | 0 B | new |
| ShortNoMatch | PatternIndex=1 | new | 69.7 ns | new | - / 8 % | new | 0 B | new |
| ShortNoMatch | PatternIndex=2 | new | 86.0 ns | new | - / 1 % | new | 0 B | new |
| ShortNoMatch | PatternIndex=3 | new | 65.8 ns | new | - / 1 % | new | 0 B | new |
| ShortNoMatch | PatternIndex=4 | new | 63.6 ns | new | - / 2 % | new | 0 B | new |

### RequestLoggingBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| HandleRequest | Stepped=False, HeaderCount=32, AlwaysLogRequestSummary=False | 30.49 us | 17.34 us | -43 % (resolved) | 4 % / 9 % | 15.03 KB | 9.98 KB | 3, 5, 6 |
| HandleRequest | Stepped=False, HeaderCount=32, AlwaysLogRequestSummary=True | 40.85 us | 24.36 us | -40 % (resolved) | 2 % / 0 % | 18.49 KB | 12.78 KB | 3, 5, 6 |
| HandleRequest | Stepped=False, HeaderCount=4, AlwaysLogRequestSummary=False | 14.69 us | 11.57 us | -21 % (unresolved) | 2 % / 1 % | 6.40 KB | 5.39 KB | 3, 5, 6 |
| HandleRequest | Stepped=False, HeaderCount=4, AlwaysLogRequestSummary=True | 23.54 us | 17.85 us | -24 % (unresolved) | 0 % / 1 % | 9.86 KB | 8.19 KB | 3, 5, 6 |
| HandleRequest | Stepped=True, HeaderCount=32, AlwaysLogRequestSummary=False | 31.96 us | 17.35 us | -46 % (resolved) | 3 % / 1 % | 15.03 KB | 9.98 KB | 3, 5, 6 |
| HandleRequest | Stepped=True, HeaderCount=32, AlwaysLogRequestSummary=True | 39.22 us | 22.65 us | -42 % (resolved) | 4 % / 6 % | 18.49 KB | 12.78 KB | 3, 5, 6 |
| HandleRequest | Stepped=True, HeaderCount=4, AlwaysLogRequestSummary=False | 14.48 us | 10.29 us | -29 % (resolved) | 0 % / 13 % | 6.40 KB | 5.39 KB | 3, 5, 6 |
| HandleRequest | Stepped=True, HeaderCount=4, AlwaysLogRequestSummary=True | 22.82 us | 17.00 us | -26 % (resolved) | 2 % / 2 % | 9.86 KB | 8.19 KB | 3, 5, 6 |

### RequestSummaryBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| EmitRequestSummary | AllFields=False | 352.2 ns | 381.6 ns | +8 % (unresolved) | 10 % / 1 % | 856 B | 944 B | 6 (new row, 8) |
| EmitRequestSummary | AllFields=True | 1.42 us | 722.7 ns | -49 % (resolved) | 1 % / 0 % | 2.77 KB | 2.00 KB | 6 (new row, 8) |

### RequestPathRedactionBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| EnrichRequestPath | - | 697.5 ns | 955.9 ns | +37 % (resolved) | 4 % / 0 % | 128 B | 128 B | 5 |
| RedactPathAndRouteValues | - | 1.48 us | 1.21 us | -19 % (unresolved) | 6 % / 3 % | 376 B | 376 B | 5 |

### AuditBenchmarks

| Row | Params | Before | After | Delta | Drift before / after | Alloc before | Alloc after | Follow-up |
|---|---|---|---|---|---|---|---|---|
| AuditToNullSink | WithHttpContext=False, DataEntries=0 | 495.6 ns | 490.0 ns | -1 % (unresolved) | 0 % / 1 % | 176 B | 176 B | 5 (Redact only) |
| AuditToNullSink | WithHttpContext=False, DataEntries=32 | 501.4 ns | 472.5 ns | -6 % (unresolved) | 1 % / 11 % | 176 B | 176 B | 5 (Redact only) |
| AuditToNullSink | WithHttpContext=True, DataEntries=0 | 2.10 us | 1.22 us | -42 % (resolved) | 1 % / 2 % | 352 B | 352 B | 5 (Redact only) |
| AuditToNullSink | WithHttpContext=True, DataEntries=32 | 2.12 us | 1.23 us | -42 % (resolved) | 1 % / 1 % | 352 B | 352 B | 5 (Redact only) |
| SerializeForSpool | WithHttpContext=False, DataEntries=0 | 1.22 us | 1.30 us | +6 % (unresolved) | 6 % / 0 % | 1.69 KB | 1.69 KB | 5 (Redact only) |
| SerializeForSpool | WithHttpContext=False, DataEntries=32 | 3.43 us | 3.46 us | +1 % (unresolved) | 3 % / 0 % | 3.07 KB | 3.07 KB | 5 (Redact only) |
| SerializeForSpool | WithHttpContext=True, DataEntries=0 | 1.41 us | 1.36 us | -3 % (unresolved) | 2 % / 5 % | 1.92 KB | 1.91 KB | 5 (Redact only) |
| SerializeForSpool | WithHttpContext=True, DataEntries=32 | 3.50 us | 3.60 us | +3 % (unresolved) | 5 % / 2 % | 3.30 KB | 3.30 KB | 5 (Redact only) |

## Raw tables

The `github` exports of BenchmarkDotNet for every run cited above, one block per run and class, so the numbers
above do not depend on files that are gone. Only the tables are kept; the host header of every run is the one
in the code block at the top of this page. Two changes were made to the
exports: the micro sign of `us` is written `u`, and the bold BenchmarkDotNet puts on the first row of a group is
dropped. The job line of each table says which job it ran with.

Logging and Audit runs, in the order they ran (final, baseline, baseline, final); [All rows](#all-rows) averages the
two of each commit.

### Logging and Audit, run 1: final (`c26c284`)

`AuditBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | WithHttpContext | DataEntries | Mean       | Error       | StdDev   | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|------------:|---------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   488.6 ns |    12.82 ns |  0.70 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 0           | 1,298.1 ns | 1,248.18 ns | 68.42 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   446.4 ns | 1,376.85 ns | 75.47 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 32          | 3,467.8 ns |   181.26 ns |  9.94 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 1,236.5 ns |   435.19 ns | 23.85 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,331.6 ns |   711.15 ns | 38.98 ns | 0.1545 |    1952 B |
| AuditToNullSink   | True            | 32          | 1,238.7 ns |   181.97 ns |  9.97 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 3,633.9 ns |   656.75 ns | 36.00 ns | 0.2670 |    3384 B |

`DroppedPathBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 239.0 ns |  34.90 ns |  1.91 ns |  1.00 |    0.01 | 0.0336 |     424 B |        1.00 |
| LogContext       | False      | 249.2 ns | 219.11 ns | 12.01 ns |  1.04 |    0.04 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 397.2 ns |   7.18 ns |  0.39 ns |  1.66 |    0.01 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 413.8 ns |  38.21 ns |  2.09 ns |  1.73 |    0.01 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 597.5 ns | 255.67 ns | 14.01 ns |  2.50 |    0.05 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 619.2 ns |  38.12 ns |  2.09 ns |  2.59 |    0.02 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 604.0 ns |  46.09 ns |  2.53 ns |  2.53 |    0.02 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 625.0 ns | 245.30 ns | 13.45 ns |  2.62 |    0.05 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 617.1 ns | 101.67 ns |  5.57 ns |  2.58 |    0.03 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 641.0 ns | 203.49 ns | 11.15 ns |  2.68 |    0.04 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 629.8 ns | 104.24 ns |  5.71 ns |  2.64 |    0.03 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 248.1 ns |  37.74 ns |  2.07 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 256.9 ns | 121.60 ns |  6.67 ns |  1.04 |    0.02 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 437.5 ns | 107.62 ns |  5.90 ns |  1.76 |    0.02 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 468.6 ns | 100.32 ns |  5.50 ns |  1.89 |    0.02 | 0.0682 |     856 B |        2.02 |
| Properties       | True       | 665.4 ns | 218.27 ns | 11.96 ns |  2.68 |    0.05 | 0.1135 |    1432 B |        3.38 |
| ExceptionDetails | True       | 676.3 ns | 238.75 ns | 13.09 ns |  2.73 |    0.05 | 0.1135 |    1432 B |        3.38 |
| StepUpSink       | True       | 702.5 ns | 223.29 ns | 12.24 ns |  2.83 |    0.05 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 668.3 ns | 290.90 ns | 15.95 ns |  2.69 |    0.06 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 702.1 ns | 180.69 ns |  9.90 ns |  2.83 |    0.04 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 691.9 ns | 171.07 ns |  9.38 ns |  2.79 |    0.04 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 716.1 ns | 330.11 ns | 18.09 ns |  2.89 |    0.07 | 0.1135 |    1432 B |        3.38 |

`EnricherBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | StringProperties | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |   742.2 ns |   225.12 ns |  12.34 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |   774.5 ns |    84.08 ns |   4.61 ns |  1.04 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |   874.7 ns |    76.64 ns |   4.20 ns |  1.18 |    0.02 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                | 1,555.3 ns |   429.10 ns |  23.52 ns |  2.10 |    0.04 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               | 2,348.4 ns | 1,133.74 ns |  62.14 ns |  1.00 |    0.03 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               | 2,281.3 ns |   224.80 ns |  12.32 ns |  0.97 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               | 2,328.0 ns | 1,989.57 ns | 109.05 ns |  0.99 |    0.05 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 5,072.5 ns |   984.70 ns |  53.97 ns |  2.16 |    0.05 | 0.4807 |      - |   5.93 KB |        1.01 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |   768.8 ns |   123.89 ns |   6.79 ns |  1.00 |    0.01 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |   846.3 ns |    63.68 ns |   3.49 ns |  1.10 |    0.01 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |   856.0 ns |   386.18 ns |  21.17 ns |  1.11 |    0.03 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                | 1,573.0 ns |   313.40 ns |  17.18 ns |  2.05 |    0.02 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               | 2,446.5 ns | 1,536.51 ns |  84.22 ns |  1.00 |    0.04 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               | 2,391.6 ns |   457.46 ns |  25.07 ns |  0.98 |    0.03 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               | 2,345.4 ns | 1,660.69 ns |  91.03 ns |  0.96 |    0.04 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 5,153.8 ns | 1,974.24 ns | 108.21 ns |  2.11 |    0.07 | 0.4807 |      - |   5.93 KB |        1.01 |

`PipelineBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   241.0 ns |    40.63 ns |   2.23 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   310.7 ns |    24.32 ns |   1.33 ns |  1.29 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   383.9 ns |    65.11 ns |   3.57 ns |  1.59 |    0.02 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   857.7 ns |   208.47 ns |  11.43 ns |  3.56 |    0.05 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   675.1 ns |    42.84 ns |   2.35 ns |  2.80 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   198.7 ns |   743.38 ns |  40.75 ns |  1.03 |    0.25 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   332.0 ns |   299.19 ns |  16.40 ns |  1.71 |    0.28 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   374.7 ns |    63.16 ns |   3.46 ns |  1.93 |    0.31 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   922.5 ns |    40.35 ns |   2.21 ns |  4.76 |    0.76 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   739.7 ns |   575.20 ns |  31.53 ns |  3.82 |    0.62 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   232.7 ns |   602.25 ns |  33.01 ns |  1.01 |    0.18 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   342.6 ns |    29.24 ns |   1.60 ns |  1.49 |    0.20 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   390.3 ns |    12.75 ns |   0.70 ns |  1.70 |    0.23 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,127.8 ns | 1,861.19 ns | 102.02 ns |  4.92 |    0.76 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   951.4 ns |   396.07 ns |  21.71 ns |  4.15 |    0.56 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   252.5 ns |    99.72 ns |   5.47 ns |  1.00 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   367.3 ns |   160.78 ns |   8.81 ns |  1.46 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   397.2 ns |    17.07 ns |   0.94 ns |  1.57 |    0.03 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,340.6 ns | 4,642.35 ns | 254.46 ns |  5.31 |    0.88 | 0.1202 | 0.0286 |    1520 B |        3.58 |
| WarningExported        | Redaction            | True       | 1,067.8 ns |   207.46 ns |  11.37 ns |  4.23 |    0.09 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   222.9 ns |   318.86 ns |  17.48 ns |  1.00 |    0.09 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   318.4 ns |    61.05 ns |   3.35 ns |  1.43 |    0.09 | 0.0336 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   358.0 ns |    15.83 ns |   0.87 ns |  1.61 |    0.10 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   896.6 ns |   659.64 ns |  36.16 ns |  4.04 |    0.30 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   705.6 ns |   110.52 ns |   6.06 ns |  3.18 |    0.21 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   246.3 ns |   213.31 ns |  11.69 ns |  1.00 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   327.1 ns |    28.61 ns |   1.57 ns |  1.33 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   371.3 ns |   190.27 ns |  10.43 ns |  1.51 |    0.07 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       | 1,104.4 ns |   557.22 ns |  30.54 ns |  4.49 |    0.22 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   804.1 ns |   441.28 ns |  24.19 ns |  3.27 |    0.16 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   238.3 ns |    58.50 ns |   3.21 ns |  1.00 |    0.02 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   688.6 ns |   623.39 ns |  34.17 ns |  2.89 |    0.13 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   723.7 ns |     7.76 ns |   0.43 ns |  3.04 |    0.04 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   829.8 ns |   321.96 ns |  17.65 ns |  3.48 |    0.08 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   708.4 ns |   101.65 ns |   5.57 ns |  2.97 |    0.04 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   249.5 ns |   225.94 ns |  12.38 ns |  1.00 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   732.4 ns |    45.78 ns |   2.51 ns |  2.94 |    0.12 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   829.1 ns |   191.22 ns |  10.48 ns |  3.33 |    0.14 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   984.8 ns |   649.36 ns |  35.59 ns |  3.95 |    0.21 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   791.4 ns |   310.91 ns |  17.04 ns |  3.18 |    0.15 | 0.1135 |      - |    1432 B |        3.38 |

`PreErrorBufferGrowthBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method     | HeldPerTrace | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|------------:|----------:|-------:|-------:|----------:|
| HoldTraces | 3            |    638.2 ns |    41.42 ns |   2.27 ns | 0.0172 | 0.0057 |     224 B |
| HoldTraces | 10           |  1,704.8 ns | 1,652.18 ns |  90.56 ns | 0.0362 | 0.0172 |     464 B |
| HoldTraces | 100          | 14,352.6 ns | 2,367.47 ns | 129.77 ns | 0.1628 | 0.1356 |    2104 B |

`PreErrorBufferSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Contexts | Threads | Mean        | Error        | StdDev      | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|------------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    152.5 ns |    252.78 ns |    13.86 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 15,489.0 ns |  2,028.01 ns |   111.16 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    289.3 ns |    107.26 ns |     5.88 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 16,719.4 ns | 18,891.24 ns | 1,035.49 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    175.1 ns |    316.20 ns |    17.33 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 16,869.3 ns |  1,361.37 ns |    74.62 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    107.2 ns |     78.61 ns |     4.31 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 16,698.2 ns |  3,348.77 ns |   183.56 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    369.6 ns |     59.72 ns |     3.27 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 1       | 16,807.2 ns |  2,908.22 ns |   159.41 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    221.4 ns |     20.92 ns |     1.15 ns | 0.0179 | 0.0060 |     224 B |
| FillAndFlushOnError | 4096     | 8       | 16,600.8 ns |  1,406.23 ns |    77.08 ns | 0.0610 |      - |     824 B |

`RedactionPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                  | PatternCount | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------ |------------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| LoopLongHeaderNoMatch   | 1            |   167.05 ns |   9.307 ns |  0.510 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 1            |   174.81 ns |  29.383 ns |  1.611 ns |  1.05 |    0.01 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopLongHeaderNoMatch   | 5            | 2,908.35 ns | 703.468 ns | 38.559 ns |  1.00 |    0.02 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 5            | 2,637.43 ns | 393.722 ns | 21.581 ns |  0.91 |    0.01 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopMatch               | 1            |   216.07 ns | 176.738 ns |  9.688 ns |  1.00 |    0.06 | 0.0069 |      88 B |        1.00 |
| RedactMatch             | 1            |   223.66 ns |  15.832 ns |  0.868 ns |  1.04 |    0.04 | 0.0069 |      88 B |        1.00 |
|                         |              |             |            |           |       |         |        |           |             |
| LoopMatch               | 5            |   599.43 ns |  58.044 ns |  3.182 ns |  1.00 |    0.01 | 0.0067 |      88 B |        1.00 |
| RedactMatch             | 5            |   801.82 ns |  65.180 ns |  3.573 ns |  1.34 |    0.01 | 0.0067 |      88 B |        1.00 |
|                         |              |             |            |           |       |         |        |           |             |
| LoopShortNoMatch        | 1            |    65.67 ns |  30.850 ns |  1.691 ns |  1.00 |    0.03 |      - |         - |          NA |
| RedactShortNoMatch      | 1            |    68.73 ns |  85.641 ns |  4.694 ns |  1.05 |    0.07 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopShortNoMatch        | 5            |   357.76 ns |  19.135 ns |  1.049 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactShortNoMatch      | 5            |    90.60 ns |  17.045 ns |  0.934 ns |  0.25 |    0.00 |      - |         - |          NA |

`RedactionPerPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | PatternIndex | Mean        | Error      | StdDev    | Allocated |
|------------------ |------------- |------------:|-----------:|----------:|----------:|
| ShortNoMatch      | 0            |    66.96 ns |  24.411 ns |  1.338 ns |         - |
| LongHeaderNoMatch | 0            |   169.98 ns |  23.623 ns |  1.295 ns |         - |
| ShortNoMatch      | 1            |    66.95 ns | 106.799 ns |  5.854 ns |         - |
| LongHeaderNoMatch | 1            |   169.57 ns | 111.433 ns |  6.108 ns |         - |
| ShortNoMatch      | 2            |    86.42 ns |  12.350 ns |  0.677 ns |         - |
| LongHeaderNoMatch | 2            |   178.25 ns | 100.747 ns |  5.522 ns |         - |
| ShortNoMatch      | 3            |    65.60 ns |  15.289 ns |  0.838 ns |         - |
| LongHeaderNoMatch | 3            |   167.24 ns | 120.420 ns |  6.601 ns |         - |
| ShortNoMatch      | 4            |    63.09 ns |   9.301 ns |  0.510 ns |         - |
| LongHeaderNoMatch | 4            | 2,192.11 ns | 237.834 ns | 13.036 ns |         - |

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 11.48 us |  6.114 us | 0.335 us | 0.4272 |   5.39 KB |
| HandleRequest | False   | 4           | True                    | 17.97 us |  1.930 us | 0.106 us | 0.6561 |   8.19 KB |
| HandleRequest | False   | 32          | False                   | 16.57 us | 26.247 us | 1.439 us | 0.7935 |   9.98 KB |
| HandleRequest | False   | 32          | True                    | 24.35 us |  0.428 us | 0.023 us | 1.0376 |  12.78 KB |
| HandleRequest | True    | 4           | False                   | 10.94 us |  0.518 us | 0.028 us | 0.4272 |   5.39 KB |
| HandleRequest | True    | 4           | True                    | 16.81 us |  0.909 us | 0.050 us | 0.6561 |   8.19 KB |
| HandleRequest | True    | 32          | False                   | 17.43 us |  5.489 us | 0.301 us | 0.7935 |   9.98 KB |
| HandleRequest | True    | 32          | True                    | 21.96 us | 43.272 us | 2.372 us | 1.0376 |  12.78 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error      | StdDev   | Gen0   | Allocated |
|------------------------- |-----------:|-----------:|---------:|-------:|----------:|
| RedactPathAndRouteValues | 1,190.8 ns | 1,473.6 ns | 80.77 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   955.8 ns |   229.4 ns | 12.57 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean     | Error     | StdDev  | Gen0   | Gen1   | Allocated |
|------------------- |---------- |---------:|----------:|--------:|-------:|-------:|----------:|
| EmitRequestSummary | False     | 382.8 ns | 110.17 ns | 6.04 ns | 0.0749 |      - |     944 B |
| EmitRequestSummary | True      | 721.7 ns |  65.64 ns | 3.60 ns | 0.1631 | 0.0010 |    2048 B |

`SideSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Mean       | Error       | StdDev     | Allocated |
|-------------------- |-----------:|------------:|-----------:|----------:|
| TriggerOnError      | 60.6685 ns | 247.5349 ns | 13.5682 ns |         - |
| TriggerOnBelowError |  0.4867 ns |   1.6163 ns |  0.0886 ns |         - |
| SummaryTagged       | 16.2651 ns |  32.5117 ns |  1.7821 ns |         - |
| SummaryUntagged     |  8.6418 ns |   2.4435 ns |  0.1339 ns |         - |
| ImmediateTagged     | 15.1153 ns |   5.8826 ns |  0.3224 ns |         - |
| ImmediateUntagged   |  6.3182 ns |  12.9564 ns |  0.7102 ns |         - |

`StepUpSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method          | WithCategoryRules | Mean     | Error     | StdDev   | Allocated |
|---------------- |------------------ |---------:|----------:|---------:|----------:|
| EmitBelowSwitch | False             | 19.58 ns |  0.727 ns | 0.040 ns |         - |
| EmitExported    | False             | 23.82 ns | 12.819 ns | 0.703 ns |         - |
| EmitBelowSwitch | True              | 49.59 ns |  2.990 ns | 0.164 ns |         - |
| EmitExported    | True              | 53.60 ns |  4.997 ns | 0.274 ns |         - |

### Logging and Audit, run 2: baseline (`0705cdd`)

`AuditBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | WithHttpContext | DataEntries | Mean       | Error       | StdDev    | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|------------:|----------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   495.4 ns |   141.01 ns |   7.73 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 0           | 1,181.5 ns | 4,552.80 ns | 249.55 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   504.8 ns |    71.82 ns |   3.94 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 32          | 3,382.2 ns | 5,241.10 ns | 287.28 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 2,112.4 ns |   161.33 ns |   8.84 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,426.7 ns |   363.48 ns |  19.92 ns | 0.1564 |    1968 B |
| AuditToNullSink   | True            | 32          | 2,131.9 ns |   181.50 ns |   9.95 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 3,586.6 ns |   306.21 ns |  16.78 ns | 0.2670 |    3384 B |

`DroppedPathBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 239.9 ns |  35.92 ns |  1.97 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| LogContext       | False      | 253.0 ns | 224.57 ns | 12.31 ns |  1.05 |    0.05 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 406.4 ns | 165.39 ns |  9.07 ns |  1.69 |    0.03 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 407.3 ns | 224.39 ns | 12.30 ns |  1.70 |    0.05 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 609.6 ns | 307.07 ns | 16.83 ns |  2.54 |    0.06 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 596.1 ns | 212.89 ns | 11.67 ns |  2.48 |    0.05 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 627.3 ns | 578.23 ns | 31.69 ns |  2.61 |    0.12 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 622.1 ns |  87.12 ns |  4.78 ns |  2.59 |    0.03 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 626.7 ns |  76.14 ns |  4.17 ns |  2.61 |    0.02 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 640.4 ns |  69.65 ns |  3.82 ns |  2.67 |    0.02 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 664.2 ns | 173.06 ns |  9.49 ns |  2.77 |    0.04 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 254.6 ns | 147.32 ns |  8.08 ns |  1.00 |    0.04 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 255.1 ns |  22.42 ns |  1.23 ns |  1.00 |    0.03 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 441.1 ns |  41.43 ns |  2.27 ns |  1.73 |    0.05 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 457.4 ns |  78.21 ns |  4.29 ns |  1.80 |    0.05 | 0.0677 |     856 B |        2.02 |
| Properties       | True       | 665.4 ns | 448.20 ns | 24.57 ns |  2.62 |    0.11 | 0.1135 |    1432 B |        3.38 |
| ExceptionDetails | True       | 681.0 ns | 224.14 ns | 12.29 ns |  2.68 |    0.09 | 0.1135 |    1432 B |        3.38 |
| StepUpSink       | True       | 699.4 ns | 540.22 ns | 29.61 ns |  2.75 |    0.13 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 711.3 ns | 344.72 ns | 18.90 ns |  2.80 |    0.10 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 702.2 ns |  35.15 ns |  1.93 ns |  2.76 |    0.08 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 705.6 ns | 105.05 ns |  5.76 ns |  2.77 |    0.08 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 688.6 ns | 331.39 ns | 18.16 ns |  2.71 |    0.10 | 0.1135 |    1432 B |        3.38 |

`EnricherBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | StringProperties | Mean        | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |------------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |    772.4 ns |   480.28 ns |  26.33 ns |  1.00 |    0.04 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |    763.7 ns |    94.01 ns |   5.15 ns |  0.99 |    0.03 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |    871.3 ns |    31.40 ns |   1.72 ns |  1.13 |    0.03 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                |  3,163.2 ns |   283.92 ns |  15.56 ns |  4.10 |    0.12 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               |  2,395.9 ns | 1,218.54 ns |  66.79 ns |  1.00 |    0.03 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               |  2,287.4 ns |   474.80 ns |  26.03 ns |  0.96 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               |  2,338.5 ns |   346.69 ns |  19.00 ns |  0.98 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 10,406.3 ns |   935.66 ns |  51.29 ns |  4.35 |    0.11 | 0.5035 |      - |    6.2 KB |        1.05 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |    779.0 ns |   245.76 ns |  13.47 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |    856.9 ns |   108.53 ns |   5.95 ns |  1.10 |    0.02 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |    886.4 ns |   117.03 ns |   6.41 ns |  1.14 |    0.02 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                |  3,153.8 ns | 1,878.33 ns | 102.96 ns |  4.05 |    0.13 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               |  2,307.8 ns |   309.88 ns |  16.99 ns |  1.00 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               |  2,325.5 ns |    90.14 ns |   4.94 ns |  1.01 |    0.01 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               |  2,346.5 ns | 1,751.28 ns |  95.99 ns |  1.02 |    0.04 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 10,166.9 ns | 9,515.86 ns | 521.60 ns |  4.41 |    0.20 | 0.5035 |      - |    6.2 KB |        1.05 |

`PipelineBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   232.5 ns |    72.98 ns |  4.00 ns |  1.00 |    0.02 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   691.4 ns |   205.47 ns | 11.26 ns |  2.97 |    0.06 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | Default              | False      |   710.8 ns |   125.08 ns |  6.86 ns |  3.06 |    0.05 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | Default              | False      |   824.1 ns |    76.12 ns |  4.17 ns |  3.55 |    0.06 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   664.5 ns |    59.85 ns |  3.28 ns |  2.86 |    0.04 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   244.4 ns |   110.08 ns |  6.03 ns |  1.00 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   736.5 ns |    84.87 ns |  4.65 ns |  3.02 |    0.07 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | Default              | True       |   714.9 ns | 1,098.59 ns | 60.22 ns |  2.93 |    0.22 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | Default              | True       |   878.6 ns |   282.45 ns | 15.48 ns |  3.60 |    0.09 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   753.1 ns |   353.15 ns | 19.36 ns |  3.08 |    0.09 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   233.3 ns |    38.94 ns |  2.13 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      | 1,296.2 ns |    74.42 ns |  4.08 ns |  5.56 |    0.05 | 0.1202 |      - |    1512 B |        3.57 |
| MelDebugDropped        | Redaction            | False      | 1,315.7 ns |    46.24 ns |  2.53 ns |  5.64 |    0.05 | 0.1183 |      - |    1496 B |        3.53 |
| InformationHeld        | Redaction            | False      | 1,513.4 ns |   814.69 ns | 44.66 ns |  6.49 |    0.17 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,357.6 ns |   266.71 ns | 14.62 ns |  5.82 |    0.07 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   249.3 ns |   237.73 ns | 13.03 ns |  1.00 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       | 1,470.9 ns |   742.17 ns | 40.68 ns |  5.91 |    0.30 | 0.1259 |      - |    1600 B |        3.77 |
| MelDebugDropped        | Redaction            | True       | 1,466.9 ns |   205.30 ns | 11.25 ns |  5.89 |    0.26 | 0.1259 |      - |    1584 B |        3.74 |
| InformationHeld        | Redaction            | True       | 1,665.4 ns |   977.73 ns | 53.59 ns |  6.69 |    0.35 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,475.8 ns |    21.20 ns |  1.16 ns |  5.93 |    0.26 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   228.3 ns |   225.13 ns | 12.34 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   684.5 ns |    53.99 ns |  2.96 ns |  3.00 |    0.15 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   728.6 ns |   632.42 ns | 34.66 ns |  3.20 |    0.20 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   816.4 ns |   284.32 ns | 15.58 ns |  3.58 |    0.18 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   723.1 ns |   251.63 ns | 13.79 ns |  3.17 |    0.16 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   246.7 ns |     9.96 ns |  0.55 ns |  1.00 |    0.00 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   746.9 ns |    16.79 ns |  0.92 ns |  3.03 |    0.01 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   818.7 ns |    62.69 ns |  3.44 ns |  3.32 |    0.01 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   892.9 ns |    15.75 ns |  0.86 ns |  3.62 |    0.01 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   769.6 ns |   365.48 ns | 20.03 ns |  3.12 |    0.07 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   222.2 ns |   356.18 ns | 19.52 ns |  1.01 |    0.11 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   682.8 ns |   182.17 ns |  9.99 ns |  3.09 |    0.25 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   708.6 ns |   283.26 ns | 15.53 ns |  3.21 |    0.26 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   823.2 ns |   272.07 ns | 14.91 ns |  3.72 |    0.30 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   667.1 ns |   119.34 ns |  6.54 ns |  3.02 |    0.24 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   247.0 ns |    75.62 ns |  4.14 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   736.9 ns |    29.67 ns |  1.63 ns |  2.98 |    0.04 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   785.7 ns |   318.46 ns | 17.46 ns |  3.18 |    0.08 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   892.6 ns |    16.56 ns |  0.91 ns |  3.61 |    0.05 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   760.3 ns |   353.06 ns | 19.35 ns |  3.08 |    0.08 | 0.1135 |      - |    1432 B |        3.38 |

`PreErrorBufferGrowthBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method     | HeldPerTrace | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|------------:|----------:|-------:|-------:|----------:|
| HoldTraces | 3            |    709.8 ns |    117.7 ns |   6.45 ns | 0.0820 | 0.0811 |   1.01 KB |
| HoldTraces | 10           |  1,490.3 ns |  1,046.9 ns |  57.39 ns | 0.0820 | 0.0801 |   1.01 KB |
| HoldTraces | 100          | 11,305.2 ns | 16,535.1 ns | 906.34 ns | 0.0666 | 0.0444 |   1.01 KB |

`PreErrorBufferSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Contexts | Threads | Mean        | Error        | StdDev    | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|----------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    127.2 ns |    119.66 ns |   6.56 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 13,184.6 ns |  1,024.75 ns |  56.17 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    229.8 ns |     85.75 ns |   4.70 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 13,043.4 ns |  7,651.88 ns | 419.43 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    185.3 ns |     46.85 ns |   2.57 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 13,771.3 ns | 11,562.71 ns | 633.79 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    271.9 ns |     39.91 ns |   2.19 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 14,225.0 ns |  1,977.77 ns | 108.41 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    493.2 ns |    223.47 ns |  12.25 ns | 0.0820 | 0.0811 |    1032 B |
| FillAndFlushOnError | 4096     | 1       | 14,224.2 ns |  1,147.04 ns |  62.87 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    792.9 ns |    512.34 ns |  28.08 ns | 0.0830 | 0.0820 |    1030 B |
| FillAndFlushOnError | 4096     | 8       | 13,857.7 ns | 10,676.39 ns | 585.21 ns | 0.0610 |      - |     824 B |

`RedactionPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                  | PatternCount | Mean        | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------ |------------- |------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| LoopLongHeaderNoMatch   | 1            |   163.70 ns |     2.888 ns |   0.158 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 1            |   163.46 ns |   108.697 ns |   5.958 ns |  1.00 |    0.03 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopLongHeaderNoMatch   | 5            | 2,859.01 ns |    17.790 ns |   0.975 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 5            | 2,790.05 ns | 3,345.131 ns | 183.358 ns |  0.98 |    0.06 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopMatch               | 1            |   216.40 ns |     9.486 ns |   0.520 ns |  1.00 |    0.00 | 0.0069 |      88 B |        1.00 |
| RedactMatch             | 1            |   208.29 ns |   308.823 ns |  16.928 ns |  0.96 |    0.07 | 0.0069 |      88 B |        1.00 |
|                         |              |             |              |            |       |         |        |           |             |
| LoopMatch               | 5            |   600.17 ns |    18.225 ns |   0.999 ns |  1.00 |    0.00 | 0.0067 |      88 B |        1.00 |
| RedactMatch             | 5            |   609.85 ns |   196.204 ns |  10.755 ns |  1.02 |    0.02 | 0.0067 |      88 B |        1.00 |
|                         |              |             |              |            |       |         |        |           |             |
| LoopShortNoMatch        | 1            |    65.48 ns |     5.996 ns |   0.329 ns |  1.00 |    0.01 |      - |         - |          NA |
| RedactShortNoMatch      | 1            |    68.19 ns |    30.744 ns |   1.685 ns |  1.04 |    0.02 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopShortNoMatch        | 5            |   353.53 ns |    35.980 ns |   1.972 ns |  1.00 |    0.01 |      - |         - |          NA |
| RedactShortNoMatch      | 5            |   390.85 ns |   266.339 ns |  14.599 ns |  1.11 |    0.04 |      - |         - |          NA |

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 14.58 us |  9.882 us | 0.542 us | 0.5188 |    6.4 KB |
| HandleRequest | False   | 4           | True                    | 23.50 us |  3.182 us | 0.174 us | 0.7935 |   9.86 KB |
| HandleRequest | False   | 32          | False                   | 29.83 us | 68.790 us | 3.771 us | 1.2207 |  15.03 KB |
| HandleRequest | False   | 32          | True                    | 40.51 us |  3.349 us | 0.184 us | 1.4648 |  18.49 KB |
| HandleRequest | True    | 4           | False                   | 14.50 us | 10.827 us | 0.593 us | 0.5188 |    6.4 KB |
| HandleRequest | True    | 4           | True                    | 22.57 us |  2.007 us | 0.110 us | 0.7935 |   9.86 KB |
| HandleRequest | True    | 32          | False                   | 31.53 us |  2.216 us | 0.121 us | 1.2207 |  15.03 KB |
| HandleRequest | True    | 32          | True                    | 39.96 us | 15.040 us | 0.824 us | 1.4648 |  18.49 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error      | StdDev    | Gen0   | Allocated |
|------------------------- |-----------:|-----------:|----------:|-------:|----------:|
| RedactPathAndRouteValues | 1,435.4 ns | 2,829.8 ns | 155.11 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   685.3 ns |   197.8 ns |  10.84 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean       | Error    | StdDev   | Gen0   | Allocated |
|------------------- |---------- |-----------:|---------:|---------:|-------:|----------:|
| EmitRequestSummary | False     |   335.3 ns | 939.6 ns | 51.50 ns | 0.0682 |     856 B |
| EmitRequestSummary | True      | 1,413.7 ns | 111.0 ns |  6.08 ns | 0.2251 |    2840 B |

`SideSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Mean       | Error       | StdDev     | Allocated |
|-------------------- |-----------:|------------:|-----------:|----------:|
| TriggerOnError      | 69.7523 ns | 206.2861 ns | 11.3072 ns |         - |
| TriggerOnBelowError |  0.3671 ns |   1.7333 ns |  0.0950 ns |         - |
| SummaryTagged       | 16.2288 ns |   8.6472 ns |  0.4740 ns |         - |
| SummaryUntagged     |  8.7194 ns |   4.9454 ns |  0.2711 ns |         - |
| ImmediateTagged     | 15.0433 ns |   5.8111 ns |  0.3185 ns |         - |
| ImmediateUntagged   |  7.6580 ns |  11.1661 ns |  0.6120 ns |         - |

`StepUpSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method          | WithCategoryRules | Mean     | Error      | StdDev   | Allocated |
|---------------- |------------------ |---------:|-----------:|---------:|----------:|
| EmitBelowSwitch | False             | 16.81 ns |   0.598 ns | 0.033 ns |         - |
| EmitExported    | False             | 21.50 ns |   1.449 ns | 0.079 ns |         - |
| EmitBelowSwitch | True              | 47.71 ns |   6.365 ns | 0.349 ns |         - |
| EmitExported    | True              | 43.87 ns | 106.990 ns | 5.864 ns |         - |

### Logging and Audit, run 3: baseline (`0705cdd`)

`AuditBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | WithHttpContext | DataEntries | Mean       | Error       | StdDev    | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|------------:|----------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   495.7 ns |    54.68 ns |   3.00 ns | 0.0138 |     176 B |
| SerializeForSpool | False           | 0           | 1,260.3 ns |   436.66 ns |  23.93 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   498.0 ns |    22.54 ns |   1.24 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 32          | 3,479.8 ns |   211.34 ns |  11.58 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 2,087.4 ns |    81.60 ns |   4.47 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,392.6 ns |   471.84 ns |  25.86 ns | 0.1564 |    1968 B |
| AuditToNullSink   | True            | 32          | 2,104.1 ns |   183.80 ns |  10.07 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 3,422.3 ns | 4,564.75 ns | 250.21 ns | 0.2670 |    3384 B |

`DroppedPathBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 240.1 ns |  34.21 ns |  1.87 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| LogContext       | False      | 240.9 ns |  27.52 ns |  1.51 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 400.8 ns |  22.99 ns |  1.26 ns |  1.67 |    0.01 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 407.4 ns |  42.52 ns |  2.33 ns |  1.70 |    0.01 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 598.8 ns | 175.61 ns |  9.63 ns |  2.49 |    0.04 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 609.3 ns |  94.88 ns |  5.20 ns |  2.54 |    0.03 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 619.6 ns | 421.89 ns | 23.13 ns |  2.58 |    0.09 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 637.7 ns | 157.69 ns |  8.64 ns |  2.66 |    0.04 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 624.8 ns | 508.87 ns | 27.89 ns |  2.60 |    0.10 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 656.6 ns |  89.76 ns |  4.92 ns |  2.74 |    0.03 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 651.5 ns |  52.43 ns |  2.87 ns |  2.71 |    0.02 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 238.5 ns | 216.99 ns | 11.89 ns |  1.00 |    0.06 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 250.0 ns |  19.54 ns |  1.07 ns |  1.05 |    0.05 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 431.3 ns |  97.68 ns |  5.35 ns |  1.81 |    0.08 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 484.0 ns | 165.29 ns |  9.06 ns |  2.03 |    0.10 | 0.0706 |     888 B |        2.09 |
| Properties       | True       | 689.7 ns | 510.34 ns | 27.97 ns |  2.90 |    0.16 | 0.1163 |    1464 B |        3.45 |
| ExceptionDetails | True       | 681.7 ns | 192.35 ns | 10.54 ns |  2.86 |    0.13 | 0.1163 |    1464 B |        3.45 |
| StepUpSink       | True       | 693.1 ns | 299.74 ns | 16.43 ns |  2.91 |    0.14 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 702.1 ns | 104.84 ns |  5.75 ns |  2.95 |    0.13 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 690.8 ns |  72.42 ns |  3.97 ns |  2.90 |    0.13 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 714.3 ns |  40.46 ns |  2.22 ns |  3.00 |    0.13 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 709.5 ns |  74.13 ns |  4.06 ns |  2.98 |    0.13 | 0.1135 |    1432 B |        3.38 |

`EnricherBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | StringProperties | Mean        | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |------------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |    734.4 ns |    59.83 ns |   3.28 ns |  1.00 |    0.01 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |    734.8 ns |   360.15 ns |  19.74 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |    884.4 ns |    85.26 ns |   4.67 ns |  1.20 |    0.01 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                |  3,269.9 ns |   798.38 ns |  43.76 ns |  4.45 |    0.05 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               |  2,341.5 ns |   819.96 ns |  44.94 ns |  1.00 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               |  2,243.6 ns | 3,342.76 ns | 183.23 ns |  0.96 |    0.07 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               |  2,406.9 ns |   765.63 ns |  41.97 ns |  1.03 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 10,343.7 ns |   889.19 ns |  48.74 ns |  4.42 |    0.07 | 0.5035 |      - |    6.2 KB |        1.05 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |    762.7 ns |    42.61 ns |   2.34 ns |  1.00 |    0.00 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |    846.3 ns |   321.01 ns |  17.60 ns |  1.11 |    0.02 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |    897.5 ns |    88.42 ns |   4.85 ns |  1.18 |    0.01 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                |  3,201.4 ns | 1,478.99 ns |  81.07 ns |  4.20 |    0.09 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               |  2,384.7 ns | 1,579.29 ns |  86.57 ns |  1.00 |    0.04 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               |  2,405.3 ns |   358.68 ns |  19.66 ns |  1.01 |    0.03 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               |  2,270.3 ns | 1,580.81 ns |  86.65 ns |  0.95 |    0.04 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 10,274.4 ns | 1,615.21 ns |  88.53 ns |  4.31 |    0.14 | 0.5035 |      - |    6.2 KB |        1.05 |

`PipelineBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   250.5 ns |    23.98 ns |  1.31 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   667.8 ns |   207.46 ns | 11.37 ns |  2.67 |    0.04 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | Default              | False      |   671.7 ns |   346.86 ns | 19.01 ns |  2.68 |    0.07 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | Default              | False      |   816.0 ns |   152.62 ns |  8.37 ns |  3.26 |    0.03 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   673.8 ns |    52.22 ns |  2.86 ns |  2.69 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   261.0 ns |    56.10 ns |  3.08 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   742.0 ns |   489.54 ns | 26.83 ns |  2.84 |    0.09 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | Default              | True       |   787.6 ns |   237.43 ns | 13.01 ns |  3.02 |    0.05 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | Default              | True       |   864.3 ns |   634.42 ns | 34.77 ns |  3.31 |    0.12 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   770.7 ns |   448.18 ns | 24.57 ns |  2.95 |    0.09 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   232.4 ns |    92.55 ns |  5.07 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      | 1,273.6 ns | 1,356.95 ns | 74.38 ns |  5.48 |    0.30 | 0.1202 |      - |    1512 B |        3.57 |
| MelDebugDropped        | Redaction            | False      | 1,290.0 ns |   633.00 ns | 34.70 ns |  5.55 |    0.17 | 0.1183 |      - |    1496 B |        3.53 |
| InformationHeld        | Redaction            | False      | 1,540.3 ns |    69.74 ns |  3.82 ns |  6.63 |    0.13 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,366.1 ns |   251.88 ns | 13.81 ns |  5.88 |    0.12 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   244.1 ns |    62.72 ns |  3.44 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       | 1,433.9 ns |   631.03 ns | 34.59 ns |  5.87 |    0.14 | 0.1259 |      - |    1600 B |        3.77 |
| MelDebugDropped        | Redaction            | True       | 1,417.5 ns |   876.36 ns | 48.04 ns |  5.81 |    0.18 | 0.1259 |      - |    1584 B |        3.74 |
| InformationHeld        | Redaction            | True       | 1,631.5 ns |    88.05 ns |  4.83 ns |  6.68 |    0.08 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,563.7 ns |   883.55 ns | 48.43 ns |  6.41 |    0.19 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   257.3 ns |   346.70 ns | 19.00 ns |  1.00 |    0.09 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   684.3 ns |   106.98 ns |  5.86 ns |  2.67 |    0.17 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   723.0 ns |   348.01 ns | 19.08 ns |  2.82 |    0.18 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   849.8 ns |   462.76 ns | 25.37 ns |  3.31 |    0.22 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   703.1 ns |   294.29 ns | 16.13 ns |  2.74 |    0.18 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   243.8 ns |     5.37 ns |  0.29 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   751.4 ns |    34.44 ns |  1.89 ns |  3.08 |    0.01 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   820.1 ns |   191.24 ns | 10.48 ns |  3.36 |    0.04 | 0.1154 |      - |    1448 B |        3.42 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   937.6 ns |   323.94 ns | 17.76 ns |  3.85 |    0.06 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   825.1 ns |   309.50 ns | 16.96 ns |  3.38 |    0.06 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   234.2 ns |     6.91 ns |  0.38 ns |  1.00 |    0.00 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   684.5 ns |   140.25 ns |  7.69 ns |  2.92 |    0.03 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   711.9 ns |    41.38 ns |  2.27 ns |  3.04 |    0.01 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   821.1 ns |   296.83 ns | 16.27 ns |  3.51 |    0.06 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   701.0 ns |   172.20 ns |  9.44 ns |  2.99 |    0.04 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   253.8 ns |    23.31 ns |  1.28 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   732.0 ns |    81.63 ns |  4.47 ns |  2.88 |    0.02 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   803.5 ns |   163.40 ns |  8.96 ns |  3.17 |    0.03 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   858.7 ns |   222.56 ns | 12.20 ns |  3.38 |    0.04 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   766.2 ns |   142.57 ns |  7.81 ns |  3.02 |    0.03 | 0.1135 |      - |    1432 B |        3.38 |

`PreErrorBufferGrowthBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method     | HeldPerTrace | Mean        | Error      | StdDev   | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|-----------:|---------:|-------:|-------:|----------:|
| HoldTraces | 3            |    711.2 ns | 1,185.9 ns | 65.00 ns | 0.0820 | 0.0811 |   1.01 KB |
| HoldTraces | 10           |  1,530.2 ns |   120.3 ns |  6.60 ns | 0.0820 | 0.0801 |   1.01 KB |
| HoldTraces | 100          | 12,111.1 ns | 1,254.3 ns | 68.75 ns | 0.0666 | 0.0444 |   1.01 KB |

`PreErrorBufferSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Contexts | Threads | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|------------:|----------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    120.6 ns |    165.2 ns |   9.05 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 13,480.3 ns |    500.6 ns |  27.44 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    238.0 ns |    138.5 ns |   7.59 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 13,368.6 ns |  1,655.3 ns |  90.73 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    158.9 ns |    338.0 ns |  18.53 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 14,057.0 ns |  1,287.3 ns |  70.56 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    273.8 ns |    295.7 ns |  16.21 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 13,914.2 ns | 16,164.9 ns | 886.05 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    468.4 ns |    849.4 ns |  46.56 ns | 0.0820 | 0.0815 |    1032 B |
| FillAndFlushOnError | 4096     | 1       | 14,106.8 ns |  1,458.8 ns |  79.96 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    695.5 ns |    591.3 ns |  32.41 ns | 0.0820 | 0.0811 |    1025 B |
| FillAndFlushOnError | 4096     | 8       | 14,049.7 ns |  1,055.2 ns |  57.84 ns | 0.0610 |      - |     824 B |

`RedactionPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                  | PatternCount | Mean        | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------ |------------- |------------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| LoopLongHeaderNoMatch   | 1            |   172.79 ns |    23.36 ns |   1.281 ns |  1.00 |    0.01 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 1            |   172.56 ns |    21.06 ns |   1.154 ns |  1.00 |    0.01 |      - |         - |          NA |
|                         |              |             |             |            |       |         |        |           |             |
| LoopLongHeaderNoMatch   | 5            | 2,835.79 ns | 1,140.01 ns |  62.488 ns |  1.00 |    0.03 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 5            | 2,800.51 ns | 2,477.26 ns | 135.787 ns |  0.99 |    0.05 |      - |         - |          NA |
|                         |              |             |             |            |       |         |        |           |             |
| LoopMatch               | 1            |   220.32 ns |   150.29 ns |   8.238 ns |  1.00 |    0.05 | 0.0069 |      88 B |        1.00 |
| RedactMatch             | 1            |   215.40 ns |   184.24 ns |  10.099 ns |  0.98 |    0.05 | 0.0069 |      88 B |        1.00 |
|                         |              |             |             |            |       |         |        |           |             |
| LoopMatch               | 5            |   585.65 ns |    35.55 ns |   1.949 ns |  1.00 |    0.00 | 0.0067 |      88 B |        1.00 |
| RedactMatch             | 5            |   562.62 ns | 1,024.68 ns |  56.166 ns |  0.96 |    0.08 | 0.0067 |      88 B |        1.00 |
|                         |              |             |             |            |       |         |        |           |             |
| LoopShortNoMatch        | 1            |    68.48 ns |    24.54 ns |   1.345 ns |  1.00 |    0.02 |      - |         - |          NA |
| RedactShortNoMatch      | 1            |    61.23 ns |   107.66 ns |   5.901 ns |  0.89 |    0.08 |      - |         - |          NA |
|                         |              |             |             |            |       |         |        |           |             |
| LoopShortNoMatch        | 5            |   347.10 ns |    26.47 ns |   1.451 ns |  1.00 |    0.01 |      - |         - |          NA |
| RedactShortNoMatch      | 5            |   375.59 ns |   164.88 ns |   9.037 ns |  1.08 |    0.02 |      - |         - |          NA |

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 14.81 us | 16.444 us | 0.901 us | 0.5188 |    6.4 KB |
| HandleRequest | False   | 4           | True                    | 23.59 us |  1.954 us | 0.107 us | 0.7935 |   9.86 KB |
| HandleRequest | False   | 32          | False                   | 31.15 us | 69.418 us | 3.805 us | 1.2207 |  15.03 KB |
| HandleRequest | False   | 32          | True                    | 41.18 us |  3.337 us | 0.183 us | 1.4648 |  18.49 KB |
| HandleRequest | True    | 4           | False                   | 14.46 us |  2.542 us | 0.139 us | 0.5188 |    6.4 KB |
| HandleRequest | True    | 4           | True                    | 23.07 us |  0.559 us | 0.031 us | 0.7935 |   9.86 KB |
| HandleRequest | True    | 32          | False                   | 32.38 us |  5.743 us | 0.315 us | 1.2207 |  15.03 KB |
| HandleRequest | True    | 32          | True                    | 38.48 us | 38.509 us | 2.111 us | 1.4648 |  18.49 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error     | StdDev   | Gen0   | Allocated |
|------------------------- |-----------:|----------:|---------:|-------:|----------:|
| RedactPathAndRouteValues | 1,528.6 ns | 472.72 ns | 25.91 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   709.8 ns |  13.16 ns |  0.72 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean       | Error     | StdDev   | Gen0   | Allocated |
|------------------- |---------- |-----------:|----------:|---------:|-------:|----------:|
| EmitRequestSummary | False     |   369.2 ns |   9.45 ns |  0.52 ns | 0.0682 |     856 B |
| EmitRequestSummary | True      | 1,431.0 ns | 198.78 ns | 10.90 ns | 0.2251 |    2840 B |

`SideSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Mean       | Error       | StdDev    | Allocated |
|-------------------- |-----------:|------------:|----------:|----------:|
| TriggerOnError      | 69.8035 ns | 145.5391 ns | 7.9775 ns |         - |
| TriggerOnBelowError |  0.3560 ns |   1.0320 ns | 0.0566 ns |         - |
| SummaryTagged       | 16.3107 ns |  13.9307 ns | 0.7636 ns |         - |
| SummaryUntagged     |  8.7051 ns |   2.9917 ns | 0.1640 ns |         - |
| ImmediateTagged     | 15.5034 ns |  20.4601 ns | 1.1215 ns |         - |
| ImmediateUntagged   |  8.8301 ns |  25.9013 ns | 1.4197 ns |         - |

`StepUpSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method          | WithCategoryRules | Mean      | Error      | StdDev    | Allocated |
|---------------- |------------------ |----------:|-----------:|----------:|----------:|
| EmitBelowSwitch | False             |  9.551 ns | 22.3340 ns | 1.2242 ns |         - |
| EmitExported    | False             | 21.483 ns |  0.1492 ns | 0.0082 ns |         - |
| EmitBelowSwitch | True              | 47.121 ns |  0.6082 ns | 0.0333 ns |         - |
| EmitExported    | True              | 49.787 ns | 46.4226 ns | 2.5446 ns |         - |

### Logging and Audit, run 4: final (`c26c284`)

`AuditBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | WithHttpContext | DataEntries | Mean       | Error       | StdDev    | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|------------:|----------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   491.5 ns |    16.59 ns |   0.91 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 0           | 1,294.4 ns |    81.43 ns |   4.46 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   498.6 ns |   139.02 ns |   7.62 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 32          | 3,460.4 ns |   274.65 ns |  15.05 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 1,210.0 ns |   753.24 ns |  41.29 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,394.3 ns | 1,484.70 ns |  81.38 ns | 0.1564 |    1968 B |
| AuditToNullSink   | True            | 32          | 1,229.3 ns |   215.16 ns |  11.79 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 3,575.5 ns | 2,025.50 ns | 111.02 ns | 0.2670 |    3384 B |

`DroppedPathBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 237.8 ns |  61.02 ns |  3.34 ns |  1.00 |    0.02 | 0.0336 |     424 B |        1.00 |
| LogContext       | False      | 248.9 ns |  49.55 ns |  2.72 ns |  1.05 |    0.02 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 402.2 ns | 108.29 ns |  5.94 ns |  1.69 |    0.03 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 405.6 ns |  18.21 ns |  1.00 ns |  1.71 |    0.02 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 579.3 ns |  27.10 ns |  1.49 ns |  2.44 |    0.03 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 609.3 ns | 159.42 ns |  8.74 ns |  2.56 |    0.04 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 617.8 ns | 177.36 ns |  9.72 ns |  2.60 |    0.05 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 652.7 ns | 647.39 ns | 35.49 ns |  2.75 |    0.13 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 599.1 ns | 758.77 ns | 41.59 ns |  2.52 |    0.15 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 654.8 ns | 454.86 ns | 24.93 ns |  2.75 |    0.10 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 630.1 ns |  37.86 ns |  2.08 ns |  2.65 |    0.03 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 244.1 ns |  42.38 ns |  2.32 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 253.5 ns |  58.96 ns |  3.23 ns |  1.04 |    0.01 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 444.8 ns | 220.30 ns | 12.08 ns |  1.82 |    0.05 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 477.8 ns | 187.32 ns | 10.27 ns |  1.96 |    0.04 | 0.0677 |     856 B |        2.02 |
| Properties       | True       | 689.1 ns | 298.62 ns | 16.37 ns |  2.82 |    0.06 | 0.1135 |    1432 B |        3.38 |
| ExceptionDetails | True       | 667.7 ns | 218.17 ns | 11.96 ns |  2.74 |    0.05 | 0.1135 |    1432 B |        3.38 |
| StepUpSink       | True       | 700.4 ns | 352.30 ns | 19.31 ns |  2.87 |    0.07 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 709.3 ns | 192.85 ns | 10.57 ns |  2.91 |    0.04 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 692.8 ns |  62.38 ns |  3.42 ns |  2.84 |    0.03 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 696.7 ns |  93.86 ns |  5.14 ns |  2.85 |    0.03 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 720.3 ns | 436.19 ns | 23.91 ns |  2.95 |    0.09 | 0.1135 |    1432 B |        3.38 |

`EnricherBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method           | InActivity | StringProperties | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |   749.4 ns |   209.05 ns |  11.46 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |   747.6 ns |    79.90 ns |   4.38 ns |  1.00 |    0.01 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |   833.5 ns |   786.56 ns |  43.11 ns |  1.11 |    0.05 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                | 1,486.5 ns |   557.72 ns |  30.57 ns |  1.98 |    0.04 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               | 2,326.0 ns |   209.64 ns |  11.49 ns |  1.00 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               | 2,449.6 ns | 1,155.31 ns |  63.33 ns |  1.05 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               | 2,216.9 ns | 3,349.90 ns | 183.62 ns |  0.95 |    0.07 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 5,051.7 ns | 1,347.54 ns |  73.86 ns |  2.17 |    0.03 | 0.4807 |      - |   5.93 KB |        1.01 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |   702.5 ns |   237.29 ns |  13.01 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |   807.3 ns |   790.04 ns |  43.30 ns |  1.15 |    0.06 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |   864.8 ns |   513.12 ns |  28.13 ns |  1.23 |    0.04 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                | 1,577.0 ns |   168.53 ns |   9.24 ns |  2.25 |    0.04 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               | 2,280.1 ns |   153.51 ns |   8.41 ns |  1.00 |    0.00 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               | 2,441.3 ns | 1,196.86 ns |  65.60 ns |  1.07 |    0.03 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               | 2,423.6 ns |   516.68 ns |  28.32 ns |  1.06 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 4,652.8 ns | 5,783.44 ns | 317.01 ns |  2.04 |    0.12 | 0.4807 |      - |   5.93 KB |        1.01 |

`PipelineBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   209.8 ns |   545.18 ns | 29.88 ns |  1.01 |    0.18 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   328.3 ns |   189.20 ns | 10.37 ns |  1.59 |    0.21 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   350.0 ns |   152.17 ns |  8.34 ns |  1.69 |    0.22 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   890.6 ns |    40.73 ns |  2.23 ns |  4.31 |    0.55 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   682.1 ns |    60.25 ns |  3.30 ns |  3.30 |    0.42 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   245.0 ns |    80.00 ns |  4.39 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   321.3 ns |   142.32 ns |  7.80 ns |  1.31 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   359.9 ns |    18.01 ns |  0.99 ns |  1.47 |    0.02 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   991.5 ns |   407.30 ns | 22.33 ns |  4.05 |    0.10 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   736.5 ns |    83.02 ns |  4.55 ns |  3.01 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   230.9 ns |   240.11 ns | 13.16 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   345.8 ns |    15.75 ns |  0.86 ns |  1.50 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   390.9 ns |    93.98 ns |  5.15 ns |  1.70 |    0.09 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,188.9 ns |    69.62 ns |  3.82 ns |  5.16 |    0.26 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   953.9 ns |   173.35 ns |  9.50 ns |  4.14 |    0.21 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   242.6 ns |     5.74 ns |  0.31 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   343.3 ns |   308.29 ns | 16.90 ns |  1.42 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   400.1 ns |    49.55 ns |  2.72 ns |  1.65 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,204.7 ns |    26.90 ns |  1.47 ns |  4.97 |    0.01 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,111.1 ns |   377.49 ns | 20.69 ns |  4.58 |    0.07 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   246.5 ns |   282.63 ns | 15.49 ns |  1.00 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   316.4 ns |    43.47 ns |  2.38 ns |  1.29 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   364.1 ns |   213.22 ns | 11.69 ns |  1.48 |    0.09 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   860.8 ns |   240.55 ns | 13.19 ns |  3.50 |    0.19 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   710.9 ns |   140.07 ns |  7.68 ns |  2.89 |    0.15 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   248.7 ns |   152.94 ns |  8.38 ns |  1.00 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   270.1 ns | 1,195.36 ns | 65.52 ns |  1.09 |    0.23 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   384.6 ns |     0.77 ns |  0.04 ns |  1.55 |    0.04 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       | 1,016.2 ns |   130.37 ns |  7.15 ns |  4.09 |    0.12 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   794.2 ns |   224.99 ns | 12.33 ns |  3.20 |    0.10 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   240.8 ns |   124.66 ns |  6.83 ns |  1.00 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   707.4 ns |   308.86 ns | 16.93 ns |  2.94 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   731.9 ns | 1,691.66 ns | 92.73 ns |  3.04 |    0.34 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   860.9 ns |   200.32 ns | 10.98 ns |  3.58 |    0.10 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   724.0 ns |   219.32 ns | 12.02 ns |  3.01 |    0.08 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   248.6 ns |   148.05 ns |  8.11 ns |  1.00 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   758.4 ns |    90.20 ns |  4.94 ns |  3.05 |    0.09 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   806.7 ns |    21.00 ns |  1.15 ns |  3.25 |    0.09 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   987.3 ns |   282.92 ns | 15.51 ns |  3.97 |    0.12 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   771.2 ns |   495.74 ns | 27.17 ns |  3.10 |    0.13 | 0.1135 |      - |    1432 B |        3.38 |

`PreErrorBufferGrowthBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method     | HeldPerTrace | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|------------:|----------:|-------:|-------:|----------:|
| HoldTraces | 3            |    643.5 ns |    233.1 ns |  12.78 ns | 0.0172 | 0.0057 |     224 B |
| HoldTraces | 10           |  1,727.7 ns |    234.9 ns |  12.88 ns | 0.0362 | 0.0172 |     464 B |
| HoldTraces | 100          | 14,097.9 ns | 11,616.3 ns | 636.73 ns | 0.1628 | 0.1356 |    2104 B |

`PreErrorBufferSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Contexts | Threads | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|------------:|----------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    123.0 ns |   218.15 ns |  11.96 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 15,270.9 ns | 1,089.65 ns |  59.73 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    279.7 ns |   133.57 ns |   7.32 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 16,173.2 ns | 3,700.97 ns | 202.86 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    179.5 ns |   412.24 ns |  22.60 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 16,621.8 ns |   701.25 ns |  38.44 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    100.7 ns |    59.39 ns |   3.26 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 17,150.1 ns | 1,185.40 ns |  64.98 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    377.3 ns |   279.27 ns |  15.31 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 1       | 16,624.0 ns |   762.52 ns |  41.80 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    224.4 ns |    52.06 ns |   2.85 ns | 0.0179 | 0.0060 |     224 B |
| FillAndFlushOnError | 4096     | 8       | 17,140.4 ns | 6,575.74 ns | 360.44 ns | 0.0610 |      - |     824 B |

`RedactionPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                  | PatternCount | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------ |------------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| LoopLongHeaderNoMatch   | 1            |   167.09 ns |   8.818 ns |  0.483 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 1            |   165.52 ns | 267.592 ns | 14.668 ns |  0.99 |    0.08 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopLongHeaderNoMatch   | 5            | 2,889.85 ns |  91.445 ns |  5.012 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 5            | 2,646.97 ns |   7.008 ns |  0.384 ns |  0.92 |    0.00 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopMatch               | 1            |   222.73 ns |  40.724 ns |  2.232 ns |  1.00 |    0.01 | 0.0069 |      88 B |        1.00 |
| RedactMatch             | 1            |   202.52 ns | 576.170 ns | 31.582 ns |  0.91 |    0.12 | 0.0069 |      88 B |        1.00 |
|                         |              |             |            |           |       |         |        |           |             |
| LoopMatch               | 5            |   592.84 ns |  73.505 ns |  4.029 ns |  1.00 |    0.01 | 0.0067 |      88 B |        1.00 |
| RedactMatch             | 5            |   800.05 ns | 123.319 ns |  6.760 ns |  1.35 |    0.01 | 0.0067 |      88 B |        1.00 |
|                         |              |             |            |           |       |         |        |           |             |
| LoopShortNoMatch        | 1            |    62.85 ns |   4.660 ns |  0.255 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactShortNoMatch      | 1            |    59.44 ns | 124.163 ns |  6.806 ns |  0.95 |    0.09 |      - |         - |          NA |
|                         |              |             |            |           |       |         |        |           |             |
| LoopShortNoMatch        | 5            |   353.66 ns |  71.049 ns |  3.894 ns |  1.00 |    0.01 |      - |         - |          NA |
| RedactShortNoMatch      | 5            |    90.61 ns | 149.188 ns |  8.177 ns |  0.26 |    0.02 |      - |         - |          NA |

`RedactionPerPatternBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method            | PatternIndex | Mean        | Error      | StdDev    | Allocated |
|------------------ |------------- |------------:|-----------:|----------:|----------:|
| ShortNoMatch      | 0            |    65.56 ns |  84.441 ns |  4.629 ns |         - |
| LongHeaderNoMatch | 0            |   166.32 ns |   6.415 ns |  0.352 ns |         - |
| ShortNoMatch      | 1            |    72.48 ns |   9.622 ns |  0.527 ns |         - |
| LongHeaderNoMatch | 1            |   166.48 ns |  71.411 ns |  3.914 ns |         - |
| ShortNoMatch      | 2            |    85.63 ns |   5.162 ns |  0.283 ns |         - |
| LongHeaderNoMatch | 2            |   168.52 ns |  76.547 ns |  4.196 ns |         - |
| ShortNoMatch      | 3            |    66.07 ns |  13.259 ns |  0.727 ns |         - |
| LongHeaderNoMatch | 3            |   172.00 ns |  24.891 ns |  1.364 ns |         - |
| ShortNoMatch      | 4            |    64.10 ns |  42.784 ns |  2.345 ns |         - |
| LongHeaderNoMatch | 4            | 2,194.50 ns | 294.677 ns | 16.152 ns |         - |

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean      | Error      | StdDev    | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |----------:|-----------:|----------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 11.647 us |  0.3587 us | 0.0197 us | 0.4272 |   5.39 KB |
| HandleRequest | False   | 4           | True                    | 17.731 us |  0.4566 us | 0.0250 us | 0.6409 |   8.19 KB |
| HandleRequest | False   | 32          | False                   | 18.110 us |  4.3353 us | 0.2376 us | 0.7935 |   9.98 KB |
| HandleRequest | False   | 32          | True                    | 24.380 us | 22.0065 us | 1.2062 us | 1.0376 |  12.78 KB |
| HandleRequest | True    | 4           | False                   |  9.637 us | 28.3305 us | 1.5529 us | 0.4272 |   5.39 KB |
| HandleRequest | True    | 4           | True                    | 17.190 us |  0.9418 us | 0.0516 us | 0.6409 |   8.19 KB |
| HandleRequest | True    | 32          | False                   | 17.262 us |  3.4900 us | 0.1913 us | 0.7935 |   9.98 KB |
| HandleRequest | True    | 32          | True                    | 23.334 us |  7.8814 us | 0.4320 us | 1.0376 |  12.78 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error    | StdDev  | Gen0   | Allocated |
|------------------------- |-----------:|---------:|--------:|-------:|----------:|
| RedactPathAndRouteValues | 1,221.3 ns | 60.50 ns | 3.32 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   956.0 ns |  3.92 ns | 0.21 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean     | Error    | StdDev  | Gen0   | Gen1   | Allocated |
|------------------- |---------- |---------:|---------:|--------:|-------:|-------:|----------:|
| EmitRequestSummary | False     | 380.5 ns | 18.32 ns | 1.00 ns | 0.0749 |      - |     944 B |
| EmitRequestSummary | True      | 723.7 ns | 38.73 ns | 2.12 ns | 0.1631 | 0.0010 |    2048 B |

`SideSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method              | Mean       | Error      | StdDev    | Median     | Allocated |
|-------------------- |-----------:|-----------:|----------:|-----------:|----------:|
| TriggerOnError      | 60.3670 ns | 91.4324 ns | 5.0117 ns | 57.8531 ns |         - |
| TriggerOnBelowError |  0.5903 ns |  4.7559 ns | 0.2607 ns |  0.4654 ns |         - |
| SummaryTagged       | 15.9312 ns |  1.0595 ns | 0.0581 ns | 15.9427 ns |         - |
| SummaryUntagged     |  8.5097 ns |  4.3834 ns | 0.2403 ns |  8.4947 ns |         - |
| ImmediateTagged     | 14.9507 ns |  5.9207 ns | 0.3245 ns | 14.8189 ns |         - |
| ImmediateUntagged   |  8.7970 ns | 11.4329 ns | 0.6267 ns |  8.6710 ns |         - |

`StepUpSinkBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method          | WithCategoryRules | Mean     | Error      | StdDev   | Allocated |
|---------------- |------------------ |---------:|-----------:|---------:|----------:|
| EmitBelowSwitch | False             | 18.47 ns |   1.545 ns | 0.085 ns |         - |
| EmitExported    | False             | 23.67 ns |   1.419 ns | 0.078 ns |         - |
| EmitBelowSwitch | True              | 47.61 ns | 103.425 ns | 5.669 ns |         - |
| EmitExported    | True              | 52.80 ns |   4.282 ns | 0.235 ns |         - |

Exporter runs, three per commit; the first of each commit ran before `EmptyHostDisposed` existed.

### Exporter, baseline (`0705cdd`), run 1

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method           | Exporter | Mean      | Error      | StdDev    | Allocated |
|----------------- |--------- |----------:|-----------:|----------:|----------:|
| WarningExported  | File     |  18.04 us |   2.653 us |  0.145 us |   1.82 KB |
| ErrorFlush       | File     |  75.63 us |  19.323 us |  1.059 us |   4.43 KB |
| SteppedUpRequest | File     |  77.00 us |  42.924 us |  2.353 us |  10.45 KB |
| WarningExported  | Otlp     |  23.51 us |   4.222 us |  0.231 us |   3.37 KB |
| ErrorFlush       | Otlp     |  57.65 us |  20.895 us |  1.145 us |   9.59 KB |
| SteppedUpRequest | Otlp     | 141.96 us | 622.643 us | 34.129 us |     17 KB |

### Exporter, baseline (`0705cdd`), run 2

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method            | Exporter | Mean      | Error       | StdDev     | Allocated |
|------------------ |--------- |----------:|------------:|-----------:|----------:|
| EmptyHostDisposed | File     | 337.79 us | 3,214.11 us | 176.176 us |   1.88 KB |
| WarningExported   | File     |  18.79 us |    10.45 us |   0.573 us |   1.82 KB |
| ErrorFlush        | File     |  77.32 us |    39.68 us |   2.175 us |   4.43 KB |
| SteppedUpRequest  | File     |  72.72 us |   120.47 us |   6.603 us |  10.45 KB |
| EmptyHostDisposed | Otlp     | 487.26 us | 1,375.16 us |  75.377 us |   3.32 KB |
| WarningExported   | Otlp     |  17.48 us |    33.93 us |   1.860 us |   3.32 KB |
| ErrorFlush        | Otlp     |  60.52 us |   113.92 us |   6.244 us |    9.7 KB |
| SteppedUpRequest  | Otlp     | 122.04 us |    68.01 us |   3.728 us |     17 KB |

### Exporter, baseline (`0705cdd`), run 3

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method            | Exporter | Mean      | Error        | StdDev     | Allocated |
|------------------ |--------- |----------:|-------------:|-----------:|----------:|
| EmptyHostDisposed | File     | 466.95 us | 2,019.244 us | 110.682 us |   1.88 KB |
| WarningExported   | File     |  18.14 us |     5.536 us |   0.303 us |   1.82 KB |
| ErrorFlush        | File     |  81.39 us |    10.098 us |   0.553 us |   4.43 KB |
| SteppedUpRequest  | File     |  75.31 us |    39.522 us |   2.166 us |  10.45 KB |
| EmptyHostDisposed | Otlp     | 586.11 us |   273.874 us |  15.012 us |   3.32 KB |
| WarningExported   | Otlp     |  18.39 us |    32.913 us |   1.804 us |   3.32 KB |
| ErrorFlush        | Otlp     |  60.84 us |   110.874 us |   6.077 us |    9.7 KB |
| SteppedUpRequest  | Otlp     | 123.53 us |   156.047 us |   8.553 us |  17.03 KB |

### Exporter, final, run 1 (`c26c284`)

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method           | Exporter | Mean      | Error      | StdDev    | Allocated |
|----------------- |--------- |----------:|-----------:|----------:|----------:|
| WarningExported  | File     |  18.10 us |   4.123 us |  0.226 us |   1.82 KB |
| ErrorFlush       | File     |  75.12 us |  13.678 us |  0.750 us |   4.42 KB |
| SteppedUpRequest | File     |  76.64 us |  25.429 us |  1.394 us |      9 KB |
| WarningExported  | Otlp     |  16.76 us |   6.471 us |  0.355 us |   3.32 KB |
| ErrorFlush       | Otlp     |  63.78 us | 109.155 us |  5.983 us |   9.72 KB |
| SteppedUpRequest | Otlp     | 107.25 us | 472.359 us | 25.892 us |  15.56 KB |

### Exporter, final, run 2 (`b1a9f80`)

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method            | Exporter | Mean      | Error        | StdDev     | Allocated |
|------------------ |--------- |----------:|-------------:|-----------:|----------:|
| EmptyHostDisposed | File     | 475.21 us | 2,416.813 us | 132.474 us |   1.88 KB |
| WarningExported   | File     |  18.28 us |     3.901 us |   0.214 us |   1.82 KB |
| ErrorFlush        | File     |  56.68 us |   331.137 us |  18.151 us |   4.42 KB |
| SteppedUpRequest  | File     |  62.85 us |   189.001 us |  10.360 us |   9.01 KB |
| EmptyHostDisposed | Otlp     | 384.50 us | 1,484.930 us |  81.394 us |   3.26 KB |
| WarningExported   | Otlp     |  16.35 us |    18.941 us |   1.038 us |   3.35 KB |
| ErrorFlush        | Otlp     |  67.04 us |   269.301 us |  14.761 us |    9.9 KB |
| SteppedUpRequest  | Otlp     |  87.83 us |    87.291 us |   4.785 us |   15.5 KB |

### Exporter, final, run 3 (`b1a9f80`)

`ExporterBenchmarks` (`Job=ShortRun  InvocationCount=1  IterationCount=3 LaunchCount=1  UnrollFactor=1  WarmupCount=3`)

| Method            | Exporter | Mean      | Error        | StdDev     | Allocated |
|------------------ |--------- |----------:|-------------:|-----------:|----------:|
| EmptyHostDisposed | File     | 272.33 us |   736.917 us |  40.393 us |   1.82 KB |
| WarningExported   | File     |  18.25 us |     5.094 us |   0.279 us |   1.82 KB |
| ErrorFlush        | File     |  68.00 us |   136.709 us |   7.493 us |   4.42 KB |
| SteppedUpRequest  | File     |  71.87 us |    44.252 us |   2.426 us |   8.99 KB |
| EmptyHostDisposed | Otlp     | 386.93 us | 1,875.552 us | 102.805 us |   3.26 KB |
| WarningExported   | Otlp     |  12.63 us |    18.087 us |   0.991 us |   3.61 KB |
| ErrorFlush        | Otlp     |  61.40 us |    33.512 us |   1.837 us |   9.72 KB |
| SteppedUpRequest  | Otlp     |  95.39 us |   438.789 us |  24.052 us |  15.46 KB |

### Disk, final (default job)

`DurableWriteBenchmarks` (``)

| Method       | Writers | Mean       | Error     | StdDev    | Median     | Gen0   | Allocated |
|------------- |-------- |-----------:|----------:|----------:|-----------:|-------:|----------:|
| WriteRecords | 1       | 2,155.1 us | 197.59 us | 582.58 us | 2,446.3 us |      - |   7.48 KB |
| WriteRecords | 8       |   336.2 us |  27.56 us |  75.92 us |   302.4 us | 0.4883 |    7.5 KB |

`FullSpoolDropBenchmarks` (``)

| Method          | Mean           | Error         | StdDev        | Ratio | RatioSD | Gen0      | Allocated | Alloc Ratio |
|---------------- |---------------:|--------------:|--------------:|------:|--------:|----------:|----------:|------------:|
| MeasureTheSpool | 163,777.558 us | 3,171.5405 us | 4,648.8034 us | 1.001 |    0.04 | 3250.0000 |  42188 KB |       1.000 |
| DroppedWrite    |       2.895 us |     0.0547 us |     0.0512 us | 0.000 |    0.00 |    0.2289 |   2.95 KB |       0.000 |

`SpoolDrainBenchmarks` (``)

| Method     | BatchSize | Mean        | Error    | StdDev   | Allocated |
|----------- |---------- |------------:|---------:|---------:|----------:|
| DrainSpool | 1         | 15,356.7 us | 31.92 us | 29.86 us |   4.23 KB |
| DrainSpool | 64        |    259.0 us |  2.22 us |  1.74 us |   3.71 KB |

Follow-up 6 isolated, in the order they ran (parent, final, parent, final).

### Request logging, parent (`490a54d`), run 1

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 12.45 us |  0.701 us | 0.038 us | 0.5035 | 0.1678 |   6.23 KB |
| HandleRequest | False   | 4           | True                    | 19.00 us | 27.169 us | 1.489 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | False   | 32          | False                   | 20.33 us | 42.517 us | 2.330 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | False   | 32          | True                    | 29.15 us |  3.936 us | 0.216 us | 1.4648 |      - |  18.31 KB |
| HandleRequest | True    | 4           | False                   | 11.72 us |  1.419 us | 0.078 us | 0.5035 |      - |   6.23 KB |
| HandleRequest | True    | 4           | True                    | 18.42 us |  0.655 us | 0.036 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | True    | 32          | False                   | 19.97 us |  1.223 us | 0.067 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | True    | 32          | True                    | 25.39 us | 30.666 us | 1.681 us | 1.4648 | 0.0305 |  18.31 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error    | StdDev  | Gen0   | Allocated |
|------------------------- |-----------:|---------:|--------:|-------:|----------:|
| RedactPathAndRouteValues | 1,283.9 ns | 63.61 ns | 3.49 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   969.0 ns | 48.50 ns | 2.66 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean       | Error    | StdDev   | Gen0   | Allocated |
|------------------- |---------- |-----------:|---------:|---------:|-------:|----------:|
| EmitRequestSummary | False     |   393.3 ns | 129.5 ns |  7.10 ns | 0.0682 |     856 B |
| EmitRequestSummary | True      | 1,457.6 ns | 263.2 ns | 14.43 ns | 0.2251 |    2840 B |

### Request logging, final, run 2

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 11.14 us | 14.226 us | 0.780 us | 0.4272 |   5.39 KB |
| HandleRequest | False   | 4           | True                    | 18.85 us | 47.996 us | 2.631 us | 0.6409 |   8.19 KB |
| HandleRequest | False   | 32          | False                   | 18.74 us | 16.182 us | 0.887 us | 0.7935 |   9.98 KB |
| HandleRequest | False   | 32          | True                    | 24.45 us |  2.382 us | 0.131 us | 1.0376 |  12.78 KB |
| HandleRequest | True    | 4           | False                   | 11.16 us |  1.388 us | 0.076 us | 0.4272 |   5.39 KB |
| HandleRequest | True    | 4           | True                    | 17.02 us |  4.499 us | 0.247 us | 0.6409 |   8.19 KB |
| HandleRequest | True    | 32          | False                   | 16.78 us |  1.533 us | 0.084 us | 0.7935 |   9.98 KB |
| HandleRequest | True    | 32          | True                    | 21.99 us | 22.699 us | 1.244 us | 1.0376 |  12.78 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error    | StdDev   | Gen0   | Allocated |
|------------------------- |-----------:|---------:|---------:|-------:|----------:|
| RedactPathAndRouteValues | 1,213.2 ns | 851.5 ns | 46.67 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   948.8 ns | 172.3 ns |  9.45 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean     | Error    | StdDev   | Gen0   | Gen1   | Allocated |
|------------------- |---------- |---------:|---------:|---------:|-------:|-------:|----------:|
| EmitRequestSummary | False     | 380.2 ns | 225.2 ns | 12.34 ns | 0.0749 |      - |     944 B |
| EmitRequestSummary | True      | 736.3 ns | 140.1 ns |  7.68 ns | 0.1631 | 0.0010 |    2048 B |

### Request logging, parent (`490a54d`), run 3

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 12.62 us |  0.539 us | 0.030 us | 0.5035 | 0.1678 |   6.23 KB |
| HandleRequest | False   | 4           | True                    | 19.40 us |  5.197 us | 0.285 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | False   | 32          | False                   | 21.29 us | 30.518 us | 1.673 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | False   | 32          | True                    | 28.42 us |  2.014 us | 0.110 us | 1.4648 |      - |  18.31 KB |
| HandleRequest | True    | 4           | False                   | 11.71 us |  0.997 us | 0.055 us | 0.5035 |      - |   6.23 KB |
| HandleRequest | True    | 4           | True                    | 18.81 us |  0.821 us | 0.045 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | True    | 32          | False                   | 17.75 us | 55.035 us | 3.017 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | True    | 32          | True                    | 25.64 us | 40.252 us | 2.206 us | 1.4648 | 0.0305 |  18.31 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error     | StdDev   | Gen0   | Allocated |
|------------------------- |-----------:|----------:|---------:|-------:|----------:|
| RedactPathAndRouteValues | 1,275.2 ns | 578.29 ns | 31.70 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   965.8 ns |  44.37 ns |  2.43 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean       | Error    | StdDev   | Gen0   | Allocated |
|------------------- |---------- |-----------:|---------:|---------:|-------:|----------:|
| EmitRequestSummary | False     |   380.0 ns | 197.2 ns | 10.81 ns | 0.0682 |     856 B |
| EmitRequestSummary | True      | 1,503.8 ns | 201.7 ns | 11.06 ns | 0.2251 |    2840 B |

### Request logging, final, run 4

`RequestLoggingBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 11.65 us |  0.886 us | 0.049 us | 0.4272 |   5.39 KB |
| HandleRequest | False   | 4           | True                    | 18.03 us |  3.064 us | 0.168 us | 0.6409 |   8.19 KB |
| HandleRequest | False   | 32          | False                   | 17.33 us | 34.314 us | 1.881 us | 0.7935 |   9.98 KB |
| HandleRequest | False   | 32          | True                    | 24.41 us |  0.221 us | 0.012 us | 1.0376 |  12.78 KB |
| HandleRequest | True    | 4           | False                   | 11.06 us |  1.039 us | 0.057 us | 0.4272 |   5.39 KB |
| HandleRequest | True    | 4           | True                    | 17.03 us |  9.684 us | 0.531 us | 0.6409 |   8.19 KB |
| HandleRequest | True    | 32          | False                   | 17.53 us | 16.826 us | 0.922 us | 0.7935 |   9.98 KB |
| HandleRequest | True    | 32          | True                    | 22.79 us |  3.212 us | 0.176 us | 1.0376 |  12.78 KB |

`RequestPathRedactionBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method                   | Mean       | Error     | StdDev  | Gen0   | Allocated |
|------------------------- |-----------:|----------:|--------:|-------:|----------:|
| RedactPathAndRouteValues | 1,230.2 ns |  73.96 ns | 4.05 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   955.1 ns | 101.60 ns | 5.57 ns | 0.0095 |     128 B |

`RequestSummaryBenchmarks` (`Job=ShortRun  IterationCount=3  LaunchCount=1 WarmupCount=3`)

| Method             | AllFields | Mean     | Error    | StdDev  | Gen0   | Gen1   | Allocated |
|------------------- |---------- |---------:|---------:|--------:|-------:|-------:|----------:|
| EmitRequestSummary | False     | 378.3 ns | 142.4 ns | 7.81 ns | 0.0749 |      - |     944 B |
| EmitRequestSummary | True      | 714.2 ns | 164.0 ns | 8.99 ns | 0.1631 | 0.0010 |    2048 B |
