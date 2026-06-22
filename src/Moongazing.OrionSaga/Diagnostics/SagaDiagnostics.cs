namespace Moongazing.OrionSaga.Diagnostics;

using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry instrumentation for saga orchestration. Exposes a <see cref="Meter"/> named
/// <c>Moongazing.OrionSaga</c> with run, step, and compensation counters. Registered as a singleton;
/// dispose it to release the meter.
/// </summary>
public sealed class SagaDiagnostics : IDisposable
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionSaga";

    private readonly Meter meter;

    /// <summary>Create the meter and its instruments.</summary>
    public SagaDiagnostics()
    {
        meter = new Meter(MeterName, "0.3.0");

        Runs = meter.CreateCounter<long>(
            "orionsaga.runs",
            unit: "{run}",
            description: "Saga runs, tagged outcome (succeeded/failed).");

        Steps = meter.CreateCounter<long>(
            "orionsaga.steps",
            unit: "{step}",
            description: "Step forward actions, tagged outcome (completed/failed).");

        Compensations = meter.CreateCounter<long>(
            "orionsaga.compensations",
            unit: "{compensation}",
            description: "Compensations run during rollback, tagged outcome (compensated/failed).");
    }

    /// <summary>Counts saga runs.</summary>
    public Counter<long> Runs { get; }

    /// <summary>Counts step forward actions.</summary>
    public Counter<long> Steps { get; }

    /// <summary>Counts compensations.</summary>
    public Counter<long> Compensations { get; }

    internal void RecordRun(bool succeeded) =>
        Runs.Add(1, new KeyValuePair<string, object?>("outcome", succeeded ? "succeeded" : "failed"));

    internal void RecordStep(bool completed) =>
        Steps.Add(1, new KeyValuePair<string, object?>("outcome", completed ? "completed" : "failed"));

    internal void RecordCompensation(bool compensated) =>
        Compensations.Add(1, new KeyValuePair<string, object?>("outcome", compensated ? "compensated" : "failed"));

    /// <inheritdoc />
    public void Dispose() => meter.Dispose();
}
