# OrionSaga Features

A complete reference of what OrionSaga does today, at version **0.7.0**. Every item here maps to a
public type or behavior in the shipped library. The [README](../README.md) has runnable samples for
each feature. For where the library may go next, see [ROADMAP.md](ROADMAP.md).

---

## Table of contents

1. [Ordered step execution](#1-ordered-step-execution)
2. [Automatic reverse compensation](#2-automatic-reverse-compensation)
3. [The shared context](#3-the-shared-context)
4. [Result reporting](#4-result-reporting)
5. [Compensation-failure isolation](#5-compensation-failure-isolation)
6. [Cancellation that still rolls back](#6-cancellation-that-still-rolls-back)
7. [Per-step timeouts](#7-per-step-timeouts)
8. [Typed step results](#8-typed-step-results)
9. [Retries and the rollback budget](#9-retries-and-the-rollback-budget)
10. [Conditional steps, sub-sagas and parallel groups](#10-conditional-steps-sub-sagas-and-parallel-groups)
11. [Observers and run snapshots](#11-observers-and-run-snapshots)
12. [Metrics](#12-metrics)
13. [Tracing](#13-tracing)
14. [Dependency injection](#14-dependency-injection)
15. [Reusable sagas](#15-reusable-sagas)
16. [Multi-targeting and dependencies](#16-multi-targeting-and-dependencies)

---

## 1. Ordered step execution

A saga is built from steps with `SagaBuilder<TContext>`. Steps run in the order they are added, each
receiving the same shared context and a `CancellationToken`. A forward action is
`Func<TContext, CancellationToken, Task>`.

```csharp
using Moongazing.OrionSaga.Orchestration;

var saga = new SagaBuilder<OrderContext>()
    .AddStep("reserve-stock", (order, ct) => Task.CompletedTask)
    .AddStep("charge-card",   (order, ct) => Task.CompletedTask)
    .Build();

public sealed class OrderContext
{
    public required string OrderId { get; init; }
    public decimal Amount { get; init; }
}
```

`AddStep` has two overloads: one that takes a name plus the forward delegate and optional
compensation, timeout and retry policies, and one that takes an already-constructed
`SagaStep<TContext>`. The other builder methods are listed in the sections below.

---

## 2. Automatic reverse compensation

Each step may pair its forward action with a compensating action that undoes it. When a step's
forward action throws, OrionSaga compensates every completed step in **reverse** order. The step that
threw is not compensated, because its forward action never completed.

The `compensate` argument is optional. A step added without one gets a no-op compensation, which
counts as a successful compensation during rollback.

---

## 3. The shared context

`TContext` is your own type. OrionSaga never inspects or constructs it; it only threads the single
instance you pass to `RunAsync` through every step, condition and compensation. Use it to carry ids,
amounts, and any intermediate state a later step or compensation needs. There is no constraint on
`TContext`, so a record, class, or even `object` works.

---

## 4. Result reporting

`RunAsync` returns a `SagaResult` describing the run:

| Member | Meaning |
|--------|---------|
| `Outcome` | `SagaOutcome.Succeeded`, `Failed`, or `Cancelled`. |
| `Succeeded` / `Failed` / `Cancelled` | Convenience flags for `Outcome`. |
| `TimedOut` | The cancellation came from the step's own per-step timeout. |
| `FailedStep` | The name of the step that ended the run, or null on success. |
| `Failure` | The exception that ended the run, or null on success. |
| `StepsCompleted` | Step slots whose forward action completed (a parallel group counts once). |
| `StepsSkipped` | Steps or groups skipped by a false condition. |
| `StepsCompensated` | Compensations that succeeded during rollback (each group member counts). |
| `CompensationFailures` | Compensations that threw or were stopped by the rollback budget. |
| `RollbackTimedOut` | The rollback budget elapsed before the unwind finished. |
| `RolledBackCleanly` | The run did not succeed, no compensation failed, and the budget did not run out. |

An empty saga (no steps) returns a successful result.

---

## 5. Compensation-failure isolation

If a compensation throws during rollback (after any compensation retries), OrionSaga records it as a
`CompensationFailure(StepName, Exception)` and continues compensating the remaining steps. One bad
compensation never strands the others. A non-empty `CompensationFailures` list means some effects may
not have been undone and need manual attention; `RolledBackCleanly` is false in that case.

---

## 6. Cancellation that still rolls back

`RunAsync` accepts a `CancellationToken` that it hands to every forward action and to forward retry
backoff waits. The executor does not check the token between steps; cancellation takes effect when a
step observes it. A step that throws `OperationCanceledException` ends the run with
`SagaOutcome.Cancelled`, distinct from a business `Failed`. Compensations never receive the run's
token, so a saga cancelled mid-flight still unwinds the work it already did.

---

## 7. Per-step timeouts

`AddStep(..., timeout: TimeSpan)` (and the same parameter on every other step method and on
`SagaStep`) gives the forward action a deadline. The step's token is linked to the run's token and
to the deadline. When the deadline fires and the step throws `OperationCanceledException`, the
executor raises a `SagaStepTimeoutException` (an `OperationCanceledException` carrying `StepName`
and `Timeout`), the run is `Cancelled`, and `TimedOut` is true. A timeout must be positive. It is
cooperative: a step that ignores its token runs past the deadline.

---

## 8. Typed step results

`AddResultStep<TResult>(name, execute, apply, ...)` takes a forward action returning
`Task<TResult>` and an `apply` callback that writes the value into the context for the next step. It
is adapted onto an ordinary step, so ordering, compensation, timeouts, retries and reporting are the
same. It is a distinct method, not an `AddStep` overload, so it can never capture an existing
`AddStep` call.

---

## 9. Retries and the rollback budget

- `RetryPolicy(maxAttempts, baseDelay, backoff)` with `RetryBackoff.Constant` (default) or
  `RetryBackoff.Exponential` (`baseDelay`, doubling each retry). `maxAttempts` counts the first
  attempt; `DelayBeforeAttempt(n)` returns the wait before attempt `n`.
- `forwardRetry` on a step retries a forward exception before the step fails. An
  `OperationCanceledException` (cancellation or timeout) is never retried; a timeout bounds each
  attempt separately.
- `compensationRetry` on a step, or `WithCompensationRetry(policy)` for every step, retries a
  compensation before it is recorded as a failure. The step's own policy wins.
- `WithRollbackBudget(TimeSpan)` bounds the whole rollback phase. When it elapses, the compensation
  token is cancelled; the compensation that observes it and all compensations not yet started are
  recorded in `CompensationFailures`, and `RollbackTimedOut` is true. Without a budget, rollback is
  unbounded.

---

## 10. Conditional steps, sub-sagas and parallel groups

- `AddConditionalStep` / `AddConditionalResultStep` take a `Func<TContext, bool>` evaluated just
  before the step. False skips the step: it does not run, is not compensated, counts in
  `StepsSkipped`, and is reported through `ISagaObserver.OnStepSkipped`. A condition that throws ends
  the run as `Failed`.
- `AddSubSaga(name, configure)` flattens the configured steps into the parent, renamed
  `"{name}/{step}"`, so they share the parent's single ordered run and single reverse rollback.
- `AddParallelGroup(name, configure, condition?)` runs the configured member steps concurrently as
  one step slot, members renamed `"{name}/{member}"`. On a member failure the group waits for the
  other members, then the completed members are compensated in reverse declaration order through the
  normal rollback. Member timeouts, forward retries and conditions apply; a group condition skips the
  whole group.

---

## 11. Observers and run snapshots

Implement `ISagaObserver` and attach it with `WithObserver`. Required members:

- `OnStepCompleted(stepName)`
- `OnStepFailed(stepName, exception)`
- `OnCompensated(stepName)`
- `OnCompensationFailed(stepName, exception)`

Default interface members you may override: the overloads of those four that add the one-based
`ordinal` and measured `duration`, `OnStepSkipped(stepName)` / `OnStepSkipped(stepName, ordinal)`,
and `OnProgress(SagaRunSnapshot)`. `OnProgress` runs before each step and once after a successful
run; the snapshot exposes `CurrentStep`, `CompletedSteps`, `PendingSteps`, `WouldCompensate` and
`TotalSteps` as immutable `StepReference(Name, Ordinal)` values.

Observers are observability only. Any exception an observer throws is caught and swallowed by the
executor, so an observer fault can never disrupt orchestration or rollback. With no observer, the
public `NullSagaObserver.Instance` is used and the executor skips timing and snapshots entirely.

---

## 12. Metrics

`SagaDiagnostics` derives from `OrionInstrumentation` and exposes a `Meter` named
`Moongazing.OrionSaga` (the `SagaDiagnostics.MeterName` constant). Attach an instance with
`WithDiagnostics`; `Saga.RunAsync` then records three counters, each tagged `orion.outcome`:

| Instrument | Recorded by `RunAsync` | Tag values |
|------------|------------------------|------------|
| `orion.saga.runs` | once per run | `succeeded` / `failed` (a cancelled run counts as `failed`) |
| `orion.saga.steps` | once per step slot that ran (not skipped steps, not retry attempts; a parallel group once) | `completed` / `failed` |
| `orion.saga.compensations` | once per compensation during rollback (each group member) | `compensated` / `failed` |

`SagaDiagnostics` owns the meter and is `IDisposable`. Static tags set through
`OrionInstrumentation.SetStaticTags` are added to every measurement.

---

## 13. Tracing

`SagaActivitySource` names an `ActivitySource` `Moongazing.OrionSaga` that the executor always uses,
whether or not `WithDiagnostics` is set. With a listener, each run emits an `orionsaga.run` span with
`orionsaga.step` and `orionsaga.compensation` children, tagged with `orionsaga.step.name`,
`orionsaga.step.ordinal` and `orionsaga.outcome`. Parallel group members get `orionsaga.step` spans
under the group's span, without an ordinal tag. Without a listener no `Activity` is created.

---

## 14. Dependency injection

`AddOrionSaga()` registers `SagaDiagnostics` as a singleton (via `TryAddSingleton`, so it is safe to
call more than once). Sagas themselves are built with `SagaBuilder` per definition rather than
resolved from the container, so the diagnostics singleton is the only registration. Inject it where
you build sagas and pass it to `WithDiagnostics`.

---

## 15. Reusable sagas

`Build()` copies the configured steps into an array and produces a `Saga<TContext>`. Later changes
to the builder do not affect it, and it can be run many times. A saga without parallel groups holds
no per-run state, so it can also be run concurrently with different context instances. A parallel
group records its completed members on the built saga, so a saga containing one should not be run
concurrently; build one per concurrent run.

---

## 16. Multi-targeting and dependencies

- Targets `net8.0`, `net9.0`, and `net10.0`.
- The runtime dependencies are `Microsoft.Extensions.DependencyInjection.Abstractions` and `Orion.Abstractions` (the family's shared contracts spine).
- Built with nullable reference types enabled, `latest-recommended` analyzers, and `TreatWarningsAsErrors`.
- Ships an XML documentation file and a symbol package. CI publishes an AOT smoke test with NativeAOT.
