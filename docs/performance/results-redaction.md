# Results: gated redaction and the cheaper `Redact`

What the change to `RedactionEnricher` and `CompiledRedactionPatterns` did, measured: the redaction enricher
now runs behind the enrichment gate of [ADR 0026](../adr/0026-root-enrichment-gate.md) (ADR 0022 D5), and
`Redact` tests one union of the patterns before it runs them one by one (ADR 0001 amendment). Follow-ups 3
and 5 of [analysis.md](analysis.md) are recorded here.

- Before: commit `79f8dcc`, the branch this one is stacked on (the gate of ADR 0026 is in, redaction is
  ungated, `Redact` is the plain loop). A detached worktree of that commit, built and run on its own.
  `RedactionPerPatternBenchmarks` and the `Loop*` baseline rows of `RedactionPatternBenchmarks` did not
  exist at that commit, so they have no before row. The old `RedactionPatternBenchmarks` has its three
  `Redact*` rows.
- After: commit `43c7975` (source as in `d1f1d90`, plus the benchmark classes). The commits after it on
  this branch touch documentation and one XML doc comment only. The `Loop*` rows are the old loop,
  re-implemented inside the benchmark class (no production member kept for it), and they are measured in the
  same run as the `Redact*` rows of the same group.
- Date: 2026-10-09. The two runs were made on the same machine, the before run first, one after the other.
- Machine state: laptop on AC power, CPU governor `powersave`, desktop session running; the same state as
  [baseline.md](baseline.md).
- Classes: `PipelineBenchmarks`, `EnricherBenchmarks`, `RedactionPatternBenchmarks`,
  `RedactionPerPatternBenchmarks` (after only), `RequestLoggingBenchmarks`, `AuditBenchmarks`,
  `StepUpSinkBenchmarks`.
- Commands, from the repository root (the before run from a temporary checkout of `79f8dcc`):

