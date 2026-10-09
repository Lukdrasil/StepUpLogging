# Results: the hold cost of the pre-error buffer

What [ADR 0027](../adr/0027-prebuffer-striped-lru-and-on-demand-ring.md)'s amendment of 2026-10-09 changed,
measured: a trace's stripe is now chosen from the last 4 characters of its id instead of a hash of all 32, and a
trace's ring grows 4, 16, then its capacity instead of doubling. These are the two regressions that
[results-final.md](results-final.md) recorded on the pre-error buffer (`FillAndFlushOnError`, `InformationHeld`,
`Hold` with one trace and 8 threads, and `HoldTraces` at 100 events held).

- Before: commit `a5fe20b`, the branch this one is stacked on. A **detached worktree of `a5fe20b` was built with
  the after commit's `PreErrorBufferGrowthBenchmarks.cs` copied over it** (the file has two more rows, 16 and
  17 events held). `src/` is untouched and is the code before the change, so the before rows are the old sink
  measured by the new benchmark. `PreErrorBufferSinkBenchmarks` and `PipelineBenchmarks` are the same files in
  both. The worktree was removed afterwards.
- After: commit `5112323` (`StripeOf` over the key tail, the 4, 16, capacity ring and the new benchmark rows; the
  commits after it on this branch touch documentation only).
- Date: 2026-10-09, both runs on one machine, the before run first and the after run straight after it.
- Machine state: a laptop on AC power with the CPU governor on `powersave` and a desktop session running, as in
  [baseline.md](baseline.md).
- Benchmark classes run: `PreErrorBufferSinkBenchmarks`, `PreErrorBufferGrowthBenchmarks` and
  `PipelineBenchmarks`.
- One command for both runs, from the repository root (the before run from the temporary worktree):

```bash
# before, at a5fe20b with the benchmark file of the after commit
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --job short --exporters github json \
  --filter "*PreErrorBufferSinkBenchmarks*" "*PreErrorBufferGrowthBenchmarks*" "*PipelineBenchmarks*"

# after, at 5112323: the same command, the same flags
```

```
BenchmarkDotNet v0.15.8, Linux Omarchy
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
12th Gen Intel Core i7-12700H 0.40GHz, 1 CPU, 20 logical and 14 physical cores
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
.NET SDK 10.0.400
```

## How much to trust these

`--job short` runs one launch of three warmup and three measured iterations, as [baseline.md](baseline.md)
describes. A `Mean` is read together with its `Error`, and a difference under 25 % between two rows counts as
unresolved. By that rule **no time row below is resolved**: the only row past 25 % (-30 %,
`FillAndFlushOnError`, 256 contexts, 8 threads) has an after `Error` of 35.2 us on a mean of 11.9 us.
What is resolved is the allocation column, which is deterministic and agrees between runs. Where the time rows
all move the same way (the six `FillAndFlushOnError` rows, the single-thread `Hold` rows) the direction is
reported as the direction, not as a measured size.

The sink benchmark's `Hold` is reported per hold and its threads share the holds, so the time column is wall
time per hold across all threads: holds per second is `1e9 / Mean` whatever the thread count.

## Holding under contention (`PreErrorBufferSinkBenchmarks.Hold`)

Mean time in ns and allocated bytes per hold. Capacity per trace 100, at most 1024 traces.

| Traces | Threads | Before | After | Change |
|---|---|---|---|---|
| 1 | 1 | 150.9 ns, 0 B | 117.5 ns, 0 B | -22 %, unresolved |
| 1 | 8 | 289.6 ns, 0 B | 241.5 ns, 0 B | -17 %, unresolved |
| 256 | 1 | 191.4 ns, 0 B | 144.0 ns, 0 B | -25 %, unresolved (edge of the rule) |
| 256 | 8 | 102.5 ns, 0 B | 92.6 ns, 0 B | -10 %, unresolved |
| 4096 | 1 | 378.1 ns, 224 B | 333.1 ns, 224 B | -12 %, unresolved |
| 4096 | 8 | 230.6 ns, 224 B | 217.7 ns, 224 B | -6 %, unresolved |

What the table shows:

- All six rows are faster, and the three single-thread rows (1, 256 and 4096 traces) fall by 33 ns, 47 ns
  and 45 ns. That is the direction the change predicts: the hash of the 32
  character id that picked the stripe, and that the dictionary then computed again, is gone. The size is
  larger than the 15 to 20 ns that [results-prebuffer.md](results-prebuffer.md) estimated, and the `Error`
  columns (up to 466 ns) are wider than the effect, so I do not claim a size. **Hypothesis**: the rest is run
  to run drift; not separated.
