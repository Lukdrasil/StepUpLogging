# Results: the root enrichment gate

What [ADR 0026](../adr/0026-root-enrichment-gate.md) changed, measured: the pipeline benchmarks before and
after the gate, and the ladder that attributes the cost of a dropped event. Follow-ups 1 and 2 of
[analysis.md](analysis.md) are recorded here.

- Before: commit `a3cb0d0` (`src/` as in `61d192a`, no gate), `PipelineBenchmarks` only. `ConsumerRootSink`
  and `MelDebugDropped` did not exist at that commit, so they have no before row.
- After: commit `12340e4` (the gate, `DroppedPathBenchmarks`, the `ConsumerRootSink` scenario and the
  `MelDebugDropped` row; later commits on this branch touch documentation only).
- Date: 2026-10-08. The two runs were made on the same machine, the before run first.
- Machine state: laptop on AC power, CPU governor `powersave`, desktop session running; the same state as
  [baseline.md](baseline.md).
- Commands, from the repository root (the before run from a temporary checkout of `a3cb0d0`):

```bash
# before, at a3cb0d0
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" --job short --exporters github

# after, at 12340e4
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
below that carry a claim are 1.5x to 2.6x in time and 3.2x in bytes; the allocation column is
deterministic and agrees exactly between runs. Examples of the noise: `DebugDropped` with an `Activity`
and no gate measured 736 ns in the baseline and 909 ns in the before run here; `InformationHeld` in
`FloorsAndNeverStepUp` shows 1131 ns after against 871 ns before, with an `Error` wider than its `Mean`.

## Before and after

Mean time in ns and allocated bytes per call. `Act` is `InActivity`. Rows the gate does not touch are in
the table to show they did not move.

| Scenario | Act | DebugDropped before | DebugDropped after | InformationHeld before | InformationHeld after | WarningExported before | WarningExported after |
|---|---|---|---|---|---|---|---|
| Default | no | 668 ns, 1360 B | 369 ns, 424 B | 853 ns, 1360 B | 815 ns, 1360 B | 684 ns, 1360 B | 698 ns, 1360 B |
| Default | yes | 909 ns, 1432 B | 345 ns, 424 B | 907 ns, 1432 B | 915 ns, 1432 B | 739 ns, 1432 B | 774 ns, 1432 B |
| Redaction | no | 1209 ns, 1512 B | 782 ns, 496 B | 1536 ns, 1512 B | 1543 ns, 1512 B | 1327 ns, 1512 B | 1359 ns, 1512 B |
| Redaction | yes | 1406 ns, 1600 B | 774 ns, 496 B | 1693 ns, 1600 B | 1652 ns, 1600 B | 1476 ns, 1600 B | 1406 ns, 1600 B |
| FloorsAndNeverStepUp | no | 686 ns, 1360 B | 371 ns, 424 B | 871 ns, 1360 B | 1131 ns, 1360 B | 710 ns, 1360 B | 697 ns, 1360 B |
| FloorsAndNeverStepUp | yes | 749 ns, 1432 B | 356 ns, 424 B | 929 ns, 1432 B | 936 ns, 1432 B | 824 ns, 1432 B | 763 ns, 1432 B |

The plain Serilog row of the same run is 230 to 256 ns and 424 B in every scenario, before and after.

What the table shows:

- A dropped Debug event now allocates 424 B in the `Default` and `FloorsAndNeverStepUp` scenarios, with or
  without an `Activity`: the same bytes as the plain Serilog logger, against 1360 B or 1432 B before. In
  time it is 369 ns against 668 ns without an `Activity` and 345 ns against 909 ns with one.
- With redaction on, a dropped Debug event is 782 ns and 496 B against 1209 ns and 1512 B. The 72 B and
  most of the 413 ns above the `Default` row are `RedactionEnricher`, which the gate does not skip (ADR 0022
  D5; follow-up 3 of [analysis.md](analysis.md)). That attribution is by elimination, not measured by a row.
- `InformationHeld` and `WarningExported` are at or above the floor, so the gate lets them through: their
  bytes are unchanged and their times agree with the before run within the noise.

### A consumer root sink turns the gate off

`ConsumerRootSink` adds a `NullLogEventSink` through the `configure` hook, which keeps every root enricher
running (ADR 0026 D3). It is measured in the after run only; it is the cost of a dropped event without the gate.

| Row | Act | ConsumerRootSink | Default (gated) |
|---|---|---|---|
| DebugDropped | no | 689 ns, 1360 B | 369 ns, 424 B |
| DebugDropped | yes | 877 ns, 1432 B | 345 ns, 424 B |

It matches the before run of `Default` (668 ns, 1360 B; 909 ns, 1432 B) within the noise.

### Through Microsoft.Extensions.Logging

`MelDebugDropped` is `ILoggerFactory.CreateLogger("MyApp.Orders.OrderHandler").LogDebug(...)`, the call an
application makes, in the same category as the other rows. After the change only.

| Scenario | Act | MelDebugDropped | DebugDropped (Serilog) |
|---|---|---|---|
| Default | no | 382 ns, 408 B | 369 ns, 424 B |
| Default | yes | 389 ns, 408 B | 345 ns, 424 B |
| Redaction | no | 843 ns, 480 B | 782 ns, 496 B |
| FloorsAndNeverStepUp | no | 391 ns, 408 B | 371 ns, 424 B |
| ConsumerRootSink | no | 705 ns, 1344 B | 689 ns, 1360 B |

## Where the cost of a dropped event goes (`DroppedPathBenchmarks`)

Each row is a Serilog logger one stage longer than the row above it, in the order the root runs them, with
every root enricher applied to every event (no gate in these loggers). A row minus the row above it is that
stage's cost for a Debug event. The sink rows use the library's own sinks over a null bypass logger.

| Stage added | No activity | In an activity |
|---|---|---|
| Plain (baseline) | 241 ns, 424 B | 249 ns, 424 B |
| `FromLogContext` | +2 ns, +0 B | -1 ns, +0 B |
| Trace id and span id enrichers | +163 ns, +360 B | +178 ns, +408 B |
| `ActivityContextEnricher` | -3 ns, +0 B | +49 ns, +24 B |
| `Application`, `Environment`, `MachineName` | +198 ns, +576 B | +171 ns, +576 B |
| `WithExceptionDetails` | +10 ns, +0 B | +23 ns, +0 B |
| `StepUpSink` (with the buffer wired in) | +17 ns, +0 B | +10 ns, +0 B |
| `PreErrorBufferSink` | +1 ns, +0 B | +11 ns, +0 B |
| `StepUpTriggerSink` | +22 ns, +0 B | +23 ns, +0 B |
| `SummarySink` | -9 ns, +0 B | +29 ns, +0 B |
| `ImmediateSink` | 0 ns, +0 B | -45 ns, +0 B |
| Whole root pipeline | 642 ns, 1360 B | 698 ns, 1432 B |

The whole pipeline minus the baseline is 401 ns and 936 B without an activity (449 ns and 1008 B with one),
against 428 ns and 936 B on the real host before the gate (668 ns minus 239 ns). The ladder accounts for
the 936 B.

The steps of about 10 ns and below, the negative ones, and the 45 ns drop on `ImmediateSink` in an activity
are inside the noise of a three-iteration run. What is resolved: the enrichers cost most of the time and
all of the bytes (the trace and span ids and the three properties add 936 B and about 360 ns of the 401 ns),
and the five root sinks together add about 30 ns and no bytes to a dropped event (the sum of the rows from
`StepUpSink` to `ImmediateSink`: 31 ns without an activity, 28 ns with one).

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

After (`12340e4`), `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   230.2 ns |   233.99 ns | 12.83 ns |  1.00 |    0.07 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   369.1 ns |   113.34 ns |  6.21 ns |  1.61 |    0.08 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   381.5 ns |    23.32 ns |  1.28 ns |  1.66 |    0.08 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   815.3 ns |   324.54 ns | 17.79 ns |  3.55 |    0.19 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   697.9 ns |   774.08 ns | 42.43 ns |  3.04 |    0.22 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   248.8 ns |    48.57 ns |  2.66 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   345.0 ns |   163.82 ns |  8.98 ns |  1.39 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   389.2 ns |     1.48 ns |  0.08 ns |  1.56 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   914.7 ns |    77.46 ns |  4.25 ns |  3.68 |    0.04 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   774.4 ns |    42.62 ns |  2.34 ns |  3.11 |    0.03 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   246.6 ns |   295.37 ns | 16.19 ns |  1.00 |    0.08 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   782.2 ns |   372.60 ns | 20.42 ns |  3.18 |    0.19 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | False      |   843.3 ns |   181.84 ns |  9.97 ns |  3.43 |    0.19 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | False      | 1,542.5 ns |   740.19 ns | 40.57 ns |  6.27 |    0.37 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,358.8 ns |   289.06 ns | 15.84 ns |  5.53 |    0.31 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   245.1 ns |    19.51 ns |  1.07 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   774.3 ns |   988.91 ns | 54.21 ns |  3.16 |    0.19 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | True       |   816.4 ns |   100.61 ns |  5.51 ns |  3.33 |    0.02 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | True       | 1,651.5 ns |   191.19 ns | 10.48 ns |  6.74 |    0.04 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,406.1 ns | 1,075.87 ns | 58.97 ns |  5.74 |    0.21 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   236.4 ns |    31.09 ns |  1.70 ns |  1.00 |    0.01 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   370.5 ns |   188.69 ns | 10.34 ns |  1.57 |    0.04 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   391.4 ns |    19.70 ns |  1.08 ns |  1.66 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      | 1,131.4 ns | 1,715.52 ns | 94.03 ns |  4.79 |    0.35 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   696.6 ns |   219.39 ns | 12.03 ns |  2.95 |    0.05 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   255.5 ns |   113.78 ns |  6.24 ns |  1.00 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   355.9 ns |    45.37 ns |  2.49 ns |  1.39 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   402.3 ns |     5.36 ns |  0.29 ns |  1.58 |    0.03 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   936.0 ns |    32.44 ns |  1.78 ns |  3.66 |    0.08 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   763.2 ns |    48.46 ns |  2.66 ns |  2.99 |    0.06 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   241.8 ns |    41.47 ns |  2.27 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   689.4 ns |    52.78 ns |  2.89 ns |  2.85 |    0.03 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   704.9 ns |   341.46 ns | 18.72 ns |  2.92 |    0.07 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   795.9 ns |    55.20 ns |  3.03 ns |  3.29 |    0.03 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   660.9 ns |   298.27 ns | 16.35 ns |  2.73 |    0.06 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   257.2 ns |   450.65 ns | 24.70 ns |  1.01 |    0.12 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   877.4 ns |   601.84 ns | 32.99 ns |  3.43 |    0.30 | 0.1125 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   794.9 ns |    94.45 ns |  5.18 ns |  3.11 |    0.26 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   916.3 ns |   341.30 ns | 18.71 ns |  3.58 |    0.30 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   746.7 ns |   107.09 ns |  5.87 ns |  2.92 |    0.24 | 0.1135 |      - |    1432 B |        3.38 |

