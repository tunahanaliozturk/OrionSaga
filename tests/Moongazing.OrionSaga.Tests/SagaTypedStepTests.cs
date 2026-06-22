namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaTypedStepTests
{
    private sealed class Context
    {
        public List<string> Events { get; } = [];
        public string? ReservationId { get; set; }
        public decimal ChargedAmount { get; set; }
    }

    [Fact]
    public async Task A_typed_step_flows_its_value_into_the_context_for_the_next_step()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep<string>(
                "reserve",
                execute: (_, _) => Task.FromResult("res-42"),
                apply: (c, reservationId) => c.ReservationId = reservationId)
            .AddStep(
                "charge",
                (c, _) =>
                {
                    // The next step reads the value the typed step produced, not hand-set shared state.
                    c.Events.Add($"charge:{c.ReservationId}");
                    return Task.CompletedTask;
                })
            .Build();

        var result = await saga.RunAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal("res-42", context.ReservationId);
        Assert.Equal(["charge:res-42"], context.Events);
    }

    [Fact]
    public async Task A_typed_value_is_visible_to_a_later_typed_step()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep<string>(
                "reserve",
                (_, _) => Task.FromResult("res-7"),
                (c, id) => c.ReservationId = id)
            .AddStep<decimal>(
                "price",
                // The second typed step's forward action sees the first step's applied value.
                (c, _) => Task.FromResult(c.ReservationId == "res-7" ? 19.99m : 0m),
                (c, amount) => c.ChargedAmount = amount)
            .Build();

        var result = await saga.RunAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(19.99m, context.ChargedAmount);
    }

    [Fact]
    public async Task A_later_failure_compensates_a_typed_step_in_reverse()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep<string>(
                "reserve",
                (_, _) => Task.FromResult("res-1"),
                (c, id) => c.ReservationId = id,
                compensate: (c, _) => { c.Events.Add("release-reservation"); return Task.CompletedTask; })
            .AddStep(
                "charge",
                (c, _) => { c.Events.Add("do-charge"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("refund"); return Task.CompletedTask; })
            .AddStep("ship", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        var result = await saga.RunAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal("ship", result.FailedStep);
        Assert.True(result.RolledBackCleanly);
        // The typed reserve step completed and is compensated in reverse, after charge's refund.
        Assert.Equal(["do-charge", "refund", "release-reservation"], context.Events);
    }

    [Fact]
    public async Task A_typed_step_that_itself_throws_fails_the_saga_and_rolls_back_prior_steps()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep(
                "reserve",
                (c, _) => { c.Events.Add("do-reserve"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("release-reservation"); return Task.CompletedTask; })
            .AddStep<decimal>(
                "charge",
                (_, _) => throw new InvalidOperationException("charge failed"),
                (c, amount) => c.ChargedAmount = amount)
            .Build();

        var result = await saga.RunAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("charge", result.FailedStep);
        // The typed step's forward action threw, so apply never ran and the amount stays default.
        Assert.Equal(0m, context.ChargedAmount);
        // The prior step rolled back cleanly.
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-reserve", "release-reservation"], context.Events);
    }

    [Fact]
    public async Task A_fault_in_apply_fails_the_typed_step_and_triggers_rollback()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep(
                "reserve",
                (c, _) => { c.Events.Add("do-reserve"); return Task.CompletedTask; },
                (c, _) => { c.Events.Add("release-reservation"); return Task.CompletedTask; })
            .AddStep<string>(
                "charge",
                (_, _) => Task.FromResult("ok"),
                // apply throwing is a forward fault: the saga must roll back the prior step.
                (_, _) => throw new InvalidOperationException("apply failed"))
            .Build();

        var result = await saga.RunAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Failed, result.Outcome);
        Assert.Equal("charge", result.FailedStep);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-reserve", "release-reservation"], context.Events);
    }

    [Fact]
    public async Task A_typed_step_honours_its_per_step_timeout()
    {
        var context = new Context();
        var saga = new SagaBuilder<Context>()
            .AddStep<string>(
                "slow",
                async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "never"; },
                (c, id) => c.ReservationId = id,
                timeout: TimeSpan.FromMilliseconds(50))
            .Build();

        var result = await saga.RunAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(SagaOutcome.Cancelled, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Equal("slow", result.FailedStep);
        // The forward action was cancelled, so the value never reached the context.
        Assert.Null(context.ReservationId);
    }

    [Fact]
    public void AddStep_typed_returns_the_same_builder_for_chaining()
    {
        var builder = new SagaBuilder<Context>();

        var returned = builder.AddStep<string>(
            "a", (_, _) => Task.FromResult("x"), (c, v) => c.ReservationId = v);

        Assert.Same(builder, returned);
    }

    [Fact]
    public void A_null_typed_forward_action_throws()
    {
        var builder = new SagaBuilder<Context>();

        Assert.Throws<ArgumentNullException>(
            () => builder.AddStep<string>("a", null!, (c, v) => c.ReservationId = v));
    }

    [Fact]
    public void A_null_apply_throws()
    {
        var builder = new SagaBuilder<Context>();

        Assert.Throws<ArgumentNullException>(
            () => builder.AddStep<string>("a", (_, _) => Task.FromResult("x"), null!));
    }
}
