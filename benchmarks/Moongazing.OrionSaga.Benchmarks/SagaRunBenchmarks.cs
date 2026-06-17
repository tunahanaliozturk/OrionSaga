namespace Moongazing.OrionSaga.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Measures the happy-path forward execution of a prebuilt saga: <see cref="Saga{TContext}.RunAsync"/>
/// running every step to completion. The saga is built once in <see cref="Setup"/> so the measurement
/// isolates the run loop (the per-step push onto the completed stack, observer dispatch, and the
/// synchronous-completion await fast path) rather than build cost. Step bodies complete synchronously
/// via <see cref="Task.CompletedTask"/>, so there is no scheduling or I/O noise.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SagaRunBenchmarks
{
    private Saga<OrderContext> saga = null!;
    private OrderContext context = null!;

    [Params(3, 10, 25)]
    public int StepCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var builder = new SagaBuilder<OrderContext>();
        for (var i = 0; i < StepCount; i++)
        {
            builder.AddStep(
                "step-" + i,
                (ctx, _) => { ctx.Executed++; return Task.CompletedTask; },
                (ctx, _) => { ctx.Compensated++; return Task.CompletedTask; });
        }

        saga = builder.Build();
        context = new OrderContext();
    }

    [Benchmark]
    public async Task<bool> RunToCompletion()
    {
        context.Executed = 0;
        var result = await saga.RunAsync(context);
        return result.Succeeded;
    }
}
