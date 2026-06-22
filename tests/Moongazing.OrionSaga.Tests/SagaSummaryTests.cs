namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaSummaryTests
{
    [Fact]
    public async Task On_success_every_step_is_counted_completed_and_none_compensated()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.StepsCompleted);
        Assert.Equal(0, result.StepsCompensated);
    }

    [Fact]
    public async Task An_empty_saga_reports_zero_completed_and_zero_compensated()
    {
        var saga = new SagaBuilder<object>().Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(0, result.StepsCompensated);
    }

    [Fact]
    public async Task On_failure_completed_steps_are_counted_and_all_compensate()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.False(result.Succeeded);
        // a and b completed; c failed forward and is not counted.
        Assert.Equal(2, result.StepsCompleted);
        // Both completed steps compensated cleanly.
        Assert.Equal(2, result.StepsCompensated);
    }

    [Fact]
    public async Task A_first_step_failure_counts_no_completed_and_no_compensated()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => throw new InvalidOperationException("boom"))
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(0, result.StepsCompensated);
    }

    [Fact]
    public async Task A_dirty_rollback_counts_only_the_compensations_that_succeeded()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-b boom"))
            .AddStep("c", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("d", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.False(result.Succeeded);
        // a, b, c completed; d failed forward.
        Assert.Equal(3, result.StepsCompleted);
        // c and a compensated cleanly; b's compensation threw, so it is in CompensationFailures, not counted.
        Assert.Equal(2, result.StepsCompensated);
        Assert.Single(result.CompensationFailures);
        // The completed steps either compensated cleanly or are recorded as a failure: the two add up.
        Assert.Equal(
            result.StepsCompleted,
            result.StepsCompensated + result.CompensationFailures.Count);
    }

    [Fact]
    public async Task On_a_timeout_completed_steps_are_counted_and_compensated()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep(
                "slow",
                async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
                (_, _) => Task.CompletedTask,
                timeout: TimeSpan.FromMilliseconds(50))
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.TimedOut);
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        // a completed; slow timed out before completing and is not counted.
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(1, result.StepsCompensated);
    }

    [Fact]
    public async Task On_caller_cancellation_completed_steps_are_counted_and_compensated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; })
            .Build();

        var result = await saga.RunAsync(new object(), cts.Token);

        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.False(result.TimedOut);
        // a completed before the cancelled token stopped b.
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(1, result.StepsCompensated);
    }
}
