namespace Moongazing.OrionSaga.Diagnostics;

using System.Diagnostics;

/// <summary>
/// The <see cref="System.Diagnostics.ActivitySource"/> OrionSaga emits tracing spans from. The source
/// name is <c>Moongazing.OrionSaga</c>, the same name as the <see cref="SagaDiagnostics"/> meter, so a
/// single subscription string wires up both traces and metrics.
/// </summary>
/// <remarks>
/// <para>
/// The executor starts one span per saga run, a child span per step forward action, and a child span
/// per compensation; a parallel group's member spans nest under the group's step span. Tag names are
/// the constants on this class so consumers can build dashboards against a stable schema.
/// </para>
/// <para>
/// The source is shared and never disposed: an <see cref="System.Diagnostics.ActivitySource"/> is
/// process-lifetime instrumentation, like the meter. Critically, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <see langword="null"/> when no listener is registered for the source, so the no-listener
/// happy path starts no <see cref="Activity"/>, sets no tags, and allocates nothing for tracing.
/// </para>
/// </remarks>
public static class SagaActivitySource
{
    /// <summary>The activity source name consumers subscribe to. Matches <see cref="SagaDiagnostics.MeterName"/>.</summary>
    public const string Name = "Moongazing.OrionSaga";

    /// <summary>The span name for a whole saga run.</summary>
    public const string RunActivityName = "orionsaga.run";

    /// <summary>The span name for a single step's forward action.</summary>
    public const string StepActivityName = "orionsaga.step";

    /// <summary>The span name for a single step's compensation during rollback.</summary>
    public const string CompensationActivityName = "orionsaga.compensation";

    /// <summary>Tag carrying a step's name.</summary>
    public const string StepNameTag = "orionsaga.step.name";

    /// <summary>Tag carrying a step's one-based forward ordinal.</summary>
    public const string StepOrdinalTag = "orionsaga.step.ordinal";

    /// <summary>Tag carrying the outcome of a step, compensation, or run.</summary>
    public const string OutcomeTag = "orionsaga.outcome";

    /// <summary>The shared source. Process-lifetime instrumentation; not disposed.</summary>
    internal static readonly ActivitySource Source = new(Name, ThisAssemblyVersion);

    // The package version, kept in one place so the activity source and the meter report the same value.
    // Bumped alongside the assembly version on each release.
    private const string ThisAssemblyVersion = "0.6.0";
}
