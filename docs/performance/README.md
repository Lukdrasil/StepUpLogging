# Performance

The benchmarks live in `tests/Lukdrasil.StepUpLogging.Benchmarks` (BenchmarkDotNet 0.15.8, .NET 10). They
measure the library's hot paths in isolation so a change to `src/` can be compared against a recorded number.

- [baseline.md](baseline.md): what the Logging and Audit suites measured, on which machine and commit.
- [analysis.md](analysis.md): the hot spots ranked by events per second, their causes, and the follow-up tasks.

The k6 load test in `tests/k6` measures a whole application under load. These benchmarks measure one
component at a time, so they say where the time goes; k6 says whether it matters end to end.

## Running

Always in Release; a Debug build refuses to run (BenchmarkDotNet's optimisation validator).

```bash
# list every benchmark
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- --list flat

# the Logging and Audit suites, the way baseline.md was produced
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Logging Audit --job short --exporters github json

# one class
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --filter "*PipelineBenchmarks*" --job short --exporters github

# the Disk suite
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Disk --exporters github
```

`--job short` is BenchmarkDotNet's ShortRun: one process launch, three warmup and three measured
iterations. It is enough to rank hot spots and to spot a regression of tens of percent; for a claim of a
few percent, drop `--job short` to run the default job. Results land in `BenchmarkDotNet.Artifacts/`
(git-ignored); the `github` exporter writes the markdown tables, `json` the raw data.

Close other work first and run on AC power: the numbers are only comparable with another run on the same
machine, so record the hardware header BenchmarkDotNet prints next to any result you keep.

### Suites

Every benchmark class carries exactly one suite category, which `--anyCategories` selects.

| Category | Classes | Measures |
|---|---|---|
| `Logging` | `StepUpSinkBenchmarks`, `PreErrorBufferSinkBenchmarks`, `EnricherBenchmarks`, `RedactionPatternBenchmarks`, `PipelineBenchmarks`, `RequestLoggingBenchmarks`, `RequestPathRedactionBenchmarks` | CPU and allocation of the logging pipeline, per event or per request |
| `Audit` | `AuditBenchmarks` | CPU and allocation of an audit write before the disk |
| `Disk` | `DurableWriteBenchmarks`, `FullSpoolDropBenchmarks`, `SpoolDrainBenchmarks` | The encrypted spool: fsync per record, the full-spool drop, the drain |

`Pipeline` and `RequestLogging` build a real host with `AddStepUpLogging()` and no OTLP, console or file
output; their only output sink counts events. They show what the library adds to a log call or a request,
not what an exporter costs.

### The Disk suite

The Disk benchmarks write real files and fsync them, so they are slow, they stress the disk, and the number
depends on the device. They keep their spool under the build output. That is deliberate: `/tmp` is often a
RAM disk, where an fsync costs nothing and a durable-write benchmark measures nothing. Point
`STEPUP_BENCH_SPOOL_ROOT` at the volume you want to measure:

```bash
STEPUP_BENCH_SPOOL_ROOT=/mnt/nvme/spool-bench dotnet run -c Release \
  --project tests/Lukdrasil.StepUpLogging.Benchmarks -- --anyCategories Disk --exporters github
```

The directory is created and deleted by the benchmarks. `FullSpoolDropBenchmarks` fills 100 000 files,
so give it room. There is no recorded Disk baseline in this folder.

## The smoke test

`tests/Lukdrasil.StepUpLogging/BenchmarkSmokeTests.cs` runs each Logging and Audit class once inside
`dotnet test`, so a benchmark cannot rot unnoticed. `BenchmarkSmokeRunner` is a small reflection runner,
not BenchmarkDotNet: it sets each public `[Params]` property to its first value, calls `[GlobalSetup]`,
invokes each `[Benchmark]` once, awaits a `Task` or `ValueTask` result, then calls `[GlobalCleanup]`.
It proves the scenario builds and runs and measures nothing. The contract a benchmark class keeps:

- It is `public`, in `Lukdrasil.StepUpLogging.Benchmarks`, with `[MemoryDiagnoser]`.
- It has exactly one suite category: `Logging`, `Audit` or `Disk` (a test fails otherwise).
- It is either listed in `SmokeRunClassNames` in the test, or marked `Disk`. The Disk classes are not run
  by the smoke test because they write and fsync real files.
- Its `[Params]` are public properties, and the first value of each is cheap.
- Its `[GlobalSetup]` throws `InvalidOperationException` when the scenario does not do what its name
  says (an event named "dropped" that was exported, a flush that flushed nothing). A benchmark that
  silently measures the wrong path reports a number that is worse than none.
- Its public members use only public types.

The smoke run covers the first value of each parameter only. Run the class with
`--job dry` to run every combination once:

```bash
dotnet run -c Release --project tests/Lukdrasil.StepUpLogging.Benchmarks -- \
  --anyCategories Logging Audit --job dry
```

### Allocation guards

Five claims are also pinned by ordinary unit tests, which fail the build when they stop holding:

| Test | Claim |
|---|---|
| `StepUpSink_DroppedBelowLevelSwitch_DoesNotAllocate` | `StepUpSink.Emit` allocates nothing for an event below the switch |
| `StepUpSink_DroppedWithDenyListAndFloors_DoesNotAllocate` | the same with the deny-list and category floors on |
| `Hold_RepeatedInOneW3CTrace_DoesNotAllocate` | `PreErrorBufferSink.Hold` allocates nothing once an event is in a trace's buffer |
| `Redact_ReturnsSameInstance_WhenNoPatternMatches` | a value no pattern matches comes back as the same string |
| `TryGetFloor_DoesNotAllocate_QS01` | `CategoryFloorMap.TryGetFloor` allocates nothing |

## Reading a result

`Mean` is per operation: per event for the sink, enricher and pipeline rows, per request for
`RequestLoggingBenchmarks`, per hold for `PreErrorBufferSinkBenchmarks.Hold` (it declares
`OperationsPerInvoke`). Events per second is `1 000 000 000 / Mean(ns)` for one thread. `Allocated` is
managed bytes per operation. `Ratio` against a `Baseline = true` row is the library's cost over that
baseline: `PipelineBenchmarks.PlainSerilogToNullSink` for the pipeline, `EnricherBenchmarks.NewEvent` for
the enrichers.
