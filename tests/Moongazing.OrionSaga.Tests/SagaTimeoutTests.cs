namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaTimeoutTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
    }

    [Fact]
    public async Task A_step_that_overruns_its_timeout_is_cancelled_and_prior_steps_compensate()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("slow",
                async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
                (c, _) => { c.Events.Add("undo-slow"); return Task.CompletedTask; },
                timeout: TimeSpan.FromMilliseconds(50))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.Cancelled);
        Assert.True(result.TimedOut);
        Assert.Equal("slow", result.FailedStep);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
        Assert.True(result.RolledBackCleanly);
        // slow never finished forward (no undo-slow); only the completed step a rolls back.
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_step_that_finishes_within_its_timeout_is_unaffected()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("a"); return Task.CompletedTask; },
                timeout: TimeSpan.FromSeconds(30))
            .AddStep("b",
                (c, _) => { c.Events.Add("b"); return Task.CompletedTask; },
                timeout: TimeSpan.FromSeconds(30))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(SagaOutcome.Succeeded, result.Outcome);
        Assert.False(result.TimedOut);
        Assert.Equal(["a", "b"], ledger.Events);
    }

    [Fact]
    public async Task External_cancellation_still_works_alongside_a_per_step_timeout()
    {
        var ledger = new Ledger();
        using var cts = new CancellationTokenSource();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                async (_, ct) =>
                {
                    // Cancel via the caller's token, not the (generous) per-step timeout.
                    cts.Cancel();
                    await Task.Delay(Timeout.Infinite, ct);
                },
                timeout: TimeSpan.FromSeconds(30))
            .Build();

        var result = await saga.RunAsync(ledger, cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.Cancelled);
        // The caller's token fired, so this is an external cancellation, not a timeout.
        Assert.False(result.TimedOut);
        Assert.Equal("b", result.FailedStep);
        Assert.Equal(["undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_step_token_is_linked_so_the_caller_token_still_reaches_a_timed_step()
    {
        using var cts = new CancellationTokenSource();
        var sawCallerCancellation = false;

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                async (_, ct) =>
                {
                    cts.Cancel();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        sawCallerCancellation = true;
                        throw;
                    }
                },
                timeout: TimeSpan.FromSeconds(30))
            .Build();

        var result = await saga.RunAsync(new Ledger(), cts.Token);

        Assert.True(sawCallerCancellation);
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.Cancelled);
        // The caller's token must be what cancelled the step. Asserting NOT TimedOut proves the linked
        // token honoured the caller's token: if token-linking regressed, the only way this timed step
        // could cancel would be its own per-step deadline, which would set TimedOut and fail here
        // instead of passing on an unrelated cancellation path (a false positive).
        Assert.False(result.TimedOut);
        Assert.Equal("a", result.FailedStep);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
        Assert.IsNotType<SagaStepTimeoutException>(result.Failure);
    }

    [Fact]
    public void A_non_positive_timeout_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SagaStep<object>("a", (_, _) => Task.CompletedTask, timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SagaStep<object>("a", (_, _) => Task.CompletedTask, timeout: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_step_remembers_the_timeout_it_was_given()
    {
        var step = new SagaStep<object>(
            "a", (_, _) => Task.CompletedTask, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), step.Timeout);
    }

    [Fact]
    public void A_step_without_a_timeout_has_a_null_budget()
    {
        var step = new SagaStep<object>("a", (_, _) => Task.CompletedTask);

        Assert.Null(step.Timeout);
    }
}
