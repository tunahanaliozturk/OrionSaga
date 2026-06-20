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
    private readonly bool hasObserver;

    internal Saga(IReadOnlyList<SagaStep<TContext>> steps, SagaDiagnostics? diagnostics, ISagaObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps;
        this.diagnostics = diagnostics;
        this.observer = observer ?? NullSagaObserver.Instance;

        // The default observer is the inert null singleton. Detecting it once lets the hot path skip
        // the per-step notify entirely (no closure, no no-op call) when no real observer is registered.
        hasObserver = !ReferenceEquals(this.observer, NullSagaObserver.Instance);
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
        // Default capacity: the stack grows only as steps complete. Pre-sizing to steps.Count would
        // eagerly allocate the full backing array, which a saga that fails or is cancelled early over
        // a large step list never needs, so let it grow to match the steps that actually complete.
        var completed = new Stack<SagaStep<TContext>>();

        foreach (var step in steps)
        {
            try
            {
                await ExecuteStep(step, context, cancellationToken).ConfigureAwait(false);
                completed.Push(step);
                diagnostics?.RecordStep(completed: true);
                NotifyStepCompleted(step.Name);
            }
            catch (OperationCanceledException ex)
            {
                // A cancellation is not a business failure: it is either the caller cancelling or the
                // step overrunning its per-step timeout. Either way, roll back and report it distinctly.
                // TimedOut is reported only when the per-step deadline genuinely elapsed: ExecuteStep
                // raises SagaStepTimeoutException exclusively in that case. It is never inferred from a
                // timeout merely being configured, so a step that throws OperationCanceledException for
                // unrelated reasons (its own HttpClient timeout, a child token it cancels itself) while
                // the deadline never fired is not misreported as a timeout.
                var timedOut = ex is SagaStepTimeoutException;
                diagnostics?.RecordStep(completed: false);
                NotifyStepFailed(step.Name, ex);

                var cancelFailures = await CompensateAsync(completed, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.CreateCancelled(step.Name, ex, timedOut, cancelFailures);
            }
#pragma warning disable CA1031 // a saga turns ANY step failure into a compensating rollback
            catch (Exception ex)
#pragma warning restore CA1031
            {
                diagnostics?.RecordStep(completed: false);
                NotifyStepFailed(step.Name, ex);

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
        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        timeoutCts.CancelAfter(budget);
        try
        {
            await step.Execute(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (TimeoutDeadlineFired(timeoutCts, cancellationToken))
        {
            // The per-step deadline genuinely elapsed and the caller did not cancel: report a timeout.
            // Any other OperationCanceledException (the caller's token, or a token the step cancels
            // for its own reasons) propagates unchanged so it is not misreported as a timeout.
            throw new SagaStepTimeoutException(step.Name, budget, ex);
        }
    }

    /// <summary>
    /// True when the per-step timeout source is the cancellation that actually fired: its deadline
    /// elapsed and the caller's token was not the trigger. This distinguishes a real deadline overrun
    /// from a cancellation the step raised for an unrelated reason while a timeout was merely configured.
    /// </summary>
    private static bool TimeoutDeadlineFired(CancellationTokenSource timeoutCts, CancellationToken callerToken)
        => timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested;

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
                NotifyCompensated(step.Name);
            }
#pragma warning disable CA1031 // record a compensation fault but keep rolling back the remaining steps
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(new CompensationFailure(step.Name, ex));
                diagnostics?.RecordCompensation(compensated: false);
                NotifyCompensationFailed(step.Name, ex);
            }
        }

        return failures;
    }

    // The four notify helpers below pass the step name (and exception) directly to the observer rather
    // than through an Action, so the hot path allocates no per-step closure. When no real observer is
    // registered, hasObserver is false and the call is skipped entirely: the inert null singleton's
    // callbacks are no-ops, so skipping them is behaviour-identical.

    private void NotifyStepCompleted(string stepName)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnStepCompleted(stepName);
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyStepFailed(string stepName, Exception exception)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnStepFailed(stepName, exception);
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyCompensated(string stepName)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnCompensated(stepName);
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyCompensationFailed(string stepName, Exception exception)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnCompensationFailed(stepName, exception);
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }
}