```bash
# before, at 79f8dcc
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" "*EnricherBenchmarks*" "*RedactionPatternBenchmarks*" \
           "*RequestLoggingBenchmarks*" "*AuditBenchmarks*" "*StepUpSinkBenchmarks*" \
  --job short --exporters github json

# after, at 43c7975
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" "*EnricherBenchmarks*" "*RedactionPatternBenchmarks*" \
           "*RedactionPerPatternBenchmarks*" "*RequestLoggingBenchmarks*" "*AuditBenchmarks*" \
           "*StepUpSinkBenchmarks*" --job short --exporters github json
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
Read the `Mean` of a row together with its `Error` and `StdDev`, and treat a difference under 25 % between
two rows as unresolved. The allocation column is deterministic and agrees between runs; the time column
does not. The plain Serilog row, which nothing in this change touches, measured 159.1 ns in the first
`Default` scenario of the before run and 242.0 ns in the after run, and 173.5 ns against 252.1 ns in the
`FloorsAndNeverStepUp` scenario with an activity. So a time difference between two runs is only read here
when it is large (about 1.5x and up) and the bytes agree with it. One `WarningExported` row (`Default`, with
an activity) showed 1464 B in the after run against 1432 B before; a rerun of that method alone showed 1.4 KB
again, so the 32 B did not reproduce and is treated as a one-off of that run. The `Loop*` and `Redact*` rows
of one group come from the same run, so their ratio is better evidence than a before/after pair.

## The result in short

- A Debug event the pipeline drops, with redaction on, now takes 341 ns and 424 B (365 ns and 424 B with an
  activity). Before it took 618 ns and 496 B (742 ns and 496 B). That is the row of the `Default` scenario:
  the redaction enricher no longer runs on it.
- An exported or held event with redaction on allocates 96 to 112 B less (1512 B to 1416 B, 1600 B to
  1488 B), the snapshot array `RedactionEnricher` took of the properties. Times are 26 % and 31 % lower in
  the no-activity `InformationHeld` and `WarningExported` rows, 8 % lower in `WarningExported` with an
  activity and level in `InformationHeld` with one (1234 ns both); these are inside the noise.
- `Redact` over five patterns of a short value that matches none: 355 ns to 91 ns (0.26x in the same run),
  allocation-free. Over a header of about a thousand characters that matches none: no change (2748 ns against
  2684 ns), because one of the five patterns costs 2.2 us alone and the union has to scan with it. Over a
  value that matches: not faster, 575 ns against 679 ns with an `Error` wider than the gap.
- Request logging allocates 0.17 to 0.18 KB less per request in all eight rows. Request logging runs the
  redaction enricher, so the likeliest cause is the removed `RedactionEnricher` snapshot; the benchmark does
  not isolate it from the gate. Audit is unchanged. `StepUpSink.Emit` is 1.3 to 5.9 ns slower in the four
  rows, the `TakeSkipped` read of a `[ThreadStatic]` per event.

## Before and after: the pipeline

Mean time in ns and allocated bytes per call. `Act` is `InActivity`.

### The `Redaction` scenario (redaction on, five patterns)

| Row | Act | Before (`79f8dcc`) | After (`43c7975`) |
|---|---|---|---|
| DebugDropped | no | 617.5 ns, 496 B | 341.3 ns, 424 B |
| DebugDropped | yes | 741.9 ns, 496 B | 365.5 ns, 424 B |
| MelDebugDropped | no | 692.2 ns, 480 B | 372.8 ns, 408 B |
| MelDebugDropped | yes | 792.1 ns, 480 B | 372.9 ns, 408 B |
| InformationHeld | no | 1495.4 ns, 1512 B | 1112.4 ns, 1416 B |
| InformationHeld | yes | 1233.9 ns, 1600 B | 1234.2 ns, 1488 B |
| WarningExported | no | 1378.6 ns, 1512 B | 946.5 ns, 1416 B |
| WarningExported | yes | 1102.3 ns, 1600 B | 1017.8 ns, 1488 B |
| PlainSerilogToNullSink | no | 223.3 ns, 424 B | 224.4 ns, 424 B |
| PlainSerilogToNullSink | yes | 241.8 ns, 424 B | 249.0 ns, 424 B |

The dropped rows are what the gate was extended for: 496 B to 424 B is the 72 B that
[results-root-gate.md](results-root-gate.md) attributed to `RedactionEnricher` by elimination, and the
dropped event is now the size of the plain Serilog event. The time is 0.55x (no activity) and 0.49x
(activity), outside the noise of the plain rows next to them. The exported and held rows run `Redact` as
before and do not get the gate, so their time gain is the smaller one and partly the noise; their 96 to 112 B
are measured.

### The other scenarios (redaction off, or the gate off)

These rows do not run the redaction enricher, so the change should leave them alone. Bytes are unchanged in
every row but one: `Default` with an activity, `WarningExported`, went from 1432 B to 1464 B (32 B more; not
explained, and the `InformationHeld` row next to it did not move). Each cell is before / after:

| Scenario | Act | DebugDropped | InformationHeld | WarningExported |
|---|---|---|---|---|
| Default | no | 251.9 ns, 424 B / 314.1 ns, 424 B | 760.0 ns, 1360 B / 850.0 ns, 1360 B | 588.6 ns, 1360 B / 681.5 ns, 1360 B |
| Default | yes | 268.9 ns, 424 B / 334.7 ns, 424 B | 972.4 ns, 1432 B / 882.2 ns, 1432 B | 674.7 ns, 1432 B / 766.2 ns, 1464 B |
| FloorsAndNeverStepUp | no | 243.1 ns, 424 B / 322.7 ns, 424 B | 867.6 ns, 1360 B / 847.5 ns, 1360 B | 734.2 ns, 1360 B / 708.7 ns, 1360 B |
| FloorsAndNeverStepUp | yes | 368.3 ns, 424 B / 324.4 ns, 424 B | 865.9 ns, 1432 B / 943.4 ns, 1432 B | 785.3 ns, 1432 B / 768.4 ns, 1432 B |
| ConsumerRootSink | no | 695.8 ns, 1360 B / 707.8 ns, 1360 B | 775.4 ns, 1360 B / 835.3 ns, 1360 B | 706.3 ns, 1360 B / 688.9 ns, 1360 B |
| ConsumerRootSink | yes | 751.3 ns, 1432 B / 751.4 ns, 1432 B | 894.5 ns, 1432 B / 886.6 ns, 1432 B | 770.6 ns, 1432 B / 796.3 ns, 1432 B |

The `Default` `DebugDropped` row reads 25 % slower after, but the plain row of the same run reads 52 % slower
(159.1 ns against 242.0 ns): that is the machine, not the code. The rows agree within the noise described
above. The `WarningExported` `Default` row with an activity shows 1464 B in the first after run (see the
note at the top).

## Before and after: `Redact` and the enricher

### `EnricherBenchmarks.RedactProperties`

The enricher alone over an event with 4 or 16 string properties, five patterns. `NewEvent` is the baseline
of the same run.

| Properties | Act | Before | After | NewEvent in the run (before / after) |
|---|---|---|---|---|
| 4 | no | 2321.2 ns, 1.9 KB | 1580.9 ns, 1.82 KB | 546.1 ns / 766.4 ns |
| 4 | yes | 2501.4 ns, 1.9 KB | 1545.8 ns, 1.82 KB | 685.2 ns / 774.4 ns |
| 16 | no | 7869.7 ns, 6.2 KB | 4631.0 ns, 5.93 KB | 2512.0 ns / 2305.2 ns |
| 16 | yes | 10414.4 ns, 6.2 KB | 4600.4 ns, 5.93 KB | 2519.8 ns / 2435.1 ns |

The ratio to `NewEvent` fell from 3.1 to 4.3 down to 1.9 to 2.1. The bytes fell by about 80 B (4 properties)
and about 280 B (16): the snapshot array that is no longer taken. The size of the time gain is not resolved
(the 16-property row without an activity has an `Error` of 17.6 us on 7.9 us before), but its direction
agrees in all four rows and in the ratio.

### `RedactionPatternBenchmarks`

Five sample patterns (`token/[^/]+`, `password=[^&]+`, `api[_-]?key=[^&]+`, `Bearer\s+[A-Za-z0-9._-]+`,
`\b\d{16}\b`), or the first one. Each group's baseline is the old loop, in the same run. The before run has
the three `Redact*` rows of the old class and no loop row.

| Input | Patterns | Before run, `Redact` | After run, `Loop` (baseline) | After run, `Redact` | Ratio |
|---|---|---|---|---|---|
| Short value, no match | 1 | 64.5 ns, 0 B | 62.5 ns, 0 B | 69.3 ns, 0 B | 1.11 |
| Short value, no match | 5 | 318.1 ns, 0 B | 354.6 ns, 0 B | 91.3 ns, 0 B | 0.26 |
| Header of about 1000 characters, no match | 1 | 167.0 ns, 0 B | 168.8 ns, 0 B | 173.7 ns, 0 B | 1.03 |
| Header of about 1000 characters, no match | 5 | 2093.2 ns, 0 B | 2748.3 ns, 0 B | 2684.1 ns, 0 B | 0.98 |
| Path that matches the first pattern | 1 | 224.5 ns, 88 B | 219.3 ns, 88 B | 223.9 ns, 88 B | 1.02 |
| Path that matches the first pattern | 5 | 596.2 ns, 88 B | 574.6 ns, 88 B | 678.9 ns, 88 B | 1.18 |

- One pattern builds no union (`HasPrefilter` is false), so those rows are the same code on both sides; their
  ratios of 1.02 to 1.11 are the noise floor of this table.
- Five patterns, short value: 3.9x less time, no allocation either way. The union rejects the value in one
  linear scan where the loop ran five.
- Five patterns, long header: no gain. `RedactionPerPatternBenchmarks` below shows why.
- Five patterns, a value that matches: the union scan runs first and then the loop runs all five patterns, so
  the value pays for both. The measured gap is 104 ns (1.18x, `Error` of 2.6 us on the `Redact` row), which the
  noise does not let us size. It is a cost the change adds for a value that matches, in return for the gain on
  the values that do not. In a log stream most values match nothing.
- The before run's `Redact` rows (318.1 ns, 2093.2 ns, 596.2 ns) and the after run's `Loop` rows (354.6 ns,
  2748.3 ns, 574.6 ns) are the same code; the long-header pair differs by 31 %, with `Error` of 1.3 us and
  4.9 us. That is the noise of these runs, and the reason the `Loop` row sits in the same run as `Redact`.

### `RedactionPerPatternBenchmarks` (after only)

One sample pattern alone, on the two inputs it does not match (a single pattern builds no union):

| PatternIndex | Pattern | Short value | Header of about 1000 characters |
|---|---|---|---|
| 0 | `token/[^/]+` | 67.0 ns | 174.2 ns |
| 1 | `password=[^&]+` | 69.3 ns | 161.5 ns |
| 2 | `api[_-]?key=[^&]+` | 82.7 ns | 171.7 ns |
| 3 | `Bearer\s+[A-Za-z0-9._-]+` | 65.4 ns | 158.8 ns |
| 4 | `\b\d{16}\b` | 59.5 ns | 2215.9 ns |

Four of the five patterns cost about 160 to 175 ns on the long header; `\b\d{16}\b` costs 2.2 us, 13 times
more, and it is 80 % of the five-pattern loop (2748 ns). The union contains it, so a union scan of the long
header costs about what that one pattern does and the prefilter has nothing to save. All five allocate
nothing. This is a property of that pattern under `NonBacktracking` (the word boundary and the counted
digit run), not of the prefilter; it is not investigated further here.

## Before and after: request logging, audit and the sink

`RequestLoggingBenchmarks.HandleRequest`, five patterns, before / after:

| Stepped | Headers | Summary | Before | After |
|---|---|---|---|---|
| no | 4 | no | 15.65 us, 6.4 KB | 12.72 us, 6.23 KB |
| no | 4 | yes | 20.31 us, 9.86 KB | 19.76 us, 9.69 KB |
| no | 32 | no | 32.78 us, 15.03 KB | 18.59 us, 14.85 KB |
| no | 32 | yes | 30.51 us, 18.49 KB | 28.83 us, 18.31 KB |
| yes | 4 | no | 10.47 us, 6.4 KB | 10.34 us, 6.23 KB |
| yes | 4 | yes | 22.58 us, 9.86 KB | 18.56 us, 9.69 KB |
| yes | 32 | no | 31.89 us, 15.03 KB | 20.13 us, 14.85 KB |
| yes | 32 | yes | 40.18 us, 18.49 KB | 27.05 us, 18.31 KB |

The bytes are the evidence: all eight rows are 0.17 to 0.18 KB lower. The times are lower in all eight rows
too, by 1 % to 43 %, but the before rows with 32 headers have `Error` values as large as their `Mean`
(`Stepped=no`, 32 headers, no summary: 33.1 us of `Error` on 32.78 us), so no size is claimed. The
request path calls `Redact` on every header value, and the union takes five scans of a short value down to
one; that attribution rests on the `RedactionPatternBenchmarks` rows, not on a row of this class.

`AuditBenchmarks`: unchanged. `AuditToNullSink` is 176 B and 352 B before and after in all four rows, and
its times (494.7 to 1231.0 ns after, 499.3 to 2004.5 ns before) overlap. `SerializeForSpool` is within the
noise too; one row, with an `HttpContext` and no data entries, allocates 1968 B after against 1952 B before
(+16 B), which is not explained and is not seen in the other seven rows.

`StepUpSinkBenchmarks`, before / after (no allocation in any row):

| Row | Category rules | Before | After |
|---|---|---|---|
| EmitBelowSwitch | no | 16.84 ns | 18.13 ns |
| EmitExported | no | 20.78 ns | 24.22 ns |
| EmitBelowSwitch | yes | 43.66 ns | 49.57 ns |
| EmitExported | yes | 51.08 ns | 52.47 ns |

`Emit` is 1.3 to 5.9 ns slower in each row, in the direction the code predicts: `Refuses` now asks
`EnrichmentGate.TakeSkipped` for a `[ThreadStatic]` read on every event. The `Error` values are 0.1 to 50 ns,
so the size is unresolved; it stays far under the cost of the enrichers the gate skips.

## What is not claimed

- No gain on the long-header and the matching rows of `Redact` with these patterns, as above.
- The `Redact` numbers are for the library's five sample patterns, which are all `NonBacktracking`; a
  consumer with a lookahead pattern (the `Compiled` fallback) or patterns with different timeouts gets no union
  and the old loop (ADR 0001 amendment). They do not transfer to a consumer's patterns, as ADR 0022 D7 says.
- Only the allocation column and the differences of about 1.5x are relied on; the `Redaction` rows have wide
  `Error` values.
- A root `Serilog:Filter` sees a skipped event unredacted (ADR 0026 D5 amendment); no row measures it.

## The raw tables

Before (`79f8dcc`), `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   159.1 ns |   116.47 ns |   6.38 ns |  1.00 |    0.05 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   251.9 ns | 1,089.30 ns |  59.71 ns |  1.58 |    0.33 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   334.1 ns | 1,232.89 ns |  67.58 ns |  2.10 |    0.38 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   760.0 ns |   545.93 ns |  29.92 ns |  4.78 |    0.24 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   588.6 ns |   319.71 ns |  17.52 ns |  3.70 |    0.16 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   209.1 ns |   419.66 ns |  23.00 ns |  1.01 |    0.14 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   268.9 ns |   312.68 ns |  17.14 ns |  1.30 |    0.15 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   311.2 ns |   448.98 ns |  24.61 ns |  1.50 |    0.18 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   972.4 ns |   142.56 ns |   7.81 ns |  4.69 |    0.47 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   674.7 ns |   298.50 ns |  16.36 ns |  3.26 |    0.33 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   223.3 ns |   728.48 ns |  39.93 ns |  1.02 |    0.24 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   617.5 ns | 2,149.82 ns | 117.84 ns |  2.83 |    0.68 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | False      |   692.2 ns | 2,064.08 ns | 113.14 ns |  3.17 |    0.71 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | False      | 1,495.4 ns | 2,691.96 ns | 147.56 ns |  6.86 |    1.31 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,378.6 ns |   469.36 ns |  25.73 ns |  6.32 |    1.08 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   241.8 ns |    22.95 ns |   1.26 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   741.9 ns |   817.45 ns |  44.81 ns |  3.07 |    0.16 | 0.0391 |      - |     496 B |        1.17 |
| MelDebugDropped        | Redaction            | True       |   792.1 ns | 1,536.67 ns |  84.23 ns |  3.28 |    0.30 | 0.0381 |      - |     480 B |        1.13 |
| InformationHeld        | Redaction            | True       | 1,233.9 ns | 1,325.92 ns |  72.68 ns |  5.10 |    0.26 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,102.3 ns | 1,593.72 ns |  87.36 ns |  4.56 |    0.31 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   246.6 ns |   127.45 ns |   6.99 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   243.1 ns |   212.57 ns |  11.65 ns |  0.99 |    0.05 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   323.6 ns | 1,105.41 ns |  60.59 ns |  1.31 |    0.22 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   867.6 ns |   256.48 ns |  14.06 ns |  3.52 |    0.10 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   734.2 ns |   265.05 ns |  14.53 ns |  2.98 |    0.09 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   173.5 ns |   381.05 ns |  20.89 ns |  1.01 |    0.15 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   368.3 ns |   207.47 ns |  11.37 ns |  2.14 |    0.22 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   349.7 ns |   342.65 ns |  18.78 ns |  2.03 |    0.22 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   865.9 ns | 2,019.47 ns | 110.69 ns |  5.04 |    0.75 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   785.3 ns |   195.90 ns |  10.74 ns |  4.57 |    0.46 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   237.6 ns |     8.29 ns |   0.45 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   695.8 ns |   505.90 ns |  27.73 ns |  2.93 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   838.0 ns | 2,224.70 ns | 121.94 ns |  3.53 |    0.44 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   775.4 ns |   354.14 ns |  19.41 ns |  3.26 |    0.07 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   706.3 ns |   693.84 ns |  38.03 ns |  2.97 |    0.14 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |             |           |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   221.3 ns |   728.66 ns |  39.94 ns |  1.02 |    0.24 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   751.3 ns |   166.34 ns |   9.12 ns |  3.48 |    0.61 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   846.6 ns |   565.15 ns |  30.98 ns |  3.92 |    0.69 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   894.5 ns | 1,082.32 ns |  59.33 ns |  4.14 |    0.76 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   770.6 ns |   268.50 ns |  14.72 ns |  3.57 |    0.62 | 0.1135 |      - |    1432 B |        3.38 |