- The one-trace, eight-thread row, the one [results-final.md](results-final.md) listed as +22 % against the
  baseline, is 289.6 ns before and 241.5 ns after. The code that was blamed (a hash per hold, spent inside the
  trace's lock and paid by the threads that wait for it) is shorter, and the row moves the same way as the
  one-thread row of the same trace (-22 %), but it is unresolved by the rule. The 8 threads still queue on the
  one trace's buffer lock, which this change does not touch.
- The allocation column is the same in both runs: 0 B with 1 and 256 traces, 224 B with 4096.

### `FillAndFlushOnError`

A cycle is 100 holds in one trace followed by an Error that flushes them. It ignores both parameters, so its
six rows are one measurement of the same code; their spread is the noise.

| | Before | After |
|---|---|---|
| Range of the six rows | 14.9 to 17.1 us | 11.9 to 14.5 us |
| Rows 1 / 1, 1 / 8, 256 / 1, 256 / 8, 4096 / 1, 4096 / 8 | 15.6, 15.7, 16.6, 17.1, 14.9, 15.8 us | 12.3, 12.8, 13.5, 11.9, 13.9, 14.5 us |
| Change per row | | -21 %, -19 %, -18 %, -30 %, -7 %, -9 % |
| Allocated | 824 B | 824 B |

Every after row is below every before row (the highest after row, 14.5 us, is under the lowest before row,
14.9 us). Each row alone is inside the 25 % rule or has an `Error` as wide as its `Mean` (the 256 / 8
after row: 11.9 us, error 35.2 us; the 4096 / 1 before row: 14.9 us, error 57.5 us), but six rows of the same
code agreeing on direction is evidence the cost fell. It is the 100 holds of the cycle that carry it: 33 to
47 ns saved over 100 holds is 3.3 to 4.7 us, and the rows fell by 1.0 to 5.2 us. The ring is already grown to
100 slots after the first cycle, so the ring is not what changed; the flush is still one 100-reference snapshot
(824 B).

## A trace's ring grows 4, 16, capacity (`PreErrorBufferGrowthBenchmarks`)

4096 traces pass through a sink that keeps 1024; each holds `HeldPerTrace` events in a row. Reported per
trace, one thread, so every trace pays for its buffer and evicts another. The 16 and 17 rows are new: they sit
on both sides of the first growth step.

| Events held per trace | Before | After | Change |
|---|---|---|---|
| 3 | 646.2 ns, 224 B | 570.9 ns, 224 B | same bytes; time -12 %, unresolved |
| 10 | 1675.9 ns, 464 B | 1420.4 ns, 376 B | 1.2x fewer bytes; time -15 %, unresolved |
| 16 | 2479.4 ns, 464 B | 2076.6 ns, 376 B | 1.2x fewer bytes; time -16 %, unresolved |
| 17 | 2764.6 ns, 744 B | 2392.8 ns, 1200 B | 1.6x **more** bytes; time -13 %, unresolved |
| 100 | 12234.3 ns, 2104 B | 11757.2 ns, 1200 B | 1.8x fewer bytes; time -4 %, unresolved (before `Error` 52.3 us) |

The bytes are exact and show the trade. The array sizes are 56 B for 4 slots, 88 B for 8, 152 B for 16, 280 B
for 32, 536 B for 64 and 824 B for 100, and 168 B of a trace's buffer do not depend on the ring (the buffer,
its lock and the bookkeeping nodes). A ring that grew 4, 8, 16, 32, 64, 100 therefore cost 168 B plus the arrays
it passed through; the new ring costs 168 B plus 56, 152 and 824 B at most:

| Events held | Before | After | |
|---|---|---|---|
| 1 to 4 | 224 B | 224 B | same |
| 5 to 8 | 312 B | 376 B | 64 B worse (arithmetic) |
| 9 to 16 | 464 B | 376 B | 88 B better; 10 and 16 measured |
| 17 to 32 | 744 B | 1200 B | 456 B worse; 17 measured |
| 33 to 64 | 1280 B | 1200 B | better (arithmetic) |
| 65 to 100 | 2104 B | 1200 B | better; 100 measured |

