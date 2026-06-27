namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

/// <summary>
/// Covers the v0.5.0 composition and control-flow surface: conditional steps, sub-saga composition, and
/// parallel step groups. Concurrency is proven with a gate, not with timing: a parallel member only
/// proceeds once every member has arrived, so the assertions hold deterministically and would deadlock
/// (and time out) rather than pass if the members ran sequentially.
/// </summary>
public sealed class SagaCompositionTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
        public bool Flag { get; set; }

        // A thread-safe append used by concurrently running parallel-group members.
        public void Record(string entry)
        {
            lock (Events)
            {
                Events.Add(entry);
            }
        }
    }

    // A safety net so a broken concurrency implementation fails the test deterministically instead of
    // hanging the run forever. The happy path never waits this long; it is only a deadlock guard.
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    // ---- Conditional steps ----

    [Fact]
    public async Task A_step_whose_condition_is_false_is_skipped_not_executed_and_not_compensated()
    {
        var ledger = new Ledger();
        var observer = new CountingObserver();

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddStep("a",
                (c, _) => { c.Record("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-a"); return Task.CompletedTask; })
            .AddConditionalStep("conditional",
                (c, _) => { c.Record("do-conditional"); return Task.CompletedTask; },
                condition: _ => false,
                (c, _) => { c.Record("undo-conditional"); return Task.CompletedTask; })
            .AddStep("c",
                (c, _) => { c.Record("do-c"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-c"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        // The skipped step's forward action never ran.
        Assert.Equal(["do-a", "do-c"], ledger.Events);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Equal(1, result.StepsSkipped);
        // The skip is reported, and the skipped step is never compensated (the run succeeded anyway).
        Assert.Equal(["completed:a", "skipped:conditional", "completed:c"], observer.Calls);
    }

    [Fact]
    public async Task A_step_whose_condition_is_true_runs_normally()
    {
        var ledger = new Ledger { Flag = true };

        var saga = new SagaBuilder<Ledger>()
            .AddConditionalStep("conditional",
                (c, _) => { c.Record("do-conditional"); return Task.CompletedTask; },
                condition: c => c.Flag)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(["do-conditional"], ledger.Events);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(0, result.StepsSkipped);
    }

    [Fact]
    public async Task A_skipped_step_is_not_compensated_when_a_later_step_fails()
    {
        var ledger = new Ledger();
        var boom = new InvalidOperationException("boom");

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Record("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-a"); return Task.CompletedTask; })
            .AddConditionalStep("skipped",
                (c, _) => { c.Record("do-skipped"); return Task.CompletedTask; },
                condition: _ => false,
                (c, _) => { c.Record("undo-skipped"); return Task.CompletedTask; })
            .AddStep("doomed", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("doomed", result.FailedStep);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(1, result.StepsCompensated);
        Assert.Equal(1, result.StepsSkipped);
        // Only "a" was compensated; the skipped step was never run and so never undone.
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
    }

    // ---- Sub-sagas ----

    [Fact]
    public async Task A_sub_saga_composes_its_steps_into_the_parent_order()
    {
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("first", (c, _) => { c.Record("first"); return Task.CompletedTask; })
            .AddSubSaga("payment", sub => sub
                .AddStep("authorize", (c, _) => { c.Record("authorize"); return Task.CompletedTask; })
                .AddStep("capture", (c, _) => { c.Record("capture"); return Task.CompletedTask; }))
            .AddStep("last", (c, _) => { c.Record("last"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.StepsCompleted);
        Assert.Equal(["first", "authorize", "capture", "last"], ledger.Events);
    }

    [Fact]
    public async Task A_failure_after_a_sub_saga_unwinds_its_completed_steps_in_reverse_across_the_whole()
    {
        var ledger = new Ledger();
        var boom = new InvalidOperationException("boom");

        var saga = new SagaBuilder<Ledger>()
            .AddStep("first",
                (c, _) => { c.Record("do-first"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-first"); return Task.CompletedTask; })
            .AddSubSaga("payment", sub => sub
                .AddStep("authorize",
                    (c, _) => { c.Record("do-authorize"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-authorize"); return Task.CompletedTask; })
                .AddStep("capture",
                    (c, _) => { c.Record("do-capture"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-capture"); return Task.CompletedTask; }))
            .AddStep("doomed", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("doomed", result.FailedStep);
        Assert.Equal(3, result.StepsCompleted);
        Assert.Equal(3, result.StepsCompensated);
        // Forward across the whole composed saga, then reverse compensation newest-first across stages,
        // including the sub-saga's steps in reverse: capture, authorize, first.
        Assert.Equal(
            ["do-first", "do-authorize", "do-capture", "undo-capture", "undo-authorize", "undo-first"],
            ledger.Events);
    }

    [Fact]
    public async Task A_sub_saga_step_is_named_with_its_stage_prefix()
    {
        var observer = new CountingObserver();
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddSubSaga("payment", sub => sub
                .AddStep("authorize", (_, _) => Task.CompletedTask))
            .Build();

        await saga.RunAsync(ledger);

        Assert.Equal(["completed:payment/authorize"], observer.Calls);
    }

    // ---- Parallel step groups ----

    [Fact]
    public async Task A_parallel_group_runs_its_members_concurrently()
    {
        var ledger = new Ledger();

        // Each member signals arrival and then waits until every member has arrived. The run can only
        // complete if all three forward actions are in flight at once; a sequential executor would block
        // on the first member's wait forever and trip the deadlock guard below.
        const int memberCount = 3;
        var arrived = new TaskCompletionSource[memberCount];
        for (var i = 0; i < memberCount; i++)
        {
            arrived[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var allArrived = Task.WhenAll(arrived.Select(t => t.Task));

        Task Member(int index, Ledger c)
        {
            c.Record($"arrive-{index}");
            arrived[index].SetResult();
            return allArrived.WaitAsync(GateTimeout);
        }

        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep("m0", (c, _) => Member(0, c))
                .AddStep("m1", (c, _) => Member(1, c))
                .AddStep("m2", (c, _) => Member(2, c)))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        // The group is a single stage: one completed step from the parent's perspective.
        Assert.Equal(1, result.StepsCompleted);
        // All three arrived (order is nondeterministic, so assert the set, not the sequence).
        Assert.Equal(3, ledger.Events.Count);
        Assert.Contains("arrive-0", ledger.Events);
        Assert.Contains("arrive-1", ledger.Events);
        Assert.Contains("arrive-2", ledger.Events);
    }

    [Fact]
    public async Task A_parallel_group_member_failure_compensates_every_completed_member_and_unwinds_the_rest()
    {
        var ledger = new Ledger();
        var boom = new InvalidOperationException("member-boom");

        // Two members complete and gate open; once both have completed, the third member is released to
        // fail. This makes the completed set deterministic (m0 and m1) without timing: the failing member
        // waits on a gate that the two successful members open.
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
            .AddStep("before",
                (c, _) => { c.Record("do-before"); return Task.CompletedTask; },
                (c, _) => { c.Record("undo-before"); return Task.CompletedTask; })
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => Succeed("m0", c),
                    (c, _) => { c.Record("undo-m0"); return Task.CompletedTask; })
                .AddStep("m1",
                    (c, _) => Succeed("m1", c),
                    (c, _) => { c.Record("undo-m1"); return Task.CompletedTask; })
                .AddStep("m2",
                    async (c, ct) =>
                    {
                        await bothCompleted.Task.WaitAsync(GateTimeout, ct);
                        c.Record("do-m2");
                        throw boom;
                    },
                    (c, _) => { c.Record("undo-m2"); return Task.CompletedTask; }))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("fanout", result.FailedStep);

        // The completed members (m0, m1) compensate in reverse declaration order within the group, and
        // the failed member (m2) is not compensated. Then the saga unwinds the stage before the group.
        Assert.Contains("undo-m1", ledger.Events);
        Assert.Contains("undo-m0", ledger.Events);
        Assert.DoesNotContain("undo-m2", ledger.Events);
        Assert.Contains("undo-before", ledger.Events);

        // Within the group, m1 compensates before m0 (reverse declaration order).
        Assert.True(
            ledger.Events.IndexOf("undo-m1") < ledger.Events.IndexOf("undo-m0"),
            "completed members must compensate in reverse declaration order");

        // The group as a whole unwinds before the earlier stage (overall reverse order across stages).
        Assert.True(
            ledger.Events.IndexOf("undo-m0") < ledger.Events.IndexOf("undo-before"),
            "the parallel group must unwind before the stage that ran before it");
    }

    [Fact]
    public async Task A_completed_parallel_group_is_rolled_back_when_a_later_stage_fails()
    {
        var ledger = new Ledger();
        var boom = new InvalidOperationException("later-boom");

        var saga = new SagaBuilder<Ledger>()
            .AddParallelGroup("fanout", group => group
                .AddStep("m0",
                    (c, _) => { c.Record("do-m0"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m0"); return Task.CompletedTask; })
                .AddStep("m1",
                    (c, _) => { c.Record("do-m1"); return Task.CompletedTask; },
                    (c, _) => { c.Record("undo-m1"); return Task.CompletedTask; }))
            .AddStep("after", (_, _) => throw boom)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("after", result.FailedStep);
        // The whole group rolled back: both members compensated, in reverse declaration order.
        Assert.Contains("undo-m1", ledger.Events);
        Assert.Contains("undo-m0", ledger.Events);
        Assert.True(
            ledger.Events.IndexOf("undo-m1") < ledger.Events.IndexOf("undo-m0"),
            "completed members must compensate in reverse declaration order");
    }

    [Fact]
    public async Task A_parallel_group_whose_condition_is_false_is_skipped_whole()
    {
        var ledger = new Ledger();
        var observer = new CountingObserver();

        var saga = new SagaBuilder<Ledger>()
            .WithObserver(observer)
            .AddParallelGroup("fanout",
                group => group
                    .AddStep("m0", (c, _) => { c.Record("do-m0"); return Task.CompletedTask; })
                    .AddStep("m1", (c, _) => { c.Record("do-m1"); return Task.CompletedTask; }),
                condition: _ => false)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(1, result.StepsSkipped);
        Assert.Empty(ledger.Events);
        Assert.Equal(["skipped:fanout"], observer.Calls);
    }

    [Fact]
    public void A_parallel_group_with_no_members_is_rejected()
    {
        var builder = new SagaBuilder<Ledger>();

        Assert.Throws<InvalidOperationException>(
            () => builder.AddParallelGroup("empty", _ => { }));
    }
}