After (`43c7975`), `PipelineBenchmarks`:

| Method                 | Scenario             | InActivity | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlainSerilogToNullSink | Default              | False      |   242.0 ns | 191.19 ns | 10.48 ns |  1.00 |    0.05 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | False      |   314.1 ns |  51.96 ns |  2.85 ns |  1.30 |    0.05 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | False      |   350.2 ns | 490.53 ns | 26.89 ns |  1.45 |    0.11 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | False      |   850.0 ns | 364.06 ns | 19.96 ns |  3.52 |    0.15 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   681.5 ns |  22.80 ns |  1.25 ns |  2.82 |    0.10 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Default              | True       |   242.3 ns | 119.22 ns |  6.53 ns |  1.00 |    0.03 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Default              | True       |   334.7 ns |  20.25 ns |  1.11 ns |  1.38 |    0.03 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Default              | True       |   359.1 ns | 304.07 ns | 16.67 ns |  1.48 |    0.07 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Default              | True       |   882.2 ns | 147.16 ns |  8.07 ns |  3.64 |    0.09 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | Default              | True       |   766.2 ns | 289.67 ns | 15.88 ns |  3.16 |    0.09 | 0.1163 |      - |    1464 B |        3.45 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | False      |   224.4 ns | 301.14 ns | 16.51 ns |  1.00 |    0.09 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | False      |   341.3 ns |  13.91 ns |  0.76 ns |  1.53 |    0.10 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | False      |   372.8 ns | 346.05 ns | 18.97 ns |  1.67 |    0.13 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | False      | 1,112.4 ns | 820.93 ns | 45.00 ns |  4.98 |    0.37 | 0.1125 | 0.0210 |    1416 B |        3.34 |
| WarningExported        | Redaction            | False      |   946.5 ns | 304.94 ns | 16.71 ns |  4.23 |    0.29 | 0.1125 |      - |    1416 B |        3.34 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | Redaction            | True       |   249.0 ns |  28.88 ns |  1.58 ns |  1.00 |    0.01 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | Redaction            | True       |   365.5 ns | 246.21 ns | 13.50 ns |  1.47 |    0.05 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | Redaction            | True       |   372.9 ns | 463.64 ns | 25.41 ns |  1.50 |    0.09 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | Redaction            | True       | 1,234.2 ns | 153.22 ns |  8.40 ns |  4.96 |    0.04 | 0.1183 | 0.0286 |    1488 B |        3.51 |
| WarningExported        | Redaction            | True       | 1,017.8 ns | 278.60 ns | 15.27 ns |  4.09 |    0.06 | 0.1183 |      - |    1488 B |        3.51 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | False      |   237.1 ns |   8.32 ns |  0.46 ns |  1.00 |    0.00 | 0.0336 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | False      |   322.7 ns |  68.33 ns |  3.75 ns |  1.36 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | False      |   360.3 ns |  23.69 ns |  1.30 ns |  1.52 |    0.01 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   847.5 ns |  55.81 ns |  3.06 ns |  3.57 |    0.01 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   708.7 ns | 107.00 ns |  5.87 ns |  2.99 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | FloorsAndNeverStepUp | True       |   252.1 ns |   6.60 ns |  0.36 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | FloorsAndNeverStepUp | True       |   324.4 ns | 321.66 ns | 17.63 ns |  1.29 |    0.06 | 0.0334 |      - |     424 B |        1.00 |
| MelDebugDropped        | FloorsAndNeverStepUp | True       |   340.0 ns | 642.65 ns | 35.23 ns |  1.35 |    0.12 | 0.0324 |      - |     408 B |        0.96 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   943.4 ns |  87.89 ns |  4.82 ns |  3.74 |    0.02 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   768.4 ns | 773.45 ns | 42.40 ns |  3.05 |    0.15 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | False      |   239.8 ns |  28.92 ns |  1.59 ns |  1.00 |    0.01 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | False      |   707.8 ns |  66.53 ns |  3.65 ns |  2.95 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
| MelDebugDropped        | ConsumerRootSink     | False      |   769.1 ns | 114.32 ns |  6.27 ns |  3.21 |    0.03 | 0.1068 |      - |    1344 B |        3.17 |
| InformationHeld        | ConsumerRootSink     | False      |   835.3 ns | 157.01 ns |  8.61 ns |  3.48 |    0.04 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | ConsumerRootSink     | False      |   688.9 ns | 262.65 ns | 14.40 ns |  2.87 |    0.05 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| PlainSerilogToNullSink | ConsumerRootSink     | True       |   248.1 ns |   8.83 ns |  0.48 ns |  1.00 |    0.00 | 0.0334 |      - |     424 B |        1.00 |
| DebugDropped           | ConsumerRootSink     | True       |   751.4 ns |  77.23 ns |  4.23 ns |  3.03 |    0.02 | 0.1135 |      - |    1432 B |        3.38 |
| MelDebugDropped        | ConsumerRootSink     | True       |   789.3 ns | 221.94 ns | 12.17 ns |  3.18 |    0.04 | 0.1125 |      - |    1416 B |        3.34 |
| InformationHeld        | ConsumerRootSink     | True       |   886.6 ns |  33.60 ns |  1.84 ns |  3.57 |    0.01 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | ConsumerRootSink     | True       |   796.3 ns | 373.01 ns | 20.45 ns |  3.21 |    0.07 | 0.1135 |      - |    1432 B |        3.38 |

