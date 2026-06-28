namespace Moongazing.OrionSaga.Observers;

using Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Consumer hook notified about saga progress, for logging and alerting. Implementations are
/// observability only: they must not throw, and the executor swallows any fault they raise so an
/// observer outage never disrupts orchestration or rollback.
/// </summary>
/// <remarks>
/// Each notification has two overloads: a name-only form and a richer form that also carries the
/// step's one-based <c>ordinal</c> and its measured <c>duration</c>. The richer overloads are default
/// interface methods that forward to the name-only form, so an observer written against the original
/// surface keeps compiling and behaving unchanged. Override a richer overload to receive the ordinal
/// and duration; the executor always invokes the richer overload, so an override is honoured without
/// any change at the call site.
/// </remarks>
public interface ISagaObserver
{
    /// <summary>Called after a step's forward action completes.</summary>
    /// <param name="stepName">The step name.</param>
    void OnStepCompleted(string stepName);

    /// <summary>Called when a step's forward action fails, before rollback begins.</summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="exception">The failure.</param>
    void OnStepFailed(string stepName, Exception exception);

    /// <summary>Called after a completed step is compensated during rollback.</summary>
    /// <param name="stepName">The step name.</param>
    void OnCompensated(string stepName);

    /// <summary>Called when a compensation itself fails during rollback.</summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="exception">The compensation failure.</param>
    void OnCompensationFailed(string stepName, Exception exception);

    /// <summary>
    /// Called when a step is skipped because its condition evaluated false. A skipped step's forward
    /// action does not run and it is never compensated. The default is a no-op so existing observers
    /// are unaffected; override to observe skips.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    void OnStepSkipped(string stepName)
    {
    }

    /// <summary>
    /// Called when a step is skipped, with its one-based forward position. The default forwards to
    /// <see cref="OnStepSkipped(string)"/> so existing observers are unaffected; override this overload
    /// to receive the ordinal.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="ordinal">The step's one-based position in the saga (the first step is 1).</param>
    void OnStepSkipped(string stepName, int ordinal) => OnStepSkipped(stepName);

    /// <summary>
    /// Called after a step's forward action completes, with its position and how long it took. The
    /// default forwards to <see cref="OnStepCompleted(string)"/> so existing observers are unaffected;
    /// override this overload to receive the ordinal and duration.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="ordinal">The step's one-based position in the saga (the first step is 1).</param>
    /// <param name="duration">How long the forward action ran.</param>
    void OnStepCompleted(string stepName, int ordinal, TimeSpan duration) => OnStepCompleted(stepName);

    /// <summary>
    /// Called when a step's forward action fails, with its position and how long it ran before
    /// failing. The default forwards to <see cref="OnStepFailed(string, Exception)"/> so existing
    /// observers are unaffected; override this overload to receive the ordinal and duration.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="ordinal">The step's one-based position in the saga (the first step is 1).</param>
    /// <param name="duration">How long the forward action ran before it failed.</param>
    void OnStepFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) =>
        OnStepFailed(stepName, exception);

    /// <summary>
    /// Called after a completed step is compensated, with the step's forward position and how long the
    /// compensation took. The default forwards to <see cref="OnCompensated(string)"/> so existing
    /// observers are unaffected; override this overload to receive the ordinal and duration.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="ordinal">The compensated step's one-based forward position (the first step is 1).</param>
    /// <param name="duration">How long the compensation ran.</param>
    void OnCompensated(string stepName, int ordinal, TimeSpan duration) => OnCompensated(stepName);

    /// <summary>
    /// Called when a compensation itself fails, with the step's forward position and how long the
    /// compensation ran before failing. The default forwards to
    /// <see cref="OnCompensationFailed(string, Exception)"/> so existing observers are unaffected;
    /// override this overload to receive the ordinal and duration.
    /// </summary>
    /// <param name="stepName">The step name.</param>
    /// <param name="exception">The compensation failure.</param>
    /// <param name="ordinal">The compensated step's one-based forward position (the first step is 1).</param>
    /// <param name="duration">How long the compensation ran before it failed.</param>
    void OnCompensationFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) =>
        OnCompensationFailed(stepName, exception);

    /// <summary>
    /// Called after each forward transition with a read-only snapshot of the run in progress: the
    /// step now running, the steps that have completed (and would compensate), and the steps not yet
    /// reached. The default is a no-op so existing observers are unaffected; override it to inspect a
    /// run mid-flight for diagnostics or tests. The snapshot is immutable and self-contained, so it can
    /// be retained and read after the run returns without exposing the executor's mutable state.
    /// </summary>
    /// <remarks>
    /// Fired only while there is forward progress to report: once before each step's forward action
    /// runs (with that step as the current step) and once after the final step completes (with no
    /// current step). It is not fired during rollback; the existing compensation notifications report
    /// that phase. Like every notification, a fault it raises is swallowed and never disrupts the run.
    /// </remarks>
    /// <param name="snapshot">The read-only view of the run at this point.</param>
    void OnProgress(SagaRunSnapshot snapshot)
    {
    }
}

/// <summary>A no-op observer used when the consumer registers none.</summary>
public sealed class NullSagaObserver : ISagaObserver
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullSagaObserver Instance = new();

    private NullSagaObserver()
    {
    }

    /// <inheritdoc />
    public void OnStepCompleted(string stepName)
    {
    }

    /// <inheritdoc />
    public void OnStepFailed(string stepName, Exception exception)
    {
    }

    /// <inheritdoc />
    public void OnCompensated(string stepName)
    {
    }

    /// <inheritdoc />
    public void OnCompensationFailed(string stepName, Exception exception)
    {
    }
}
