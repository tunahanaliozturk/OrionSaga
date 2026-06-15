namespace Moongazing.OrionSaga;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moongazing.OrionSaga.Diagnostics;

/// <summary>
/// Registration helpers for OrionSaga.
/// </summary>
public static class OrionSagaServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared <see cref="SagaDiagnostics"/> as a singleton so it can be injected where
    /// sagas are built and passed to <see cref="Orchestration.SagaBuilder{TContext}.WithDiagnostics"/>.
    /// Sagas themselves are constructed per definition rather than resolved, so there is nothing
    /// else to register.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddOrionSaga(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<SagaDiagnostics>();
        return services;
    }
}
