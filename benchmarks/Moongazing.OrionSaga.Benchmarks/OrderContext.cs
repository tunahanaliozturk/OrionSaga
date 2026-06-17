namespace Moongazing.OrionSaga.Benchmarks;

/// <summary>
/// A trivial mutable context threaded through the benchmark sagas. Steps mutate it in memory only;
/// no I/O, database, or broker is involved, keeping every measurement on the orchestrator hot path.
/// </summary>
public sealed class OrderContext
{
    public int Executed { get; set; }

    public int Compensated { get; set; }
}
