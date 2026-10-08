# Baseline

The numbers every optimisation in [analysis.md](analysis.md) is measured against. Logging and Audit
suites only; the Disk suite was not run (see below).

- Commit: `0705cdd3e1de91c49359ee69f2d19ac19f7406df` (`src/` is unchanged from `61d192a`; later commits touch documentation only)
- Date: 2026-10-08
- Command, from the repository root:

```bash
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Logging Audit --job short --exporters github json
```

- Machine state: laptop on AC power, CPU governor `powersave` (the default of this machine; not pinned to `performance`), desktop session running. The run took about ten minutes for 76 benchmarks.

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

`--job short` is one launch, three warmup and three measured iterations, so the `Error` column (a 99.9 %
confidence half-interval over three samples) is wide on many rows and `StdDev` is the better guide to
repeatability. The suite was run twice on this machine, about twenty minutes apart (the first run lacked
only the `MemoryDiagnoser` on `RequestPathRedactionBenchmarks`). Most rows agreed within 10 %, the
allocation columns agreed exactly except for the held-event pipeline rows (1432 B against 1464 B) and
`PreErrorBufferSinkBenchmarks.Hold` moved by up to 20 % (394 ns against 468 ns with many traces on one
thread, 656 ns against 772 ns on eight). Treat a difference under 25 % between two rows as unresolved;
the ranking in `analysis.md` rests on differences of 2x and more. The rows whose `Error` exceeds
their `Mean` (`StepUpSinkBenchmarks` without category rules, most `RequestLoggingBenchmarks` rows
without the request summary, `EnricherBenchmarks.AlwaysExport` in an activity with four properties)
are only good to an order of magnitude and are quoted as such. Rerun a class
without `--job short` before using a number to claim a few percent.

Rows in bold are BenchmarkDotNet marking the first row of a parameter combination. `Mean` is per
operation (per event, per request, per audit write; `PreErrorBufferSinkBenchmarks.Hold` is per hold
through `OperationsPerInvoke`). `Allocated` is managed bytes per operation; `-` is zero.

The output sink behind every pipeline and request row only counts events, so these numbers contain no
exporter (no OTLP, console or file serialization or I/O).

The Disk suite (`DurableWriteBenchmarks`, `FullSpoolDropBenchmarks`, `SpoolDrainBenchmarks`) was not
run: it fills disks and fsyncs, depends on the volume, and was out of scope for this baseline. Run it
with `--anyCategories Disk` and `STEPUP_BENCH_SPOOL_ROOT` as described in [README.md](README.md). Where
`analysis.md` needs the cost of an fsync it says that the figure is not measured here.


## Pipeline: one log call through `AddStepUpLogging()`

`PipelineBenchmarks`

