namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// One step of a saga over a shared context: a forward action and the compensating action that
/// undoes it. The compensation runs only if the forward action completed and a later step then
/// failed. A step with no real compensation uses a no-op. A step may declare a <see cref="Timeout"/>:
/// if its forward action runs longer than that budget it is cancelled and the saga rolls back.
/// </summary>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
public sealed class SagaStep<TContext>
{
    /// <summary>Create a step.</summary>
    /// <param name="name">A short identifier used in results and telemetry.</param>
    /// <param name="execute">The forward action.</param>
    /// <param name="compensate">The compensating action; defaults to a no-op when null.</param>
    /// <param name="timeout">
    /// An optional maximum duration for the forward action. When the action runs longer than this it
    /// is cancelled and the saga rolls back, reporting the timeout as the cause. Null means no budget.
    /// Must be positive when supplied.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is supplied and is not strictly positive.
    /// </exception>
    public SagaStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(execute);
        if (timeout is { } budget && budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), budget, "Timeout must be positive.");
        }

        Name = name;
        Execute = execute;
        Compensate = compensate ?? ((_, _) => Task.CompletedTask);
        Timeout = timeout;
    }

    /// <summary>The step name.</summary>
    public string Name { get; }

    /// <summary>The forward action.</summary>
    public Func<TContext, CancellationToken, Task> Execute { get; }

    /// <summary>The compensating action.</summary>
    public Func<TContext, CancellationToken, Task> Compensate { get; }

    /// <summary>
    /// The maximum duration allowed for the forward action, or null for no budget. When set and the
    /// action overruns, the step is cancelled and the saga rolls back with a timeout outcome.
    /// </summary>
    public TimeSpan? Timeout { get; }
}
