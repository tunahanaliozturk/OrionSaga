namespace Moongazing.OrionSaga.Demo;

/// <summary>
/// Runnable tour of OrionSaga. Builds a realistic three-step order saga (reserve stock, charge card,
/// ship) with compensations via SagaBuilder, then runs four scenarios: full success, a late failure
/// that triggers reverse-order rollback, a mid-step failure that unwinds only what completed, and a
/// SagaDiagnostics MeterListener that prints the metered counters.
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("OrionSaga demo - in-process saga orchestration with compensation");
        Console.WriteLine("Saga steps: reserve-stock -> charge-card -> ship-order");

        await HappyPathDemo.RunAsync();
        await CompensationDemo.RunAsync();
        await MidStepFailureDemo.RunAsync();
        await DiagnosticsDemo.RunAsync();

        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("Demo complete.");
    }
}
