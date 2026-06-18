namespace Moongazing.OrionSaga.Demo;

using System.Diagnostics.Metrics;

using Moongazing.OrionSaga.Diagnostics;

/// <summary>
/// Scenario 4: attach a <see cref="MeterListener"/> to the <see cref="SagaDiagnostics"/> meter and
/// run a success and a rollback back to back. The listener accumulates the run, step, and
/// compensation counters by their 'outcome' tag and prints the totals, the same data an
/// OpenTelemetry exporter would scrape in production.
/// </summary>
public static class DiagnosticsDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Header("4. SagaDiagnostics via MeterListener");

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);

        using var diagnostics = new SagaDiagnostics();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SagaDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var outcome = "unknown";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome" && tag.Value is string value)
                {
                    outcome = value;
                }
            }

            var key = $"{instrument.Name} [{outcome}]";
            totals[key] = totals.GetValueOrDefault(key) + measurement;
        });

        listener.Start();

        // One success and one rollback so every counter (runs, steps, compensations) is exercised.
        var okCtx = new OrderContext { OrderId = "ORD-4004", Amount = 19.99m };
        await OrderSagaFactory
            .Build(new WarehouseService(), new PaymentService(), new ShippingService(), diagnostics: diagnostics)
            .RunAsync(okCtx);

        var failCtx = new OrderContext { OrderId = "ORD-4005", Amount = 19.99m };
        await OrderSagaFactory
            .Build(new WarehouseService(), new PaymentService(), new ShippingService(failBooking: true), diagnostics: diagnostics)
            .RunAsync(failCtx);

        listener.RecordObservableInstruments();

        Console.WriteLine("  metered totals (after 1 success + 1 rollback):");
        foreach (var entry in totals.OrderBy(static e => e.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {entry.Key,-35} = {entry.Value}");
        }
    }
}
