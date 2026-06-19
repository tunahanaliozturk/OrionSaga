namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// Thrown internally when a step's forward action overruns its per-step timeout: the per-step
/// deadline genuinely elapsed while the caller's token had not been cancelled. It derives from
/// <see cref="OperationCanceledException"/> so a timeout is still observed as a cancellation by
/// callers, while letting the saga distinguish a real deadline overrun from an unrelated
/// cancellation a step raised for its own reasons (for example an inner HttpClient timeout).
/// </summary>
public sealed class SagaStepTimeoutException : OperationCanceledException
{
    /// <summary>Create a timeout exception for a named step and its elapsed budget.</summary>
    /// <param name="stepName">The step whose deadline elapsed.</param>
    /// <param name="timeout">The per-step budget that was exceeded.</param>
    /// <param name="innerException">The underlying cancellation observed when the deadline fired.</param>
    public SagaStepTimeoutException(string stepName, TimeSpan timeout, Exception innerException)
        : base($"Step '{stepName}' exceeded its per-step timeout of {timeout}.", innerException)
    {
        StepName = stepName;
        Timeout = timeout;
    }

    /// <summary>The step whose per-step deadline elapsed.</summary>
    public string StepName { get; }

    /// <summary>The per-step budget that was exceeded.</summary>
    public TimeSpan Timeout { get; }
}
