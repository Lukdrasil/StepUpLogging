# Results: the striped pre-error buffer

What [ADR 0027](../adr/0027-prebuffer-striped-lru-and-on-demand-ring.md) changed, measured: the pre-error
buffer's one global LRU lock is now up to 16 independent stripes, and a trace's ring starts with 4 slots and
grows to its capacity instead of being allocated whole. Follow-ups 4 and 7 of [analysis.md](analysis.md) are
recorded here.

- Before: commit `4298f9c`, the branch this one is stacked on (one `_lruGate`, a `Queue` pre-sized to the
  capacity). `PreErrorBufferSinkBenchmarks` at that commit has `Contexts` 1 and 4096 and
  `PreErrorBufferGrowthBenchmarks` did not exist, so a **detached worktree of `4298f9c` was built with the
  after commit's two benchmark files copied over it** (`PreErrorBufferSinkBenchmarks.cs`, with the extra 256
  row, and `PreErrorBufferGrowthBenchmarks.cs`). `src/` is untouched and is the code before the change, so
  the before rows are the old sink measured by the new benchmarks. `PipelineBenchmarks` is the same file in
  both. The worktree was removed afterwards.
- After: commit `256327e` (the striped sink and the new benchmarks; the commits after it on this branch touch
  documentation only).
- Date: 2026-10-09. The two runs were made on the same machine, the before run first, one after the other.
- Machine state: laptop on AC power, CPU governor `powersave`, desktop session running; the same state as
  [baseline.md](baseline.md).
- Classes: `PreErrorBufferSinkBenchmarks`, `PreErrorBufferGrowthBenchmarks`, `PipelineBenchmarks`.
- Commands, from the repository root (the before run from the temporary worktree):

```bash
# before, at 4298f9c with the two benchmark files of the after commit
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PreErrorBufferSinkBenchmarks*" "*PreErrorBufferGrowthBenchmarks*" "*PipelineBenchmarks*" \
  --job short --exporters github json

# after, at 256327e: the same command
```

```
BenchmarkDotNet v0.15.8, Linux Omarchy
12th Gen Intel Core i7-12700H 0.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3
```

## How much to trust these

`--job short` is one launch, three warmup and three measured iterations (see [baseline.md](baseline.md)).
Read the `Mean` of a row together with its `Error`, and treat a difference under 25 % between two rows as
unresolved. Below, a change is called resolved only when it is well outside that: the 8-thread rows with many
traces (2.7x and 3.1x) and the allocation column, which is deterministic and agrees between runs. The
single-thread rows move by 13 % to 23 %, which is inside the rule, and several rows have an `Error` as wide as
their `Mean` (the `FillAndFlushOnError` row at 256 contexts and 8 threads: 17.1 us, error 20.6 us). The
`Contexts` 256 row is new, so the before rows for it come from the old code run by the new benchmark.

The sink benchmark's `Hold` is reported per hold and its threads share the holds, so the time column is wall
time per hold across all threads: holds per second is `1e9 / Mean` whatever the thread count.

## Holding under contention (`PreErrorBufferSinkBenchmarks.Hold`)

Mean time in ns and allocated bytes per hold. Capacity per trace 100, at most 1024 traces.

| Traces | Threads | Before | After | Change |
|---|---|---|---|---|
| 1 | 1 | 127.5 ns, 0 B | 150.0 ns, 0 B | +18 %, unresolved |
| 1 | 8 | 227.0 ns, 0 B | 274.4 ns, 0 B | +21 %, unresolved |
| 256 | 1 | 164.3 ns, 0 B | 185.4 ns, 0 B | +13 %, unresolved |
| 256 | 8 | 291.8 ns, 0 B | 106.2 ns, 0 B | 2.7x faster |
| 4096 | 1 | 488.5 ns, 1032 B | 386.4 ns, 224 B | 21 % faster, 4.6x fewer bytes |
| 4096 | 8 | 710.9 ns, 1029 B | 231.1 ns, 224 B | 3.1x faster, 4.6x fewer bytes |