Before (`79f8dcc`), `EnricherBenchmarks`:

| Method           | InActivity | StringProperties | Mean        | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |------------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |    546.1 ns |    658.7 ns |  36.11 ns |  1.00 |    0.08 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |    594.7 ns |  1,164.9 ns |  63.85 ns |  1.09 |    0.12 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |    893.4 ns |    286.5 ns |  15.70 ns |  1.64 |    0.09 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                |  2,321.2 ns |  1,587.9 ns |  87.04 ns |  4.26 |    0.28 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               |  2,512.0 ns |    806.3 ns |  44.19 ns |  1.00 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               |  2,279.6 ns |  6,287.7 ns | 344.65 ns |  0.91 |    0.12 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               |  1,826.3 ns |  5,022.2 ns | 275.28 ns |  0.73 |    0.10 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               |  7,869.7 ns | 17,594.3 ns | 964.40 ns |  3.13 |    0.34 | 0.5035 |      - |    6.2 KB |        1.05 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |    685.2 ns |  2,025.5 ns | 111.02 ns |  1.02 |    0.21 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |    790.6 ns |  2,241.5 ns | 122.86 ns |  1.18 |    0.24 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |    717.2 ns |  1,539.1 ns |  84.36 ns |  1.07 |    0.20 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                |  2,501.4 ns |  6,142.8 ns | 336.71 ns |  3.72 |    0.71 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               |  2,519.8 ns |    319.6 ns |  17.52 ns |  1.00 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               |  2,367.1 ns |  2,507.5 ns | 137.44 ns |  0.94 |    0.05 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               |  2,274.8 ns |  5,017.4 ns | 275.02 ns |  0.90 |    0.09 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 10,414.4 ns |  2,888.5 ns | 158.33 ns |  4.13 |    0.06 | 0.5035 |      - |    6.2 KB |        1.05 |

