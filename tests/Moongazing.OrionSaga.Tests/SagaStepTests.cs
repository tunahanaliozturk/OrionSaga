namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaStepTests
{
    [Fact]
    public void A_step_exposes_its_name_and_forward_action()
    {
        Func<object, CancellationToken, Task> execute = (_, _) => Task.CompletedTask;

        var step = new SagaStep<object>("ship", execute);

        Assert.Equal("ship", step.Name);
        Assert.Same(execute, step.Execute);
    }

    [Fact]
    public void A_step_keeps_the_compensation_it_was_given()
    {
        Func<object, CancellationToken, Task> compensate = (_, _) => Task.CompletedTask;

        var step = new SagaStep<object>("ship", (_, _) => Task.CompletedTask, compensate);

        Assert.Same(compensate, step.Compensate);
    }

    [Fact]
    public async Task A_step_without_a_compensation_gets_a_noop_that_completes()
    {
        var step = new SagaStep<object>("ship", (_, _) => Task.CompletedTask);

        Assert.NotNull(step.Compensate);
        // The default compensation is a no-op that simply completes.
        await step.Compensate(new object(), CancellationToken.None);
    }

    [Fact]
    public void A_null_name_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SagaStep<object>(null!, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void An_empty_name_throws()
    {
        Assert.Throws<ArgumentException>(
            () => new SagaStep<object>(string.Empty, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void A_null_forward_action_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SagaStep<object>("ship", null!));
    }
}