What the table shows:

- The global lock was the contention. With 256 traces alive nothing is evicted and all the cost is the lock and
  the lookup: eight threads went from 292 ns to 106 ns a hold. With 4096 traces, where every hold also
  evicts, eight threads went from 711 ns to 231 ns, and now beat one thread (386 ns): 4.3 M holds a second
  against 1.4 M before. The analysis put the ceiling at the one-thread time of the baseline (468 ns); the
  striped sink goes below it because the eight threads now overlap.
- The one-trace, eight-thread row is unchanged within the noise (227 ns, 274 ns). All eight threads still
  queue on the one trace's own buffer lock; that gap is out of scope (ADR 0027, Consequences).
- The single-thread rows with no eviction (1 and 256 traces) are 13 % to 18 % slower, in the direction the
  code predicts and inside the noise of a three-iteration run. **Hypothesis:** the 15 to 20 ns is one more
  hash of the 32-character trace id, which `StripeOf` computes to pick the stripe and the dictionaries then
  compute again. It is not measured separately.
- A new trace's first hold allocates 224 B against 1032 B: a 4-slot array (56 B) in place of the 100-slot
  queue (824 B and its `Queue` object). The other 168 B (the buffer, its lock object and the bookkeeping
  nodes) are not the ring.

### `FillAndFlushOnError`

A cycle is 100 holds in one trace followed by an Error that flushes them. It ignores both parameters, so its
six rows are one measurement of the same code; their spread is the noise.

| | Before | After |
|---|---|---|
| Range of the six rows | 13.2 to 17.1 us | 15.4 to 17.3 us |
| Rows 1 / 1, 1 / 8, 256 / 1, 4096 / 1, 4096 / 8 | 13.2, 13.6, 14.0, 14.3, 13.6 us | 15.4, 15.9, 16.1, 16.0, 16.5 us |
| Allocated | 824 B | 824 B |

The after rows are about 15 % higher in five of six, which is the 15 to 20 ns more per hold over the cycle's
100 holds (about 2 us). It is a single-thread effect and the same size as the single-thread `Hold` rows. The
ring is already grown to 100 slots after the first cycle, so the ring is not what is added. The flush is
still one 100-reference snapshot (824 B), as before.

## A trace's ring grows on demand (`PreErrorBufferGrowthBenchmarks`)

4096 traces pass through a sink that keeps 1024; each holds `HeldPerTrace` events in a row. Reported per
trace, one thread, so every trace pays for its buffer and evicts another.

| Events held per trace | Before | After | Change |
|---|---|---|---|
| 3 | 705.9 ns, 1.01 KB | 588.4 ns, 224 B | 4.6x fewer bytes; time inside the noise (error 1.6 us) |
| 10 | 1492.1 ns, 1.01 KB | 1687.8 ns, 464 B | 2.2x fewer bytes; time inside the noise |
| 100 | 11786.7 ns, 1.01 KB | 14343.3 ns, 2104 B | 2.1x **more** bytes; time +22 %, unresolved |

The bytes are exact and show the trade. A ring that grows copies: 4, 8, 16, 32, 64, then 100 slots are 56,
88, 152, 280, 536 and 824 B of array, and each step leaves the one before to the garbage collector. A trace
that holds 3 events pays for one array, 10 events for three (56 + 88 + 152 = 296 B on top of the 168 B
that does not depend on the ring: 464 B), and a trace that fills the ring pays for six (1936 B of arrays
and 2104 B in all), about twice the 1034 B of the pre-sized queue. From those sizes the crossover is between
32 and 64 events held (744 B at 32, 1280 B at 64, against 1034 B before); that is arithmetic from the array
sizes, not a measured row. At the default capacity of 100 a request that fills its ring is the case this
change makes more expensive. The 22 % more time at 100 is inside the 25 % rule, and the extra bytes are what
the garbage collector pays for (Gen0 collections per 1000 operations: 0.16 against 0.08).

