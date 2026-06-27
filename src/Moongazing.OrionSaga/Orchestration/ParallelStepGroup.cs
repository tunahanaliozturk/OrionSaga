namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// A set of independent steps run concurrently within one stage of a saga. The group is opt-in: a saga
/// stays sequential unless a group is added via <see cref="SagaBuilder{TContext}.AddParallelGroup"/>.
/// The group is composed onto a single ordinary <see cref="SagaStep{TContext}"/> so the parent saga's
/// ordering and reverse-order rollback are unchanged: the group occupies one slot in the parent's flat
/// list, and the overall newest-first unwind across stages is preserved.
/// </summary>
/// <remarks>
/// <para>
/// Forward: every member's forward action is started concurrently and the group waits for all of them.
/// If every member completes, the group completes and the parent may later roll it back. If any member
/// faults, the group waits for the in-flight members to settle, compensates the members that did
/// complete (so no member's effect is left dangling), and then surfaces the first member failure so the
/// parent rolls back the stages that ran before the group. The faulted member is not compensated, since
/// its forward action did not complete, exactly as for a single failed step.
/// </para>
/// <para>
/// Compensation order within a group is defined and stable: completed members compensate in reverse of
/// their declaration order in the group. This holds both when the parent rolls the whole group back
/// after a later stage fails and when the group compensates its own completed members after one member
/// faults. Across stages the parent's newest-first order still governs, so the group as a whole unwinds
/// at its slot.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The shared context threaded through the saga.</typeparam>
internal sealed class ParallelStepGroup<TContext>
{
    private readonly IReadOnlyList<SagaStep<TContext>> members;

    // Members that completed their forward action, in declaration order. Recorded on the success path so
    // the group's compensation (run by the parent when a later stage fails) can unwind them in reverse.
    private SagaStep<TContext>[]? completedMembers;

    internal ParallelStepGroup(IReadOnlyList<SagaStep<TContext>> members)
    {
        this.members = members;
    }

    /// <summary>
    /// The group's forward action: run every member concurrently. On full success, record the completed
    /// members for later rollback and return. On any member failure, compensate the members that did
    /// complete (reverse declaration order) and rethrow the first failure so the parent rolls back the
    /// prior stages.
    /// </summary>
    internal async Task ExecuteAsync(TContext context, CancellationToken cancellationToken)
    {
        var tasks = new Task[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            // Start each member's forward action. RunMemberAsync honours the member's own per-step
            // timeout and forward retry, so a group member behaves like a standalone step on its own
            // attempt while sharing the group's cancellation token.
            tasks[i] = RunMemberAsync(members[i], context, cancellationToken);
        }

        // Wait for every member to settle before deciding the group's outcome, so a failure never leaves
        // a sibling running unobserved. WhenAll surfaces the first exception; the per-task status below
        // tells us exactly which members completed and which faulted.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the group turns any member fault into an in-group rollback then rethrows
        catch (Exception)
#pragma warning restore CA1031
        {
            // At least one member faulted. Gather the members that completed cleanly so they can be
            // compensated in reverse declaration order, then surface the originating failure.
            var done = new List<SagaStep<TContext>>(members.Count);
            for (var i = 0; i < members.Count; i++)
            {
                if (tasks[i].IsCompletedSuccessfully)
                {
                    done.Add(members[i]);
                }
            }

            await CompensateMembersAsync(done, context, cancellationToken).ConfigureAwait(false);

            // Rethrow the first member failure (preserving its stack) so the parent treats the group as
            // a failed step: a cancellation propagates as a cancellation, any other fault as a failure.
            ThrowFirstFailure(tasks);
            throw; // unreachable: ThrowFirstFailure always throws when a task faulted.
        }

        // Every member completed: remember them (declaration order) for the parent-driven rollback.
        completedMembers = [.. members];
    }

    /// <summary>
    /// The group's compensation, invoked by the parent when a later stage fails and the group is unwound
    /// as one slot. Compensate every member that completed, in reverse declaration order, under the
    /// rollback token the parent supplies (so a rollback budget still bounds the group).
    /// </summary>
    internal Task CompensateAsync(TContext context, CancellationToken rollbackToken)
    {
        // No completed members recorded means the group never fully completed, so there is nothing to
        // undo here. The success path always records them, so this is only the defensive empty case.
        if (completedMembers is not { Length: > 0 } done)
        {
            return Task.CompletedTask;
        }

        return CompensateMembersReverseAsync(done, context, rollbackToken);
    }

    private static async Task RunMemberAsync(
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

    // Compensate the given completed members in reverse declaration order. A compensation fault is
    // swallowed so the remaining members still compensate, mirroring the parent's best-effort rollback.
    private static async Task CompensateMembersReverseAsync(
        IReadOnlyList<SagaStep<TContext>> done, TContext context, CancellationToken rollbackToken)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try
            {
                await done[i].Compensate(context, rollbackToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // keep compensating the remaining members even if one undo fails
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort: a member compensation fault must not stop the others from unwinding.
            }
        }
    }

    // The in-group rollback after a member faults: the supplied list is in declaration order, so reuse
    // the reverse-order helper to keep the documented within-group order identical on both paths.
    private static Task CompensateMembersAsync(
        IReadOnlyList<SagaStep<TContext>> done, TContext context, CancellationToken cancellationToken) =>
        CompensateMembersReverseAsync(done, context, cancellationToken);

    private static void ThrowFirstFailure(Task[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task.IsFaulted && task.Exception is { } aggregate)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(aggregate.InnerException ?? aggregate).Throw();
            }

            if (task.IsCanceled)
            {
                // A cancelled member surfaces as a cancellation so the parent reports the group as a
                // cancellation rather than a business failure, consistent with a single cancelled step.
                task.GetAwaiter().GetResult();
            }
        }
    }
}
