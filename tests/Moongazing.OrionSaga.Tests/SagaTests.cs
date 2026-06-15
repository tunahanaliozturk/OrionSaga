namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
    }

    [Fact]
    public async Task All_steps_run_in_order_on_success()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a", (c, _) => { c.Events.Add("a"); return Task.CompletedTask; })
            .AddStep("b", (c, _) => { c.Events.Add("b"); return Task.CompletedTask; })
            .AddStep("c", (c, _) => { c.Events.Add("c"); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(["a", "b", "c"], ledger.Events);
    }

    [Fact]
    public async Task A_failure_compensates_completed_steps_in_reverse()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (c, _) => { c.Events.Add("do-b"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-b"); return Task.CompletedTask; })
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal("c", result.FailedStep);
        Assert.IsType<InvalidOperationException>(result.Failure);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-a", "do-b", "undo-b", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task The_failing_step_is_not_compensated()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (_, _) => throw new InvalidOperationException("boom"),
                (c, _) => { c.Events.Add("undo-b"); return Task.CompletedTask; })
            .Build();

        await saga.RunAsync(ledger);

        // Only step a completed, so only a is compensated; b never finished forward.
        Assert.Equal(["undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_compensation_failure_is_recorded_but_rollback_continues()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (_, _) => Task.CompletedTask,
                (_, _) => throw new InvalidOperationException("compensation boom"))
            .AddStep("c", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBackCleanly);
        Assert.Single(result.CompensationFailures);
        Assert.Equal("b", result.CompensationFailures[0].StepName);
        // Despite b's compensation throwing, a was still compensated.
        Assert.Equal(["undo-a"], ledger.Events);
    }

    [Fact]
    public async Task An_empty_saga_succeeds()
    {
        var result = await new SagaBuilder<Ledger>().Build().RunAsync(new Ledger());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_cancelled_saga_still_rolls_back()
    {
        var ledger = new Ledger();
        using var cts = new CancellationTokenSource();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b", (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            })
            .Build();

        var result = await saga.RunAsync(ledger, cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("b", result.FailedStep);
        Assert.Equal(["undo-a"], ledger.Events);
    }
}