Before (`79f8dcc`), `RedactionPatternBenchmarks`:

| Method                  | PatternCount | Mean        | Error       | StdDev    | Gen0   | Allocated |
|------------------------ |------------- |------------:|------------:|----------:|-------:|----------:|
| RedactShortNoMatch      | 1            |    64.50 ns |    57.42 ns |  3.148 ns |      - |         - |
| RedactLongHeaderNoMatch | 1            |   167.01 ns |    75.52 ns |  4.140 ns |      - |         - |
| RedactMatch             | 1            |   224.50 ns |    20.84 ns |  1.142 ns | 0.0069 |      88 B |
| RedactShortNoMatch      | 5            |   318.10 ns | 1,043.93 ns | 57.222 ns |      - |         - |
| RedactLongHeaderNoMatch | 5            | 2,093.19 ns | 1,318.90 ns | 72.293 ns |      - |         - |
| RedactMatch             | 5            |   596.17 ns |    65.78 ns |  3.606 ns | 0.0067 |      88 B |

Before (`79f8dcc`), `RequestLoggingBenchmarks`:

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error      | StdDev   | Gen0   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|-----------:|---------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 15.65 us |   2.045 us | 0.112 us | 0.5188 |    6.4 KB |
| HandleRequest | False   | 4           | True                    | 20.31 us |  44.198 us | 2.423 us | 0.7935 |   9.86 KB |
| HandleRequest | False   | 32          | False                   | 32.78 us |  33.072 us | 1.813 us | 1.2207 |  15.03 KB |
| HandleRequest | False   | 32          | True                    | 30.51 us | 100.771 us | 5.524 us | 1.4954 |  18.49 KB |
| HandleRequest | True    | 4           | False                   | 10.47 us |  35.536 us | 1.948 us | 0.5188 |    6.4 KB |
| HandleRequest | True    | 4           | True                    | 22.58 us |   0.594 us | 0.033 us | 0.7935 |   9.86 KB |
| HandleRequest | True    | 32          | False                   | 31.89 us |  10.073 us | 0.552 us | 1.2207 |  15.03 KB |
| HandleRequest | True    | 32          | True                    | 40.18 us |   3.713 us | 0.204 us | 1.4648 |  18.49 KB |