Setup checks, in both runs: the sink keeps exactly 1024 traces after the 4096, and an Error on the last trace
flushes exactly `HeldPerTrace` events.

## `PipelineBenchmarks`: a held event end to end

`InformationHeld` is the pipeline row that reaches `PreErrorBufferSink.Hold`. Its trace never changes (the key
is the one Activity's trace id, or the global key without an Activity), so it measures the uncontended
single-thread path.

| Scenario | Act | Before | After |
|---|---|---|---|
| Default | no | 824 ns, 1360 B | 898 ns, 1360 B |
| Default | yes | 919 ns, 1432 B | 999 ns, 1432 B |
| Redaction | no | 1088 ns, 1416 B | 1176 ns, 1416 B |
| Redaction | yes | 1206 ns, 1488 B | 1419 ns, 1488 B |
| FloorsAndNeverStepUp | no | 846 ns, 1360 B | 907 ns, 1360 B |
| FloorsAndNeverStepUp | yes | 941 ns, 1432 B | 976 ns, 1432 B |
| ConsumerRootSink | no | 858 ns, 1360 B | 848 ns, 1360 B |
| ConsumerRootSink | yes | 896 ns, 1432 B | 977 ns, 1432 B |

The bytes are identical. In time seven of eight rows are 4 % to 18 % slower and one is 1 % faster, every one
inside the 25 % rule; the plain Serilog rows move by about the same (183 to 262 ns across both runs). I do not
resolve a cost here, but a hold that is 15 to 20 ns slower (previous section) would show as about 2 % of an
800 to 1400 ns event, and the direction agrees. `DebugDropped`, `MelDebugDropped` and `WarningExported` do not
reach the buffer; their rows are in the raw tables.

## The raw tables

Before (`4298f9c` with the two benchmark files of the after commit), `PreErrorBufferSinkBenchmarks`:

| Method              | Contexts | Threads | Mean        | Error        | StdDev      | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|------------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    127.5 ns |    112.59 ns |     6.17 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 13,210.4 ns |    258.14 ns |    14.15 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    227.0 ns |     68.33 ns |     3.75 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 13,616.7 ns |  7,186.88 ns |   393.94 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    164.3 ns |    270.62 ns |    14.83 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 14,032.9 ns |  1,332.28 ns |    73.03 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    291.8 ns |    122.28 ns |     6.70 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 17,068.7 ns | 20,608.65 ns | 1,129.63 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    488.5 ns |    106.78 ns |     5.85 ns | 0.0820 | 0.0815 |    1032 B |
| FillAndFlushOnError | 4096     | 1       | 14,252.7 ns |  2,098.42 ns |   115.02 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    710.9 ns |    406.32 ns |    22.27 ns | 0.0830 | 0.0820 |    1029 B |
| FillAndFlushOnError | 4096     | 8       | 13,564.9 ns | 14,510.82 ns |   795.39 ns | 0.0610 |      - |     824 B |

After (`256327e`), `PreErrorBufferSinkBenchmarks`:

| Method              | Contexts | Threads | Mean        | Error        | StdDev      | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|------------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    150.0 ns |    170.34 ns |     9.34 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 15,389.1 ns |  1,013.47 ns |    55.55 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    274.4 ns |    213.16 ns |    11.68 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 15,854.2 ns |  1,356.87 ns |    74.37 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    185.4 ns |    434.18 ns |    23.80 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 16,128.6 ns |  9,233.02 ns |   506.09 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    106.2 ns |     83.48 ns |     4.58 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 17,263.4 ns |  4,054.29 ns |   222.23 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    386.4 ns |    245.88 ns |    13.48 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 1       | 15,972.9 ns | 23,987.74 ns | 1,314.85 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    231.1 ns |     63.18 ns |     3.46 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 8       | 16,507.5 ns |    777.31 ns |    42.61 ns | 0.0610 |      - |     824 B |

Before, `PreErrorBufferGrowthBenchmarks`:

| Method     | HeldPerTrace | Mean        | Error    | StdDev   | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|---------:|---------:|-------:|-------:|----------:|
| HoldTraces | 3            |    705.9 ns | 176.0 ns |  9.65 ns | 0.0820 | 0.0811 |   1.01 KB |
| HoldTraces | 10           |  1,492.1 ns | 841.1 ns | 46.10 ns | 0.0820 | 0.0801 |   1.01 KB |
| HoldTraces | 100          | 11,786.7 ns | 332.8 ns | 18.24 ns | 0.0814 | 0.0610 |   1.01 KB |

After, `PreErrorBufferGrowthBenchmarks`:

| Method     | HeldPerTrace | Mean        | Error      | StdDev   | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|-----------:|---------:|-------:|-------:|----------:|
| HoldTraces | 3            |    588.4 ns | 1,567.6 ns | 85.93 ns | 0.0172 | 0.0057 |     224 B |
| HoldTraces | 10           |  1,687.8 ns |   934.2 ns | 51.20 ns | 0.0362 | 0.0172 |     464 B |
| HoldTraces | 100          | 14,343.3 ns |   869.1 ns | 47.64 ns | 0.1628 | 0.1356 |    2104 B |

Before, `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   240.1 ns |    22.61 ns |  1.24 ns |  1.00 |    0.01 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   329.5 ns |     6.75 ns |  0.37 ns |  1.37 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   339.8 ns | 1,009.23 ns | 55.32 ns |  1.42 |    0.20 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   824.2 ns |   115.24 ns |  6.32 ns |  3.43 |    0.03 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   698.2 ns |   483.52 ns | 26.50 ns |  2.91 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   253.4 ns |   258.61 ns | 14.18 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   331.9 ns |   279.08 ns | 15.30 ns |  1.31 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   377.4 ns |   424.62 ns | 23.27 ns |  1.49 |    0.11 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   918.7 ns |   568.32 ns | 31.15 ns |  3.63 |    0.20 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   778.0 ns |   169.53 ns |  9.29 ns |  3.08 |    0.15 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   182.8 ns |   726.51 ns | 39.82 ns |  1.03 |    0.27 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   338.2 ns |    83.81 ns |  4.59 ns |  1.91 |    0.34 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   387.4 ns |    70.96 ns |  3.89 ns |  2.18 |    0.39 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,087.8 ns |   787.19 ns | 43.15 ns |  6.13 |    1.11 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   961.2 ns |   502.80 ns | 27.56 ns |  5.42 |    0.97 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   254.2 ns |    37.31 ns |  2.05 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   362.5 ns |   341.66 ns | 18.73 ns |  1.43 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   393.8 ns |    68.62 ns |  3.76 ns |  1.55 |    0.02 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,206.2 ns | 1,029.98 ns | 56.46 ns |  4.74 |    0.20 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,059.7 ns |   104.46 ns |  5.73 ns |  4.17 |    0.03 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   253.7 ns |   260.68 ns | 14.29 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   311.6 ns |     9.34 ns |  0.51 ns |  1.23 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   358.2 ns |     7.70 ns |  0.42 ns |  1.41 |    0.07 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   845.7 ns |   462.49 ns | 25.35 ns |  3.34 |    0.19 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   715.9 ns |   248.34 ns | 13.61 ns |  2.83 |    0.15 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   239.8 ns |     8.94 ns |  0.49 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   330.4 ns |    87.47 ns |  4.79 ns |  1.38 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   362.0 ns |    17.42 ns |  0.95 ns |  1.51 |    0.00 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   941.3 ns |   306.87 ns | 16.82 ns |  3.93 |    0.06 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   770.9 ns |   128.35 ns |  7.04 ns |  3.21 |    0.03 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   243.7 ns |   137.66 ns |  7.55 ns |  1.00 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   674.8 ns |    62.95 ns |  3.45 ns |  2.77 |    0.07 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   739.9 ns |   107.97 ns |  5.92 ns |  3.04 |    0.08 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   858.4 ns |   156.22 ns |  8.56 ns |  3.52 |    0.10 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   720.5 ns |   347.97 ns | 19.07 ns |  2.96 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   250.0 ns |    58.86 ns |  3.23 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   762.5 ns |   207.90 ns | 11.40 ns |  3.05 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   776.7 ns |   363.14 ns | 19.90 ns |  3.11 |    0.08 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   896.0 ns |   341.33 ns | 18.71 ns |  3.58 |    0.08 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   785.8 ns |   204.44 ns | 11.21 ns |  3.14 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |

After, `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   231.7 ns | 244.28 ns | 13.39 ns |  1.00 |    0.07 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   314.6 ns | 655.15 ns | 35.91 ns |  1.36 |    0.15 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   355.1 ns |   7.37 ns |  0.40 ns |  1.54 |    0.08 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   898.0 ns | 162.69 ns |  8.92 ns |  3.88 |    0.20 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   727.8 ns | 838.75 ns | 45.97 ns |  3.15 |    0.24 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   261.8 ns | 312.90 ns | 17.15 ns |  1.00 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   320.6 ns | 397.18 ns | 21.77 ns |  1.23 |    0.10 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   366.6 ns | 323.68 ns | 17.74 ns |  1.40 |    0.10 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   998.6 ns | 265.57 ns | 14.56 ns |  3.83 |    0.22 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   764.2 ns | 321.97 ns | 17.65 ns |  2.93 |    0.17 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   237.1 ns |  32.86 ns |  1.80 ns |  1.00 |    0.01 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   351.4 ns | 188.92 ns | 10.36 ns |  1.48 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   404.6 ns | 234.86 ns | 12.87 ns |  1.71 |    0.05 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,176.3 ns | 266.88 ns | 14.63 ns |  4.96 |    0.06 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   958.0 ns | 344.15 ns | 18.86 ns |  4.04 |    0.07 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   243.8 ns |  30.69 ns |  1.68 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   371.6 ns |   5.46 ns |  0.30 ns |  1.52 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   391.4 ns |  33.01 ns |  1.81 ns |  1.61 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,418.7 ns | 269.83 ns | 14.79 ns |  5.82 |    0.06 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,098.6 ns |  68.05 ns |  3.73 ns |  4.51 |    0.03 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   240.9 ns |  27.84 ns |  1.53 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   311.8 ns | 197.25 ns | 10.81 ns |  1.29 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   331.8 ns | 454.93 ns | 24.94 ns |  1.38 |    0.09 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   906.5 ns | 259.77 ns | 14.24 ns |  3.76 |    0.06 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   707.2 ns | 131.01 ns |  7.18 ns |  2.94 |    0.03 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   262.3 ns | 251.62 ns | 13.79 ns |  1.00 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   340.7 ns | 251.87 ns | 13.81 ns |  1.30 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   370.2 ns |  80.82 ns |  4.43 ns |  1.41 |    0.06 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   975.6 ns | 664.12 ns | 36.40 ns |  3.73 |    0.20 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   806.1 ns | 390.40 ns | 21.40 ns |  3.08 |    0.15 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   236.1 ns | 121.90 ns |  6.68 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   681.6 ns | 243.07 ns | 13.32 ns |  2.89 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   693.6 ns | 344.48 ns | 18.88 ns |  2.94 |    0.10 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   848.2 ns | 275.40 ns | 15.10 ns |  3.59 |    0.11 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   704.9 ns | 221.71 ns | 12.15 ns |  2.99 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   243.6 ns |  12.87 ns |  0.71 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   803.0 ns | 287.97 ns | 15.78 ns |  3.30 |    0.06 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   820.5 ns |  81.33 ns |  4.46 ns |  3.37 |    0.02 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   976.6 ns | 180.20 ns |  9.88 ns |  4.01 |    0.04 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   764.0 ns |  58.53 ns |  3.21 ns |  3.14 |    0.01 | 0.1135 |      - |    1432 B |        3.38 |
