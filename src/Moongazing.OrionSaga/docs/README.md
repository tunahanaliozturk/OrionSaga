# OrionSaga

In-process saga orchestration for .NET: run an ordered list of steps over a shared context and, when
one fails, compensate the steps that completed in reverse order. No broker, no database, no
background process.

![OrionSaga run and rollback: two steps complete, the third throws, so the second and then the first are compensated; a compensation that throws is recorded and the rest still run](https://raw.githubusercontent.com/tunahanaliozturk/OrionSaga/main/docs/diagrams/run-and-rollback.png)

## Install

    dotnet add package OrionSaga

Targets `net8.0`, `net9.0` and `net10.0`. Depends only on
`Microsoft.Extensions.DependencyInjection.Abstractions` and `Orion.Abstractions`.

## Quick start

A complete console program (`dotnet new console`, then replace `Program.cs`):

```csharp
using Moongazing.OrionSaga.Orchestration;

var saga = new SagaBuilder<OrderContext>()
    .AddStep("reserve-stock",
        execute:    (order, ct) => Log($"reserve stock for {order.OrderId}"),
        compensate: (order, ct) => Log($"release stock for {order.OrderId}"))
    .AddStep("charge-card",
        execute:    (order, ct) => Log($"charge {order.Amount} for {order.OrderId}"),
        compensate: (order, ct) => Log($"refund {order.OrderId}"))
    .AddStep("book-courier",
        execute:    (order, ct) => throw new InvalidOperationException("no courier available"),
        compensate: (order, ct) => Log($"cancel courier for {order.OrderId}"))
    .Build();

SagaResult result = await saga.RunAsync(new OrderContext { OrderId = "order-42", Amount = 99.90m });

Console.WriteLine($"{result.Outcome} at {result.FailedStep}: {result.Failure?.Message}");
Console.WriteLine($"Compensated {result.StepsCompensated} step(s), clean rollback: {result.RolledBackCleanly}");

static Task Log(string message)
{
    Console.WriteLine(message);
    return Task.CompletedTask;
}

public sealed class OrderContext
{
    public required string OrderId { get; init; }
    public decimal Amount { get; init; }
}
```

It prints the two forward actions, then `refund` and `release stock` (newest first), then
`Failed at book-courier: no courier available` and `Compensated 2 step(s), clean rollback: True`.
`book-courier` is not compensated, because it never completed.

## What the result tells you

- `Outcome` is `Succeeded`, `Failed` (a step threw) or `Cancelled` (a step threw
  `OperationCanceledException`: the run's token or its per-step timeout; `TimedOut` marks the latter).
- `FailedStep` and `Failure` name the step that ended the run and its exception.
- `StepsCompleted`, `StepsSkipped` and `StepsCompensated` count what happened.
- A compensation that throws is recorded in `CompensationFailures` and the remaining compensations
  still run. `RolledBackCleanly` is true only when none failed and the rollback budget did not run
  out.

## Building blocks

- `AddStep(name, execute, compensate?, timeout?, forwardRetry?, compensationRetry?)` and
  `AddResultStep<TResult>(name, execute, apply, ...)` for a step that hands a value to the next one.
- `AddConditionalStep` / `AddConditionalResultStep`: a predicate that skips the step when false.
- `AddSubSaga(name, configure)`: flattens a named group of steps into the saga.
- `AddParallelGroup(name, configure)`: runs member steps concurrently as one step.
- `RetryPolicy(maxAttempts, baseDelay, RetryBackoff.Constant | Exponential)` for forward and
  compensation retries; `WithCompensationRetry` sets one for every compensation.
- `WithRollbackBudget(TimeSpan)` bounds the whole rollback; without it, compensations get a token
  that never cancels.
- Timeouts and cancellation are cooperative: a step must observe its `CancellationToken`.

## Observability

- `WithObserver(ISagaObserver)`: step completed, failed, skipped, compensated, compensation failed,
  and an `OnProgress` snapshot. An observer exception is swallowed.
- `WithDiagnostics(SagaDiagnostics)`: counters `orion.saga.runs`, `orion.saga.steps` and
  `orion.saga.compensations` (tag `orion.outcome`) on the `Moongazing.OrionSaga` meter.
  `services.AddOrionSaga()` registers one `SagaDiagnostics` singleton to inject.
- Spans `orionsaga.run`, `orionsaga.step` and `orionsaga.compensation` on the
  `Moongazing.OrionSaga` activity source, created only when a listener subscribes.

## Scope

In process only: if the process dies mid-run, the run does not resume and its completed steps are
not compensated. A built saga can be reused; one that contains a parallel group should not be run
from several threads at once.

## Related packages

- `Orion.Abstractions` - the Orion family's shared contracts (telemetry naming, options, clock); a
  dependency of this package.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionSaga
- Changelog: https://github.com/tunahanaliozturk/OrionSaga/blob/main/CHANGELOG.md
- License: MIT