Before (`79f8dcc`), `AuditBenchmarks`:

| Method            | WithHttpContext | DataEntries | Mean       | Error      | StdDev    | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|-----------:|----------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   501.7 ns |   106.7 ns |   5.85 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 0           | 1,194.0 ns | 1,417.6 ns |  77.70 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   499.3 ns |   159.5 ns |   8.74 ns | 0.0138 |     176 B |
| SerializeForSpool | False           | 32          | 3,421.0 ns | 1,870.5 ns | 102.53 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 1,462.2 ns | 2,415.4 ns | 132.40 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,336.9 ns | 2,090.6 ns | 114.59 ns | 0.1545 |    1952 B |
| AuditToNullSink   | True            | 32          | 2,004.5 ns | 3,193.1 ns | 175.03 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 2,497.9 ns | 9,627.5 ns | 527.72 ns | 0.2670 |    3384 B |

Before (`79f8dcc`), `StepUpSinkBenchmarks`:

| Method          | WithCategoryRules | Mean     | Error     | StdDev   | Allocated |
|---------------- |------------------ |---------:|----------:|---------:|----------:|
| EmitBelowSwitch | False             | 16.84 ns |  1.755 ns | 0.096 ns |         - |
| EmitExported    | False             | 20.78 ns | 26.490 ns | 1.452 ns |         - |
| EmitBelowSwitch | True              | 43.66 ns | 49.883 ns | 2.734 ns |         - |
| EmitExported    | True              | 51.08 ns |  1.644 ns | 0.090 ns |         - |

