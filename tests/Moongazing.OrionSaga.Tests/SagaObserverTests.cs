namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaObserverTests
{
    private sealed class RecordingObserver : ISagaObserver
    {
        public List<string> Completed { get; } = [];
        public List<string> Failed { get; } = [];
        public List<string> Compensated { get; } = [];
        public List<string> CompensationFailed { get; } = [];

        public void OnStepCompleted(string step) => Completed.Add(step);
        public void OnStepFailed(string step, Exception exception) => Failed.Add(step);
        public void OnCompensated(string step) => Compensated.Add(step);
        public void OnCompensationFailed(string step, Exception exception) => CompensationFailed.Add(step);
    }

    private sealed class ThrowingObserver : ISagaObserver
    {
        public void OnStepCompleted(string step) => throw new InvalidOperationException("boom");
        public void OnStepFailed(string step, Exception exception) => throw new InvalidOperationException("boom");
        public void OnCompensated(string step) => throw new InvalidOperationException("boom");
        public void OnCompensationFailed(string step, Exception exception) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task The_observer_sees_completion_failure_and_compensation()
    {
        var observer = new RecordingObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(["a"], observer.Completed);
        Assert.Equal(["b"], observer.Failed);
        Assert.Equal(["a"], observer.Compensated);
    }

    [Fact]
    public async Task A_faulting_observer_does_not_break_the_saga()
    {
        var saga = new SagaBuilder<object>()
            .WithObserver(new ThrowingObserver())
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
    }
}
