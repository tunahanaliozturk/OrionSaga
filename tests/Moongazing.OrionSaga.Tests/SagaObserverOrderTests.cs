namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaObserverOrderTests
{
    [Fact]
    public async Task On_success_the_observer_sees_only_completions_in_order()
    {
        var observer = new CountingObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(["completed:a", "completed:b"], observer.Calls);
    }

    [Fact]
    public async Task On_failure_completions_precede_the_failure_then_compensations_in_reverse()
    {
        var observer = new CountingObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(
            ["completed:a", "completed:b", "failed:c", "compensated:b", "compensated:a"],
            observer.Calls);
    }

    [Fact]
    public async Task A_compensation_failure_is_observed_in_place_of_a_compensation()
    {
        var observer = new CountingObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-b boom"))
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(
            ["completed:a", "completed:b", "failed:c", "compensationFailed:b", "compensated:a"],
            observer.Calls);
    }

    [Fact]
    public async Task The_default_observer_is_a_noop_and_does_not_fail_the_saga()
    {
        // No WithObserver call: the saga uses NullSagaObserver internally.
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NullSagaObserver_exposes_a_shared_instance_whose_callbacks_are_inert()
    {
        var observer = NullSagaObserver.Instance;

        Assert.Same(NullSagaObserver.Instance, observer);
        // Every callback is a no-op and must not throw.
        observer.OnStepCompleted("a");
        observer.OnStepFailed("a", new InvalidOperationException());
        observer.OnCompensated("a");
        observer.OnCompensationFailed("a", new InvalidOperationException());
    }

    [Fact]
    public async Task A_faulting_observer_does_not_prevent_compensation_during_rollback()
    {
        var ledger = new List<string>();
        var saga = new SagaBuilder<object>()
            .WithObserver(new AlwaysThrowingObserver())
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => { ledger.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        var result = await saga.RunAsync(new object());

        // Even though every observer callback throws, the rollback still ran a's compensation.
        Assert.False(result.Succeeded);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["undo-a"], ledger);
    }

    private sealed class AlwaysThrowingObserver : ISagaObserver
    {
        public void OnStepCompleted(string stepName) => throw new InvalidOperationException("boom");
        public void OnStepFailed(string stepName, Exception exception) => throw new InvalidOperationException("boom");
        public void OnCompensated(string stepName) => throw new InvalidOperationException("boom");
        public void OnCompensationFailed(string stepName, Exception exception) => throw new InvalidOperationException("boom");
    }
}
