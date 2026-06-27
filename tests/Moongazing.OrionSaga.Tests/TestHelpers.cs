namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Observers;

/// <summary>Wraps a <see cref="SagaDiagnostics"/> so tests can dispose its meter deterministically.</summary>
internal sealed class SagaDiagnosticsHolder : IDisposable
{
    public SagaDiagnostics Diagnostics { get; } = new();

    public void Dispose() => Diagnostics.Dispose();
}

/// <summary>An observer that records every callback in a single ordered list for sequence assertions.</summary>
internal sealed class CountingObserver : ISagaObserver
{
    public List<string> Calls { get; } = [];

    public void OnStepCompleted(string stepName) => Calls.Add($"completed:{stepName}");
    public void OnStepFailed(string stepName, Exception exception) => Calls.Add($"failed:{stepName}");
    public void OnCompensated(string stepName) => Calls.Add($"compensated:{stepName}");
    public void OnCompensationFailed(string stepName, Exception exception) => Calls.Add($"compensationFailed:{stepName}");
    public void OnStepSkipped(string stepName) => Calls.Add($"skipped:{stepName}");
}
