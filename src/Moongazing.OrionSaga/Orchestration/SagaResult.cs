namespace Moongazing.OrionSaga.Orchestration;

/// <summary>A compensation that itself failed, leaving that step's effect potentially not undone.</summary>
/// <param name="StepName">The step whose compensation threw.</param>
/// <param name="Exception">The exception the compensation threw.</param>
public readonly record struct CompensationFailure(string StepName, Exception Exception);

/// <summary>
/// How a saga run ended. Distinguishes a business failure from a cancellation so callers can tell an
/// operator or timeout cancellation apart from a step that genuinely faulted.
/// </summary>
public enum SagaOutcome
{
    /// <summary>Every step completed; no rollback ran.</summary>
    Succeeded = 0,

    /// <summary>A step's forward action threw a non-cancellation exception; completed steps rolled back.</summary>
    Failed = 1,

    /// <summary>
    /// The run was cancelled rather than failing on its own: either the caller's token was cancelled
    /// or a step exceeded its per-step timeout. Completed steps rolled back.
    /// </summary>
    Cancelled = 2,
}

/// <summary>
/// The outcome of running a saga: success, or the step that ended it (a failure, a cancellation, or a
/// per-step timeout) plus the result of rolling back.
/// </summary>
public sealed class SagaResult
{
    private SagaResult(
        SagaOutcome outcome,
        string? failedStep,
        Exception? failure,
        bool timedOut,
        int stepsCompleted,
        int stepsCompensated,
        int stepsSkipped,
        IReadOnlyList<CompensationFailure> compensationFailures,
        bool rollbackTimedOut)
    {
        Outcome = outcome;
        FailedStep = failedStep;
        Failure = failure;
        TimedOut = timedOut;
        StepsCompleted = stepsCompleted;
        StepsCompensated = stepsCompensated;
        StepsSkipped = stepsSkipped;
        CompensationFailures = compensationFailures;
        RollbackTimedOut = rollbackTimedOut;
    }

    /// <summary>How the run ended.</summary>
    public SagaOutcome Outcome { get; }

    /// <summary>True when every step completed.</summary>
    public bool Succeeded => Outcome == SagaOutcome.Succeeded;

    /// <summary>
    /// True when the run was cancelled (by the caller's token or a per-step timeout) rather than
    /// failing on its own. Distinct from <see cref="Failed"/> so a cancellation is not mistaken for a
    /// business failure.
    /// </summary>
    public bool Cancelled => Outcome == SagaOutcome.Cancelled;

    /// <summary>True when a step's forward action threw a non-cancellation exception.</summary>
    public bool Failed => Outcome == SagaOutcome.Failed;

    /// <summary>
    /// True when the cancellation was caused by a step exceeding its per-step timeout rather than by
    /// the caller's token. Always false unless <see cref="Cancelled"/> is true.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>
    /// How many step forward actions completed. On success this equals the number of steps in the
    /// saga. On a failure, cancellation, or timeout it is the count of steps that completed before the
    /// run ended; the step that ended the saga is not counted, since its forward action did not
    /// complete. A parallel group counts as one completed step (one stage), regardless of how many
    /// members it ran; its members are reflected individually in <see cref="StepsCompensated"/> when the
    /// group is rolled back.
    /// </summary>
    public int StepsCompleted { get; }

    /// <summary>
    /// How many completed compensations ran cleanly during rollback. Always zero on success, because a
    /// successful run rolls nothing back. A compensation that itself threw is recorded in
    /// <see cref="CompensationFailures"/> and is not counted here, so <see cref="StepsCompensated"/> plus
    /// <see cref="CompensationFailures"/> count equals the number of compensations rollback attempted.
    /// Each completed parallel group member that rollback unwinds is counted individually here, since the
    /// group's members compensate through the same per-step routine as sequential steps; a rolled-back
    /// group therefore contributes one count per completed member, not one for the group as a whole.
    /// </summary>
    public int StepsCompensated { get; }

    /// <summary>
    /// How many steps were skipped because their condition evaluated false. A skipped step's forward
    /// action did not run and it was not compensated, so it counts toward neither
    /// <see cref="StepsCompleted"/> nor <see cref="StepsCompensated"/>.
    /// </summary>
    public int StepsSkipped { get; }

    /// <summary>The name of the step that ended the saga, or null on success.</summary>
    public string? FailedStep { get; }

    /// <summary>
    /// The exception that ended the saga, or null on success. For a cancellation this is the
    /// observed <see cref="OperationCanceledException"/>.
    /// </summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Compensations that themselves failed during rollback. Empty when the saga succeeded or
    /// rolled back cleanly. A non-empty list means some effects may not have been undone and need
    /// manual attention.
    /// </summary>
    public IReadOnlyList<CompensationFailure> CompensationFailures { get; }

    /// <summary>
    /// True when the rollback phase was cut short by the configured rollback budget before every
    /// completed step finished compensating. Always false when no rollback budget is set or the
    /// rollback completed within it. When true, one or more compensations were cancelled mid-flight,
    /// so some effects may not have been undone and the run needs manual attention.
    /// </summary>
    public bool RollbackTimedOut { get; }

    /// <summary>
    /// True when the saga did not succeed but every completed step compensated cleanly and the rollback
    /// was not cut short by its budget.
    /// </summary>
    public bool RolledBackCleanly => !Succeeded && CompensationFailures.Count == 0 && !RollbackTimedOut;

    internal static SagaResult Success { get; } =
        new(
            SagaOutcome.Succeeded,
            failedStep: null,
            failure: null,
            timedOut: false,
            stepsCompleted: 0,
            stepsCompensated: 0,
            stepsSkipped: 0,
            [],
            rollbackTimedOut: false);

    internal static SagaResult CreateSuccess(int stepsCompleted, int stepsSkipped) =>
        stepsCompleted == 0 && stepsSkipped == 0
            ? Success
            : new(
                SagaOutcome.Succeeded,
                failedStep: null,
                failure: null,
                timedOut: false,
                stepsCompleted,
                stepsCompensated: 0,
                stepsSkipped,
                [],
                rollbackTimedOut: false);

    internal static SagaResult CreateFailed(
        string failedStep,
        Exception failure,
        int stepsCompleted,
        int stepsCompensated,
        int stepsSkipped,
        IReadOnlyList<CompensationFailure> compensationFailures,
        bool rollbackTimedOut) =>
        new(
            SagaOutcome.Failed,
            failedStep,
            failure,
            timedOut: false,
            stepsCompleted,
            stepsCompensated,
            stepsSkipped,
            compensationFailures,
            rollbackTimedOut);

    internal static SagaResult CreateCancelled(
        string failedStep,
        Exception failure,
        bool timedOut,
        int stepsCompleted,
        int stepsCompensated,
        int stepsSkipped,
        IReadOnlyList<CompensationFailure> compensationFailures,
        bool rollbackTimedOut) =>
        new(
            SagaOutcome.Cancelled,
            failedStep,
            failure,
            timedOut,
            stepsCompleted,
            stepsCompensated,
            stepsSkipped,
            compensationFailures,
            rollbackTimedOut);
}