After (`43c7975`), `EnricherBenchmarks`:

| Method           | InActivity | StringProperties | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |-----------:|------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| NewEvent         | False      | 4                |   766.4 ns |   210.06 ns |  11.51 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | False      | 4                |   746.3 ns |    88.45 ns |   4.85 ns |  0.97 |    0.01 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |   903.9 ns |   793.51 ns |  43.50 ns |  1.18 |    0.05 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                | 1,580.9 ns |   421.21 ns |  23.09 ns |  2.06 |    0.04 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | False      | 16               | 2,305.2 ns |   863.53 ns |  47.33 ns |  1.00 |    0.03 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | False      | 16               | 2,365.0 ns |   212.44 ns |  11.64 ns |  1.03 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               | 2,351.6 ns |   449.70 ns |  24.65 ns |  1.02 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 4,631.0 ns | 8,693.44 ns | 476.52 ns |  2.01 |    0.18 | 0.4807 |      - |   5.93 KB |        1.01 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 4                |   774.4 ns |   196.81 ns |  10.79 ns |  1.00 |    0.02 | 0.1440 |      - |   1.77 KB |        1.00 |
| ActivityContext  | True       | 4                |   883.6 ns |   341.56 ns |  18.72 ns |  1.14 |    0.03 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |   861.4 ns |   362.47 ns |  19.87 ns |  1.11 |    0.03 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                | 1,545.8 ns |   436.53 ns |  23.93 ns |  2.00 |    0.04 | 0.1469 |      - |   1.82 KB |        1.03 |
|                  |            |                  |            |             |           |       |         |        |        |           |             |
| NewEvent         | True       | 16               | 2,435.1 ns |   893.07 ns |  48.95 ns |  1.00 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| ActivityContext  | True       | 16               | 2,299.9 ns |   255.32 ns |  13.99 ns |  0.94 |    0.02 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               | 2,326.2 ns |   203.45 ns |  11.15 ns |  0.96 |    0.02 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 4,600.4 ns | 9,607.59 ns | 526.62 ns |  1.89 |    0.19 | 0.4807 |      - |   5.93 KB |        1.01 |

After (`43c7975`), `RedactionPatternBenchmarks`:

| Method                  | PatternCount | Mean        | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------ |------------- |------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| LoopLongHeaderNoMatch   | 1            |   168.79 ns |   106.124 ns |   5.817 ns |  1.00 |    0.04 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 1            |   173.71 ns |    23.007 ns |   1.261 ns |  1.03 |    0.03 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopLongHeaderNoMatch   | 5            | 2,748.25 ns | 4,910.979 ns | 269.187 ns |  1.01 |    0.12 |      - |         - |          NA |
| RedactLongHeaderNoMatch | 5            | 2,684.06 ns |   503.878 ns |  27.619 ns |  0.98 |    0.09 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopMatch               | 1            |   219.33 ns |    36.481 ns |   2.000 ns |  1.00 |    0.01 | 0.0069 |      88 B |        1.00 |
| RedactMatch             | 1            |   223.89 ns |    33.981 ns |   1.863 ns |  1.02 |    0.01 | 0.0069 |      88 B |        1.00 |
|                         |              |             |              |            |       |         |        |           |             |
| LoopMatch               | 5            |   574.62 ns |   637.490 ns |  34.943 ns |  1.00 |    0.08 | 0.0067 |      88 B |        1.00 |
| RedactMatch             | 5            |   678.92 ns | 2,591.195 ns | 142.032 ns |  1.18 |    0.22 | 0.0067 |      88 B |        1.00 |
|                         |              |             |              |            |       |         |        |           |             |
| LoopShortNoMatch        | 1            |    62.52 ns |    29.300 ns |   1.606 ns |  1.00 |    0.03 |      - |         - |          NA |
| RedactShortNoMatch      | 1            |    69.29 ns |    35.568 ns |   1.950 ns |  1.11 |    0.04 |      - |         - |          NA |
|                         |              |             |              |            |       |         |        |           |             |
| LoopShortNoMatch        | 5            |   354.61 ns |     9.746 ns |   0.534 ns |  1.00 |    0.00 |      - |         - |          NA |
| RedactShortNoMatch      | 5            |    91.32 ns |    74.643 ns |   4.091 ns |  0.26 |    0.01 |      - |         - |          NA |

