# Results: the root enrichment gate

What [ADR 0026](../adr/0026-root-enrichment-gate.md) changed, measured: the pipeline benchmarks before and
after the gate, and the ladder that attributes the cost of a dropped event. Follow-ups 1 and 2 of
[analysis.md](analysis.md) are recorded here.

- Before: commit `a3cb0d0` (`src/` as in `61d192a`, no gate), `PipelineBenchmarks` only. `ConsumerRootSink`
  and `MelDebugDropped` did not exist at that commit, so they have no before row.
- After: commit `4a81f58` (the gate with `FromLogContext` ungated and first, `DroppedPathBenchmarks`, the
  `ConsumerRootSink` scenario and the `MelDebugDropped` row). The benchmarked code and benchmark classes are
  those of that commit; the commits after it on this branch touch documentation only.
- Date: 2026-10-08. The two runs were made on the same machine, the before run first.
  An earlier after run, at `12340e4`, was discarded: `64d3ffb` changed `DroppedPathBenchmarks.Setup` and
  the review fix moved `FromLogContext` back ahead of the always-export enricher, so it did not describe this code.
- Machine state: laptop on AC power, CPU governor `powersave`, desktop session running; the same state as
  [baseline.md](baseline.md).
- Commands, from the repository root (the before run from a temporary checkout of `a3cb0d0`):

