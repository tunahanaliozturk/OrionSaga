namespace Moongazing.OrionSaga.Demo;

/// <summary>
/// The shared context threaded through the order saga. OrionSaga does not own this type; it only
/// passes it to each step. Steps record what they did here so compensations know what to undo and
/// the demo can print a final ledger.
/// </summary>
public sealed class OrderContext
{
    public required string OrderId { get; init; }

    public decimal Amount { get; init; }

    public bool StockReserved { get; set; }

    public string? PaymentReference { get; set; }

    public string? ShipmentTrackingId { get; set; }
}

/// <summary>
/// Stand-in collaborators for the saga steps. These are deliberately trivial and in-memory: the
/// point of the demo is the orchestration and compensation flow, not real inventory or a real
/// payment gateway. Each method writes a line so the console shows the true call order, including
/// the reverse-order unwind.
/// </summary>
public sealed class WarehouseService
{
    public Task ReserveAsync(OrderContext ctx, CancellationToken ct)
    {
        ctx.StockReserved = true;
        Console.WriteLine($"    [warehouse] reserved stock for {ctx.OrderId}");
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(OrderContext ctx, CancellationToken ct)
    {
        ctx.StockReserved = false;
        Console.WriteLine($"    [warehouse] released stock for {ctx.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class PaymentService
{
    private readonly bool failCharge;

    public PaymentService(bool failCharge = false) => this.failCharge = failCharge;

    public Task ChargeAsync(OrderContext ctx, CancellationToken ct)
    {
        if (failCharge)
        {
            Console.WriteLine($"    [payments] charge DECLINED for {ctx.OrderId} ({ctx.Amount:0.00})");
            throw new InvalidOperationException(
                $"card declined charging {ctx.Amount:0.00} for {ctx.OrderId}");
        }

        ctx.PaymentReference = $"pay_{Guid.NewGuid():N}"[..12];
        Console.WriteLine($"    [payments] charged {ctx.Amount:0.00} for {ctx.OrderId} -> {ctx.PaymentReference}");
        return Task.CompletedTask;
    }

    public Task RefundAsync(OrderContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"    [payments] refunded {ctx.PaymentReference} for {ctx.OrderId}");
        ctx.PaymentReference = null;
        return Task.CompletedTask;
    }
}

public sealed class ShippingService
{
    private readonly bool failBooking;

    public ShippingService(bool failBooking = false) => this.failBooking = failBooking;

    public Task BookAsync(OrderContext ctx, CancellationToken ct)
    {
        if (failBooking)
        {
            Console.WriteLine($"    [shipping] courier booking FAILED for {ctx.OrderId}");
            throw new InvalidOperationException($"no courier capacity for {ctx.OrderId}");
        }

        ctx.ShipmentTrackingId = $"trk_{Guid.NewGuid():N}"[..12];
        Console.WriteLine($"    [shipping] booked courier for {ctx.OrderId} -> {ctx.ShipmentTrackingId}");
        return Task.CompletedTask;
    }

    public Task CancelAsync(OrderContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"    [shipping] cancelled shipment {ctx.ShipmentTrackingId} for {ctx.OrderId}");
        ctx.ShipmentTrackingId = null;
        return Task.CompletedTask;
    }
}