After (`12340e4`), `DroppedPathBenchmarks`:

| Method           | InActivity | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |----------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Plain            | False      | 240.6 ns |  13.50 ns |  0.74 ns |  1.00 |    0.00 | 0.0334 |     424 B |        1.00 |
| LogContext       | False      | 243.0 ns |   6.61 ns |  0.36 ns |  1.01 |    0.00 | 0.0334 |     424 B |        1.00 |
| TraceIds         | False      | 405.6 ns |  41.03 ns |  2.25 ns |  1.69 |    0.01 | 0.0625 |     784 B |        1.85 |
| ActivityContext  | False      | 402.9 ns |  31.75 ns |  1.74 ns |  1.67 |    0.01 | 0.0625 |     784 B |        1.85 |
| Properties       | False      | 601.2 ns |  59.68 ns |  3.27 ns |  2.50 |    0.01 | 0.1078 |    1360 B |        3.21 |
| ExceptionDetails | False      | 611.2 ns | 113.81 ns |  6.24 ns |  2.54 |    0.02 | 0.1078 |    1360 B |        3.21 |
| StepUpSink       | False      | 628.4 ns |  87.48 ns |  4.79 ns |  2.61 |    0.02 | 0.1078 |    1360 B |        3.21 |
| PreErrorBuffer   | False      | 629.3 ns | 201.51 ns | 11.05 ns |  2.62 |    0.04 | 0.1078 |    1360 B |        3.21 |
| Trigger          | False      | 651.3 ns | 300.50 ns | 16.47 ns |  2.71 |    0.06 | 0.1078 |    1360 B |        3.21 |
| Summary          | False      | 642.0 ns | 290.60 ns | 15.93 ns |  2.67 |    0.06 | 0.1078 |    1360 B |        3.21 |
| Immediate        | False      | 641.7 ns | 448.82 ns | 24.60 ns |  2.67 |    0.09 | 0.1078 |    1360 B |        3.21 |
|                  |            |          |           |          |       |         |        |           |             |
| Plain            | True       | 248.6 ns |  14.45 ns |  0.79 ns |  1.00 |    0.00 | 0.0334 |     424 B |        1.00 |
| LogContext       | True       | 247.5 ns | 212.06 ns | 11.62 ns |  1.00 |    0.04 | 0.0334 |     424 B |        1.00 |
| TraceIds         | True       | 425.5 ns |  14.41 ns |  0.79 ns |  1.71 |    0.01 | 0.0663 |     832 B |        1.96 |
| ActivityContext  | True       | 474.9 ns | 270.41 ns | 14.82 ns |  1.91 |    0.05 | 0.0682 |     856 B |        2.02 |
| Properties       | True       | 646.3 ns |  91.71 ns |  5.03 ns |  2.60 |    0.02 | 0.1135 |    1432 B |        3.38 |
| ExceptionDetails | True       | 669.6 ns | 203.66 ns | 11.16 ns |  2.69 |    0.04 | 0.1135 |    1432 B |        3.38 |
| StepUpSink       | True       | 679.8 ns | 157.77 ns |  8.65 ns |  2.73 |    0.03 | 0.1135 |    1432 B |        3.38 |
| PreErrorBuffer   | True       | 690.6 ns | 144.85 ns |  7.94 ns |  2.78 |    0.03 | 0.1135 |    1432 B |        3.38 |
| Trigger          | True       | 713.4 ns |  88.78 ns |  4.87 ns |  2.87 |    0.02 | 0.1135 |    1432 B |        3.38 |
| Summary          | True       | 742.5 ns |  96.84 ns |  5.31 ns |  2.99 |    0.02 | 0.1135 |    1432 B |        3.38 |
| Immediate        | True       | 697.6 ns | 377.52 ns | 20.69 ns |  2.81 |    0.07 | 0.1135 |    1432 B |        3.38 |
