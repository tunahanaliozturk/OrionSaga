namespace Moongazing.OrionSaga.Demo;

using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Scenario 5: the v0.2 per-step timeout and the distinct Cancelled outcome. The ship-order step is
/// given a short timeout but its forward action runs longer than the budget. The step is cancelled,
/// the completed steps (reserve-stock, charge-card) roll back in reverse, and the result reports
/// SagaOutcome.Cancelled with TimedOut true. A second run cancels the caller's own token instead, to
/// show the same Cancelled outcome but with TimedOut false, so a timeout and an operator cancellation
/// are told apart.
/// </summary>
public static class TimeoutDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Header("5. Per-step timeout and the distinct Cancelled outcome");

        await RunTimeoutAsync();
        await RunCallerCancelAsync();
    }

    private static async Task RunTimeoutAsync()
    {
        Console.WriteLine();
        Console.WriteLine("  5a. ship-order overruns its 100ms budget");

        var ctx = new OrderContext { OrderId = "ORD-5005", Amount = 42.00m };
        var saga = new SagaBuilder<OrderContext>()
            .WithObserver(new ConsoleSagaObserver())
            .AddStep(
                "reserve-stock",
                execute: (c, ct) => new WarehouseService().ReserveAsync(c, ct),
                compensate: (c, ct) => new WarehouseService().ReleaseAsync(c, ct))
            .AddStep(
                "charge-card",
                execute: (c, ct) => new PaymentService().ChargeAsync(c, ct),
                compensate: (c, ct) => new PaymentService().RefundAsync(c, ct))
            .AddStep(
                "ship-order",
                execute: async (c, ct) =>
                {
                    Console.WriteLine($"    [shipping] booking courier for {c.OrderId} (slow)...");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                },
                compensate: (c, ct) => Task.CompletedTask,
                timeout: TimeSpan.FromMilliseconds(100))
            .Build();

        var result = await saga.RunAsync(ctx);
        PrintOutcome(result);
        Console.WriteLine("  Note: TimedOut is true only because the per-step deadline genuinely fired.");
    }

    private static async Task RunCallerCancelAsync()
    {
        Console.WriteLine();
        Console.WriteLine("  5b. caller cancels mid-flight (not a timeout)");

        var ctx = new OrderContext { OrderId = "ORD-5006", Amount = 42.00m };
        using var cts = new CancellationTokenSource();

        var saga = new SagaBuilder<OrderContext>()
            .WithObserver(new ConsoleSagaObserver())
            .AddStep(
                "reserve-stock",
                execute: (c, ct) => new WarehouseService().ReserveAsync(c, ct),
                compensate: (c, ct) => new WarehouseService().ReleaseAsync(c, ct))
            .AddStep(
                "charge-card",
                execute: async (c, ct) =>
                {
                    Console.WriteLine($"    [payments] charging {c.Amount:0.00} for {c.OrderId} (operator cancels)...");
                    cts.Cancel();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                },
                compensate: (c, ct) => new PaymentService().RefundAsync(c, ct))
            .Build();

        var result = await saga.RunAsync(ctx, cts.Token);
        PrintOutcome(result);
        Console.WriteLine("  Note: Cancelled is true but TimedOut is false: this was the caller's token, not a deadline.");
    }

    private static void PrintOutcome(SagaResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"  RESULT: outcome={result.Outcome} cancelled={result.Cancelled} " +
            $"timedOut={result.TimedOut} failed={result.Failed}");
        Console.WriteLine($"          ended at step '{result.FailedStep}', rolled back cleanly: {result.RolledBackCleanly}");
    }
}
