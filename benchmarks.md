# Benchmarks

Micro-benchmarks for OrionSaga's in-process orchestrator, built with
[BenchmarkDotNet](https://benchmarkdotnet.org/). They cover the hot paths a consumer actually pays
for at runtime: assembling a saga, running it forward to completion, unwinding it when a step fails,
and the overhead of the optional observer and diagnostics hooks. Everything runs in memory; step
bodies complete synchronously via `Task.CompletedTask`, so no measurement touches a database, broker,
network, or real I/O. The numbers reflect orchestration cost alone.

The suite lives in `benchmarks/Moongazing.OrionSaga.Benchmarks` and references the library directly.

## Benchmark classes

| Class | What it measures |
|-------|------------------|
| `SagaBuildBenchmarks` | Cost of assembling a saga: chaining `SagaBuilder.AddStep` and the terminal `Build()` that snapshots the steps to an array. Swept over 3, 10, and 25 steps. |
| `SagaRunBenchmarks` | The happy path: `Saga.RunAsync` driving every step to completion over a prebuilt saga (the completed-step stack, observer dispatch, and synchronous-await fast path). Swept over 3, 10, and 25 steps. |
| `SagaCompensationBenchmarks` | The failure-and-rollback path: the final step throws, forcing every completed step to compensate in reverse and a `SagaResult` to be assembled. Compensations all succeed, so the run rolls back cleanly. Swept over 3, 10, and 25 steps. |
| `SagaInstrumentationBenchmarks` | Overhead the optional hooks add to a successful 10-step run: bare (baseline) vs. an attached `ISagaObserver` vs. attached `SagaDiagnostics` metrics. |

Each class is annotated with `[MemoryDiagnoser]`, so allocations are reported alongside time.

## Running

Run the whole suite from the repository root:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionSaga.Benchmarks
```

Filter to one class (or use `*` to be prompted interactively):

```
dotnet run -c Release --project benchmarks/Moongazing.OrionSaga.Benchmarks -- --filter "*SagaRunBenchmarks*"
```

List the available benchmarks without running them:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionSaga.Benchmarks -- --list flat
```

Each benchmark class declares two jobs, `.NET 8.0` and `.NET 9.0`, so a run reports both side by
side. Those runtimes must be installed for the run to execute (the project itself targets `net10.0`
as its build/host framework).

> Results are intentionally not committed. Micro-benchmark numbers are hardware- and
> runtime-specific; run the suite on your own machine to get figures that mean anything for your
> environment.
