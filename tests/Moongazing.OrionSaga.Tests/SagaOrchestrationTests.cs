namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaOrchestrationTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
    }

    [Fact]
    public async Task A_first_step_failure_leaves_nothing_to_compensate()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => throw new InvalidOperationException("boom"),
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-b"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal("a", result.FailedStep);
        Assert.True(result.RolledBackCleanly);
        // a never completed forward, so nothing is compensated.
        Assert.Empty(ledger.Events);
    }

    [Fact]
    public async Task A_mid_step_failure_unwinds_only_the_steps_that_completed()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (c, _) => { c.Events.Add("do-b"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-b"); return Task.CompletedTask; })
            .AddStep("c",
                (_, _) => throw new InvalidOperationException("boom"),
                (c, _) => { c.Events.Add("undo-c"); return Task.CompletedTask; })
            .AddStep("d",
                (c, _) => { c.Events.Add("do-d"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-d"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal("c", result.FailedStep);
        // c failed forward (no undo-c), d never ran (no do-d/undo-d); a and b unwind in reverse.
        Assert.Equal(["do-a", "do-b", "undo-b", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task The_failing_step_exception_is_surfaced_unchanged()
    {
        var failure = new TimeoutException("downstream timed out");
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.FromException(failure))
            .Build();

        var result = await saga.RunAsync(new Ledger());

        Assert.Same(failure, result.Failure);
        Assert.Equal("b", result.FailedStep);
    }

    [Fact]
    public async Task The_same_context_instance_is_threaded_through_every_step_and_compensation()
    {
        var context = new Ledger();
        var seen = new List<Ledger>();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { seen.Add(c); return Task.CompletedTask; },
                (c, _) => { seen.Add(c); return Task.CompletedTask; })
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(context);

        Assert.All(seen, c => Assert.Same(context, c));
    }

    [Fact]
    public async Task Compensations_run_in_strict_reverse_order_for_many_steps()
    {
        var ledger = new Ledger();
        var builder = new SagaBuilder<Ledger>();
        for (var i = 0; i < 5; i++)
        {
            var name = $"s{i}";
            builder.AddStep(name,
                (c, _) => { c.Events.Add($"do-{name}"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add($"undo-{name}"); return Task.CompletedTask; });
        }

        builder.AddStep("boom", (_, _) => throw new InvalidOperationException("boom"));
        var saga = builder.Build();

        await saga.RunAsync(ledger);

        Assert.Equal(
            ["do-s0", "do-s1", "do-s2", "do-s3", "do-s4", "undo-s4", "undo-s3", "undo-s2", "undo-s1", "undo-s0"],
            ledger.Events);
    }

    [Fact]
    public async Task A_step_that_observes_cancellation_is_treated_as_a_failure_and_rolls_back()
    {
        var ledger = new Ledger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b", (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            })
            .Build();

        var result = await saga.RunAsync(ledger, cts.Token);

        // The executor does not pre-check the token; a cancelling step surfaces as a normal failure.
        Assert.False(result.Succeeded);
        Assert.Equal("b", result.FailedStep);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
        Assert.Equal(["undo-a"], ledger.Events);
    }

    [Fact]
    public async Task Compensation_ignores_a_cancelled_token_so_rollback_still_completes()
    {
        var ledger = new Ledger();
        using var cts = new CancellationTokenSource();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, ct) =>
                {
                    // Compensation receives CancellationToken.None even though the saga token is cancelled.
                    c.Events.Add(ct.IsCancellationRequested ? "undo-a-cancelled" : "undo-a");
                    return Task.CompletedTask;
                })
            .AddStep("b", (_, _) =>
            {
                cts.Cancel();
                throw new InvalidOperationException("boom");
            })
            .Build();

        var result = await saga.RunAsync(ledger, cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_non_cancelled_token_is_passed_to_forward_steps()
    {
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a", (_, ct) => { observedToken = ct; return Task.CompletedTask; })
            .Build();

        await saga.RunAsync(new Ledger(), cts.Token);

        Assert.Equal(cts.Token, observedToken);
    }
}
