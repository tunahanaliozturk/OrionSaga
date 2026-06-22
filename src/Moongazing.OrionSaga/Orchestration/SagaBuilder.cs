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

    /// <summary>Add a step with a forward action and a compensating action.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="execute">The forward action.</param>
    /// <param name="compensate">The compensating action; a no-op when null.</param>
    /// <param name="timeout">
    /// An optional maximum duration for the forward action. When it overruns, the step is cancelled
    /// and the saga rolls back, reporting the timeout. Null means no budget. Must be positive when set.
    /// </param>
    public SagaBuilder<TContext> AddStep(
        string name,
        Func<TContext, CancellationToken, Task> execute,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null)
    {
        steps.Add(new SagaStep<TContext>(name, execute, compensate, timeout));
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
    /// Func{TContext, CancellationToken, Task}, Func{TContext, CancellationToken, Task}?, TimeSpan?)"/>.
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
    /// <exception cref="ArgumentNullException">
    /// <paramref name="execute"/> or <paramref name="apply"/> is null.
    /// </exception>
    public SagaBuilder<TContext> AddResultStep<TResult>(
        string name,
        Func<TContext, CancellationToken, Task<TResult>> execute,
        Action<TContext, TResult> apply,
        Func<TContext, CancellationToken, Task>? compensate = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(apply);

        steps.Add(new SagaStep<TContext>(name, Adapt(execute, apply), compensate, timeout));
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

    /// <summary>Build the runnable saga.</summary>
    public Saga<TContext> Build() => new(steps.ToArray(), diagnostics, observer);

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
