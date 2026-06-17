namespace Moongazing.OrionSaga.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Measures the overhead the optional instrumentation hooks add to a successful run, so a consumer
/// can weigh the cost of wiring an observer or the metrics meter. Three prebuilt sagas over identical
/// steps are compared: bare, with an <see cref="ISagaObserver"/> attached (per-step fault-safe
/// dispatch), and with <see cref="SagaDiagnostics"/> attached (per-step and per-run counter writes).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SagaInstrumentationBenchmarks
{
    private const int StepCount = 10;

    private Saga<OrderContext> bare = null!;
    private Saga<OrderContext> withObserver = null!;
    private Saga<OrderContext> withDiagnostics = null!;
    private SagaDiagnostics diagnostics = null!;
    private OrderContext context = null!;

    /// <summary>A counting observer: cheap, fault-safe, exercises the observer dispatch path.</summary>
    private sealed class CountingObserver : ISagaObserver
    {
        public int Completed { get; private set; }

        public void OnStepCompleted(string stepName) => Completed++;

        public void OnStepFailed(string stepName, Exception exception)
        {
        }

        public void OnCompensated(string stepName)
        {
        }

        public void OnCompensationFailed(string stepName, Exception exception)
        {
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        diagnostics = new SagaDiagnostics();
        context = new OrderContext();

        bare = Build(b => { });
        withObserver = Build(b => b.WithObserver(new CountingObserver()));
        withDiagnostics = Build(b => b.WithDiagnostics(diagnostics));
    }

    [GlobalCleanup]
    public void Cleanup() => diagnostics.Dispose();

    private static Saga<OrderContext> Build(Action<SagaBuilder<OrderContext>> configure)
    {
        var builder = new SagaBuilder<OrderContext>();
        for (var i = 0; i < StepCount; i++)
        {
            builder.AddStep(
                "step-" + i,
                (ctx, _) => { ctx.Executed++; return Task.CompletedTask; },
                (ctx, _) => { ctx.Compensated++; return Task.CompletedTask; });
        }

        configure(builder);
        return builder.Build();
    }

    [Benchmark(Baseline = true)]
    public async Task<bool> Bare() => (await bare.RunAsync(context)).Succeeded;

    [Benchmark]
    public async Task<bool> WithObserver() => (await withObserver.RunAsync(context)).Succeeded;

    [Benchmark]
    public async Task<bool> WithDiagnostics() => (await withDiagnostics.RunAsync(context)).Succeeded;
}
