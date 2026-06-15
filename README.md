# OrionSaga

[![CI/CD](https://github.com/tunahanaliozturk/OrionSaga/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionSaga/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionSaga.svg)](https://www.nuget.org/packages/OrionSaga/)

In-process saga orchestration for .NET. Run an ordered sequence of steps over a shared context;
if any step fails, OrionSaga automatically compensates the already-completed steps in reverse,
so a multi-step operation either fully completes or cleanly unwinds.

Part of the **Orion** family. Usable entirely on its own.

## Why

Some operations touch several systems in a row: reserve stock, charge a card, book a courier. If
the courier booking fails, you must release the charge and the stock, in the right order, or you
leave money and inventory stranded. Writing that rollback by hand is error-prone. OrionSaga lets
you declare each step next to its compensation and runs the unwind for you when something breaks.

## Install

```
dotnet add package OrionSaga
```

## Quick start

```csharp
var saga = new SagaBuilder<OrderContext>()
    .AddStep("reserve-stock",
        execute:    (ctx, ct) => inventory.ReserveAsync(ctx.OrderId, ct),
        compensate: (ctx, ct) => inventory.ReleaseAsync(ctx.OrderId, ct))
    .AddStep("charge-card",
        execute:    (ctx, ct) => payments.ChargeAsync(ctx.OrderId, ctx.Amount, ct),
        compensate: (ctx, ct) => payments.RefundAsync(ctx.OrderId, ct))
    .AddStep("book-courier",
        execute:    (ctx, ct) => courier.BookAsync(ctx.OrderId, ct),
        compensate: (ctx, ct) => courier.CancelAsync(ctx.OrderId, ct))
    .Build();

var result = await saga.RunAsync(context, ct);

if (!result.Succeeded)
{
    logger.LogWarning("Order saga failed at {Step}: {Error}", result.FailedStep, result.Failure!.Message);
    if (!result.RolledBackCleanly)
    {
        // Some compensations themselves failed: these need manual attention.
        foreach (var failure in result.CompensationFailures)
        {
            logger.LogError(failure.Exception, "Compensation for {Step} failed", failure.StepName);
        }
    }
}
```

## Semantics

- Steps run in the order they are added, sharing one context instance.
- The first step to throw stops forward progress. The step that failed is **not** compensated (its
  forward action did not complete); every step that did complete is compensated in reverse order.
- A compensation that itself throws is recorded in `CompensationFailures` and rollback continues
  for the remaining steps, so one bad compensation does not strand the others.
- `RolledBackCleanly` is true when the saga failed but every compensation succeeded.
- Compensation runs with a non-cancelled token, so a saga cancelled mid-flight still unwinds.

## Results

| Property | Meaning |
|----------|---------|
| `Succeeded` | Every step completed |
| `FailedStep` | The step that threw (null on success) |
| `Failure` | The exception that failed the saga |
| `CompensationFailures` | Compensations that threw during rollback |
| `RolledBackCleanly` | Failed, but every compensation succeeded |

## Telemetry and events

Build with `.WithDiagnostics(...)` to emit to the `Moongazing.OrionSaga` meter: `orionsaga.runs`,
`orionsaga.steps`, and `orionsaga.compensations`, each tagged with an outcome. Add an
`ISagaObserver` with `.WithObserver(...)` to react to step completion, failure, and compensation.
The observer is fault-safe.

## Scope

OrionSaga is an in-process orchestrator: it coordinates the steps of one operation within a single
process. It is not a durable, persisted workflow engine; if the process dies mid-saga, the
in-flight run does not resume. For most "do these few things, undo on failure" cases that is
exactly enough, and it stays dependency-light.

## Design

- Multi-targets `net8.0`, `net9.0`, `net10.0`.
- `TreatWarningsAsErrors`, latest analyzers, nullable enabled.
- The executor is generic over your context type and has no dependency beyond the DI abstractions.

## License

MIT.
