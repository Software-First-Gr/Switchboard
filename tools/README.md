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

These two original suites run in-process (emit toolchain); the runtime-async study below is process-isolated. That is a measurement limitation: do not treat these as process-isolated results, and compare the two separate runs only as a directional experiment. Each run's setup inspects `AsyncStateMachineAttribute` on the loaded library methods and aborts if they do not match that build's expected lowering. Both fixtures include `MemoryDiagnoser`; the configured `ShortRun` is a quick comparison, not a statistically conclusive performance gate. For more stable numbers, change `Job.ShortRun` in `RuntimeAsyncConfig.cs` to `Job.Default` or a longer job and rerun on an otherwise idle machine.

The request suite measures typed sends with completed tasks and with a caller-controlled gate that proves the dispatch task is pending before release. It varies scope-per-dispatch on and off. The notification suite compares the current interface `foreach` loop against an array-index branch with an enumerable fallback, using the same ordered singleton handlers resolved from real Microsoft DI. The setup also executes both branches and checks the expected handler order, including a deliberately non-array enumerable.

The notification result is a **loop-level prototype**, not an end-to-end `Publish` comparison: it excludes Switchboard's telemetry, wrapper lookup, and actual application-handler work. Treat small deltas as potential measurement noise, not as evidence to change production code without a separate end-to-end validation.

## Runtime-async study

Does .NET 11's runtime-native async do what it was built for, and what does it give Switchboard? It compares three builds of the same app:

| Variant | Switchboard | Application code (handlers, behaviors) |
| --- | --- | --- |
| **StateMachines** | compiler-generated state machines | compiler-generated state machines |
| **LibraryRA** | runtime async | state machines — what consumers would have got had the package shipped with it (it ships without, since 2.0.0) |
| **AllRA** | runtime async | runtime async — an app that also sets `<Features>runtime-async=on</Features>` |

```bash
# BenchmarkDotNet, process-isolated, each job rebuilt with its own switches (~40 min)
cd tools/Switchboard.Benchmarks && dotnet run -c Release -- --filter '*Switchboard.Benchmarks.Study.*'
# Stack traces, cold start and a Postgres load test (Testcontainers, needs Docker; ~12 min)
tools/run-runtime-async-study.sh --cold 30 --load-runs 5 --warmup 5 --duration 20
```

Each process checks `AsyncStateMachineAttribute` on the code it loaded and aborts if it is not the variant it was built as.

### Why runtime async exists, and what it promises

The C# compiler turns every `async` method into a state-machine struct, boxed onto the heap the first time the method suspends, plus a `Task` per call. Exceptions unwind one frame at a time: each awaiting frame catches and rethrows. The .NET team's 2023 experiment ([runtimelab report](https://github.com/dotnet/runtimelab/blob/feature/async2-experiment/docs/design/features/runtime-handled-tasks.md), [conclusion](https://github.com/dotnet/runtime/issues/94620)) moved suspension into the runtime instead. It found async-to-async calls several times cheaper, `Task` objects "essentially removed unless the task actually yields", and exceptions through deep chains far cheaper. It also found that a classic caller of a runtime-async method gains nothing. .NET 11 ships it as a preview opt-in and compiles its own libraries with it ([what's new](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)): "cleaner stack traces, better debuggability, and lower overhead".

### Results — 2026-11-24

Apple M4 Max (14 cores), macOS 27.2, .NET 11.0.0, BenchmarkDotNet 0.16.0-preview.2. Default job, one launch, 6 warmups, 15 iterations. Times are means; changes are against StateMachines. Run-to-run noise was 1–6% unless marked.

**1. The feature works as advertised — on plain async-to-async code.** N nested `async` methods calling each other directly, no mediator (`RawAsyncChainBenchmarks`):

| Depth | Case | StateMachines | AllRA | Allocated |
| ---: | --- | ---: | ---: | ---: |
| 1 | completes synchronously | 17.6 ns | −28% | 144 → 72 B |
| 4 | completes synchronously | 42.5 ns | −54% | 360 → 72 B |
| 8 | completes synchronously | 75.1 ns | −63% | 648 → 72 B |
| 4 | suspends once, resumes | 194 ns | −37% | 648 → 536 B |
| 8 | suspends once, resumes | 321 ns | −44% | 1,096 → 792 B |
| 4 | throws | 6.0 µs | −63% | 4.8 → 2.4 KB |
| 8 | throws | 10.5 µs | −78% | 11.1 → 2.9 KB |

LibraryRA equals StateMachines here (±6%): the chain is application code, so it is a control.

**2. Through Switchboard it does not.** `Send` through N async pass-through behaviors (`SendDepthBenchmarks`, `ExceptionDepthBenchmarks`, `PublishAndStreamBenchmarks`):

| Case | StateMachines | LibraryRA | AllRA |
| --- | ---: | ---: | ---: |
| Send, completes synchronously, 0 / 4 / 8 behaviors | 31 / 128 / 214 ns | −3% / +2% / +1% | +3% / +3% / +4% |
| Send, suspends once, 0 / 4 / 8 behaviors | 66 / 290 / 479 ns | +2% / −6% / 0% | **+27% / +37% / +50%** |
| …allocated | 200 / 1,096 / 1,992 B | +0 B | **+80 / +688 / +1,296 B** |
| Send, suspends, scope per dispatch, 8 behaviors | 589 ns, 2,376 B | +4%, **+88 B** | +42%, +1,456 B |
| Send that throws, 8 behaviors (sd 6–17%) | 11.6 µs | −1% to +32% | +38% to +43% |
| Publish to 4 handlers, first suspends | 187 ns, 640 B | +6%, **+232 B** | 0%, +184 B |
| Stream of 32 items | 533 ns | −2% | −3% |

