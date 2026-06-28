namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

/// <summary>
/// Covers the v0.6.0 saga-state inspection surface: the read-only <see cref="SagaRunSnapshot"/> handed
/// to <see cref="ISagaObserver.OnProgress(SagaRunSnapshot)"/> after each forward transition. The
/// snapshot reflects the in-progress run (current, completed, pending, and what would compensate) and
/// exposes nothing mutable from the executor.
/// </summary>
public sealed class SagaSnapshotTests
{
    /// <summary>Records every snapshot handed to it so the progression can be asserted after the run.</summary>
    private sealed class SnapshotRecorder : ISagaObserver
    {
        public List<SagaRunSnapshot> Snapshots { get; } = [];

        public void OnStepCompleted(string stepName)
        {
        }

        public void OnStepFailed(string stepName, Exception exception)
        {
        }

        public void OnCompensated(string stepName)
        {
        }

        public void OnCompensationFailed(string stepName, Exception exception)
        {
        }

        public void OnProgress(SagaRunSnapshot snapshot) => Snapshots.Add(snapshot);
    }

    private static string[] Names(IReadOnlyList<StepReference> refs) =>
        [.. refs.Select(r => r.Name)];

    [Fact]
    public async Task The_snapshot_reflects_current_completed_and_pending_as_the_run_progresses()
    {
        var recorder = new SnapshotRecorder();
        var saga = new SagaBuilder<object>()
            .WithObserver(recorder)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        // One snapshot before each step plus one terminal snapshot: four in total for three steps.
        Assert.Equal(4, recorder.Snapshots.Count);

        // Before step a: a is current, nothing completed, b and c pending.
        var first = recorder.Snapshots[0];
        Assert.Equal("a", first.CurrentStep?.Name);
        Assert.Equal(1, first.CurrentStep?.Ordinal);
        Assert.Empty(first.CompletedSteps);
        Assert.Equal(["b", "c"], Names(first.PendingSteps));
        Assert.Equal(3, first.TotalSteps);

        // Before step b: a completed, b current, c pending.
        var second = recorder.Snapshots[1];
        Assert.Equal("b", second.CurrentStep?.Name);
        Assert.Equal(["a"], Names(second.CompletedSteps));
        Assert.Equal(["c"], Names(second.PendingSteps));

        // Before step c: a and b completed (run order), c current, nothing pending.
        var third = recorder.Snapshots[2];
        Assert.Equal("c", third.CurrentStep?.Name);
        Assert.Equal(["a", "b"], Names(third.CompletedSteps));
        Assert.Empty(third.PendingSteps);

        // Terminal: no current step, all three completed.
        var terminal = recorder.Snapshots[3];
        Assert.Null(terminal.CurrentStep);
        Assert.Equal(["a", "b", "c"], Names(terminal.CompletedSteps));
        Assert.Empty(terminal.PendingSteps);
    }

    [Fact]
    public async Task What_would_compensate_is_the_completed_steps_in_reverse_unwind_order()
    {
        var recorder = new SnapshotRecorder();
        var saga = new SagaBuilder<object>()
            .WithObserver(recorder)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        // Before step c, a and b have completed: they would unwind newest-first, b then a.
        var beforeC = recorder.Snapshots[2];
        Assert.Equal(["a", "b"], Names(beforeC.CompletedSteps));
        Assert.Equal(["b", "a"], Names(beforeC.WouldCompensate));
    }

    [Fact]
    public async Task A_skipped_conditional_step_does_not_appear_in_the_completed_set()
    {
        var recorder = new SnapshotRecorder();
        var saga = new SagaBuilder<object>()
            .WithObserver(recorder)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddConditionalStep("skipped", (_, _) => Task.CompletedTask, condition: _ => false)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        // The terminal snapshot shows a and c completed; the skipped step is absent and never a
        // compensation candidate. Ordinals preserve the declared positions (a=1, c=3).
        var terminal = recorder.Snapshots[^1];
        Assert.Equal(["a", "c"], Names(terminal.CompletedSteps));
        Assert.Equal([1, 3], terminal.CompletedSteps.Select(r => r.Ordinal).ToArray());
        Assert.DoesNotContain(terminal.CompletedSteps, r => r.Name == "skipped");
    }

    [Fact]
    public async Task The_snapshot_is_immutable_and_a_later_transition_does_not_mutate_an_earlier_one()
    {
        var recorder = new SnapshotRecorder();
        var saga = new SagaBuilder<object>()
            .WithObserver(recorder)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        // The first snapshot (taken before step a) still reports an empty completed set after the run
        // finished, proving it copied state rather than aliasing the executor's live stack.
        var first = recorder.Snapshots[0];
        Assert.Empty(first.CompletedSteps);
        Assert.Equal("a", first.CurrentStep?.Name);

        // The returned collections are not the executor's mutable internals: they are read-only views.
        Assert.IsNotType<List<StepReference>>(first.CompletedSteps);
        Assert.IsNotType<Stack<StepReference>>(first.CompletedSteps);
    }

    [Fact]
    public async Task The_snapshot_collections_cannot_be_down_cast_to_a_mutable_array_and_mutated()
    {
        var recorder = new SnapshotRecorder();
        var saga = new SagaBuilder<object>()
            .WithObserver(recorder)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .AddStep("d", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        // Before step c, a and b have completed and d is still pending; this snapshot has a non-empty
        // completed set and a non-empty pending set, so both leak vectors can be probed.
        var beforeC = recorder.Snapshots[2];
        Assert.Equal(["a", "b"], Names(beforeC.CompletedSteps));
        Assert.Equal(["d"], Names(beforeC.PendingSteps));

        // The reported lists must not be a plain StepReference[] that a caller could down-cast and
        // mutate. Against the unfixed code (which returned the executor's StepReference[] as
        // IReadOnlyList) these casts SUCCEED, the writes below mutate the snapshot, and the trailing
        // assertions fail -- this test is RED until the snapshot exposes a truly immutable collection.
        Assert.Null(beforeC.CompletedSteps as StepReference[]);
        Assert.Null(beforeC.PendingSteps as StepReference[]);
        Assert.Null(beforeC.WouldCompensate as StepReference[]);

        // Mutating an enumerated copy must not touch the snapshot, and re-reading returns the originals.
        var copy = beforeC.CompletedSteps.ToArray();
        copy[0] = new StepReference("hacked", 99);
        Assert.Equal(["a", "b"], Names(beforeC.CompletedSteps));
        Assert.Equal(["b", "a"], Names(beforeC.WouldCompensate));
    }

    [Fact]
    public async Task A_run_with_no_observer_does_not_build_snapshots_and_behaves_unchanged()
    {
        // No WithObserver: OnProgress is never called and no snapshot is built (the no-observer path is
        // gated). The run still behaves exactly as before.
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.StepsCompleted);
    }

    [Fact]
    public async Task A_faulting_progress_observer_does_not_disrupt_the_run()
    {
        var saga = new SagaBuilder<object>()
            .WithObserver(new ThrowingProgressObserver())
            .AddStep("a", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        // The observer throws on every OnProgress, but the run completes regardless.
        Assert.True(result.Succeeded);
    }

    private sealed class ThrowingProgressObserver : ISagaObserver
    {
        public void OnStepCompleted(string stepName)
        {
        }

        public void OnStepFailed(string stepName, Exception exception)
        {
        }

        public void OnCompensated(string stepName)
        {
        }

        public void OnCompensationFailed(string stepName, Exception exception)
        {
        }

        public void OnProgress(SagaRunSnapshot snapshot) => throw new InvalidOperationException("boom");
    }
}
