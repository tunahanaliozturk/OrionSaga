namespace Moongazing.OrionSaga.Orchestration;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;

/// <summary>
/// A runnable saga: an ordered list of steps over a shared context. <see cref="RunAsync"/> executes
/// the steps in order; if one fails, it compensates the already-completed steps in reverse and
/// returns a failure result describing what happened. Build one with
/// <see cref="SagaBuilder{TContext}"/>.
/// </summary>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
public sealed class Saga<TContext>
{
    private readonly IReadOnlyList<SagaStep<TContext>> steps;
    private readonly SagaDiagnostics? diagnostics;
    private readonly ISagaObserver observer;

    internal Saga(IReadOnlyList<SagaStep<TContext>> steps, SagaDiagnostics? diagnostics, ISagaObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps;
        this.diagnostics = diagnostics;
        this.observer = observer ?? NullSagaObserver.Instance;
    }

    /// <summary>
    /// Run the saga. On success every step ran; on failure the failing step and the rollback
    /// outcome are reported. Compensation runs even if the supplied token is cancelled, so a
    /// cancelled saga still rolls back.
    /// </summary>
    /// <param name="context">The shared context.</param>
    /// <param name="cancellationToken">Cancels forward progress (rollback still runs).</param>
    public async Task<SagaResult> RunAsync(TContext context, CancellationToken cancellationToken = default)
    {
        var completed = new Stack<SagaStep<TContext>>();

        foreach (var step in steps)
        {
            try
            {
                await step.Execute(context, cancellationToken).ConfigureAwait(false);
                completed.Push(step);
                diagnostics?.RecordStep(completed: true);
                SafeObserve(() => observer.OnStepCompleted(step.Name));
            }
#pragma warning disable CA1031 // a saga turns ANY step failure into a compensating rollback
            catch (Exception ex)
#pragma warning restore CA1031
            {
                diagnostics?.RecordStep(completed: false);
                SafeObserve(() => observer.OnStepFailed(step.Name, ex));

                var compensationFailures = await CompensateAsync(completed, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.Failed(step.Name, ex, compensationFailures);
            }
        }

        diagnostics?.RecordRun(succeeded: true);
        return SagaResult.Success;
    }

    private async Task<IReadOnlyList<CompensationFailure>> CompensateAsync(
        Stack<SagaStep<TContext>> completed, TContext context)
    {
        var failures = new List<CompensationFailure>();

        while (completed.Count > 0)
        {
            var step = completed.Pop();
            try
            {
                // Roll back with a non-cancelled token: a cancelled saga must still undo its work.
                await step.Compensate(context, CancellationToken.None).ConfigureAwait(false);
                diagnostics?.RecordCompensation(compensated: true);
                SafeObserve(() => observer.OnCompensated(step.Name));
            }
#pragma warning disable CA1031 // record a compensation fault but keep rolling back the remaining steps
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(new CompensationFailure(step.Name, ex));
                diagnostics?.RecordCompensation(compensated: false);
                SafeObserve(() => observer.OnCompensationFailed(step.Name, ex));
            }
        }

        return failures;
    }

    private static void SafeObserve(Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }
}