- **LibraryRA (what the package ships) is a wash:** same speed, plus 88–232 B where Switchboard's own async methods (`DispatchInNewScope`, the notification loop) suspend.
- **AllRA is a regression** whenever the request suspends: up to 50% slower and 65% more allocation with eight behaviors, and exceptions get slower instead of 4× faster.
- **The reason is structural.** Between every behavior sits a `RequestHandlerDelegate<TResponse>` returning a `Task`, called from Switchboard's non-async `Pipeline.Invoke`. Each boundary is a classic-to-runtime-async transition, where the runtime has to materialize a `Task` and bridge its own continuation onto it — paying both models. The research predicted this ("a classic caller gains nothing"). The public pipeline shape is MediatR's (`next()` returns a `Task`), so it cannot simply be removed.
- One unexplained outlier: a synchronous throw with no behaviors measured −73% for both runtime-async variants, at 23–32% noise. It is not reproduced at any other depth, so treat it as noise.

**3. In an application against a real database the difference disappears into the I/O.** `GetOrder` through three async behaviors (counting, validating, timing) to a handler querying Postgres 17 with Npgsql 11, scope per dispatch on, transient handlers, 32 concurrent callers. Five interleaved 20 s runs per variant against one Testcontainers instance:

| Variant | Requests/s | p50 | p99 | Client CPU / request | Allocated / request |
| --- | ---: | ---: | ---: | ---: | ---: |
| StateMachines | 31,405 | 889 µs | 2.79 ms | 71.9 µs | 3,243 B |
| LibraryRA | 33,286 | 888 µs | 2.24 ms | 72.1 µs (+0.2%) | 3,354 B (+3.4%) |
| AllRA | 36,226 | 833 µs | 1.91 ms | 71.7 µs (−0.4%) | 3,860 B (+19.1%) |

Throughput and latency swung 17k–38k req/s between runs of the *same* variant (Postgres and the client share the machine), so those columns show no difference. CPU per request is flat. Allocation is the one stable signal (±0.5% across runs): +111 B per request for LibraryRA, +617 B for AllRA — more Gen0 collections (240 → 264 → 331 over 100 s), not more work.

**4. Stack traces: better only where everything is runtime async.** Live frames seen by a handler behind three async behaviors (`Probes.Stack`):

| Variant | Before the first await | After resuming from a real suspension | Exception stack |
| --- | ---: | ---: | ---: |
| StateMachines | 42 | 10 | 5 |
| LibraryRA | 39 | 10 | 5 |
| AllRA | 21 | 6 | 3 |

LibraryRA removes only Switchboard's own three builder frames. AllRA halves the stack. After a real suspension (`await Task.Yield()`), no variant shows the behaviors or the mediator: the resumed stack starts at the handler in all three. Microsoft's example that shows the full chain awaits completed tasks only. Exception stack traces were already clean.

**5. Size and startup.** `Switchboard.dll` shrinks from 72,192 to 66,048 bytes (−8.5%; 12 → 5 state-machine types). In a fresh process, the first Send + Publish + stream JIT-compiles 451 instead of 466 methods (−5% IL). Wall time is unchanged within noise: 22.5 / 23.6 / 22.1 ms. AllRA's second `Send` takes 151 µs instead of 25 µs, a tier-0 cost of runtime-async code that is gone after warm-up.

**6. What breaks it: the delegate, not the plumbing.** Eight levels of each call shape (`ChainShapeBenchmarks`), AllRA against StateMachines:

| Shape | Completes synchronously | Suspends once | Allocated (suspends) |
| --- | ---: | ---: | ---: |
| async → async (direct call) | −65% | −42% | −25% |
| async → interface method (as `IPipelineBehavior.Handle`) | −65% | −47% | −30% |
| async → plain Task-returning method → async (as `Pipeline.Invoke`) | −64% | −43% | −25% |
| async → delegate (as a behavior's `next()`) | **+8%** | **+59%** | **+106%** |

Switchboard's non-async forwarding methods are harmless: the JIT compiles runtime-async variants of them. The `RequestHandlerDelegate<TResponse>` a behavior awaits is what forces every boundary back onto classic `Task`s. That delegate is MediatR's API, so only a second, opt-in behavior interface whose `next` is an interface could avoid it.

### What Switchboard gains

- **Speed:** nothing measurable, in microbenchmarks or under load.
- **Allocations:** slightly worse — +3.4% per request in the database test, from Switchboard's own async methods suspending under non-async callers.
- **Stack traces:** three fewer frames, invisible next to the app's own state-machine frames.
- **Size:** 6 KB smaller.
- **For apps that also turn it on:** Switchboard's pipeline makes it a loss, even though the same app's plain async code gets 30–78% faster.

The feature works as designed. Switchboard is the kind of code it does not help: short async methods wrapped by delegate-based, non-async plumbing, called from code that is mostly not runtime async.

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