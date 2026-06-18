namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaBuilderTests
{
    private sealed class Ledger
    {
        public List<string> Events { get; } = [];
    }

    [Fact]
    public void AddStep_returns_the_same_builder_for_chaining()
    {
        var builder = new SagaBuilder<Ledger>();

        var returned = builder.AddStep("a", (_, _) => Task.CompletedTask);

        Assert.Same(builder, returned);
    }

    [Fact]
    public void WithDiagnostics_returns_the_same_builder_for_chaining()
    {
        var builder = new SagaBuilder<Ledger>();
        using var diagnostics = new SagaDiagnosticsHolder();

        var returned = builder.WithDiagnostics(diagnostics.Diagnostics);

        Assert.Same(builder, returned);
    }

    [Fact]
    public void WithObserver_returns_the_same_builder_for_chaining()
    {
        var builder = new SagaBuilder<Ledger>();

        var returned = builder.WithObserver(new CountingObserver());

        Assert.Same(builder, returned);
    }

    [Fact]
    public async Task A_step_added_without_a_compensation_compensates_as_a_noop()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("a", (c, _) => { c.Events.Add("do-a"); return Task.CompletedTask; })
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        var result = await saga.RunAsync(ledger);

        // Step a has no compensation; rollback runs the implicit no-op without error.
        Assert.False(result.Succeeded);
        Assert.True(result.RolledBackCleanly);
        Assert.Equal(["do-a"], ledger.Events);
    }

    [Fact]
    public async Task An_already_constructed_step_can_be_added()
    {
        var ledger = new Ledger();
        var step = new SagaStep<Ledger>(
            "a",
            (c, _) => { c.Events.Add("a"); return Task.CompletedTask; });

        var saga = new SagaBuilder<Ledger>().AddStep(step).Build();
        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(["a"], ledger.Events);
    }

    [Fact]
    public void Adding_a_null_step_throws()
    {
        var builder = new SagaBuilder<Ledger>();

        Assert.Throws<ArgumentNullException>(() => builder.AddStep(null!));
    }

    [Fact]
    public async Task Build_snapshots_the_steps_so_later_additions_do_not_affect_a_built_saga()
    {
        var ledger = new Ledger();
        var builder = new SagaBuilder<Ledger>()
            .AddStep("a", (c, _) => { c.Events.Add("a"); return Task.CompletedTask; });

        var saga = builder.Build();

        // Mutating the builder after Build must not leak into the already-built saga.
        builder.AddStep("b", (c, _) => { c.Events.Add("b"); return Task.CompletedTask; });

        var result = await saga.RunAsync(ledger);

        Assert.True(result.Succeeded);
        Assert.Equal(["a"], ledger.Events);
    }

    [Fact]
    public async Task Build_can_be_called_more_than_once_producing_independent_sagas()
    {
        var first = new Ledger();
        var second = new Ledger();
        var builder = new SagaBuilder<Ledger>()
            .AddStep("a", (c, _) => { c.Events.Add("a"); return Task.CompletedTask; });

        var sagaOne = builder.Build();
        var sagaTwo = builder.Build();

        Assert.NotSame(sagaOne, sagaTwo);

        await sagaOne.RunAsync(first);
        await sagaTwo.RunAsync(second);

        Assert.Equal(["a"], first.Events);
        Assert.Equal(["a"], second.Events);
    }

    [Fact]
    public async Task Steps_run_in_the_order_they_were_added()
    {
        var ledger = new Ledger();
        var saga = new SagaBuilder<Ledger>()
            .AddStep("c", (c, _) => { c.Events.Add("c"); return Task.CompletedTask; })
            .AddStep("a", (c, _) => { c.Events.Add("a"); return Task.CompletedTask; })
            .AddStep("b", (c, _) => { c.Events.Add("b"); return Task.CompletedTask; })
            .Build();

        await saga.RunAsync(ledger);

        Assert.Equal(["c", "a", "b"], ledger.Events);
    }
}
