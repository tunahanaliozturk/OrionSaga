namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// One step of a saga over a shared context: a forward action and the compensating action that
/// undoes it. The compensation runs only if the forward action completed and a later step then
/// failed. A step with no real compensation uses a no-op. A step may declare a <see cref="Timeout"/>:
/// if its forward action runs longer than that budget it is cancelled and the saga rolls back. A step
/// may also declare a <see cref="ForwardRetry"/> and/or <see cref="CompensationRetry"/> policy so a
/// transient fault in the forward action or its compensation is retried before being treated as a
/// failure. A step may declare a <see cref="Condition"/>: when it evaluates false against the context
/// the step is skipped entirely, neither executed nor compensated.
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
    /// <param name="forwardRetry">
    /// An optional retry-with-backoff policy for the forward action. When set, a transient fault in the
    /// forward action is retried up to the policy's attempt count before the step is treated as failed
    /// and the saga rolls back. Null means a single attempt with no retry. A per-step
    /// <paramref name="timeout"/>, when set, bounds each individual attempt, and the policy's backoff
    /// waits honour the step's cancellation token. Cancellation and per-step timeouts are not retried.
    /// </param>
    /// <param name="compensationRetry">
    /// An optional retry-with-backoff policy for the compensating action. When set, a transient fault
    /// in compensation is retried up to the policy's attempt count before being recorded as a
    /// compensation failure. Null falls back to any saga-wide compensation retry policy, or a single
    /// attempt when neither is set.
    /// </param>
    /// <param name="condition">
    /// An optional predicate evaluated against the context just before the step would run. When it
    /// returns false the step is skipped: its forward action does not run, it is not counted as
    /// completed, and it is never compensated. Null means the step always runs. The predicate is read
    /// only on the forward path and must not mutate the context.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is supplied and is not strictly positive.
    /// </exception>
    public SagaStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null,
        RetryPolicy? forwardRetry = null,
        RetryPolicy? compensationRetry = null,
        Func<TContext, bool>? condition = null)
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
        ForwardRetry = forwardRetry;
        CompensationRetry = compensationRetry;
        Condition = condition;
    }

    /// <summary>
    /// When this step is the slot of a parallel group, the group composed onto it; otherwise null. The
    /// executor reads this on the rollback path so a group's completed members compensate through the
    /// saga's own per-step compensation routine (per-step retry, observer notifications, and failure
    /// recording) rather than an ad-hoc inline undo. Forward execution still runs through
    /// <see cref="Execute"/>; only rollback is special-cased so the group's members unwind as if each
    /// were an ordinary completed step. Null for every non-group step, so an ordinary step pays nothing.
    /// </summary>
    internal ParallelStepGroup<TContext>? Group { get; init; }

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

    /// <summary>
    /// The retry-with-backoff policy for the forward action, or null for a single attempt. When set, a
    /// transient forward fault is retried up to the policy's attempt count before the step fails.
    /// </summary>
    public RetryPolicy? ForwardRetry { get; }

    /// <summary>
    /// The retry-with-backoff policy for the compensating action, or null to fall back to any saga-wide
    /// compensation retry policy (and a single attempt when neither is set). When set, a transient
    /// compensation fault is retried up to the policy's attempt count before being recorded as a failure.
    /// </summary>
    public RetryPolicy? CompensationRetry { get; }

    /// <summary>
    /// An optional predicate evaluated against the context immediately before the step would run. When
    /// it returns false the step is skipped: it is neither executed nor compensated and is not counted
    /// among the completed steps. Null means the step always runs.
    /// </summary>
    public Func<TContext, bool>? Condition { get; }
}
