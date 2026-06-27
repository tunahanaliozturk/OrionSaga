namespace Moongazing.OrionSaga.Orchestration;

using System.Diagnostics;

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
    private readonly RetryPolicy? compensationRetry;
    private readonly TimeSpan? rollbackBudget;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal Saga(
        IReadOnlyList<SagaStep<TContext>> steps,
        SagaDiagnostics? diagnostics,
        ISagaObserver? observer)
        : this(steps, diagnostics, observer, compensationRetry: null, rollbackBudget: null, delay: null)
    {
    }

    internal Saga(
        IReadOnlyList<SagaStep<TContext>> steps,
        SagaDiagnostics? diagnostics,
        ISagaObserver? observer,
        RetryPolicy? compensationRetry,
        TimeSpan? rollbackBudget,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps;
        this.diagnostics = diagnostics;
        this.observer = observer ?? NullSagaObserver.Instance;
        this.compensationRetry = compensationRetry;
        this.rollbackBudget = rollbackBudget;

        // The backoff delayer is injectable so tests can assert retry/backoff timing without sleeping.
        // The default is Task.Delay; the field is only ever read on a retry path, so the no-retry happy
        // path never touches it.
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));

        // The default observer is the inert null singleton. Detecting it once lets the hot path skip
        // the per-step notify entirely (no closure, no no-op call) when no real observer is registered.
        hasObserver = !ReferenceEquals(this.observer, NullSagaObserver.Instance);
    }

    /// <summary>
    /// Run the saga. On success every step ran. Otherwise the step that ended the saga and the
    /// rollback outcome are reported: a forward fault yields <see cref="SagaOutcome.Failed"/>, while
    /// the caller's token being cancelled or a step overrunning its per-step timeout yields
    /// <see cref="SagaOutcome.Cancelled"/>. A step's per-step timeout is honoured alongside the
    /// supplied token via a linked token. A step may declare a forward retry policy, in which case a
    /// transient forward fault is retried before the step is treated as failed. Compensation runs even
    /// when the supplied token is cancelled, so a cancelled saga still rolls back; a configured rollback
    /// budget bounds the whole unwind so a hung compensation cannot block forever.
    /// </summary>
    /// <param name="context">The shared context.</param>
    /// <param name="cancellationToken">Cancels forward progress (rollback still runs).</param>
    public async Task<SagaResult> RunAsync(TContext context, CancellationToken cancellationToken = default)
    {
        // Default capacity: the stack grows only as steps complete. Pre-sizing to steps.Count would
        // eagerly allocate the full backing array, which a saga that fails or is cancelled early over
        // a large step list never needs, so let it grow to match the steps that actually complete. Each
        // entry carries the step's original one-based forward ordinal so rollback reports the position
        // the step actually ran at, unshifted by any conditional steps that were skipped before it.
        var completed = new Stack<CompletedStep>();

        // One-based position of the step about to run. Tracked as a plain local so the ordinal carried
        // to the observer costs nothing beyond an int increment, and nothing at all when no observer is
        // registered (the value is simply never read).
        var ordinal = 0;

        // Steps skipped because their condition evaluated false. A skipped step never runs and is never
        // compensated, so it is tracked separately from the completed-step stack and reported on its own.
        var skipped = 0;

        foreach (var step in steps)
        {
            ordinal++;

            // Only measure forward-action duration when a real observer will consume it. The timing
            // source is allocation-free (a struct timestamp, no Stopwatch object), and gating it on
            // hasObserver keeps the no-observer happy path byte-for-byte as it was: no timestamp read,
            // no duration computed.
            var startTimestamp = hasObserver ? Stopwatch.GetTimestamp() : 0L;

            try
            {
                // A conditional step whose predicate is false is skipped: not executed, not pushed onto
                // the completed stack (so it is never compensated), and not counted as completed. The
                // predicate is evaluated inside the try so a predicate that throws is handled exactly
                // like a forward fault: it stops forward progress and rolls back the completed steps,
                // rather than escaping the run. The ordinal still advances so a later notification's
                // position matches the step's declared slot. A step with no condition pays nothing.
                if (step.Condition is { } condition && !condition(context))
                {
                    skipped++;
                    NotifyStepSkipped(step.Name, ordinal);
                    continue;
                }

                await ExecuteStep(step, context, cancellationToken).ConfigureAwait(false);
                completed.Push(new CompletedStep(step, ordinal));
                diagnostics?.RecordStep(completed: true);
                NotifyStepCompleted(step.Name, ordinal, startTimestamp);
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
                var completedCount = CountCompleted(completed);
                diagnostics?.RecordStep(completed: false);
                NotifyStepFailed(step.Name, ex, ordinal, startTimestamp);

                var rollback = await CompensateAsync(completed, step, ordinal, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.CreateCancelled(
                    step.Name, ex, timedOut, completedCount,
                    rollback.Compensated, skipped, rollback.Failures, rollback.RollbackTimedOut);
            }
#pragma warning disable CA1031 // a saga turns ANY step failure into a compensating rollback
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var completedCount = CountCompleted(completed);
                diagnostics?.RecordStep(completed: false);
                NotifyStepFailed(step.Name, ex, ordinal, startTimestamp);

                var rollback = await CompensateAsync(completed, step, ordinal, context).ConfigureAwait(false);
                diagnostics?.RecordRun(succeeded: false);
                return SagaResult.CreateFailed(
                    step.Name, ex, completedCount,
                    rollback.Compensated, skipped, rollback.Failures, rollback.RollbackTimedOut);
            }
        }

        diagnostics?.RecordRun(succeeded: true);
        return SagaResult.CreateSuccess(CountCompleted(completed), skipped);
    }

    // The number of completed step forward actions reported in the result. An ordinary completed step
    // counts as one; a completed parallel group slot counts as one stage (one forward slot) regardless
    // of how many members it ran, so the parent's StepsCompleted keeps counting stages, not members,
    // exactly as before. The group's per-member compensation is reflected in StepsCompensated and any
    // CompensationFailures, not in StepsCompleted.
    private static int CountCompleted(Stack<CompletedStep> completed) => completed.Count;

    private async Task ExecuteStep(SagaStep<TContext> step, TContext context, CancellationToken cancellationToken)
    {
        // No retry policy: run the single attempt directly, leaving the no-retry path exactly as it was.
        if (step.ForwardRetry is not { } retry)
        {
            await ExecuteAttempt(step, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Retry the forward action up to the policy's attempt count. A successful attempt returns
        // immediately. A cancellation (caller token or per-step timeout) is never retried: it is not a
        // transient fault, so it propagates on the spot. Any other exception is retried until the last
        // attempt, where it propagates and the saga rolls back as it would without a policy.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAttempt(step, context, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Cancellation and per-step timeouts are terminal, not transient. Do not retry.
                throw;
            }
#pragma warning disable CA1031 // a transient forward fault is retried until the attempt budget is spent
            catch (Exception) when (attempt < retry.MaxAttempts)
#pragma warning restore CA1031
            {
                // Wait the backoff for the next attempt, honouring the caller's token so a cancelled
                // token cuts the wait short rather than blocking for the full delay.
                var wait = retry.DelayBeforeAttempt(attempt + 1);
                if (wait > TimeSpan.Zero)
                {
                    await delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ExecuteAttempt(
        SagaStep<TContext> step, TContext context, CancellationToken cancellationToken)
    {
        if (step.Timeout is not { } budget)
        {
            await step.Execute(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Link the caller's token with a per-step deadline so either source cancels the forward action.
        // The deadline bounds a single attempt: under a retry policy each attempt gets a fresh budget.
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

    private async Task<RollbackOutcome> CompensateAsync(
        Stack<CompletedStep> completed, SagaStep<TContext> failedStep, int failedOrdinal, TContext context)
    {
        var failures = new List<CompensationFailure>();
        var compensated = 0;
        var rollbackTimedOut = false;

        // The rollback budget bounds the whole unwind: a single CTS cancelled after the budget feeds the
        // token every compensation observes, so a hung compensation is cut short rather than blocking
        // forever. With no budget set the token is None, preserving the prior unbounded behaviour and
        // allocating no CTS on that path. The undo still runs under cancellation; the budget only bounds
        // how long it may take.
        using var budgetCts = rollbackBudget is { } budget ? new CancellationTokenSource(budget) : null;
        var rollbackToken = budgetCts?.Token ?? CancellationToken.None;

        // The unwind worklist, newest-first. Built by expanding each completed slot into the concrete
        // units that need compensating, so a parallel group slot contributes its completed members (in
        // reverse declaration order) and an ordinary step contributes itself. The failing step is added
        // first: when it is a parallel group, the members that completed before a sibling faulted must
        // still be undone, and they unwind before any earlier stage. A failing ordinary step contributes
        // nothing, since its own forward action did not complete.
        var work = BuildRollbackWork(completed, failedStep, failedOrdinal);

        foreach (var unit in work)
        {
            var startTimestamp = hasObserver ? Stopwatch.GetTimestamp() : 0L;

            // If the rollback budget has already elapsed, the remaining units cannot compensate. Record
            // each as a budget-driven failure so the result reflects that their effects may linger.
            if (rollbackToken.IsCancellationRequested)
            {
                rollbackTimedOut = true;
                var cancelled = new OperationCanceledException(rollbackToken);
                failures.Add(new CompensationFailure(unit.Step.Name, cancelled));
                diagnostics?.RecordCompensation(compensated: false);
                NotifyCompensationFailed(unit.Step.Name, cancelled, unit.Ordinal, startTimestamp);
                continue;
            }

            try
            {
                await CompensateStep(unit.Step, context, rollbackToken).ConfigureAwait(false);
                compensated++;
                diagnostics?.RecordCompensation(compensated: true);
                NotifyCompensated(unit.Step.Name, unit.Ordinal, startTimestamp);
            }
            catch (OperationCanceledException ex) when (rollbackToken.IsCancellationRequested)
            {
                // The rollback budget elapsed mid-compensation: this unit's undo was cut short.
                rollbackTimedOut = true;
                failures.Add(new CompensationFailure(unit.Step.Name, ex));
                diagnostics?.RecordCompensation(compensated: false);
                NotifyCompensationFailed(unit.Step.Name, ex, unit.Ordinal, startTimestamp);
            }
#pragma warning disable CA1031 // record a compensation fault but keep rolling back the remaining units
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A compensation that itself throws (after its own retry) is recorded as a failure and
                // surfaced to the observer, then the remaining units still unwind. This is identical for
                // an ordinary step and for a parallel group member, since both arrive here as a unit.
                failures.Add(new CompensationFailure(unit.Step.Name, ex));
                diagnostics?.RecordCompensation(compensated: false);
                NotifyCompensationFailed(unit.Step.Name, ex, unit.Ordinal, startTimestamp);
            }
        }

        return new RollbackOutcome(failures, compensated, rollbackTimedOut);
    }

    // Flatten the completed-step stack (plus the failing step) into the ordered list of compensation
    // units, newest-first. A parallel group slot expands into its completed members in reverse
    // declaration order so each member compensates through the saga's own per-step routine. Each unit
    // carries the original forward ordinal of the slot it came from, so a skip earlier in the saga does
    // not shift the position reported for a later step, and a group's members all report the group slot's
    // forward position.
    private static List<CompensationUnit> BuildRollbackWork(
        Stack<CompletedStep> completed, SagaStep<TContext> failedStep, int failedOrdinal)
    {
        var work = new List<CompensationUnit>(completed.Count);

        // The failing step's completed members come first. Only a parallel group contributes here; an
        // ordinary failed step did not complete, so it has nothing to undo. A group whose member faulted
        // exposes the members that did complete via CompletedMembers.
        AppendGroupMembersReversed(work, failedStep, failedOrdinal);

        // Then the completed stack, newest-first. Popping a Stack already yields newest-first; expand a
        // group slot into its members and pass an ordinary step through unchanged.
        while (completed.Count > 0)
        {
            var entry = completed.Pop();
            if (entry.Step.Group is { } group)
            {
                AppendGroupMembers(work, group, entry.Ordinal);
            }
            else
            {
                work.Add(new CompensationUnit(entry.Step, entry.Ordinal));
            }
        }

        return work;
    }

    // Append a group's completed members in reverse declaration order, each tagged with the group slot's
    // forward ordinal, so within-group compensation order matches the documented reverse order.
    private static void AppendGroupMembers(List<CompensationUnit> work, ParallelStepGroup<TContext> group, int ordinal)
    {
        var members = group.CompletedMembers;
        for (var i = members.Count - 1; i >= 0; i--)
        {
            work.Add(new CompensationUnit(members[i], ordinal));
        }
    }

    // Append the failing step's completed members when it is a parallel group; a no-op for any other
    // step, since an ordinary failed step has no completed work of its own to undo.
    private static void AppendGroupMembersReversed(List<CompensationUnit> work, SagaStep<TContext> failedStep, int ordinal)
    {
        if (failedStep.Group is { } group)
        {
            AppendGroupMembers(work, group, ordinal);
        }
    }

    private async Task CompensateStep(SagaStep<TContext> step, TContext context, CancellationToken rollbackToken)
    {
        // The effective compensation policy is the step's own, falling back to the saga-wide one. No
        // policy means a single attempt, leaving the no-retry rollback path exactly as it was.
        var policy = step.CompensationRetry ?? compensationRetry;
        if (policy is not { } retry)
        {
            await step.Compensate(context, rollbackToken).ConfigureAwait(false);
            return;
        }

        // Retry a transient compensation fault up to the policy's attempt count. A cancellation from the
        // rollback budget is terminal, not transient, so it propagates immediately rather than being
        // retried; the caller records it as a budget-driven failure.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await step.Compensate(context, rollbackToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (rollbackToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a transient compensation fault is retried until the attempt budget is spent
            catch (Exception) when (attempt < retry.MaxAttempts)
#pragma warning restore CA1031
            {
                var wait = retry.DelayBeforeAttempt(attempt + 1);
                if (wait > TimeSpan.Zero)
                {
                    await delay(wait, rollbackToken).ConfigureAwait(false);
                }
            }
        }
    }

    // The rollback phase outcome: clean compensations counted, faults listed, and whether the rollback
    // budget cut the unwind short. A small struct so the no-allocation goal of the result path is kept.
    private readonly record struct RollbackOutcome(
        IReadOnlyList<CompensationFailure> Failures, int Compensated, bool RollbackTimedOut);

    // A completed step paired with the one-based forward ordinal it ran at. Carrying the ordinal forward
    // (rather than recomputing it from the stack depth) keeps a later step's compensation position
    // correct even when conditional steps earlier in the saga were skipped.
    private readonly record struct CompletedStep(SagaStep<TContext> Step, int Ordinal);

    // A single unit of compensation work: the step (or group member) to undo and the forward ordinal to
    // report for it. A parallel group expands into one unit per completed member, all tagged with the
    // group slot's ordinal.
    private readonly record struct CompensationUnit(SagaStep<TContext> Step, int Ordinal);

    // The four notify helpers below pass the step name, ordinal, and duration directly to the observer
    // rather than through an Action, so the hot path allocates no per-step closure. When no real
    // observer is registered, hasObserver is false and the call is skipped entirely (and no timestamp
    // was captured), so the no-observer path pays nothing for the richer payload.

    private void NotifyStepCompleted(string stepName, int ordinal, long startTimestamp)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnStepCompleted(stepName, ordinal, Stopwatch.GetElapsedTime(startTimestamp));
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyStepFailed(string stepName, Exception exception, int ordinal, long startTimestamp)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnStepFailed(stepName, exception, ordinal, Stopwatch.GetElapsedTime(startTimestamp));
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyCompensated(string stepName, int ordinal, long startTimestamp)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnCompensated(stepName, ordinal, Stopwatch.GetElapsedTime(startTimestamp));
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyCompensationFailed(string stepName, Exception exception, int ordinal, long startTimestamp)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnCompensationFailed(stepName, exception, ordinal, Stopwatch.GetElapsedTime(startTimestamp));
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }

    private void NotifyStepSkipped(string stepName, int ordinal)
    {
        if (!hasObserver)
        {
            return;
        }

        try
        {
            observer.OnStepSkipped(stepName, ordinal);
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never disrupt orchestration or rollback.
        }
    }
}
