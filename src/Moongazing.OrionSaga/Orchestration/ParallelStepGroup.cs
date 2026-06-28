namespace Moongazing.OrionSaga.Orchestration;

using System.Diagnostics;

using Moongazing.OrionSaga.Diagnostics;

/// <summary>
/// A set of independent steps run concurrently within one stage of a saga. The group is opt-in: a saga
/// stays sequential unless a group is added via <see cref="SagaBuilder{TContext}.AddParallelGroup"/>.
/// The group is composed onto a single ordinary <see cref="SagaStep{TContext}"/> so the parent saga's
/// ordering and reverse-order rollback are unchanged: the group occupies one slot in the parent's flat
/// list, and the overall newest-first unwind across stages is preserved.
/// </summary>
/// <remarks>
/// <para>
/// Forward: every member whose own condition holds has its forward action started concurrently and the
/// group waits for all of them. If every started member completes, the group completes and the parent
/// may later roll it back. If any member faults, the group waits for the in-flight members to settle and
/// then surfaces a member failure so the parent rolls back the group's completed members and the stages
/// that ran before it. A member that overran its own per-step timeout takes priority when surfacing, so
/// a member deadline overrun is reported as a timeout (the parent's TimedOut outcome) rather than being
/// masked by a sibling's generic fault or by declaration order. The faulted member is not compensated,
/// since its forward action did not complete, exactly as for a single failed step.
/// </para>
/// <para>
/// Compensation is never performed inline by the group. Both after an in-group member fault and when a
/// later stage fails, the parent saga unwinds the group's completed members through its own per-step
/// compensation routine: each completed member compensates in reverse of its declaration order in the
/// group, honouring per-step compensation retry, emitting the same observer notifications, and recording
/// compensation failures in the parent result exactly like a sequential step. Across stages the parent's
/// newest-first order still governs, so the group as a whole unwinds at its slot.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
internal sealed class ParallelStepGroup<TContext>
{
    private readonly IReadOnlyList<SagaStep<TContext>> members;

    // Members that completed their forward action, in declaration order. Recorded on both the success
    // and the failure path so the parent saga can unwind them (in reverse) through its own per-step
    // compensation routine. Null until the forward action has run; an empty array means none completed.
    private SagaStep<TContext>[]? completedMembers;

    internal ParallelStepGroup(IReadOnlyList<SagaStep<TContext>> members)
    {
        this.members = members;
    }

    /// <summary>
    /// The members that completed their forward action, in declaration order. Populated by
    /// <see cref="ExecuteAsync"/> on both success and failure so the parent can compensate them through
    /// its own routine. Empty before the group runs and when no member completed.
    /// </summary>
    internal IReadOnlyList<SagaStep<TContext>> CompletedMembers => completedMembers ?? [];

    /// <summary>
    /// The group's forward action: run every member whose condition holds concurrently. On full success
    /// record the completed members and return so the parent may later roll them back. On any member
    /// failure record the members that did complete (so the parent compensates them through its own
    /// routine) and rethrow the first failure so the parent rolls back the group and the prior stages.
    /// The group never compensates inline.
    /// </summary>
    internal async Task ExecuteAsync(TContext context, CancellationToken cancellationToken)
    {
        // Evaluate each member's condition once on the forward path, then start only the members that
        // should run. A skipped member's slot holds a placeholder completed task so the completed-member
        // scan can tell a skip apart from a real completion; a skipped member never runs and so is never
        // a candidate for compensation. A member predicate that throws faults that member's task rather
        // than escaping synchronously, so it routes through the group's normal failure path (the parent
        // rolls back the members that completed and the prior stages) and never leaves a started sibling
        // running unobserved.
        var skipped = new bool[members.Count];
        var tasks = new Task[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            bool run;
            try
            {
                run = ShouldRun(member, context);
            }
#pragma warning disable CA1031 // a throwing member predicate is treated as a member forward fault
            catch (Exception ex)
#pragma warning restore CA1031
            {
                skipped[i] = false;
                tasks[i] = Task.FromException(ex);
                continue;
            }

            skipped[i] = !run;
            tasks[i] = run ? RunMemberAsync(member, context, cancellationToken) : Task.CompletedTask;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the group records its completed members then rethrows for the parent to undo
        catch (Exception)
#pragma warning restore CA1031
        {
            // At least one member faulted. Record the members that completed cleanly (excluding skipped
            // ones) so the parent compensates them in reverse declaration order through its own routine,
            // then surface the originating failure. No inline compensation happens here.
            completedMembers = CollectCompleted(tasks, skipped);
            ThrowFirstFailure(tasks);
            throw; // unreachable: ThrowFirstFailure always throws when a task faulted.
        }

        // Every started member completed: record them (declaration order, skipped ones excluded) for the
        // parent-driven rollback.
        completedMembers = CollectCompleted(tasks, skipped);
    }

    // A member runs when it has no condition or its condition holds against the context. Evaluated only
    // on the forward path; a member with no condition always runs.
    private static bool ShouldRun(SagaStep<TContext> member, TContext context) =>
        member.Condition is not { } condition || condition(context);

    private SagaStep<TContext>[] CollectCompleted(Task[] tasks, bool[] skipped)
    {
        var done = new List<SagaStep<TContext>>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            // A member counts as completed only if it actually ran and finished successfully. A skipped
            // member never ran, so it is excluded and is never compensated.
            if (!skipped[i] && tasks[i].IsCompletedSuccessfully)
            {
                done.Add(members[i]);
            }
        }

        return [.. done];
    }

