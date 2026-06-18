namespace Moongazing.OrionSaga.Demo;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Assembles the canonical three-step order saga (reserve stock, charge card, ship) from the given
/// collaborators. Centralised so every demo runs the exact same definition and only the injected
/// services (which ones are wired to fail) differ between scenarios.
/// </summary>
public static class OrderSagaFactory
{
    public static Saga<OrderContext> Build(
        WarehouseService warehouse,
        PaymentService payments,
        ShippingService shipping,
        ISagaObserver? observer = null,
        SagaDiagnostics? diagnostics = null)
    {
        var builder = new SagaBuilder<OrderContext>()
            .AddStep(
                "reserve-stock",
                execute: warehouse.ReserveAsync,
                compensate: warehouse.ReleaseAsync)
            .AddStep(
                "charge-card",
                execute: payments.ChargeAsync,
                compensate: payments.RefundAsync)
            .AddStep(
                "ship-order",
                execute: shipping.BookAsync,
                compensate: shipping.CancelAsync);

        if (observer is not null)
        {
            builder.WithObserver(observer);
        }

        if (diagnostics is not null)
        {
            builder.WithDiagnostics(diagnostics);
        }

        return builder.Build();
    }

    /// <summary>Print a saga outcome in a consistent shape across demos.</summary>
    public static void PrintResult(SagaResult result, OrderContext ctx)
    {
        Console.WriteLine();
        if (result.Succeeded)
        {
            Console.WriteLine($"  RESULT: SUCCEEDED - order {ctx.OrderId} fully placed");
            Console.WriteLine($"          payment={ctx.PaymentReference} tracking={ctx.ShipmentTrackingId}");
            return;
        }

        Console.WriteLine($"  RESULT: ROLLED BACK - failed at step '{result.FailedStep}'");
        Console.WriteLine($"          cause: {result.Failure!.Message}");
        Console.WriteLine($"          rolled back cleanly: {result.RolledBackCleanly}");

        if (result.CompensationFailures.Count > 0)
        {
            Console.WriteLine("          compensation failures (need manual attention):");
            foreach (var failure in result.CompensationFailures)
            {
                Console.WriteLine($"            - {failure.StepName}: {failure.Exception.Message}");
            }
        }

        Console.WriteLine($"          final state: stockReserved={ctx.StockReserved} " +
            $"payment={ctx.PaymentReference ?? "none"} tracking={ctx.ShipmentTrackingId ?? "none"}");
    }
}
