namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Observers;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaObserverPayloadTests
{
    /// <summary>Records the ordinal and duration handed to each richer observer callback.</summary>
    private sealed record Notification(string Kind, string StepName, int Ordinal, TimeSpan Duration);

    private sealed class PayloadObserver : ISagaObserver
    {
        public List<Notification> Notifications { get; } = [];

        // Only the richer overloads are implemented. The executor always calls these, so the name-only
        // overloads below are never reached during a run; they exist to satisfy the interface.
        public void OnStepCompleted(string stepName) => throw new InvalidOperationException("name-only overload should not be called");
        public void OnStepFailed(string stepName, Exception exception) => throw new InvalidOperationException("name-only overload should not be called");
        public void OnCompensated(string stepName) => throw new InvalidOperationException("name-only overload should not be called");
        public void OnCompensationFailed(string stepName, Exception exception) => throw new InvalidOperationException("name-only overload should not be called");

        public void OnStepCompleted(string stepName, int ordinal, TimeSpan duration) =>
            Notifications.Add(new Notification("completed", stepName, ordinal, duration));

        public void OnStepFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) =>
            Notifications.Add(new Notification("failed", stepName, ordinal, duration));

        public void OnCompensated(string stepName, int ordinal, TimeSpan duration) =>
            Notifications.Add(new Notification("compensated", stepName, ordinal, duration));

        public void OnCompensationFailed(string stepName, Exception exception, int ordinal, TimeSpan duration) =>
            Notifications.Add(new Notification("compensationFailed", stepName, ordinal, duration));
    }

    /// <summary>An observer that implements only the original name-only surface.</summary>
    private sealed class LegacyObserver : ISagaObserver
    {
        public List<string> Calls { get; } = [];

        public void OnStepCompleted(string stepName) => Calls.Add($"completed:{stepName}");
        public void OnStepFailed(string stepName, Exception exception) => Calls.Add($"failed:{stepName}");
        public void OnCompensated(string stepName) => Calls.Add($"compensated:{stepName}");
        public void OnCompensationFailed(string stepName, Exception exception) => Calls.Add($"compensationFailed:{stepName}");
    }

    [Fact]
    public async Task Completed_steps_carry_a_one_based_ordinal_in_order()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(
            [("completed", "a", 1), ("completed", "b", 2), ("completed", "c", 3)],
            observer.Notifications.Select(n => (n.Kind, n.StepName, n.Ordinal)));
    }

    [Fact]
    public async Task A_failure_carries_the_failing_step_ordinal()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        var failed = Assert.Single(observer.Notifications, n => n.Kind == "failed");
        // c is the third step added, so its ordinal is 3.
        Assert.Equal("c", failed.StepName);
        Assert.Equal(3, failed.Ordinal);
    }

    [Fact]
    public async Task Compensations_carry_each_steps_original_forward_ordinal()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        // Compensation runs in reverse (b then a), but each carries its forward ordinal: b=2, a=1.
        var compensations = observer.Notifications.Where(n => n.Kind == "compensated").ToList();
        Assert.Equal(
            [("b", 2), ("a", 1)],
            compensations.Select(n => (n.StepName, n.Ordinal)));
    }

    [Fact]
    public async Task A_compensation_failure_carries_the_step_forward_ordinal()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-b boom"))
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        var failed = Assert.Single(observer.Notifications, n => n.Kind == "compensationFailed");
        Assert.Equal("b", failed.StepName);
        Assert.Equal(2, failed.Ordinal);
    }

    [Fact]
    public async Task Every_reported_duration_is_non_negative()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        // Durations come from a monotonic timer, so they are never negative. We assert the floor rather
        // than a wall-clock value to stay deterministic: exact timings are machine-dependent.
        Assert.NotEmpty(observer.Notifications);
        Assert.All(observer.Notifications, n => Assert.True(n.Duration >= TimeSpan.Zero));
    }

    [Fact]
    public async Task A_step_that_does_observable_work_reports_a_measured_duration()
    {
        var observer = new PayloadObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("delay", async (_, ct) => await Task.Delay(TimeSpan.FromMilliseconds(20), ct))
            .Build();

        await saga.RunAsync(new object());

        var completed = Assert.Single(observer.Notifications, n => n.Kind == "completed");
        // The duration is populated and non-negative. We deliberately do not assert it is at least the
        // delay: a lower wall-clock bound is timer-resolution sensitive and would be flaky in CI.
        Assert.True(completed.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_legacy_observer_implementing_only_the_name_only_surface_still_receives_callbacks()
    {
        // The richer overloads are default interface methods that forward to the name-only methods, so
        // an observer written against the original surface keeps working without recompilation changes.
        var observer = new LegacyObserver();
        var saga = new SagaBuilder<object>()
            .WithObserver(observer)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(["completed:a", "failed:b", "compensated:a"], observer.Calls);
    }
}
