namespace Moongazing.OrionSaga.Orchestration;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;

/// <summary>
/// A runnable saga: an ordered list of steps over a shared context. <see cref="RunAsync"/> executes
/// the steps in order; if one fails, is cancelled, or overruns its per-step timeout, it compensates
/// the already-completed steps in reverse and returns a result describing what happened. Build one
/// with <see cref="SagaBuilder{TContext}"/>.
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
    /// Run the saga. On success every step ran. Otherwise the step that ended the saga and the
    /// rollback outcome are reported: a forward fault yields <see cref="SagaOutcome.Failed"/>, while
    /// the caller's token being cancelled or a step overrunning its per-step timeout yields
    /// <see cref="SagaOutcome.Cancelled"/>. A step's per-step timeout is honoured alongside the
    /// supplied token via a linked token. Compensation runs even when the supplied token is
    /// cancelled, so a cancelled saga still rolls back.
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
                await ExecuteStep(step, context, cancellationToken).ConfigureAwait(false);
                completed.Push(step);
                diagnostics?.RecordStep(completed: true);
                SafeObserve(() => observer.OnStepCompleted(step.Name));
            }
            catch (OperationCanceledException ex)
            {
                // A cancellation is not a business failure: it is either the caller cancelling or the
                // step overrunning its per-step timeout. Either way, roll back and report it distinctly.
                var timedOut = step.Timeout is not null && !cancellationToken.IsCancellationRequested;
                diagnostics?.RecordStep(completed: false);
                SafeObserve(() => observer.OnStepFailed(step.Name, ex));

                var cancelFailures = await CompensateAsync(completed, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.CreateCancelled(step.Name, ex, timedOut, cancelFailures);
            }
#pragma warning disable CA1031 // a saga turns ANY step failure into a compensating rollback
            catch (Exception ex)
#pragma warning restore CA1031
            {
                diagnostics?.RecordStep(completed: false);
                SafeObserve(() => observer.OnStepFailed(step.Name, ex));

                var compensationFailures = await CompensateAsync(completed, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.CreateFailed(step.Name, ex, compensationFailures);
            }
        }

        diagnostics?.RecordRun(succeeded: true);
        return SagaResult.Success;
    }

    private static async Task ExecuteStep(SagaStep<TContext> step, TContext context, CancellationToken cancellationToken)
    {
        if (step.Timeout is not { } budget)
        {
            await step.Execute(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Link the caller's token with a per-step deadline so either source cancels the forward action.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(budget);
        await step.Execute(context, linked.Token).ConfigureAwait(false);
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