| Method                 | Scenario             | InActivity | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |--------------------- |----------- |-----------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| **PlainSerilogToNullSink** | **Default**              | **False**      |   **238.1 ns** |  **21.90 ns** |  **1.20 ns** |  **1.00** |    **0.01** | **0.0334** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | Default              | False      |   685.4 ns | 235.14 ns | 12.89 ns |  2.88 |    0.05 | 0.1078 |      - |    1360 B |        3.21 |
| InformationHeld        | Default              | False      |   844.3 ns |  85.12 ns |  4.67 ns |  3.55 |    0.02 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | Default              | False      |   681.9 ns |  40.07 ns |  2.20 ns |  2.86 |    0.01 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| **PlainSerilogToNullSink** | **Default**              | **True**       |   **251.0 ns** | **200.29 ns** | **10.98 ns** |  **1.00** |    **0.05** | **0.0334** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | Default              | True       |   735.5 ns | 752.87 ns | 41.27 ns |  2.93 |    0.18 | 0.1135 |      - |    1432 B |        3.38 |
| InformationHeld        | Default              | True       |   929.7 ns | 291.70 ns | 15.99 ns |  3.71 |    0.15 | 0.1163 | 0.0286 |    1464 B |        3.45 |
| WarningExported        | Default              | True       |   745.4 ns | 201.00 ns | 11.02 ns |  2.97 |    0.12 | 0.1135 |      - |    1432 B |        3.38 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| **PlainSerilogToNullSink** | **Redaction**            | **False**      |   **236.8 ns** |  **39.27 ns** |  **2.15 ns** |  **1.00** |    **0.01** | **0.0336** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | Redaction            | False      | 1,309.4 ns |  59.95 ns |  3.29 ns |  5.53 |    0.05 | 0.1202 |      - |    1512 B |        3.57 |
| InformationHeld        | Redaction            | False      | 1,564.3 ns | 435.65 ns | 23.88 ns |  6.61 |    0.10 | 0.1202 | 0.0229 |    1512 B |        3.57 |
| WarningExported        | Redaction            | False      | 1,353.1 ns | 956.33 ns | 52.42 ns |  5.71 |    0.20 | 0.1202 |      - |    1512 B |        3.57 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| **PlainSerilogToNullSink** | **Redaction**            | **True**       |   **260.8 ns** |  **69.75 ns** |  **3.82 ns** |  **1.00** |    **0.02** | **0.0336** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | Redaction            | True       | 1,453.0 ns | 129.52 ns |  7.10 ns |  5.57 |    0.07 | 0.1259 |      - |    1600 B |        3.77 |
| InformationHeld        | Redaction            | True       | 1,669.9 ns | 303.69 ns | 16.65 ns |  6.40 |    0.10 | 0.1259 | 0.0305 |    1600 B |        3.77 |
| WarningExported        | Redaction            | True       | 1,499.3 ns | 137.21 ns |  7.52 ns |  5.75 |    0.08 | 0.1259 |      - |    1600 B |        3.77 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| **PlainSerilogToNullSink** | **FloorsAndNeverStepUp** | **False**      |   **235.2 ns** |  **41.12 ns** |  **2.25 ns** |  **1.00** |    **0.01** | **0.0334** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | FloorsAndNeverStepUp | False      |   672.6 ns |  37.65 ns |  2.06 ns |  2.86 |    0.02 | 0.1078 |      - |    1360 B |        3.21 |
| InformationHeld        | FloorsAndNeverStepUp | False      |   872.0 ns | 277.67 ns | 15.22 ns |  3.71 |    0.06 | 0.1078 | 0.0210 |    1360 B |        3.21 |
| WarningExported        | FloorsAndNeverStepUp | False      |   695.0 ns |  42.29 ns |  2.32 ns |  2.95 |    0.03 | 0.1078 |      - |    1360 B |        3.21 |
|                        |                      |            |            |           |          |       |         |        |        |           |             |
| **PlainSerilogToNullSink** | **FloorsAndNeverStepUp** | **True**       |   **248.3 ns** |  **80.60 ns** |  **4.42 ns** |  **1.00** |    **0.02** | **0.0334** |      **-** |     **424 B** |        **1.00** |
| DebugDropped           | FloorsAndNeverStepUp | True       |   766.0 ns |  21.49 ns |  1.18 ns |  3.09 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |
| InformationHeld        | FloorsAndNeverStepUp | True       |   902.4 ns |  44.58 ns |  2.44 ns |  3.63 |    0.06 | 0.1135 | 0.0277 |    1432 B |        3.38 |
| WarningExported        | FloorsAndNeverStepUp | True       |   771.4 ns | 102.14 ns |  5.60 ns |  3.11 |    0.05 | 0.1135 |      - |    1432 B |        3.38 |


## StepUpSink: the decision per event

`StepUpSinkBenchmarks`

| Method          | WithCategoryRules | Mean     | Error     | StdDev   | Allocated |
|---------------- |------------------ |---------:|----------:|---------:|----------:|
| **EmitBelowSwitch** | **False**             | **15.71 ns** | **33.514 ns** | **1.837 ns** |         **-** |
| EmitExported    | False             | 18.51 ns | 42.853 ns | 2.349 ns |         - |
| **EmitBelowSwitch** | **True**              | **46.75 ns** |  **5.671 ns** | **0.311 ns** |         **-** |
| EmitExported    | True              | 50.93 ns |  1.330 ns | 0.073 ns |         - |


## PreErrorBufferSink: hold and flush

`PreErrorBufferSinkBenchmarks`

| Method              | Contexts | Threads | Mean        | Error        | StdDev    | Gen0   | Gen1   | Allocated |
|-------------------- |--------- |-------- |------------:|-------------:|----------:|-------:|-------:|----------:|
| **Hold**                | **1**        | **1**       |    **127.2 ns** |     **24.69 ns** |   **1.35 ns** |      **-** |      **-** |         **-** |
| FillAndFlushOnError | 1        | 1       | 13,304.7 ns |  5,296.37 ns | 290.31 ns | 0.0610 |      - |     824 B |
| **Hold**                | **1**        | **8**       |    **236.7 ns** |     **95.41 ns** |   **5.23 ns** |      **-** |      **-** |         **-** |
| FillAndFlushOnError | 1        | 8       | 13,333.3 ns |  2,361.41 ns | 129.44 ns | 0.0610 |      - |     824 B |
| **Hold**                | **4096**     | **1**       |    **468.2 ns** |    **319.74 ns** |  **17.53 ns** | **0.0820** | **0.0815** |    **1032 B** |
| FillAndFlushOnError | 4096     | 1       | 13,262.5 ns | 13,017.16 ns | 713.51 ns | 0.0610 |      - |     824 B |
| **Hold**                | **4096**     | **8**       |    **771.9 ns** |    **135.47 ns** |   **7.43 ns** | **0.0830** | **0.0820** |    **1031 B** |
| FillAndFlushOnError | 4096     | 8       | 14,306.4 ns |    388.96 ns |  21.32 ns | 0.0610 |      - |     824 B |