    private static async Task RunMemberAsync(
        SagaStep<TContext> member, TContext context, CancellationToken cancellationToken)
    {
        // Start a member span as a child of the group's step span (the current Activity when the group
        // fans out). Null when no listener, so the no-listener path starts no Activity. The span covers
        // the member's retries and timeout so its duration reflects the whole member forward run, and
        // its outcome is tagged once the member completes or faults. Each concurrent member gets its own
        // span, and they nest under the group span because Activity.Current is the group span here.
        using var memberActivity = SagaActivitySource.Source.StartActivity(
            SagaActivitySource.StepActivityName, ActivityKind.Internal);
        if (memberActivity is not null)
        {
            memberActivity.SetTag(SagaActivitySource.StepNameTag, member.Name);
        }

        try
        {
            await RunMemberCoreAsync(member, context, cancellationToken).ConfigureAwait(false);
            memberActivity?.SetTag(SagaActivitySource.OutcomeTag, "completed");
        }
        catch (SagaStepTimeoutException)
        {
            // A member that overran its own per-step deadline is tagged "timedout", distinct from an
            // ordinary cancellation, so the member span matches the run-level TimedOut outcome the parent
            // reports. SagaStepTimeoutException derives from OperationCanceledException, so this filter
            // must precede the plain cancellation catch below.
            memberActivity?.SetTag(SagaActivitySource.OutcomeTag, "timedout");
            throw;
        }
        catch (OperationCanceledException)
        {
            memberActivity?.SetTag(SagaActivitySource.OutcomeTag, "cancelled");
            throw;
        }
#pragma warning disable CA1031 // tag the member span's outcome, then rethrow for the group's failure path
        catch (Exception)
#pragma warning restore CA1031
        {
            memberActivity?.SetTag(SagaActivitySource.OutcomeTag, "failed");
            throw;
        }
    }

    private static async Task RunMemberCoreAsync(
        SagaStep<TContext> member, TContext context, CancellationToken cancellationToken)
    {
        // Honour a member's own forward retry and per-step timeout so a group member is as resilient as
        // a standalone step. A member with neither runs its forward action once, directly.
        if (member.ForwardRetry is not { } retry)
        {
            await RunMemberAttemptAsync(member, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunMemberAttemptAsync(member, context, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // a transient member fault is retried until the attempt budget is spent
            catch (Exception) when (attempt < retry.MaxAttempts)
#pragma warning restore CA1031
            {
                var wait = retry.DelayBeforeAttempt(attempt + 1);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task RunMemberAttemptAsync(
        SagaStep<TContext> member, TContext context, CancellationToken cancellationToken)
    {
        if (member.Timeout is not { } budget)
        {
            await member.Execute(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        timeoutCts.CancelAfter(budget);
        try
        {
            await member.Execute(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SagaStepTimeoutException(member.Name, budget, ex);
        }
    }

    private static void ThrowFirstFailure(Task[] tasks)
    {
        // A member that overran its per-step deadline takes priority over every other settled outcome, so
        // a member timeout is never masked by a sibling's generic fault or by array order. Without this,
        // the first faulted task in declaration order would surface, and a group whose member genuinely
        // timed out would be misreported as a plain Failed instead of TimedOut. Surfacing the timeout
        // preserves the TimedOut outcome through the group to the parent's result, observer, and span.
        //
        // A timed-out member's task does not land Faulted: SagaStepTimeoutException derives from
        // OperationCanceledException, and because it is thrown while the awaited per-step token was
        // cancelled the TPL marks the task Canceled (so task.Exception is null on it). The deadline must
        // therefore be detected by extracting each task's exception, not by reading task.Exception.
        foreach (var task in tasks)
        {
            if (TryGetException(task) is SagaStepTimeoutException timeout)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(timeout).Throw();
            }
        }

        // No member timed out: surface the first settled non-success in declaration order. A faulted
        // member rethrows its original exception; a cancelled member surfaces as a cancellation so the
        // parent reports the group as a cancellation rather than a business failure, consistent with a
        // single cancelled step.
        foreach (var task in tasks)
        {
            if (task.IsFaulted && task.Exception is { } aggregate)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(aggregate.InnerException ?? aggregate).Throw();
            }

            if (task.IsCanceled)
            {
                task.GetAwaiter().GetResult();
            }
        }
    }

    // Extract a settled task's exception without throwing, covering both a Faulted task (exception on
    // task.Exception) and a Canceled task (no task.Exception; the original OperationCanceledException is
    // surfaced only by observing the result). Returns null for a task that completed successfully. Used to
    // find a member deadline overrun, whose SagaStepTimeoutException lands the member task Canceled.
    private static Exception? TryGetException(Task task)
    {
        if (task.IsFaulted)
        {
            var aggregate = task.Exception;
            return aggregate?.InnerException ?? aggregate;
        }

        if (task.IsCanceled)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
#pragma warning disable CA1031 // peek the cancellation/timeout exception without disturbing the task
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        }

        return null;
    }
}
