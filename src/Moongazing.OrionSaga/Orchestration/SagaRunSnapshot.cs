namespace Moongazing.OrionSaga.Orchestration;

using System.Collections.Immutable;

/// <summary>
/// A read-only view of a saga run in progress: the step currently running, the steps that have
/// completed so far (and would therefore be compensated if a later step failed), and the steps that
/// have not run yet. Handed to <see cref="Observers.ISagaObserver.OnProgress(SagaRunSnapshot)"/> after
/// each forward transition so diagnostics and tests can inspect the run without reaching into the
/// executor's mutable state.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is immutable and self-contained: it copies the names and ordinals it reports into its
/// own <see cref="System.Collections.Immutable.ImmutableArray{T}"/> fields at the moment it is taken,
/// so holding a reference to it never exposes the executor's live <c>completed</c> stack or step list,
/// and a later transition cannot mutate a snapshot taken earlier. The collections it exposes are truly
/// immutable: a caller cannot down-cast a returned list to a mutable array to alter the snapshot or the
/// run. Each name is a step's identifier; ordinals are one-based forward positions.
/// </para>
/// <para>
/// <see cref="WouldCompensate"/> is the completed steps in the reverse order they would unwind, so a
/// caller can see exactly what rollback would do from the current point without running it. It is the
/// reverse of <see cref="CompletedSteps"/>.
/// </para>
/// </remarks>
public sealed class SagaRunSnapshot
{
    private readonly ImmutableArray<StepReference> completedSteps;
    private readonly ImmutableArray<StepReference> pendingSteps;

    internal SagaRunSnapshot(
        StepReference? currentStep,
        ImmutableArray<StepReference> completedSteps,
        ImmutableArray<StepReference> pendingSteps,
        int totalSteps)
    {
        CurrentStep = currentStep;
        this.completedSteps = completedSteps.IsDefault ? ImmutableArray<StepReference>.Empty : completedSteps;
        this.pendingSteps = pendingSteps.IsDefault ? ImmutableArray<StepReference>.Empty : pendingSteps;
        TotalSteps = totalSteps;
    }

    /// <summary>
    /// The step whose forward action is running at the moment the snapshot is taken, or null when no
    /// step is currently running (for example after the final step completed and before the run
    /// returns). Carries the step's name and one-based forward ordinal.
    /// </summary>
    public StepReference? CurrentStep { get; }

    /// <summary>
    /// The steps whose forward actions have completed, in the order they ran. These are exactly the
    /// steps that would be compensated, in reverse, if a later step failed. Empty before the first
    /// step completes. A skipped conditional step never appears here; a completed parallel group
    /// appears as its single slot.
    /// </summary>
    /// <remarks>
    /// Backed by an <see cref="ImmutableArray{T}"/>, so the returned list is truly immutable: a caller
    /// cannot down-cast it to a mutable array and alter the snapshot or the executor's state.
    /// </remarks>
    public IReadOnlyList<StepReference> CompletedSteps => completedSteps;

    /// <summary>
    /// The steps that have not run yet, in declaration order, starting after
    /// <see cref="CurrentStep"/>. Empty once the run reaches its last step. Includes conditional steps
    /// whose predicate has not yet been evaluated, since whether they will run is not known in advance.
    /// </summary>
    /// <remarks>
    /// Backed by an <see cref="ImmutableArray{T}"/>, so the returned list is truly immutable: a caller
    /// cannot down-cast it to a mutable array and alter the snapshot or the executor's state.
    /// </remarks>
    public IReadOnlyList<StepReference> PendingSteps => pendingSteps;

    /// <summary>
    /// The completed steps in the reverse order they would unwind, i.e. what a rollback from the
    /// current point would compensate, newest-first. The reverse of <see cref="CompletedSteps"/>.
    /// </summary>
    public IReadOnlyList<StepReference> WouldCompensate
    {
        get
        {
            var count = completedSteps.Length;
            if (count == 0)
            {
                return ImmutableArray<StepReference>.Empty;
            }

            // Build the reversed view into an immutable array, so the returned list is leak-proof for the
            // same reason CompletedSteps is: a caller cannot down-cast it to a mutable array.
            var builder = ImmutableArray.CreateBuilder<StepReference>(count);
            for (var i = 0; i < count; i++)
            {
                builder.Add(completedSteps[count - 1 - i]);
            }

            return builder.MoveToImmutable();
        }
    }

    /// <summary>The total number of steps declared in the saga, including any not yet reached.</summary>
    public int TotalSteps { get; }
}

/// <summary>
/// A reference to a step in a <see cref="SagaRunSnapshot"/>: its name and one-based forward ordinal.
/// A value type carrying only copied identifiers, so it exposes nothing mutable from the executor.
/// </summary>
/// <param name="Name">The step name.</param>
/// <param name="Ordinal">The step's one-based forward position (the first step is 1).</param>
public readonly record struct StepReference(string Name, int Ordinal);
