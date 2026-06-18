namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaResultTests
{
    [Fact]
    public async Task A_successful_result_has_no_failure_details_and_is_not_rolled_back()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
        Assert.Null(result.FailedStep);
        Assert.Null(result.Failure);
        Assert.Empty(result.CompensationFailures);
        // RolledBackCleanly is false on success: there was nothing to roll back.
        Assert.False(result.RolledBackCleanly);
    }

    [Fact]
    public async Task A_clean_rollback_carries_the_failure_and_no_compensation_failures()
    {
        var failure = new InvalidOperationException("boom");
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => throw failure)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.False(result.Succeeded);
        Assert.Equal("b", result.FailedStep);
        Assert.Same(failure, result.Failure);
        Assert.Empty(result.CompensationFailures);
        Assert.True(result.RolledBackCleanly);
    }

    [Fact]
    public async Task A_dirty_rollback_is_not_clean_and_lists_each_compensation_failure()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-a boom"))
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-b boom"))
            .AddStep("c", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBackCleanly);
        Assert.Equal(2, result.CompensationFailures.Count);
        // Compensation runs in reverse, so b's failure is recorded before a's.
        Assert.Equal("b", result.CompensationFailures[0].StepName);
        Assert.Equal("a", result.CompensationFailures[1].StepName);
        Assert.IsType<InvalidOperationException>(result.CompensationFailures[0].Exception);
    }

    [Fact]
    public void CompensationFailure_carries_the_step_name_and_exception()
    {
        var exception = new InvalidOperationException("boom");

        var failure = new CompensationFailure("ship", exception);

        Assert.Equal("ship", failure.StepName);
        Assert.Same(exception, failure.Exception);
    }

    [Fact]
    public void CompensationFailure_has_value_equality()
    {
        var exception = new InvalidOperationException("boom");

        var a = new CompensationFailure("ship", exception);
        var b = new CompensationFailure("ship", exception);

        Assert.Equal(a, b);
    }
}
