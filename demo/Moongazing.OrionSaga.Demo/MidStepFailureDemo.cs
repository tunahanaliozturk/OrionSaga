namespace Moongazing.OrionSaga.Demo;

/// <summary>
/// Scenario 3: a middle step (charge-card) fails. Only reserve-stock had completed, so only it is
/// compensated; ship-order never runs. This contrasts with scenario 2 and shows that the rollback
/// set is exactly the steps that completed before the failure - no more, no less.
/// </summary>
public static class MidStepFailureDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Header("3. Middle step fails - only the completed step unwinds");

        var ctx = new OrderContext { OrderId = "ORD-3003", Amount = 250.00m };
        var saga = OrderSagaFactory.Build(
            warehouse: new WarehouseService(),
            payments: new PaymentService(failCharge: true),
            shipping: new ShippingService(),
            observer: new ConsoleSagaObserver());

        var result = await saga.RunAsync(ctx);
        OrderSagaFactory.PrintResult(result, ctx);

        Console.WriteLine();
        Console.WriteLine("  Note: charge-card failed, so only reserve-stock was compensated.");
        Console.WriteLine("  ship-order never executed and there was nothing to refund.");
    }
}
