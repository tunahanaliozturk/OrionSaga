namespace Moongazing.OrionSaga.Orchestration;

/// <summary>A compensation that itself failed, leaving that step's effect potentially not undone.</summary>
/// <param name="StepName">The step whose compensation threw.</param>
/// <param name="Exception">The exception the compensation threw.</param>
public readonly record struct CompensationFailure(string StepName, Exception Exception);

/// <summary>
/// The outcome of running a saga: success, or the step that failed plus the result of rolling back.
/// </summary>
public sealed class SagaResult
{
    private SagaResult(
        bool succeeded,
        string? failedStep,
        Exception? failure,
        IReadOnlyList<CompensationFailure> compensationFailures)
    {
        Succeeded = succeeded;
        FailedStep = failedStep;
        Failure = failure;
        CompensationFailures = compensationFailures;
    }

    /// <summary>True when every step completed.</summary>
    public bool Succeeded { get; }

    /// <summary>The name of the step that failed, or null on success.</summary>
    public string? FailedStep { get; }

    /// <summary>The exception that failed the saga, or null on success.</summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Compensations that themselves failed during rollback. Empty when the saga succeeded or
    /// rolled back cleanly. A non-empty list means some effects may not have been undone and need
    /// manual attention.
    /// </summary>
    public IReadOnlyList<CompensationFailure> CompensationFailures { get; }

    /// <summary>True when the saga failed but every completed step compensated cleanly.</summary>
    public bool RolledBackCleanly => !Succeeded && CompensationFailures.Count == 0;

    internal static SagaResult Success { get; } =
        new(succeeded: true, failedStep: null, failure: null, []);

    internal static SagaResult Failed(
        string failedStep, Exception failure, IReadOnlyList<CompensationFailure> compensationFailures) =>
        new(succeeded: false, failedStep, failure, compensationFailures);
}
