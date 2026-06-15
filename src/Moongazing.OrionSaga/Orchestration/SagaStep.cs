namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// One step of a saga over a shared context: a forward action and the compensating action that
/// undoes it. The compensation runs only if the forward action completed and a later step then
/// failed. A step with no real compensation uses a no-op.
/// </summary>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
public sealed class SagaStep<TContext>
{
    /// <summary>Create a step.</summary>
    /// <param name="name">A short identifier used in results and telemetry.</param>
    /// <param name="execute">The forward action.</param>
    /// <param name="compensate">The compensating action; defaults to a no-op when null.</param>
    public SagaStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(execute);
        Name = name;
        Execute = execute;
        Compensate = compensate ?? ((_, _) => Task.CompletedTask);
    }

    /// <summary>The step name.</summary>
    public string Name { get; }

    /// <summary>The forward action.</summary>
    public Func<TContext, CancellationToken, Task> Execute { get; }

    /// <summary>The compensating action.</summary>
    public Func<TContext, CancellationToken, Task> Compensate { get; }
}