```bash
# before, at a3cb0d0
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" --job short --exporters github

# after, at 4a81f58
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" "*DroppedPathBenchmarks*" --job short --exporters github
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
Two runs of the same row moved by tens of percent in the baseline, so read the `Mean` of a row together
with its `StdDev`, and treat a difference under 25 % between two rows as unresolved. The differences
below that carry a claim are 1.7x to 2.5x in time and 3.0x to 3.4x in bytes; the allocation column is
deterministic and agrees exactly between runs. Examples of the noise: `DebugDropped` with an `Activity`
and no gate measured 736 ns in the baseline and 909 ns in the before run here; `WarningExported` in
`Default` without an `Activity` shows 1021 ns after against 684 ns before, with an `Error` wider than its
`Mean`, and the plain Serilog row of that scenario shows 214.5 ns with an `Error` of 637 ns.

## Before and after

Mean time in ns and allocated bytes per call. `Act` is `InActivity`. Rows the gate does not touch are in
the table to show they did not move.

| Scenario | Act | DebugDropped before | DebugDropped after | InformationHeld before | InformationHeld after | WarningExported before | WarningExported after |
|---|---|---|---|---|---|---|---|
| Default | no | 668 ns, 1360 B | 343 ns, 424 B | 853 ns, 1360 B | 833 ns, 1360 B | 684 ns, 1360 B | 1021 ns, 1360 B |
| Default | yes | 909 ns, 1432 B | 362 ns, 424 B | 907 ns, 1432 B | 870 ns, 1432 B | 739 ns, 1432 B | 751 ns, 1432 B |
| Redaction | no | 1209 ns, 1512 B | 659 ns, 496 B | 1536 ns, 1512 B | 1572 ns, 1512 B | 1327 ns, 1512 B | 1280 ns, 1512 B |
| Redaction | yes | 1406 ns, 1600 B | 828 ns, 496 B | 1693 ns, 1600 B | 1608 ns, 1600 B | 1476 ns, 1600 B | 1492 ns, 1600 B |
| FloorsAndNeverStepUp | no | 686 ns, 1360 B | 364 ns, 424 B | 871 ns, 1360 B | 862 ns, 1360 B | 710 ns, 1360 B | 699 ns, 1360 B |
| FloorsAndNeverStepUp | yes | 749 ns, 1432 B | 385 ns, 424 B | 929 ns, 1432 B | 853 ns, 1432 B | 824 ns, 1432 B | 800 ns, 1432 B |

The plain Serilog row is 236 to 252 ns and 424 B in every scenario of the before run, and 214.5 to 259.2 ns and 424 B in every scenario of the after run.

What the table shows:

- A dropped Debug event now allocates 424 B in the `Default` and `FloorsAndNeverStepUp` scenarios, with or
  without an `Activity`: the same bytes as the plain Serilog logger, against 1360 B or 1432 B before. In
  time it is 343 ns against 668 ns without an `Activity` and 362 ns against 909 ns with one.
- With redaction on, a dropped Debug event is 659 ns and 496 B against 1209 ns and 1512 B without an
  `Activity` (828 ns and 496 B against 1406 ns and 1600 B with one). The 72 B and most of the 316 ns
  (466 ns with an `Activity`) above the `Default` row are `RedactionEnricher`, which the gate does not skip
  (ADR 0022 D5; follow-up 3 of [analysis.md](analysis.md)). That attribution is by elimination, not
  measured by a row, and the `Redaction` rows have `Error` values as wide as their `Mean`.
- `InformationHeld` and `WarningExported` are at or above the floor, so the gate lets them through: their
  bytes are unchanged and their times agree with the before run within the noise, except `WarningExported`
  in `Default` without an `Activity` (1021 ns against 684 ns, `Error` wider than the `Mean`).

### A consumer root sink turns the gate off

`ConsumerRootSink` adds a `NullLogEventSink` through the `configure` hook, which keeps every root enricher
running (ADR 0026 D2). It is measured in the after run only; it is the cost of a dropped event without the gate.

| Row | Act | ConsumerRootSink | Default (gated) |
|---|---|---|---|
| DebugDropped | no | 691 ns, 1360 B | 343 ns, 424 B |
| DebugDropped | yes | 768 ns, 1432 B | 362 ns, 424 B |

It matches the before run of `Default` (668 ns, 1360 B; 909 ns, 1432 B) within the noise.

### Through Microsoft.Extensions.Logging

`MelDebugDropped` is `ILoggerFactory.CreateLogger("MyApp.Orders.OrderHandler").LogDebug(...)`, the call an
application makes, in the same category as the other rows. After the change only.

| Scenario | Act | MelDebugDropped | DebugDropped (Serilog) |
|---|---|---|---|
| Default | no | 389 ns, 408 B | 343 ns, 424 B |
| Default | yes | 390 ns, 408 B | 362 ns, 424 B |
| Redaction | no | 862 ns, 480 B | 659 ns, 496 B |
| FloorsAndNeverStepUp | no | 391 ns, 408 B | 364 ns, 424 B |
| ConsumerRootSink | no | 741 ns, 1344 B | 691 ns, 1360 B |

## Where the cost of a dropped event goes (`DroppedPathBenchmarks`)

Each row is a Serilog logger one stage longer than the row above it, in the order the root runs them, with
every root enricher applied to every event (no gate in these loggers). A row minus the row above it is that
stage's cost for a Debug event. The sink rows use the library's own sinks over a null bypass logger.

| Stage added | No activity | In an activity |
|---|---|---|
| Plain (baseline) | 243 ns, 424 B | 251 ns, 424 B |
| `FromLogContext` | +3 ns, +0 B | +7 ns, +0 B |
| Trace id and span id enrichers | +156 ns, +360 B | +192 ns, +408 B |
| `ActivityContextEnricher` | +16 ns, +0 B | +30 ns, +24 B |
| `Application`, `Environment`, `MachineName` | +176 ns, +576 B | +165 ns, +576 B |
| `WithExceptionDetails` | +25 ns, +0 B | +15 ns, +0 B |
| `StepUpSink` (with the buffer wired in) | -4 ns, +0 B | +28 ns, +0 B |
| `PreErrorBufferSink` | -8 ns, +0 B | +10 ns, +0 B |
| `StepUpTriggerSink` | +17 ns, +0 B | -7 ns, +0 B |
| `SummarySink` | +31 ns, +0 B | +30 ns, +0 B |
| `ImmediateSink` | +3 ns, +0 B | -6 ns, +0 B |
| Whole root pipeline | 657 ns, 1360 B | 713 ns, 1432 B |

The whole pipeline minus the baseline is 414 ns and 936 B without an activity (463 ns and 1008 B with one),
against 429 ns and 936 B on the real host before the gate (667.6 ns minus 238.7 ns, the before run). The
ladder accounts for the 936 B.

The steps of about 15 ns and below, and the negative ones, are inside the noise of a three-iteration run
(the `Error` of most rows is wider). What is resolved: the enrichers cost most of the time and all of the
bytes (the trace and span ids and the three properties add 936 B and about 330 ns of the 414 ns), and the
five root sinks together add about 40 ns and no bytes to a dropped event (the sum of the rows from
`StepUpSink` to `ImmediateSink`: 39 ns without an activity, 55 ns with one).

### The raw tables

Before (`a3cb0d0`), `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   238.7 ns |    46.31 ns |  2.54 ns |  1.00 |    0.01 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   667.6 ns |   166.20 ns |  9.11 ns |  2.80 |    0.04 | 0.1078 |      - |    1360 B |        3.21 |
| InformationHeld        | Default              | False      |   852.8 ns |   372.05 ns | 20.39 ns |  3.57 |    0.08 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   684.3 ns |    65.49 ns |  3.59 ns |  2.87 |    0.03 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   247.9 ns |   136.40 ns |  7.48 ns |  1.00 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   908.9 ns |    58.26 ns |  3.19 ns |  3.67 |    0.10 | 0.1135 |      - |    1432 B |        3.38 |
| InformationHeld        | Default              | True       |   907.4 ns |   121.15 ns |  6.64 ns |  3.66 |    0.10 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   738.7 ns |   501.70 ns | 27.50 ns |  2.98 |    0.12 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   239.1 ns |    19.22 ns |  1.05 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      | 1,208.5 ns | 1,742.43 ns | 95.51 ns |  5.05 |    0.35 | 0.1202 |      - |    1512 B |        3.57 |
| InformationHeld        | Redaction            | False      | 1,536.4 ns |   466.09 ns | 25.55 ns |  6.43 |    0.10 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,326.7 ns |   196.83 ns | 10.79 ns |  5.55 |    0.04 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   251.8 ns |   214.68 ns | 11.77 ns |  1.00 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       | 1,406.1 ns |    95.13 ns |  5.21 ns |  5.59 |    0.22 | 0.1259 |      - |    1600 B |        3.77 |
| InformationHeld        | Redaction            | True       | 1,692.9 ns |   529.02 ns | 29.00 ns |  6.73 |    0.28 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,476.0 ns |   260.69 ns | 14.29 ns |  5.87 |    0.24 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   235.9 ns |   144.57 ns |  7.92 ns |  1.00 |    0.04 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   686.2 ns |   140.19 ns |  7.68 ns |  2.91 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   870.8 ns |   147.26 ns |  8.07 ns |  3.69 |    0.11 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   709.9 ns |    78.83 ns |  4.32 ns |  3.01 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   248.0 ns |    37.89 ns |  2.08 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   748.7 ns |    92.39 ns |  5.06 ns |  3.02 |    0.03 | 0.1135 |      - |    1432 B |        3.38 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   928.7 ns |    61.91 ns |  3.39 ns |  3.74 |    0.03 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   824.0 ns |   851.56 ns | 46.68 ns |  3.32 |    0.16 | 0.1135 |      - |    1432 B |        3.38 |

