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

# Exporter: once per commit, with the collector of otlp-collector.yaml running
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
- The allocation column is deterministic and agrees between runs; a byte difference is a fact where a
  nanosecond difference under 25 % is not.
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
  recorded, over the 100 holds of a cycle. Allocations unchanged at 824 B.
- **`InformationHeld`: +3 % to +16 % (unresolved)** on the six non-redacting scenarios, the same direction
  as the single-thread hold seen from the whole pipeline. **Hypothesis**: one more stripe lookup per held
  event; not measured separately.
- **`HoldTraces`, `HeldPerTrace=100` (a trace that fills its ring): 11.7 us to 14.2 us, +21 % (unresolved),
  1.01 KB to 2.05 KB allocated per trace.** A ring that grows from 4 slots to 100 allocates the steps on the
  way, so a trace that fills it costs twice the bytes of the old fixed ring; a trace that holds 3 or 10
  events allocates 224 B or 464 B instead of 1.01 KB.
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
| `WarningExported` | File | 18.4 us | 18.3 us | 1.82 KB | 1.82 KB |
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
  request: 96 us, error 439 us), and the runs of one commit differ by up to 30 %. The -25 % on the OTLP request
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
encryptor, is 1.8 us (no request, no data) to 5.7 us (request, 32 data entries) per record in the baseline
(`AuditToNullSink` plus `SerializeForSpool`) and 1.8 us to 4.8 us at the final commit. Against 2.16 ms per
record with one writer that is 0.08 % to 0.26 %; against 336 us per record with eight overlapping writers,
0.5 % to 1.7 %, the largest share the measurements give. Halving the audit CPU would return under 1 % of a
write. So the audit path stays unchanged, and so does the spool envelope, a contract with the receiving service (ADR 0020). The one
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
