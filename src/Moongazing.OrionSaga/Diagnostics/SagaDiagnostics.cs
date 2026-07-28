namespace Moongazing.OrionSaga.Diagnostics;

using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for saga orchestration. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine, so it shares the family's naming and static-tag
/// conventions: a <see cref="Meter"/> named <c>Moongazing.OrionSaga</c> (subscribe by that name)
/// exposing run, step, and compensation counters under the <c>orion.saga.*</c> names, each tagged
/// outcome. Multi-tenant / multi-region labels configured through
/// <see cref="OrionInstrumentation.SetStaticTags"/> are stamped onto every measurement. Registered
/// as a singleton; dispose it to release the meter.
/// </summary>
public sealed class SagaDiagnostics : OrionInstrumentation
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionSaga";

    /// <summary>Create the meter and its instruments.</summary>
    public SagaDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionSaga"), MeterVersion.Value)
    {
        Runs = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("saga", "runs"),
            unit: "{run}",
            description: "Saga runs, tagged outcome (succeeded/failed).");

        Steps = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("saga", "steps"),
            unit: "{step}",
            description: "Step forward actions, tagged outcome (completed/failed).");

        Compensations = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("saga", "compensations"),
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
        Runs.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, succeeded ? "succeeded" : "failed")));

    internal void RecordStep(bool completed) =>
        Steps.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, completed ? "completed" : "failed")));

    internal void RecordCompensation(bool compensated) =>
        Compensations.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, compensated ? "compensated" : "failed")));
}
