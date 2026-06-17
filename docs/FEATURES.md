# OrionSaga Features

A complete reference of what OrionSaga does today, at version **0.1.0**. Every item here maps to a
public type or behavior in the shipped library. For where the library may go next, see
[ROADMAP.md](ROADMAP.md).

---

## Table of contents

1. [Ordered step execution](#1-ordered-step-execution)
2. [Automatic reverse compensation](#2-automatic-reverse-compensation)
3. [The shared context](#3-the-shared-context)
4. [Result reporting](#4-result-reporting)
5. [Compensation-failure isolation](#5-compensation-failure-isolation)
6. [Cancellation that still rolls back](#6-cancellation-that-still-rolls-back)
7. [Observers](#7-observers)
8. [Diagnostics and metrics](#8-diagnostics-and-metrics)
9. [Dependency injection](#9-dependency-injection)
10. [Reusable, immutable sagas](#10-reusable-immutable-sagas)
11. [Multi-targeting and dependencies](#11-multi-targeting-and-dependencies)

---

## 1. Ordered step execution

A saga is built from steps with `SagaBuilder<TContext>.AddStep`. Steps run in the order they are
added, each receiving the same shared context and the run's `CancellationToken`. A forward action is
`Func<TContext, CancellationToken, Task>`.

```csharp
var saga = new SagaBuilder<OrderContext>()
    .AddStep("reserve-stock", (ctx, ct) => inventory.ReserveAsync(ctx.OrderId, ct))
    .AddStep("charge-card",   (ctx, ct) => payments.ChargeAsync(ctx.OrderId, ctx.Amount, ct))
    .Build();
```

`AddStep` has two overloads: one that takes a name plus the forward (and optional compensating)
delegates, and one that takes an already-constructed `SagaStep<TContext>`.

---

## 2. Automatic reverse compensation

Each step may pair its forward action with a compensating action that undoes it. When a later step
throws, OrionSaga compensates every completed step in **reverse** order. The step that threw is not
compensated, because its forward action never completed.

```csharp
.AddStep("charge-card",
    execute:    (ctx, ct) => payments.ChargeAsync(ctx.OrderId, ctx.Amount, ct),
    compensate: (ctx, ct) => payments.RefundAsync(ctx.OrderId, ct))
```

The `compensate` argument is optional. A step added without one uses a no-op during rollback, so
read-only or already-atomic steps need no undo logic.

---

## 3. The shared context

`TContext` is your own type. OrionSaga never inspects or constructs it; it only threads the single
instance you pass to `RunAsync` through every step and compensation. Use it to carry ids, amounts,
and any intermediate state a later step or compensation needs.

```csharp
public sealed class OrderContext
{
    public required string OrderId { get; init; }
    public decimal Amount { get; init; }
}
```

There is no constraint on `TContext`, so a record, class, or even `object` works.

---

## 4. Result reporting

`RunAsync` returns a `SagaResult` describing the run:

| Member | Meaning |
|--------|---------|
| `Succeeded` | True when every step completed. |
| `FailedStep` | The name of the step that threw, or null on success. |
| `Failure` | The exception that failed the saga, or null on success. |
| `CompensationFailures` | The compensations that themselves threw during rollback. |
| `RolledBackCleanly` | True when the saga failed but every completed step compensated cleanly. |

An empty saga (no steps) returns a successful result.

---

## 5. Compensation-failure isolation

If a compensation throws during rollback, OrionSaga records it as a `CompensationFailure(StepName,
Exception)` and continues compensating the remaining steps. One bad compensation never strands the
others. A non-empty `CompensationFailures` list means some effects may not have been undone and need
manual attention; `RolledBackCleanly` is false in that case.

---

## 6. Cancellation that still rolls back

`RunAsync` accepts a `CancellationToken` that cancels forward progress. If a step observes the token
and throws `OperationCanceledException`, that fails the saga like any other exception. Critically,
compensation runs with `CancellationToken.None`, so a saga cancelled mid-flight still unwinds the
work it already did rather than abandoning it half-done.

---

## 7. Observers

Implement `ISagaObserver` and attach it with `WithObserver` to receive progress callbacks:

- `OnStepCompleted(stepName)`
- `OnStepFailed(stepName, exception)`
- `OnCompensated(stepName)`
- `OnCompensationFailed(stepName, exception)`

Observers are observability only. Any exception an observer throws is caught and swallowed by the
executor, so an observer fault can never disrupt orchestration or rollback. When no observer is
registered, an internal `NullSagaObserver` no-op is used.

---

## 8. Diagnostics and metrics

`SagaDiagnostics` exposes a `System.Diagnostics.Metrics.Meter` named `Moongazing.OrionSaga` (the
`SagaDiagnostics.MeterName` constant) with three counters, each tagged with an `outcome`:

| Instrument | Tag values |
|------------|------------|
| `orionsaga.runs` | `succeeded` / `failed` |
| `orionsaga.steps` | `completed` / `failed` |
| `orionsaga.compensations` | `compensated` / `failed` |

Attach an instance with `WithDiagnostics` and subscribe with OpenTelemetry by meter name.
`SagaDiagnostics` owns the meter and is `IDisposable`.

---

## 9. Dependency injection

`AddOrionSaga()` registers `SagaDiagnostics` as a singleton (via `TryAddSingleton`, so it is safe to
call more than once). Sagas themselves are built with `SagaBuilder` per definition rather than
resolved from the container, so the diagnostics singleton is the only registration. Inject it where
you build sagas and pass it to `WithDiagnostics`.

---

## 10. Reusable, immutable sagas

`Build()` snapshots the configured steps into an array and produces a `Saga<TContext>`. The result
holds no per-run state, so a single built saga can be reused across many `RunAsync` calls, including
concurrently with different context instances.

---

## 11. Multi-targeting and dependencies

- Targets `net8.0`, `net9.0`, and `net10.0`.
- The only runtime dependency is `Microsoft.Extensions.DependencyInjection.Abstractions`.
- Built with nullable reference types enabled, latest analyzers, and `TreatWarningsAsErrors`.
- Ships an XML documentation file and a symbol package.
</content>
