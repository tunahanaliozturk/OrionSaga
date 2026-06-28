namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

/// <summary>
/// Compensation-correctness coverage for the v0.5.0 composition surface. These assert the behaviours the
/// review flagged: a throwing condition predicate rolls back like a forward fault; a parallel group's
/// in-group compensation runs through the saga's own per-step routine (per-step retry, observer
/// notifications, result recording); a group compensation failure is surfaced in the parent result;
/// rollback ordinals stay correct after a skipped step; and a per-member condition inside a group is
/// honoured rather than silently dropped. All gating is via TaskCompletionSource, never wall-clock sleeps.
/// </summary>
public sealed class SagaCompositionCorrectnessTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private sealed class Ledger
    {
        public List<string> Events { get; } = [];

        public void Record(string entry)
        {
            lock (Events)
            {
                Events.Add(entry);
            }
        }

        public List<string> Snapshot()
        {
            lock (Events)
            {
                return [.. Events];
            }
        }
    }

    /// <summary>Records each richer callback with its ordinal, locking so concurrent forward members are safe.</summary>
    private sealed record Note(string Kind, string StepName, int Ordinal);

    private sealed class OrdinalObserver : ISagaObserver
    {
        private readonly List<Note> notes = [];

        public IReadOnlyList<Note> Notes
        {
            get { lock (notes) { return [.. notes]; } }
        }

        private void Add(Note note)
        {
            lock (notes)
            {
                notes.Add(note);
            }
        }

        public void OnStepCompleted(string stepName) => Add(new Note("completed", stepName, 0));
        public void OnStepFailed(string stepName, Exception exception) => Add(new Note("failed", stepName, 0));
        public void OnCompensated(string stepName) => Add(new Note("compensated", stepName, 0));
        public void OnCompensationFailed(string stepName, Exception exception) => Add(new Note("compensationFailed", stepName, 0));
        public void OnStepSkipped(string stepName) => Add(new Note("skipped", stepName, 0));

        public void OnStepCompleted(string stepName, int ordinal, TimeSpan duration) => Add(new Note("completed", stepName, ordinal));
        public void OnStepFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) => Add(new Note("failed", stepName, ordinal));
        public void OnCompensated(string stepName, int ordinal, TimeSpan duration) => Add(new Note("compensated", stepName, ordinal));
        public void OnCompensationFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) => Add(new Note("compensationFailed", stepName, ordinal));
        public void OnStepSkipped(string stepName, int ordinal) => Add(new Note("skipped", stepName, ordinal));
    }

    // ---- Finding 1: a throwing condition predicate rolls back like a forward fault ----

    [Fact]
    public async Task A_throwing_condition_predicate_rolls_back_completed_steps_and_reports_failure()
    {
        var ledger = new Ledger();
        var observer = new OrdinalObserver();
        var boom = new InvalidOperationException("predicate-boom");

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddStep("a",
                (c, _) => { c.Record("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-a"); return Task.CompletedTask; })
            .AddConditionalStep("conditional",
                (c, _) => { c.Record("do-conditional"); return Task.CompletedTask; },
                condition: _ => throw boom,
                (c, _) => { c.Record("undo-conditional"); return Task.CompletedTask; })
            .AddStep("c",
                (c, _) => { c.Record("do-c"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-c"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        // The throwing predicate is treated exactly like the step's forward action throwing: the run
        // fails on that step, forward progress stops (c never runs), and the prior step rolls back.
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("conditional", result.FailedStep);
        Assert.Same(boom, result.Failure);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(1, result.StepsCompensated);
        Assert.Equal(0, result.StepsSkipped);
        Assert.Equal(["do-a", "undo-a"], ledger.Snapshot());

        // The observer sees the step fail, not skip, and the prior step compensate.
        Assert.Contains(new Note("failed", "conditional", 2), observer.Notes);
        Assert.Contains(new Note("compensated", "a", 1), observer.Notes);
        Assert.DoesNotContain(observer.Notes, n => n.Kind == "skipped");
    }

    // ---- Findings 2 + 3: in-group compensation uses the saga's per-step routine ----

    [Fact]
    public async Task A_group_member_compensation_is_retried_via_the_member_policy_when_a_later_stage_fails()
    {
        var ledger = new Ledger();
        var observer = new OrdinalObserver();
        var boom = new InvalidOperationException("later-boom");

        // m0's compensation fails on its first attempt and succeeds on the second; the per-member
        // compensation retry must be honoured, proving the group is unwound through the saga's own
        // per-step compensation routine, not an ad-hoc inline undo that ignores retry.
        var m0Attempts = 0;
        var recordedWaits = new List<TimeSpan>();
        var retry = new RetryPolicy(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(10));

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => { c.Record("do-m0"); return Task.CompletedTask; },
                    (c, _) =>
                    {
                        if (Interlocked.Increment(ref m0Attempts) == 1)
                        {
                            throw new InvalidOperationException("transient-undo");
                        }

                        c.Record("undo-m0");
                        return Task.CompletedTask;
                    },
                    compensationRetry: retry)
                .AddStep("m1",
                    (c, _) => { c.Record("do-m1"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m1"); return Task.CompletedTask; }))
            .AddStep("after", (_, _) => throw boom)
            .Build((wait, _) => { recordedWaits.Add(wait); return Task.CompletedTask; });

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("after", result.FailedStep);

        // m0's compensation was retried: two attempts, the second clean. The backoff wait was requested
        // through the injected delayer, so the per-member policy genuinely drove the retry.
        Assert.Equal(2, m0Attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(10)], recordedWaits);

        // Both members compensated cleanly in the end (reverse declaration order: m1 then m0), and the
        // result counts each member individually since they ran through the per-step routine.
        var events = ledger.Snapshot();
        Assert.True(events.IndexOf("undo-m1") < events.IndexOf("undo-m0"));
        Assert.Equal(2, result.StepsCompensated);
        Assert.Empty(result.CompensationFailures);

        // The observer received a per-member compensation notification for each member, carrying the
        // group slot's forward ordinal (the group was the first and only stage, ordinal 1).
        Assert.Contains(new Note("compensated", "fanout/m0", 1), observer.Notes);
        Assert.Contains(new Note("compensated", "fanout/m1", 1), observer.Notes);
    }

    [Fact]
    public async Task A_group_member_compensation_failure_is_recorded_in_the_parent_result_and_surfaced_to_the_observer()
    {
        var ledger = new Ledger();
        var observer = new OrdinalObserver();
        var boom = new InvalidOperationException("later-boom");
        var undoBoom = new InvalidOperationException("undo-m1-boom");

        // m1's compensation always throws (after exhausting its single attempt). The failure must land in
        // the parent's CompensationFailures and be surfaced to the observer, exactly like a sequential
        // compensation failure, rather than being swallowed by an inline group undo.
        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => { c.Record("do-m0"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m0"); return Task.CompletedTask; })
                .AddStep("m1",
                    (c, _) => { c.Record("do-m1"); return Task.CompletedTask; },
                    (_, _) => throw undoBoom))
            .AddStep("after", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("after", result.FailedStep);

        // m1's compensation failure is in the parent result, named with its group-qualified name.
        var failure = Assert.Single(result.CompensationFailures);
        Assert.Equal("fanout/m1", failure.StepName);
        Assert.Same(undoBoom, failure.Exception);

        // m0 still compensated cleanly despite m1's failure (best-effort continues across members).
        Assert.Contains("undo-m0", ledger.Snapshot());
        Assert.Equal(1, result.StepsCompensated);
        Assert.False(result.RolledBackCleanly);

        // The observer was told about the compensation failure with the group slot's ordinal.
        Assert.Contains(new Note("compensationFailed", "fanout/m1", 1), observer.Notes);
    }

    [Fact]
    public async Task An_in_group_member_failure_surfaces_completed_member_compensation_failures_to_the_parent()
    {
        var ledger = new Ledger();
        var observer = new OrdinalObserver();
        var memberBoom = new InvalidOperationException("m2-boom");
        var undoBoom = new InvalidOperationException("undo-m0-boom");

        // m0 and m1 complete (gating makes the completed set deterministic), then m2 fails. m0's
        // compensation throws. That in-group compensation failure must still reach the parent result,
        // proving the in-group rollback path also flows through the saga's per-step routine.
        var bothCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;

        Task Succeed(string name, Ledger c)
        {
            c.Record($"do-{name}");
            if (Interlocked.Increment(ref completedCount) == 2)
            {
                bothCompleted.SetResult();
            }

            return Task.CompletedTask;
        }

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => Succeed("m0", c),
                    (_, _) => throw undoBoom)
                .AddStep("m1",
                    (c, _) => Succeed("m1", c),
                    (c, _) => { c.Record("undo-m1"); return Task.CompletedTask; })
                .AddStep("m2",
                    async (c, ct) =>
                    {
                        await bothCompleted.Task.WaitAsync(GateTimeout, ct);
                        c.Record("do-m2");
                        throw memberBoom;
                    }))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("fanout", result.FailedStep);

        // m1 compensated cleanly; m0's compensation failure is surfaced to the parent result, not lost.
        Assert.Contains("undo-m1", ledger.Snapshot());
        var failure = Assert.Single(result.CompensationFailures);
        Assert.Equal("fanout/m0", failure.StepName);
        Assert.Same(undoBoom, failure.Exception);
        Assert.Equal(1, result.StepsCompensated);
        Assert.Contains(new Note("compensationFailed", "fanout/m0", 1), observer.Notes);
    }

    // ---- A parallel-group member that overruns its per-step timeout reports TimedOut ----

    [Fact]
    public async Task A_group_member_that_overruns_its_per_step_timeout_reports_the_TimedOut_outcome()
    {
        var ledger = new Ledger();

        // A single-member group whose member hangs until its own per-step deadline fires. The deadline is
        // the only thing that ends the run, so the member overrun must surface as a timeout, not a generic
        // failure. Deterministic: the member blocks on an infinite delay that the per-step timeout cancels.
        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep(
                    "slow",
                    async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
                    timeout: TimeSpan.FromMilliseconds(20)))
            .Build();

        var result = await saga.RunAsync(ledger);

        // The member deadline overrun is preserved through the group to the parent result: the run is a
        // cancellation tagged TimedOut, reported against the group slot, not a generic Failed.
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Equal("fanout", result.FailedStep);
        Assert.True(result.RolledBackCleanly);
    }

    [Fact]
    public async Task A_member_timeout_is_reported_as_TimedOut_even_when_a_sibling_fails_generically_first()
    {
        var ledger = new Ledger();

        // Two members settle differently: m-fail throws a generic fault immediately, m-slow overruns its
        // own per-step deadline. The group waits for both (Task.WhenAll), then surfaces a failure. The
        // member deadline overrun must take priority so the run reports TimedOut, not the sibling's generic
        // fault -- proving a timeout is never masked by another member's failure or by declaration order.
        // m-fail is declared first, so under the unfixed first-faulted-in-order behaviour its generic fault
        // would surface and the run would be a plain Failed: this test is RED against that code.
        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep(
                    "m-fail",
                    (_, _) => throw new InvalidOperationException("generic-boom"))
                .AddStep(
                    "m-slow",
                    async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
                    timeout: TimeSpan.FromMilliseconds(20)))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Equal("fanout", result.FailedStep);
    }

    // ---- Finding 4: rollback ordinals stay correct after a skipped step ----

    [Fact]
    public async Task Compensation_ordinals_preserve_the_original_forward_position_after_a_skip()
    {
        var ledger = new Ledger();
        var observer = new OrdinalObserver();
        var boom = new InvalidOperationException("boom");

        // Step 2 is skipped. Step 3 ("c") still occupies forward ordinal 3; when it rolls back, the
        // observer must see ordinal 3 for its compensation, not 2. Without carrying the original ordinal,
        // a skip would shift the later step's reported rollback position down by one.
        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddStep("a",
                (c, _) => { c.Record("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-a"); return Task.CompletedTask; })
            .AddConditionalStep("skipped",
                (c, _) => { c.Record("do-skipped"); return Task.CompletedTask; },
                condition: _ => false,
                (c, _) => { c.Record("undo-skipped"); return Task.CompletedTask; })
            .AddStep("c",
                (c, _) => { c.Record("do-c"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-c"); return Task.CompletedTask; })
            .AddStep("doomed", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("doomed", result.FailedStep);
        Assert.Equal(1, result.StepsSkipped);

        // "a" ran at ordinal 1, "c" at ordinal 3 (the skip kept slot 2). Their compensations report
        // those original positions, and the failing step reports ordinal 4.
        Assert.Contains(new Note("completed", "a", 1), observer.Notes);
        Assert.Contains(new Note("skipped", "skipped", 2), observer.Notes);
        Assert.Contains(new Note("completed", "c", 3), observer.Notes);
        Assert.Contains(new Note("failed", "doomed", 4), observer.Notes);
        Assert.Contains(new Note("compensated", "c", 3), observer.Notes);
        Assert.Contains(new Note("compensated", "a", 1), observer.Notes);
    }

    // ---- Finding 5: existing AddStep call sites still compile unchanged ----

    [Fact]
    public async Task The_original_AddStep_overload_still_binds_with_all_positional_arguments()
    {
        // The pre-composition call shape: name, execute, compensate, timeout, forwardRetry,
        // compensationRetry, with no condition argument. This must keep compiling and resolving to the
        // original AddStep overload now that the conditional capability lives on a distinct method. If
        // composition had instead removed this overload (or shadowed it), this six-argument call would no
        // longer compile, which is exactly the source-compat regression the review flagged.
        var ledger = new Ledger();
        var policy = new RetryPolicy(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1));

        var saga = new SagaBuilder<Ledger>()
            .AddStep(
                "reserve",
                (c, _) => { c.Record("do"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo"); return Task.CompletedTask; },
                TimeSpan.FromSeconds(1),
                policy,
                policy)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(["do"], ledger.Snapshot());
    }

    [Fact]
    public async Task A_returning_forward_action_with_a_compensation_still_binds_to_AddStep_not_a_typed_overload()
    {
        // A forward action that happens to return Task<T> alongside a compensation lambda. This is the
        // call the AddResultStep remarks warn about: if the typed surface were an AddStep overload, the
        // compensation lambda could rebind to the typed 'apply' parameter, silently dropping the
        // compensation. Because the typed surface is the distinctly named AddResultStep, this binds to the
        // untyped AddStep and the compensation is preserved. The step is forced to fail so the
        // compensation must run; if it had been dropped, "undo" would never appear.
        var ledger = new Ledger();
        var boom = new InvalidOperationException("boom");

        var saga = new SagaBuilder<Ledger>()
            .AddStep(
                "reserve",
                (c, _) => { c.Record("do"); return Task.FromResult(42); },
                (c, _) => { c.Record("undo"); return Task.CompletedTask; })
            .AddStep("doomed", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal(["do", "undo"], ledger.Snapshot());
    }

    // ---- Finding 6: a per-member condition inside a group is honoured, not dropped ----

    [Fact]
    public async Task A_false_member_condition_skips_only_that_member_and_leaves_it_uncompensated()
    {
        var ledger = new Ledger();
        var boom = new InvalidOperationException("later-boom");

        // m1 carries a false condition: it must be skipped (its forward action never runs), while m0 and
        // m2 run. When a later stage fails and the group rolls back, the skipped m1 is never compensated.
        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => { c.Record("do-m0"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m0"); return Task.CompletedTask; })
                .AddConditionalStep("m1",
                    (c, _) => { c.Record("do-m1"); return Task.CompletedTask; },
                    condition: _ => false,
                    (c, _) => { c.Record("undo-m1"); return Task.CompletedTask; })
                .AddStep("m2",
                    (c, _) => { c.Record("do-m2"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m2"); return Task.CompletedTask; }))
            .AddStep("after", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);

        var events = ledger.Snapshot();
        // m1's forward action never ran and it was never compensated.
        Assert.DoesNotContain("do-m1", events);
        Assert.DoesNotContain("undo-m1", events);
        // The other members ran and compensated.
        Assert.Contains("do-m0", events);
        Assert.Contains("do-m2", events);
        Assert.Contains("undo-m0", events);
        Assert.Contains("undo-m2", events);
        // Two members compensated, none failed.
        Assert.Equal(2, result.StepsCompensated);
        Assert.Empty(result.CompensationFailures);
    }

    [Fact]
    public async Task A_true_member_condition_runs_that_member_normally()
    {
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep("m0", (c, _) => { c.Record("do-m0"); return Task.CompletedTask; })
                .AddConditionalStep("m1",
                    (c, _) => { c.Record("do-m1"); return Task.CompletedTask; },
                    condition: _ => true))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        var events = ledger.Snapshot();
        Assert.Contains("do-m0", events);
        Assert.Contains("do-m1", events);
    }
}
