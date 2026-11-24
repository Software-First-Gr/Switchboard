# Performance experiments

This folder keeps reproducible, non-shipping experiments for changes that might improve Switchboard. The benchmark project is intentionally outside `Switchboard.slnx` and is not included in the NuGet package.

## Run

Use the .NET 11 SDK and runtime. Run the entire suite once with runtime-native async enabled and once with compiler-generated state machines:

```bash
dotnet build tools/Switchboard.Benchmarks/Switchboard.Benchmarks.csproj --configuration Release -t:Rebuild -p:UseRuntimeAsync=true
dotnet run --project tools/Switchboard.Benchmarks/Switchboard.Benchmarks.csproj --configuration Release --no-build -- --filter '*'
dotnet build tools/Switchboard.Benchmarks/Switchboard.Benchmarks.csproj --configuration Release -t:Rebuild -p:UseRuntimeAsync=false
dotnet run --project tools/Switchboard.Benchmarks/Switchboard.Benchmarks.csproj --configuration Release --no-build -- --filter '*'
```

BenchmarkDotNet 0.15.8 does not recognize `net11.0` in its normal process-isolated toolchain, so this harness uses its in-process emit toolchain. That is a measurement limitation: do not treat these as process-isolated results, and compare the two separate runs only as a directional experiment. Each run's setup inspects `AsyncStateMachineAttribute` on the loaded library methods and aborts if they do not match that build's expected lowering. Both fixtures include `MemoryDiagnoser`; the configured `ShortRun` is a quick comparison, not a statistically conclusive performance gate. For more stable numbers, change `Job.ShortRun` in `RuntimeAsyncConfig.cs` to `Job.Default` or a longer job and rerun on an otherwise idle machine.

The request suite measures typed sends with completed tasks and with a caller-controlled gate that proves the dispatch task is pending before release. It varies scope-per-dispatch on and off. The notification suite compares the current interface `foreach` loop against an array-index branch with an enumerable fallback, using the same ordered singleton handlers resolved from real Microsoft DI. The setup also executes both branches and checks the expected handler order, including a deliberately non-array enumerable.

The notification result is a **loop-level prototype**, not an end-to-end `Publish` comparison: it excludes Switchboard's telemetry, wrapper lookup, and actual application-handler work. Treat small deltas as potential measurement noise, not as evidence to change production code without a separate end-to-end validation.

## Results

### Sample run — 2026-11-24

Apple M4 Max, macOS 27.2.1, .NET SDK 11.0.100, .NET 11.0.0 (Arm64 RyuJIT), BenchmarkDotNet 0.15.8. Each `ShortRun` used one launch, three warmups and three measured iterations with the in-process emit toolchain; high process priority was denied. These measurements are directional only, not process-isolated or statistically strong.

Request dispatch results:

| Async mode | Scope per dispatch | Completed request | Caller-released suspension |
| --- | --- | ---: | ---: |
| State machines | Off | 22.96 ns / 24 B | 49.32 ns / 168 B |
| State machines | On | 68.80 ns / 296 B | 1,130 ns / 592 B |
| Runtime async | Off | 23.85 ns / 24 B | 52.01 ns / 168 B |
| Runtime async | On | 67.23 ns / 296 B | 1,413 ns / 751 B |

Notification loop results (time / allocated bytes per operation):

| Async mode | Handlers | Interface enumeration | Array branch with fallback |
| --- | ---: | ---: | ---: |
| State machines | 1 | 26.30 ns / 64 B | 22.75 ns / 32 B |
| State machines | 8 | 72.80 ns / 120 B | 63.34 ns / 88 B |
| State machines | 32 | 223.56 ns / 312 B | 208.12 ns / 280 B |
| Runtime async | 1 | 27.86 ns / 64 B | 23.42 ns / 32 B |
| Runtime async | 8 | 77.67 ns / 120 B | 66.61 ns / 88 B |
| Runtime async | 32 | 250.40 ns / 312 B | 216.50 ns / 280 B |

The array branch consistently saved 32 B and was 7–16% faster in this loop-only prototype, but the absolute time difference was 4–34 ns and the sample is short and in-process. Keep the experiment; before changing production code, benchmark the actual wrapper and full `Publish` path with a process-isolated toolchain and longer runs. The request results show scope-per-dispatch has noticeable overhead for completed requests; the suspended scoped case was especially noisy, and the runtime-async comparison does not establish a reliable win or regression. Do not commit generated `BenchmarkDotNet.Artifacts` unless they are intentionally curated as a report.