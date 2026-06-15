namespace Moongazing.OrionSaga.Orchestration;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;

/// <summary>
/// Fluently assembles a <see cref="Saga{TContext}"/> from steps, with optional diagnostics and an
/// observer. Steps run in the order they are added.
/// </summary>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
public sealed class SagaBuilder<TContext>
{
    private readonly List<SagaStep<TContext>> steps = [];
    private SagaDiagnostics? diagnostics;
    private ISagaObserver? observer;

    /// <summary>Add a step with a forward action and a compensating action.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="execute">The forward action.</param>
    /// <param name="compensate">The compensating action; a no-op when null.</param>
    public SagaBuilder<TContext> AddStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null)
    {
        steps.Add(new SagaStep<TContext>(name, execute, compensate));
        return this;
    }

    /// <summary>Add an already-constructed step.</summary>
    /// <param name="step">The step.</param>
    public SagaBuilder<TContext> AddStep(SagaStep<TContext> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        steps.Add(step);
        return this;
    }

    /// <summary>Emit telemetry to the given diagnostics instance.</summary>
    /// <param name="value">The diagnostics instance.</param>
    public SagaBuilder<TContext> WithDiagnostics(SagaDiagnostics value)
    {
        diagnostics = value;
        return this;
    }

    /// <summary>Report progress to the given observer.</summary>
    /// <param name="value">The observer.</param>
    public SagaBuilder<TContext> WithObserver(ISagaObserver value)
    {
        observer = value;
        return this;
    }

    /// <summary>Build the runnable saga.</summary>
    public Saga<TContext> Build() => new(steps.ToArray(), diagnostics, observer);
}
