namespace Moongazing.OrionSaga.Demo;

/// <summary>
/// Scenario 1: every step succeeds. The saga runs reserve-stock, charge-card, ship-order in order
/// and returns <c>SagaResult.Succeeded</c>. No compensation runs.
/// </summary>
public static class HappyPathDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Header("1. Happy path - all three steps succeed");

        var ctx = new OrderContext { OrderId = "ORD-1001", Amount = 149.90m };
        var saga = OrderSagaFactory.Build(
            warehouse: new WarehouseService(),
            payments: new PaymentService(),
            shipping: new ShippingService(),
            observer: new ConsoleSagaObserver());

        var result = await saga.RunAsync(ctx);
        OrderSagaFactory.PrintResult(result, ctx);
    }
}