After (`4a81f58`), `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   214.5 ns |   637.24 ns |  34.93 ns |  1.02 |    0.21 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   343.4 ns |    13.29 ns |   0.73 ns |  1.63 |    0.25 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   388.5 ns |   284.25 ns |  15.58 ns |  1.85 |    0.29 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   833.2 ns |   871.36 ns |  47.76 ns |  3.96 |    0.63 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      | 1,021.5 ns | 1,427.48 ns |  78.24 ns |  4.85 |    0.81 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   234.4 ns |   169.27 ns |   9.28 ns |  1.00 |    0.05 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   361.7 ns |   101.17 ns |   5.55 ns |  1.54 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   390.1 ns |    16.04 ns |   0.88 ns |  1.67 |    0.06 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   870.0 ns |   176.29 ns |   9.66 ns |  3.72 |    0.13 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   751.3 ns |   161.00 ns |   8.82 ns |  3.21 |    0.11 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   239.2 ns |    29.22 ns |   1.60 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   658.8 ns |   939.75 ns |  51.51 ns |  2.75 |    0.19 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | False      |   862.3 ns |   113.60 ns |   6.23 ns |  3.60 |    0.03 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | False      | 1,572.4 ns |   205.76 ns |  11.28 ns |  6.57 |    0.06 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,280.3 ns | 3,326.48 ns | 182.34 ns |  5.35 |    0.66 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   236.2 ns |   104.70 ns |   5.74 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   828.4 ns |   129.85 ns |   7.12 ns |  3.51 |    0.08 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | True       |   661.0 ns | 1,409.52 ns |  77.26 ns |  2.80 |    0.29 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | True       | 1,608.1 ns | 1,807.29 ns |  99.06 ns |  6.81 |    0.39 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,492.3 ns |   218.81 ns |  11.99 ns |  6.32 |    0.14 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   237.7 ns |    27.38 ns |   1.50 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   363.6 ns |    35.10 ns |   1.92 ns |  1.53 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   391.2 ns |   405.29 ns |  22.22 ns |  1.65 |    0.08 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   862.4 ns |   167.21 ns |   9.17 ns |  3.63 |    0.04 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   699.1 ns |   493.72 ns |  27.06 ns |  2.94 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   244.8 ns |    60.63 ns |   3.32 ns |  1.00 |    0.02 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   385.4 ns |   307.81 ns |  16.87 ns |  1.57 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   432.9 ns |   308.93 ns |  16.93 ns |  1.77 |    0.06 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   853.1 ns | 1,925.56 ns | 105.55 ns |  3.48 |    0.38 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   799.6 ns |   134.09 ns |   7.35 ns |  3.27 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   259.2 ns |   195.96 ns |  10.74 ns |  1.00 |    0.05 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   691.2 ns |   340.62 ns |  18.67 ns |  2.67 |    0.11 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   741.3 ns |    92.66 ns |   5.08 ns |  2.86 |    0.10 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   821.3 ns |   580.66 ns |  31.83 ns |  3.17 |    0.16 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   707.0 ns |   112.14 ns |   6.15 ns |  2.73 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   251.3 ns |   211.13 ns |  11.57 ns |  1.00 |    0.06 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   768.1 ns |   886.72 ns |  48.60 ns |  3.06 |    0.21 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   792.1 ns |   413.84 ns |  22.68 ns |  3.16 |    0.15 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   912.9 ns |   216.69 ns |  11.88 ns |  3.64 |    0.15 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   748.4 ns |     5.05 ns |   0.28 ns |  2.98 |    0.12 | 0.1135 |      - |    1432 B |        3.38 |

After (`4a81f58`), `DroppedPathBenchmarks`:

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 242.5 ns |  22.09 ns |  1.21 ns |  1.00 |    0.01 | 0.0334 |     424 B |        1.00 |
| LogContext       | False      | 245.2 ns | 204.11 ns | 11.19 ns |  1.01 |    0.04 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 401.5 ns |  52.36 ns |  2.87 ns |  1.66 |    0.01 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 417.5 ns | 117.62 ns |  6.45 ns |  1.72 |    0.02 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 593.0 ns | 160.91 ns |  8.82 ns |  2.45 |    0.03 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 617.8 ns | 220.97 ns | 12.11 ns |  2.55 |    0.04 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 614.2 ns |  76.45 ns |  4.19 ns |  2.53 |    0.02 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 606.4 ns | 292.90 ns | 16.06 ns |  2.50 |    0.06 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 623.1 ns |  87.51 ns |  4.80 ns |  2.57 |    0.02 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 654.2 ns |  83.50 ns |  4.58 ns |  2.70 |    0.02 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 656.9 ns | 300.52 ns | 16.47 ns |  2.71 |    0.06 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 250.5 ns |  78.07 ns |  4.28 ns |  1.00 |    0.02 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 257.1 ns | 105.82 ns |  5.80 ns |  1.03 |    0.03 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 449.0 ns | 178.51 ns |  9.78 ns |  1.79 |    0.04 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 478.7 ns | 142.26 ns |  7.80 ns |  1.91 |    0.04 | 0.0682 |     856 B |        2.02 |
| Properties       | True       | 643.7 ns |  74.83 ns |  4.10 ns |  2.57 |    0.04 | 0.1135 |    1432 B |        3.38 |
| ExceptionDetails | True       | 658.5 ns | 229.47 ns | 12.58 ns |  2.63 |    0.06 | 0.1135 |    1432 B |        3.38 |
| StepUpSink       | True       | 686.1 ns | 398.06 ns | 21.82 ns |  2.74 |    0.09 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 695.7 ns | 211.98 ns | 11.62 ns |  2.78 |    0.06 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 688.9 ns | 114.79 ns |  6.29 ns |  2.75 |    0.05 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 719.2 ns | 108.22 ns |  5.93 ns |  2.87 |    0.05 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 713.1 ns | 191.66 ns | 10.51 ns |  2.85 |    0.06 | 0.1135 |    1432 B |        3.38 |