The 17 to 32 band and, by a smaller 64 B, the 5 to 8 band are the price of skipping the middle steps: a trace
that crosses 16 events allocates the whole 100-slot array at once. The 17-event row is the worst case, and its
time does not show it (2764.6 ns before, 2392.8 ns after), but its collections per 1000 operations rise
(Gen0 from 0.0572 to 0.0954, Gen1 from 0.0267 to 0.0916), which is the garbage collector paying for the
bigger array. At 100 events the same counts fall (Gen0 0.1628 to 0.0888, Gen1 0.1356 to 0.0666). The 5 to 8 and
33 to 64 rows are arithmetic from the array sizes, not measured rows. A host whose requests mostly log 17 to 32
events held pays more per trace than before; a host whose requests log 3 events, or 33 or more, pays the same or
less.

Setup checks, in both runs: the sink keeps exactly 1024 traces after the 4096, and an Error on the last trace
flushes exactly `HeldPerTrace` events.

## `PipelineBenchmarks`: a held event end to end

`InformationHeld` is the pipeline row that reaches `PreErrorBufferSink.Hold`. Its trace never changes (the key
is the one Activity's trace id, or the global key without an Activity), so it measures the uncontended
single-thread path.

| Scenario | Act | Before | After | Change |
|---|---|---|---|---|
| Default | no | 862.5 ns, 1360 B | 854.2 ns, 1360 B | -1 % |
| Default | yes | 1001.8 ns, 1432 B | 905.3 ns, 1432 B | -10 % |
| Redaction | no | 1194.7 ns, 1416 B | 1183.9 ns, 1416 B | -1 % |
| Redaction | yes | 1289.0 ns, 1488 B | 1267.3 ns, 1488 B | -2 % |
| FloorsAndNeverStepUp | no | 881.0 ns, 1360 B | 823.8 ns, 1360 B | -6 % |
| FloorsAndNeverStepUp | yes | 930.5 ns, 1432 B | 1028.8 ns, 1432 B | +11 % |
| ConsumerRootSink | no | 928.3 ns, 1360 B | 890.6 ns, 1360 B | -4 % |
| ConsumerRootSink | yes | 935.2 ns, 1432 B | 907.0 ns, 1432 B | -3 % |

The bytes are identical. Seven of eight rows are 1 % to 10 % faster and one is 11 % slower, all inside the
25 % rule and inside the `Error` of most rows (up to 1176 ns), so I do not resolve a change. A hold that is 33
to 47 ns faster would show as 3 % to 5 % of an 800 to 1400 ns event, which is the size of the movement and
cannot be told from the run to run spread here. [results-final.md](results-final.md) listed `InformationHeld`
as +3 % to +16 % against the baseline; this run compares against `a5fe20b`, so it does not restate that figure.

## The raw tables

Before (`a5fe20b` with the benchmark file of the after commit), `PreErrorBufferSinkBenchmarks`:


| Method              | Contexts | Threads | Mean        | Error        | StdDev      | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|------------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    150.9 ns |    249.89 ns |    13.70 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 15,578.5 ns |  2,231.46 ns |   122.31 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    289.6 ns |    176.18 ns |     9.66 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 15,700.4 ns |  8,190.59 ns |   448.95 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    191.4 ns |     51.72 ns |     2.83 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 16,620.5 ns |  2,889.11 ns |   158.36 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |    102.5 ns |     75.21 ns |     4.12 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 17,112.0 ns |  4,828.55 ns |   264.67 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    378.1 ns |     76.90 ns |     4.22 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 1       | 14,909.7 ns | 57,518.72 ns | 3,152.79 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    230.6 ns |    227.35 ns |    12.46 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 8       | 15,845.5 ns | 29,260.27 ns | 1,603.85 ns | 0.0610 |      - |     824 B |

After (`5112323`), `PreErrorBufferSinkBenchmarks`:

| Method              | Contexts | Threads | Mean         | Error        | StdDev       | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |-------------:|-------------:|-------------:|-------:|-------:|----------:|
| Hold                | 1        | 1       |    117.46 ns |    177.20 ns |     9.713 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 1       | 12,269.18 ns |  8,555.27 ns |   468.943 ns | 0.0610 |      - |     824 B |
| Hold                | 1        | 8       |    241.47 ns |     66.46 ns |     3.643 ns |      - |      - |         - |
| FillAndFlushOnError | 1        | 8       | 12,760.78 ns |  1,136.70 ns |    62.306 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 1       |    144.04 ns |    286.75 ns |    15.718 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 1       | 13,546.16 ns |  4,733.90 ns |   259.481 ns | 0.0610 |      - |     824 B |
| Hold                | 256      | 8       |     92.63 ns |     24.28 ns |     1.331 ns |      - |      - |         - |
| FillAndFlushOnError | 256      | 8       | 11,912.00 ns | 35,194.11 ns | 1,929.108 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 1       |    333.10 ns |    465.75 ns |    25.529 ns | 0.0176 | 0.0057 |     224 B |
| FillAndFlushOnError | 4096     | 1       | 13,861.10 ns |  1,541.84 ns |    84.513 ns | 0.0610 |      - |     824 B |
| Hold                | 4096     | 8       |    217.69 ns |    130.78 ns |     7.169 ns | 0.0179 | 0.0060 |     224 B |
| FillAndFlushOnError | 4096     | 8       | 14,471.26 ns |    923.88 ns |    50.641 ns | 0.0610 |      - |     824 B |

Before, `PreErrorBufferGrowthBenchmarks`:

| Method     | HeldPerTrace | Mean        | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|------------:|------------:|-------:|-------:|----------:|
| HoldTraces | 3            |    646.2 ns |    109.8 ns |     6.02 ns | 0.0172 | 0.0057 |     224 B |
| HoldTraces | 10           |  1,675.9 ns |    951.1 ns |    52.13 ns | 0.0362 | 0.0172 |     464 B |
| HoldTraces | 16           |  2,479.4 ns |  1,296.7 ns |    71.07 ns | 0.0343 | 0.0153 |     464 B |
| HoldTraces | 17           |  2,764.6 ns |    200.2 ns |    10.97 ns | 0.0572 | 0.0267 |     744 B |
| HoldTraces | 100          | 12,234.3 ns | 52,264.4 ns | 2,864.79 ns | 0.1628 | 0.1356 |    2104 B |

After, `PreErrorBufferGrowthBenchmarks`:

| Method     | HeldPerTrace | Mean        | Error      | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------- |------------:|-----------:|----------:|-------:|-------:|----------:|
| HoldTraces | 3            |    570.9 ns |   114.1 ns |   6.25 ns | 0.0172 | 0.0057 |     224 B |
| HoldTraces | 10           |  1,420.4 ns |   392.5 ns |  21.51 ns | 0.0286 | 0.0134 |     376 B |
| HoldTraces | 16           |  2,076.6 ns |   156.8 ns |   8.59 ns | 0.0267 | 0.0114 |     376 B |
| HoldTraces | 17           |  2,392.8 ns |   443.2 ns |  24.29 ns | 0.0954 | 0.0916 |    1200 B |
| HoldTraces | 100          | 11,757.2 ns | 5,508.8 ns | 301.96 ns | 0.0888 | 0.0666 |    1200 B |

Before, `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   231.8 ns |   242.35 ns | 13.28 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   316.5 ns |    90.22 ns |  4.95 ns |  1.37 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   314.7 ns |   636.14 ns | 34.87 ns |  1.36 |    0.15 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   862.5 ns |   162.35 ns |  8.90 ns |  3.73 |    0.19 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   697.8 ns |    91.08 ns |  4.99 ns |  3.02 |    0.16 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   245.3 ns |    15.22 ns |  0.83 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   312.5 ns |   956.11 ns | 52.41 ns |  1.27 |    0.19 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   371.8 ns |    93.26 ns |  5.11 ns |  1.52 |    0.02 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       | 1,001.8 ns |   101.20 ns |  5.55 ns |  4.08 |    0.02 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   773.3 ns |   118.98 ns |  6.52 ns |  3.15 |    0.02 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   242.8 ns |   118.45 ns |  6.49 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   340.3 ns |    22.82 ns |  1.25 ns |  1.40 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   400.7 ns |   176.43 ns |  9.67 ns |  1.65 |    0.05 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,194.7 ns |   917.17 ns | 50.27 ns |  4.92 |    0.21 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   910.4 ns |   327.97 ns | 17.98 ns |  3.75 |    0.11 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   247.2 ns |    10.88 ns |  0.60 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   299.0 ns | 1,140.05 ns | 62.49 ns |  1.21 |    0.22 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   395.9 ns |    64.74 ns |  3.55 ns |  1.60 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,289.0 ns |    70.53 ns |  3.87 ns |  5.21 |    0.02 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,055.1 ns |   176.30 ns |  9.66 ns |  4.27 |    0.04 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   240.6 ns |    87.04 ns |  4.77 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   316.9 ns |     3.85 ns |  0.21 ns |  1.32 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   369.4 ns |   127.31 ns |  6.98 ns |  1.54 |    0.04 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   881.0 ns |   391.81 ns | 21.48 ns |  3.66 |    0.10 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   731.8 ns |   129.37 ns |  7.09 ns |  3.04 |    0.06 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   246.0 ns |    77.03 ns |  4.22 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   332.4 ns |    49.08 ns |  2.69 ns |  1.35 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   374.6 ns |   270.98 ns | 14.85 ns |  1.52 |    0.06 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   930.5 ns |    20.85 ns |  1.14 ns |  3.78 |    0.06 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   809.5 ns |   826.70 ns | 45.31 ns |  3.29 |    0.17 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   246.8 ns |   160.63 ns |  8.80 ns |  1.00 |    0.04 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   676.6 ns |    37.19 ns |  2.04 ns |  2.74 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   730.3 ns |   324.55 ns | 17.79 ns |  2.96 |    0.11 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   928.3 ns |   107.61 ns |  5.90 ns |  3.76 |    0.12 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   696.9 ns |   151.45 ns |  8.30 ns |  2.83 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   244.7 ns |    74.60 ns |  4.09 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   755.7 ns | 1,066.38 ns | 58.45 ns |  3.09 |    0.21 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   793.3 ns |   160.75 ns |  8.81 ns |  3.24 |    0.06 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   935.2 ns | 1,039.29 ns | 56.97 ns |  3.82 |    0.21 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   768.7 ns |   107.91 ns |  5.91 ns |  3.14 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |

After, `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   238.2 ns |   185.54 ns | 10.17 ns |  1.00 |    0.05 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   318.6 ns |   126.53 ns |  6.94 ns |  1.34 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   364.0 ns |    17.94 ns |  0.98 ns |  1.53 |    0.06 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   854.2 ns |   294.63 ns | 16.15 ns |  3.59 |    0.15 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   681.9 ns |   385.52 ns | 21.13 ns |  2.87 |    0.13 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   247.2 ns |    84.64 ns |  4.64 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   326.0 ns |    33.24 ns |  1.82 ns |  1.32 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   382.3 ns |   114.27 ns |  6.26 ns |  1.55 |    0.03 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   905.3 ns | 1,176.02 ns | 64.46 ns |  3.66 |    0.23 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   785.4 ns |   235.18 ns | 12.89 ns |  3.18 |    0.07 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   238.7 ns |     6.29 ns |  0.34 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   333.8 ns |   100.76 ns |  5.52 ns |  1.40 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   363.3 ns |   381.94 ns | 20.94 ns |  1.52 |    0.08 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,183.9 ns |    72.12 ns |  3.95 ns |  4.96 |    0.02 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   932.5 ns |   587.99 ns | 32.23 ns |  3.91 |    0.12 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   246.2 ns |    93.70 ns |  5.14 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   366.6 ns |   129.17 ns |  7.08 ns |  1.49 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   390.0 ns |    11.47 ns |  0.63 ns |  1.58 |    0.03 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,267.3 ns |   345.94 ns | 18.96 ns |  5.15 |    0.11 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,038.0 ns |   165.41 ns |  9.07 ns |  4.22 |    0.08 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   241.0 ns |    15.28 ns |  0.84 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   317.2 ns |   327.58 ns | 17.96 ns |  1.32 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   385.6 ns |   403.28 ns | 22.11 ns |  1.60 |    0.08 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   823.8 ns |   635.04 ns | 34.81 ns |  3.42 |    0.13 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   707.8 ns |    99.51 ns |  5.45 ns |  2.94 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   235.3 ns |   148.81 ns |  8.16 ns |  1.00 |    0.04 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   322.1 ns |    77.52 ns |  4.25 ns |  1.37 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   368.8 ns |   373.61 ns | 20.48 ns |  1.57 |    0.09 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       | 1,028.8 ns | 1,150.13 ns | 63.04 ns |  4.38 |    0.27 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   796.5 ns |   511.66 ns | 28.05 ns |  3.39 |    0.14 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   207.6 ns |   279.12 ns | 15.30 ns |  1.00 |    0.09 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   691.3 ns |    19.40 ns |  1.06 ns |  3.34 |    0.22 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   755.9 ns |   611.77 ns | 33.53 ns |  3.65 |    0.28 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   890.6 ns |   197.60 ns | 10.83 ns |  4.31 |    0.29 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   702.1 ns |    89.51 ns |  4.91 ns |  3.39 |    0.23 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   234.3 ns |   289.52 ns | 15.87 ns |  1.00 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   752.2 ns |    67.94 ns |  3.72 ns |  3.22 |    0.20 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   824.9 ns |   375.39 ns | 20.58 ns |  3.53 |    0.23 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   907.0 ns |   900.07 ns | 49.34 ns |  3.88 |    0.30 | 0.1125 | 0.0267 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   761.2 ns |    24.24 ns |  1.33 ns |  3.26 |    0.20 | 0.1135 |      - |    1432 B |        3.38 |
