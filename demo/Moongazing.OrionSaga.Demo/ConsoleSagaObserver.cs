namespace Moongazing.OrionSaga.Demo;

using Moongazing.OrionSaga.Observers;

/// <summary>
/// An <see cref="ISagaObserver"/> that narrates the saga to the console. The executor calls these
/// hooks as steps complete, fail, and compensate. Observers are observability only: the executor
/// swallows anything they throw, so an observer fault can never disrupt orchestration or rollback.
/// </summary>
public sealed class ConsoleSagaObserver : ISagaObserver
{
    public void OnStepCompleted(string stepName) =>
        Console.WriteLine($"  observer> step completed   : {stepName}");

    public void OnStepFailed(string stepName, Exception exception) =>
        Console.WriteLine($"  observer> step FAILED      : {stepName} ({exception.Message})");

    public void OnCompensated(string stepName) =>
        Console.WriteLine($"  observer> compensated      : {stepName}");

    public void OnCompensationFailed(string stepName, Exception exception) =>
        Console.WriteLine($"  observer> compensation FAILED: {stepName} ({exception.Message})");
}