`FillAndFlushOnError` does not use `Contexts` or `Threads`, so its four rows are one measurement taken
four times; read their spread (13.3 to 14.3 us) as its noise. `Hold` is one benchmark per row.


## Root enrichers

`EnricherBenchmarks`

| Method           | InActivity | StringProperties | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------- |----------- |----------------- |------------:|-----------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| **NewEvent**         | **False**      | **4**                |    **734.0 ns** |   **463.0 ns** |  **25.38 ns** |  **1.00** |    **0.04** | **0.1440** |      **-** |   **1.77 KB** |        **1.00** |
| ActivityContext  | False      | 4                |    781.9 ns |   151.7 ns |   8.31 ns |  1.07 |    0.03 | 0.1440 |      - |   1.77 KB |        1.00 |
| AlwaysExport     | False      | 4                |    925.5 ns |   355.9 ns |  19.51 ns |  1.26 |    0.04 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | False      | 4                |  3,315.8 ns |   953.9 ns |  52.29 ns |  4.52 |    0.15 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |            |           |       |         |        |        |           |             |
| **NewEvent**         | **False**      | **16**               |  **2,340.1 ns** |   **389.1 ns** |  **21.33 ns** |  **1.00** |    **0.01** | **0.4768** | **0.0076** |   **5.88 KB** |        **1.00** |
| ActivityContext  | False      | 16               |  2,374.2 ns |   157.9 ns |   8.66 ns |  1.01 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| AlwaysExport     | False      | 16               |  2,376.7 ns |   537.9 ns |  29.48 ns |  1.02 |    0.01 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | False      | 16               | 10,313.4 ns |   520.6 ns |  28.54 ns |  4.41 |    0.04 | 0.5035 |      - |    6.2 KB |        1.05 |
|                  |            |                  |             |            |           |       |         |        |        |           |             |
| **NewEvent**         | **True**       | **4**                |    **743.2 ns** |   **122.6 ns** |   **6.72 ns** |  **1.00** |    **0.01** | **0.1440** |      **-** |   **1.77 KB** |        **1.00** |
| ActivityContext  | True       | 4                |    889.4 ns |   215.4 ns |  11.80 ns |  1.20 |    0.02 | 0.1879 |      - |    2.3 KB |        1.31 |
| AlwaysExport     | True       | 4                |    821.5 ns | 2,120.5 ns | 116.23 ns |  1.11 |    0.14 | 0.1860 |      - |   2.28 KB |        1.29 |
| RedactProperties | True       | 4                |  3,309.7 ns | 1,227.8 ns |  67.30 ns |  4.45 |    0.09 | 0.1526 |      - |    1.9 KB |        1.08 |
|                  |            |                  |             |            |           |       |         |        |        |           |             |
| **NewEvent**         | **True**       | **16**               |  **2,420.7 ns** | **1,507.5 ns** |  **82.63 ns** |  **1.00** |    **0.04** | **0.4768** | **0.0076** |   **5.88 KB** |        **1.00** |
| ActivityContext  | True       | 16               |  2,322.8 ns |   444.6 ns |  24.37 ns |  0.96 |    0.03 | 0.4807 | 0.0038 |    5.9 KB |        1.00 |
| AlwaysExport     | True       | 16               |  2,441.4 ns |   296.7 ns |  16.26 ns |  1.01 |    0.03 | 0.4768 | 0.0076 |   5.88 KB |        1.00 |
| RedactProperties | True       | 16               | 10,522.9 ns | 2,115.6 ns | 115.96 ns |  4.35 |    0.13 | 0.5035 |      - |    6.2 KB |        1.05 |


## Redaction patterns

`RedactionPatternBenchmarks`

