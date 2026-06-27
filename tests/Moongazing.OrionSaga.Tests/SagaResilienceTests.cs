namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaResilienceTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
    }

    /// <summary>
    /// A backoff delayer that records each requested wait and returns synchronously, so retry/backoff
    /// behaviour is asserted with no wall-clock sleep. It still honours the token so a cancelled wait
    /// surfaces the cancellation exactly as the real Task.Delay would.
    /// </summary>
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waits.Add(duration);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_transient_forward_fault_succeeds_after_retries_with_no_rollback()
    {
        var ledger = new Ledger();
        var attempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("flaky",
                (c, _) =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    c.Events.Add("do-flaky");
                    return Task.CompletedTask;
                },
                (c, _) => { c.Events.Add("undo-flaky"); return Task.CompletedTask; },
                forwardRetry: new RetryPolicy(3, TimeSpan.FromMilliseconds(10)))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(3, attempts); // two transient faults, then success
        Assert.Equal(2, result.StepsCompleted);
        // No rollback: the flaky step recovered, so nothing was undone.
        Assert.Equal(["do-a", "do-flaky"], ledger.Events);
        // Two retries means two backoff waits, each the base delay (constant backoff).
        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)], delay.Waits);
    }

    [Fact]
    public async Task A_forward_fault_that_exhausts_its_retries_rolls_back_as_before()
    {
        var ledger = new Ledger();
        var attempts = 0;
        var failure = new InvalidOperationException("always");
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("doomed",
                (_, _) => { attempts++; throw failure; },
                forwardRetry: new RetryPolicy(3, TimeSpan.FromMilliseconds(5)))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("doomed", result.FailedStep);
        Assert.Same(failure, result.Failure); // the last fault is what ends the saga
        Assert.Equal(3, attempts); // all three attempts were spent
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_cancellation_is_not_retried()
    {
        var attempts = 0;
        var delay = new RecordingDelay();
        using var cts = new CancellationTokenSource();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("cancel",
                async (_, ct) =>
                {
                    attempts++;
                    cts.Cancel();
                    await Task.Delay(Timeout.Infinite, ct);
                },
                forwardRetry: new RetryPolicy(5, TimeSpan.FromMilliseconds(10)))
            .Build(delay.Delay);

        var result = await saga.RunAsync(new Ledger(), cts.Token);

        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, attempts); // cancellation is terminal, not transient: no retry
        Assert.Empty(delay.Waits);
    }

    [Fact]
    public async Task A_per_step_timeout_is_not_retried()
    {
        var attempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("slow",
                async (_, ct) => { attempts++; await Task.Delay(Timeout.Infinite, ct); },
                timeout: TimeSpan.FromMilliseconds(50),
                forwardRetry: new RetryPolicy(5, TimeSpan.FromMilliseconds(10)))
            .Build(delay.Delay);

        var result = await saga.RunAsync(new Ledger());

        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Equal(1, attempts); // a per-step timeout is terminal, not transient: no retry
        Assert.Empty(delay.Waits);
    }

    [Fact]
    public async Task A_transient_compensation_fault_succeeds_on_retry_per_step()
    {
        var ledger = new Ledger();
        var undoAttempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) =>
                {
                    undoAttempts++;
                    if (undoAttempts < 2)
                    {
                        throw new InvalidOperationException("transient undo");
                    }

                    c.Events.Add("undo-a");
                    return Task.CompletedTask;
                },
                compensationRetry: new RetryPolicy(3, TimeSpan.FromMilliseconds(10)))
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal(2, undoAttempts); // one transient failure, then success
        Assert.True(result.RolledBackCleanly); // compensation recovered: clean rollback
        Assert.Equal(1, result.StepsCompensated);
        Assert.Empty(result.CompensationFailures);
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
        Assert.Equal([TimeSpan.FromMilliseconds(10)], delay.Waits);
    }

    [Fact]
    public async Task A_saga_wide_compensation_retry_applies_to_every_step()
    {
        var ledger = new Ledger();
        var undoAttempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .WithCompensationRetry(new RetryPolicy(3, TimeSpan.FromMilliseconds(5)))
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) =>
                {
                    undoAttempts++;
                    if (undoAttempts < 3)
                    {
                        throw new InvalidOperationException("transient undo");
                    }

                    c.Events.Add("undo-a");
                    return Task.CompletedTask;
                })
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.Equal(3, undoAttempts); // saga-wide policy gave the compensation its retries
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_per_step_compensation_retry_overrides_the_saga_wide_one()
    {
        var ledger = new Ledger();
        var undoAttempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            // Saga-wide allows only a single attempt; the step opts into more.
            .WithCompensationRetry(new RetryPolicy(1, TimeSpan.Zero))
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) =>
                {
                    undoAttempts++;
                    if (undoAttempts < 2)
                    {
                        throw new InvalidOperationException("transient undo");
                    }

                    c.Events.Add("undo-a");
                    return Task.CompletedTask;
                },
                compensationRetry: new RetryPolicy(2, TimeSpan.FromMilliseconds(5)))
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.Equal(2, undoAttempts); // the step's policy (2 attempts) won, not the saga-wide 1
        Assert.True(result.RolledBackCleanly);
    }

    [Fact]
    public async Task An_exhausted_compensation_retry_records_the_failure()
    {
        var ledger = new Ledger();
        var undoAttempts = 0;
        var delay = new RecordingDelay();

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (_, _) => { undoAttempts++; throw new InvalidOperationException("undo always fails"); },
                compensationRetry: new RetryPolicy(3, TimeSpan.FromMilliseconds(5)))
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build(delay.Delay);

        var result = await saga.RunAsync(ledger);

        Assert.Equal(3, undoAttempts); // all attempts spent before recording the failure
        Assert.False(result.RolledBackCleanly);
        Assert.Single(result.CompensationFailures);
        Assert.Equal("a", result.CompensationFailures[0].StepName);
        Assert.Equal(0, result.StepsCompensated);
    }

    [Fact]
    public async Task The_rollback_budget_cancels_a_hung_compensation_and_reports_it()
    {
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .WithRollbackBudget(TimeSpan.FromMilliseconds(50))
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                // A compensation that honours its token but never completes on its own: it blocks until
                // the rollback budget cancels it. No fixed sleep, so timing is deterministic.
                async (_, ct) => await Task.Delay(Timeout.Infinite, ct))
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackTimedOut);
        Assert.False(result.RolledBackCleanly); // the budget cut the unwind short
        Assert.Equal(0, result.StepsCompensated);
        Assert.Single(result.CompensationFailures);
        Assert.Equal("a", result.CompensationFailures[0].StepName);
        Assert.IsAssignableFrom<OperationCanceledException>(result.CompensationFailures[0].Exception);
    }

    [Fact]
    public async Task The_rollback_budget_marks_steps_it_never_reached_as_failures()
    {
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .WithRollbackBudget(TimeSpan.FromMilliseconds(50))
            .AddStep("first",
                (_, _) => Task.CompletedTask,
                (c, _) => { c.Events.Add("undo-first"); return Task.CompletedTask; })
            .AddStep("second",
                (_, _) => Task.CompletedTask,
                // Unwinds newest-first: this hangs until the budget elapses, leaving no time for first.
                async (_, ct) => await Task.Delay(Timeout.Infinite, ct))
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.True(result.RollbackTimedOut);
        Assert.Equal(0, result.StepsCompensated);
        // Both completed steps are reported as compensation failures: second was cut off mid-undo,
        // and first never got a chance once the budget was already spent.
        Assert.Equal(2, result.CompensationFailures.Count);
        Assert.Equal("second", result.CompensationFailures[0].StepName);
        Assert.Equal("first", result.CompensationFailures[1].StepName);
        Assert.DoesNotContain("undo-first", ledger.Events);
    }

    [Fact]
    public async Task A_rollback_within_its_budget_is_clean()
    {
        var ledger = new Ledger();

        var saga = new SagaBuilder<Ledger>()
            .WithRollbackBudget(TimeSpan.FromSeconds(30))
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("boom", (_, _) => throw new InvalidOperationException("forward boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.RollbackTimedOut);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(1, result.StepsCompensated);
        Assert.Equal(["do-a", "undo-a"], ledger.Events);
    }

    [Fact]
    public void A_non_positive_rollback_budget_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SagaBuilder<Ledger>().WithRollbackBudget(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SagaBuilder<Ledger>().WithRollbackBudget(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task No_retry_or_budget_configured_behaves_exactly_as_before()
    {
        var ledger = new Ledger();
        var failure = new InvalidOperationException("boom");

        var saga = new SagaBuilder<Ledger>()
            .AddStep("a",
                (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-a"); return Task.CompletedTask; })
            .AddStep("b",
                (c, _) => { c.Events.Add("do-b"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("undo-b"); return Task.CompletedTask; })
            .AddStep("c", (_, _) => throw failure)
            .Build();

        var result = await saga.RunAsync(ledger);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("c", result.FailedStep);
        Assert.Same(failure, result.Failure);
        Assert.True(result.RolledBackCleanly);
        Assert.False(result.RollbackTimedOut);
        Assert.Equal(2, result.StepsCompensated);
        // Reverse compensation, unchanged from 0.3.0 behaviour.
        Assert.Equal(["do-a", "do-b", "undo-b", "undo-a"], ledger.Events);
    }

    [Fact]
    public async Task A_step_retry_property_is_remembered_on_the_step()
    {
        var forward = new RetryPolicy(3, TimeSpan.FromSeconds(1));
        var comp = new RetryPolicy(2, TimeSpan.FromSeconds(2));

        var step = new SagaStep<Ledger>(
            "a", (_, _) => Task.CompletedTask, forwardRetry: forward, compensationRetry: comp);

        Assert.Same(forward, step.ForwardRetry);
        Assert.Same(comp, step.CompensationRetry);
        await Task.CompletedTask;
    }
}
