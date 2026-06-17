namespace Moongazing.OrionSaga.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Measures the failure-and-rollback hot path: the final step throws, so <see cref="Saga{TContext}.RunAsync"/>
/// must catch, walk the completed-step stack in reverse, run every compensation, and assemble a
/// <see cref="SagaResult"/>. This exercises the exception path, the compensation loop, and the
/// allocation of the compensation-failures list. Compensations here all succeed, so the saga rolls
/// back cleanly; the cost measured is the unwind itself, not compensation faults.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SagaCompensationBenchmarks
{
    private Saga<OrderContext> saga = null!;
    private OrderContext context = null!;

    [Params(3, 10, 25)]
    public int StepCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var builder = new SagaBuilder<OrderContext>();
        for (var i = 0; i < StepCount - 1; i++)
        {
            builder.AddStep(
                "step-" + i,
                (ctx, _) => { ctx.Executed++; return Task.CompletedTask; },
                (ctx, _) => { ctx.Compensated++; return Task.CompletedTask; });
        }

        // Final step always fails, forcing every prior step to compensate in reverse.
        builder.AddStep(
            "step-fail",
            (_, _) => throw new InvalidOperationException("boom"),
            (ctx, _) => { ctx.Compensated++; return Task.CompletedTask; });

        saga = builder.Build();
        context = new OrderContext();
    }

    [Benchmark]
    public async Task<bool> RunAndCompensate()
    {
        context.Compensated = 0;
        var result = await saga.RunAsync(context);
        return result.RolledBackCleanly;
    }
}