After (`43c7975`), `RedactionPerPatternBenchmarks`:

| Method            | PatternIndex | Mean        | Error      | StdDev    | Allocated |
|------------------ |------------- |------------:|-----------:|----------:|----------:|
| ShortNoMatch      | 0            |    67.04 ns |   7.224 ns |  0.396 ns |         - |
| LongHeaderNoMatch | 0            |   174.24 ns | 119.207 ns |  6.534 ns |         - |
| ShortNoMatch      | 1            |    69.33 ns |   7.329 ns |  0.402 ns |         - |
| LongHeaderNoMatch | 1            |   161.47 ns |  81.469 ns |  4.466 ns |         - |
| ShortNoMatch      | 2            |    82.69 ns |  68.500 ns |  3.755 ns |         - |
| LongHeaderNoMatch | 2            |   171.65 ns |  87.873 ns |  4.817 ns |         - |
| ShortNoMatch      | 3            |    65.42 ns |  18.551 ns |  1.017 ns |         - |
| LongHeaderNoMatch | 3            |   158.79 ns |  31.483 ns |  1.726 ns |         - |
| ShortNoMatch      | 4            |    59.50 ns |   6.945 ns |  0.381 ns |         - |
| LongHeaderNoMatch | 4            | 2,215.87 ns | 763.474 ns | 41.849 ns |         - |

After (`43c7975`), `RequestLoggingBenchmarks`:

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|-------:|----------:|
| HandleRequest | False   | 4           | False                   | 12.72 us |  4.899 us | 0.269 us | 0.5035 | 0.1678 |   6.23 KB |
| HandleRequest | False   | 4           | True                    | 19.76 us |  6.653 us | 0.365 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | False   | 32          | False                   | 18.59 us | 65.551 us | 3.593 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | False   | 32          | True                    | 28.83 us |  6.329 us | 0.347 us | 1.4648 |      - |  18.31 KB |
| HandleRequest | True    | 4           | False                   | 10.34 us | 42.872 us | 2.350 us | 0.5035 |      - |   6.23 KB |
| HandleRequest | True    | 4           | True                    | 18.56 us |  3.081 us | 0.169 us | 0.7629 |      - |   9.69 KB |
| HandleRequest | True    | 32          | False                   | 20.13 us |  1.840 us | 0.101 us | 1.1902 |      - |  14.85 KB |
| HandleRequest | True    | 32          | True                    | 27.05 us |  4.583 us | 0.251 us | 1.4648 | 0.0305 |  18.31 KB |

After (`43c7975`), `AuditBenchmarks`:

| Method            | WithHttpContext | DataEntries | Mean       | Error        | StdDev    | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|-------------:|----------:|-------:|----------:|
| AuditToNullSink   | False           | 0           |   494.7 ns |     11.81 ns |   0.65 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 0           | 1,264.3 ns |     74.62 ns |   4.09 ns | 0.1373 |    1728 B |
| AuditToNullSink   | False           | 32          |   498.7 ns |     81.10 ns |   4.45 ns | 0.0134 |     176 B |
| SerializeForSpool | False           | 32          | 2,868.7 ns | 11,366.19 ns | 623.02 ns | 0.2480 |    3144 B |
| AuditToNullSink   | True            | 0           | 1,231.0 ns |     96.19 ns |   5.27 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 0           | 1,409.0 ns |    470.18 ns |  25.77 ns | 0.1564 |    1968 B |
| AuditToNullSink   | True            | 32          | 1,228.2 ns |     87.99 ns |   4.82 ns | 0.0267 |     352 B |
| SerializeForSpool | True            | 32          | 3,623.2 ns |    501.47 ns |  27.49 ns | 0.2670 |    3384 B |

After (`43c7975`), `StepUpSinkBenchmarks`:

| Method          | WithCategoryRules | Mean     | Error     | StdDev   | Allocated |
|---------------- |------------------ |---------:|----------:|---------:|----------:|
| EmitBelowSwitch | False             | 18.13 ns | 35.519 ns | 1.947 ns |         - |
| EmitExported    | False             | 24.22 ns | 13.309 ns | 0.730 ns |         - |
| EmitBelowSwitch | True              | 49.57 ns |  2.431 ns | 0.133 ns |         - |
| EmitExported    | True              | 52.47 ns | 38.869 ns | 2.131 ns |         - |