| Method                  | PatternCount | Mean        | Error     | StdDev   | Gen0   | Allocated |
|------------------------ |------------- |------------:|----------:|---------:|-------:|----------:|
| **RedactShortNoMatch**      | **1**            |    **68.38 ns** |  **84.49 ns** | **4.631 ns** |      **-** |         **-** |
| RedactLongHeaderNoMatch | 1            |   167.55 ns |  87.28 ns | 4.784 ns |      - |         - |
| RedactMatch             | 1            |   220.90 ns | 119.63 ns | 6.557 ns | 0.0069 |      88 B |
| **RedactShortNoMatch**      | **5**            |   **352.13 ns** |  **34.38 ns** | **1.884 ns** |      **-** |         **-** |
| RedactLongHeaderNoMatch | 5            | 2,899.81 ns |  46.24 ns | 2.534 ns |      - |         - |
| RedactMatch             | 5            |   601.83 ns |  26.82 ns | 1.470 ns | 0.0067 |      88 B |


## Request logging middleware

`RequestLoggingBenchmarks`

| Method        | Stepped | HeaderCount | AlwaysLogRequestSummary | Mean     | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|-------------- |-------- |------------ |------------------------ |---------:|----------:|---------:|-------:|-------:|----------:|
| **HandleRequest** | **False**   | **4**           | **False**                   | **12.69 μs** | **45.622 μs** | **2.501 μs** | **0.5188** |      **-** |    **6.4 KB** |
| **HandleRequest** | **False**   | **4**           | **True**                    | **23.78 μs** |  **1.626 μs** | **0.089 μs** | **0.7935** | **0.2441** |   **9.86 KB** |
| **HandleRequest** | **False**   | **32**          | **False**                   | **30.45 μs** | **56.681 μs** | **3.107 μs** | **1.2207** |      **-** |  **15.03 KB** |
| **HandleRequest** | **False**   | **32**          | **True**                    | **42.38 μs** | **21.490 μs** | **1.178 μs** | **1.4648** |      **-** |  **18.49 KB** |
| **HandleRequest** | **True**    | **4**           | **False**                   | **13.61 μs** | **22.518 μs** | **1.234 μs** | **0.5188** |      **-** |    **6.4 KB** |
| **HandleRequest** | **True**    | **4**           | **True**                    | **22.11 μs** | **26.809 μs** | **1.469 μs** | **0.7935** |      **-** |   **9.86 KB** |
| **HandleRequest** | **True**    | **32**          | **False**                   | **29.94 μs** | **40.714 μs** | **2.232 μs** | **1.2207** |      **-** |  **15.03 KB** |
| **HandleRequest** | **True**    | **32**          | **True**                    | **39.71 μs** |  **1.742 μs** | **0.096 μs** | **1.4648** |      **-** |  **18.49 KB** |


## Request path redaction

`RequestPathRedactionBenchmarks`

| Method                   | Mean       | Error    | StdDev   | Gen0   | Allocated |
|------------------------- |-----------:|---------:|---------:|-------:|----------:|
| RedactPathAndRouteValues | 1,485.5 ns | 247.5 ns | 13.56 ns | 0.0286 |     376 B |
| EnrichRequestPath        |   705.8 ns | 295.6 ns | 16.21 ns | 0.0095 |     128 B |


## Audit write before the disk

`AuditBenchmarks`

| Method            | WithHttpContext | DataEntries | Mean       | Error       | StdDev   | Gen0   | Allocated |
|------------------ |---------------- |------------ |-----------:|------------:|---------:|-------:|----------:|
| **AuditToNullSink**   | **False**           | **0**           |   **494.7 ns** |   **132.02 ns** |  **7.24 ns** | **0.0134** |     **176 B** |
| SerializeForSpool | False           | 0           | 1,302.0 ns |   173.70 ns |  9.52 ns | 0.1373 |    1728 B |
| **AuditToNullSink**   | **False**           | **32**          |   **487.6 ns** |    **32.97 ns** |  **1.81 ns** | **0.0134** |     **176 B** |
| SerializeForSpool | False           | 32          | 3,424.3 ns |   312.82 ns | 17.15 ns | 0.2480 |    3144 B |
| **AuditToNullSink**   | **True**            | **0**           | **2,109.9 ns** |    **53.26 ns** |  **2.92 ns** | **0.0267** |     **352 B** |
| SerializeForSpool | True            | 0           | 1,420.3 ns |   650.99 ns | 35.68 ns | 0.1564 |    1968 B |
| **AuditToNullSink**   | **True**            | **32**          | **2,112.0 ns** |   **560.67 ns** | **30.73 ns** | **0.0267** |     **352 B** |
| SerializeForSpool | True            | 32          | 3,574.5 ns | 1,559.78 ns | 85.50 ns | 0.2670 |    3384 B |
