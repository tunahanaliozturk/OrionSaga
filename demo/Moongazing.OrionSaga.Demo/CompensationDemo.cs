namespace Moongazing.OrionSaga.Demo;

/// <summary>
/// Scenario 2: the last step (ship-order) fails. reserve-stock and charge-card already completed, so
/// the executor compensates them in reverse order: refund the card first, then release the stock.
/// The failing step is not compensated (its forward action never completed). The result is a clean
/// rollback.
/// </summary>
public static class CompensationDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Header("2. Later step fails - reverse-order compensation");

        var ctx = new OrderContext { OrderId = "ORD-2002", Amount = 89.00m };
        var saga = OrderSagaFactory.Build(
            warehouse: new WarehouseService(),
            payments: new PaymentService(),
            shipping: new ShippingService(failBooking: true),
            observer: new ConsoleSagaObserver());

        var result = await saga.RunAsync(ctx);
        OrderSagaFactory.PrintResult(result, ctx);

        Console.WriteLine();
        Console.WriteLine("  Note: compensations ran in reverse - charge-card refunded before");
        Console.WriteLine("  reserve-stock was released; ship-order itself was never compensated.");
    }
}
