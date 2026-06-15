namespace Moongazing.OrionSaga.Observers;

/// <summary>
/// Consumer hook notified about saga progress, for logging and alerting. Implementations are
/// observability only: they must not throw, and the executor swallows any fault they raise so an
/// observer outage never disrupts orchestration or rollback.
/// </summary>
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
