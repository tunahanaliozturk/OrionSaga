namespace Moongazing.OrionSaga.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Measures the cost of assembling a saga: chaining <see cref="SagaBuilder{TContext}.AddStep(string, Func{TContext, CancellationToken, Task}, Func{TContext, CancellationToken, Task}?)"/>
/// and the terminal <see cref="SagaBuilder{TContext}.Build"/> (which snapshots the step list to an array).
/// This is the per-definition setup cost a consumer pays before any step runs.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SagaBuildBenchmarks
{
    private static readonly Func<OrderContext, CancellationToken, Task> Execute =
        (ctx, _) => { ctx.Executed++; return Task.CompletedTask; };

    private static readonly Func<OrderContext, CancellationToken, Task> Compensate =
        (ctx, _) => { ctx.Compensated++; return Task.CompletedTask; };

    [Params(3, 10, 25)]
    public int StepCount { get; set; }

    [Benchmark]
    public Saga<OrderContext> BuildSaga()
    {
        var builder = new SagaBuilder<OrderContext>();
        for (var i = 0; i < StepCount; i++)
        {
            builder.AddStep("step-" + i, Execute, Compensate);
        }

        return builder.Build();
    }
}
