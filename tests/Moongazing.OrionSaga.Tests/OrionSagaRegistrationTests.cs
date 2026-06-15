namespace Moongazing.OrionSaga.Tests;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionSaga;
using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class OrionSagaRegistrationTests
{
    [Fact]
    public void AddOrionSaga_registers_diagnostics()
    {
        var services = new ServiceCollection();
        services.AddOrionSaga();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<SagaDiagnostics>());
    }

    [Fact]
    public async Task A_saga_built_with_registered_diagnostics_runs()
    {
        var services = new ServiceCollection();
        services.AddOrionSaga();
        using var provider = services.BuildServiceProvider();

        var saga = new SagaBuilder<object>()
            .WithDiagnostics(provider.GetRequiredService<SagaDiagnostics>())
            .AddStep("a", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
    }
}
