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
    private RetryPolicy? compensationRetry;
    private TimeSpan? rollbackBudget;

    /// <summary>Add a step with a forward action and a compensating action.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="execute">The forward action.</param>
    /// <param name="compensate">The compensating action; a no-op when null.</param>
    /// <param name="timeout">
    /// An optional maximum duration for the forward action. When it overruns, the step is cancelled
    /// and the saga rolls back, reporting the timeout. Null means no budget. Must be positive when set.
    /// </param>
    /// <param name="forwardRetry">
    /// An optional retry-with-backoff policy for the forward action. When set, a transient forward fault
    /// is retried up to the policy's attempt count before the step fails and the saga rolls back. Null
    /// means a single attempt. A per-step <paramref name="timeout"/> bounds each attempt individually.
    /// </param>
    /// <param name="compensationRetry">
    /// An optional retry-with-backoff policy for this step's compensation. Null falls back to any
    /// saga-wide policy set via <see cref="WithCompensationRetry"/>, or a single attempt when neither is set.
    /// </param>
    /// <param name="condition">
    /// An optional predicate evaluated against the context just before this step would run. When it
    /// returns false the step is skipped: it is neither executed nor compensated and does not count as
    /// completed. Null means the step always runs. Prefer this over branching inside
    /// <paramref name="execute"/> so a skipped step is reflected in the result and observer payloads.
    /// </param>
    public SagaBuilder<TContext> AddStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null,
        RetryPolicy? forwardRetry = null,
        RetryPolicy? compensationRetry = null,
        Func<TContext, bool>? condition = null)
    {
        steps.Add(new SagaStep<TContext>(
            name, execute, compensate, timeout, forwardRetry, compensationRetry, condition));
        return this;
    }

    /// <summary>
    /// Add a step whose forward action returns a value, flowing that value into the context for the
    /// next step instead of having the step mutate shared state by hand. The forward action produces a
    /// <typeparamref name="TResult"/>; <paramref name="apply"/> then writes it into the context. This
    /// is an ergonomic layer over the untyped step: internally the produced value is applied to the
    /// context, so compensation, ordering, timeout, and reporting behave exactly as for any other step.
    /// </summary>
    /// <remarks>
    /// This is deliberately a distinct method, not a generic overload of <see cref="AddStep(string,
    /// Func{TContext, CancellationToken, Task}, Func{TContext, CancellationToken, Task}?, TimeSpan?,
    /// RetryPolicy?, RetryPolicy?, Func{TContext, bool}?)"/>.
    /// An existing untyped call such as
    /// <c>AddStep("reserve", (_, _) =&gt; ReserveAsync(), (ctx, _) =&gt; ReleaseAsync(ctx))</c> whose
    /// forward action happens to return a <see cref="Task{TResult}"/> is convertible to a generic
    /// <c>AddStep&lt;TResult&gt;</c> candidate as well (the third lambda binds to <paramref name="apply"/>
    /// because an expression-bodied lambda returning a <see cref="Task"/> is also convertible to an
    /// <see cref="Action{T1, T2}"/>). Overload resolution could then silently rebind the caller's
    /// compensation to <paramref name="apply"/>, dropping the compensation and leaving the returned
    /// <see cref="Task"/> unobserved. Keeping the typed surface under its own name removes it from the
    /// <c>AddStep</c> candidate set entirely, so no existing <c>AddStep</c> call can ever rebind to it.
    /// Do not collapse this back into an <c>AddStep</c> overload.
    /// </remarks>
    /// <typeparam name="TResult">The type the forward action produces.</typeparam>
    /// <param name="name">The step name.</param>
    /// <param name="execute">The forward action, producing a value.</param>
    /// <param name="apply">
    /// Writes the produced value into the context so later steps can read it. Runs immediately after
    /// the forward action, on the same forward path; a fault it raises fails the step like any other
    /// forward fault and triggers rollback of the prior steps.
    /// </param>
    /// <param name="compensate">The compensating action; a no-op when null.</param>
    /// <param name="timeout">
    /// An optional maximum duration for the forward action. When it overruns, the step is cancelled
    /// and the saga rolls back, reporting the timeout. Null means no budget. Must be positive when set.
    /// The budget covers the forward action only; <paramref name="apply"/> is expected to be a cheap,
    /// synchronous assignment.
    /// </param>
    /// <param name="forwardRetry">
    /// An optional retry-with-backoff policy for the forward action. When set, a transient fault in the
    /// forward action (or in <paramref name="apply"/>, since they run on the same attempt) is retried up
    /// to the policy's attempt count before the step fails. Null means a single attempt. A per-step
    /// <paramref name="timeout"/> bounds each attempt individually.
    /// </param>
    /// <param name="compensationRetry">
    /// An optional retry-with-backoff policy for this step's compensation. Null falls back to any
    /// saga-wide policy set via <see cref="WithCompensationRetry"/>, or a single attempt when neither is set.
    /// </param>
    /// <param name="condition">
    /// An optional predicate evaluated against the context just before this step would run. When it
    /// returns false the step is skipped: neither <paramref name="execute"/> nor <paramref name="apply"/>
    /// runs, the step is not compensated, and it does not count as completed. Null means the step always
    /// runs.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="execute"/> or <paramref name="apply"/> is null.
    /// </exception>
    public SagaBuilder<TContext> AddResultStep<TResult>(
        string name,
        Func<TContext, CancellationToken, Task<TResult>> execute,
        Action<TContext, TResult> apply,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null,
        RetryPolicy? forwardRetry = null,
        RetryPolicy? compensationRetry = null,
        Func<TContext, bool>? condition = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(apply);

        steps.Add(new SagaStep<TContext>(
            name, Adapt(execute, apply), compensate, timeout, forwardRetry, compensationRetry, condition));
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

    /// <summary>
    /// Compose a named sub-saga into this saga so a complex flow reads as a few stages rather than one
    /// long list of steps. The <paramref name="configure"/> callback declares the sub-saga's steps over
    /// the same context; those steps are flattened inline into this saga at the point of the call, each
    /// prefixed with <paramref name="name"/> (as <c>"{name}/{step}"</c>) so it stays identifiable in
    /// results and telemetry.
    /// </summary>
    /// <remarks>
    /// Flattening, rather than nesting, is deliberate: the sub-saga's steps become ordinary steps of the
    /// parent, so they participate in the parent's single ordered run and its single reverse-order
    /// rollback. A failure anywhere after the sub-saga unwinds the sub-saga's completed steps along with
    /// the rest, newest-first, across the whole composed saga, with no special cases in the executor.
    /// Only the sub-saga's steps are consumed; any diagnostics or observer configured on the inner
    /// builder is ignored, because the parent owns run-level concerns. Conditional steps, timeouts,
    /// retries, and parallel groups declared inside the sub-saga are preserved as-is.
    /// </remarks>
    /// <param name="name">The sub-saga name, used to prefix its steps. Must be non-empty.</param>
    /// <param name="configure">Declares the sub-saga's steps on a fresh builder over the same context.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public SagaBuilder<TContext> AddSubSaga(string name, Action<SagaBuilder<TContext>> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        var inner = new SagaBuilder<TContext>();
        configure(inner);

        foreach (var step in inner.steps)
        {
            // Re-wrap each inner step under the stage-qualified name so it is identifiable in results
            // and telemetry, preserving its forward/compensation actions and every per-step policy. The
            // step then sits in the parent's flat list, so ordering and reverse compensation are the
            // parent executor's existing behaviour with no nesting.
            steps.Add(new SagaStep<TContext>(
                $"{name}/{step.Name}",
                step.Execute,
                step.Compensate,
                step.Timeout,
                step.ForwardRetry,
                step.CompensationRetry,
                step.Condition));
        }

        return this;
    }

    /// <summary>
    /// Add a named group of independent steps that run concurrently within one stage. The group is
    /// strictly opt-in; without it a saga runs sequentially as before. The <paramref name="configure"/>
    /// callback declares the group's members on a fresh builder over the same context; their forward
    /// actions all start together and the group waits for all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group is composed onto a single ordinary step occupying one slot in this saga's flat list, so
    /// the parent's ordering and reverse-order rollback are unchanged and the group as a whole unwinds at
    /// its position relative to the other stages.
    /// </para>
    /// <para>
    /// On failure of any member the group waits for the in-flight members to settle, compensates every
    /// member that completed (in reverse of their declaration order in the group), and then surfaces the
    /// failure so the stages before the group roll back. When the group completes but a later stage
    /// fails, the parent rolls the group back as one slot and its completed members again compensate in
    /// reverse declaration order. Each member's own per-step timeout and forward retry are honoured;
    /// members run concurrently, so their forward actions must be safe to run against the shared context
    /// at the same time. The group itself is not a transaction: a partial failure is surfaced and
    /// compensated, not isolated.
    /// </para>
    /// </remarks>
    /// <param name="name">The group name, used to prefix its members and identify the group slot. Must be non-empty.</param>
    /// <param name="configure">Declares the group's member steps on a fresh builder over the same context.</param>
    /// <param name="condition">
    /// An optional predicate evaluated against the context just before the group would run. When it
    /// returns false the whole group is skipped: no member runs and none is compensated. Null means the
    /// group always runs.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The group declares no members.</exception>
    public SagaBuilder<TContext> AddParallelGroup(
        string name,
        Action<SagaBuilder<TContext>> configure,
        Func<TContext, bool>? condition = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        var inner = new SagaBuilder<TContext>();
        configure(inner);

        if (inner.steps.Count == 0)
        {
            throw new InvalidOperationException($"Parallel group '{name}' must declare at least one member.");
        }

        // Prefix each member so it stays identifiable in any member-level diagnostics, then hand the
        // members to the group. The group is composed onto a single step: its Execute fans the members
        // out concurrently, and its Compensate unwinds the completed members in reverse declaration
        // order. The parent saga sees one ordinary step, so its ordering and reverse rollback are unchanged.
        // Conditional skipping applies at the group level (the group-step's condition), not per member:
        // a member's own condition is intentionally not carried in, since the group runs all its members
        // and a per-member skip would have no slot to be reported against.
        var members = new SagaStep<TContext>[inner.steps.Count];
        for (var i = 0; i < inner.steps.Count; i++)
        {
            var member = inner.steps[i];
            members[i] = new SagaStep<TContext>(
                $"{name}/{member.Name}",
                member.Execute,
                member.Compensate,
                member.Timeout,
                member.ForwardRetry,
                member.CompensationRetry,
                condition: null);
        }

        var group = new ParallelStepGroup<TContext>(members);
        steps.Add(new SagaStep<TContext>(
            name,
            group.ExecuteAsync,
            group.CompensateAsync,
            timeout: null,
            forwardRetry: null,
            compensationRetry: null,
            condition));

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

    /// <summary>
    /// Apply a saga-wide retry-with-backoff policy to every step's compensation, since a failed undo is
    /// the most expensive outcome of a rollback. A step that declares its own compensation retry overrides
    /// this for that step. Null clears the saga-wide policy.
    /// </summary>
    /// <param name="value">The compensation retry policy, or null to clear it.</param>
    public SagaBuilder<TContext> WithCompensationRetry(RetryPolicy? value)
    {
        compensationRetry = value;
        return this;
    }

    /// <summary>
    /// Bound the whole rollback/compensation phase to a maximum duration. Today rollback runs with a
    /// non-cancelled token, so a hung compensation can block forever; with a budget set, the token passed
    /// to compensations is cancelled once the budget elapses, so a hung compensation is cut short and the
    /// run reports the rollback as having timed out. The budget covers the entire unwind, not each step.
    /// Null means no budget (the prior unbounded behaviour). Must be positive when set.
    /// </summary>
    /// <param name="value">The rollback-phase budget, or null for no budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is set and not positive.</exception>
    public SagaBuilder<TContext> WithRollbackBudget(TimeSpan? value)
    {
        if (value is { } budget && budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), budget, "Rollback budget must be positive.");
        }

        rollbackBudget = value;
        return this;
    }

    /// <summary>Build the runnable saga.</summary>
    public Saga<TContext> Build() => Build(delay: null);

    // Build with an optional injected backoff delay. The public Build passes null, so the saga uses the
    // real Task.Delay; tests pass a delay that records the requested wait and returns synchronously, so
    // retry/backoff behaviour is asserted without any wall-clock sleep. Internal so only the test project
    // (via InternalsVisibleTo) can reach the seam; the public surface stays the single parameterless Build.
    internal Saga<TContext> Build(Func<TimeSpan, CancellationToken, Task>? delay) =>
        new(steps.ToArray(), diagnostics, observer, compensationRetry, rollbackBudget, delay);

    // Fold the typed forward action and its apply step into the single untyped delegate shape the
    // executor already runs. The value the forward action produces is handed to apply, which lands it
    // in the context; the executor never sees the result type, so the typed path adds no hot-path cost.
    private static Func<TContext, CancellationToken, Task> Adapt<TResult>(
        Func<TContext, CancellationToken, Task<TResult>> execute,
        Action<TContext, TResult> apply) =>
        async (context, cancellationToken) =>
        {
            var result = await execute(context, cancellationToken).ConfigureAwait(false);
            apply(context, result);
        };
}
