namespace Moongazing.OrionSaga.Tests;

using System.Diagnostics.Metrics;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class SagaDiagnosticsTests
{
    /// <summary>
    /// Captures counter measurements emitted by a single <see cref="SagaDiagnostics"/> instance,
    /// keyed by "{instrument}:{outcome}", via a <see cref="MeterListener"/>.
    /// </summary>
    private sealed class MeterRecorder : IDisposable
    {
        private readonly MeterListener listener;
        private readonly object gate = new();
        private readonly Dictionary<string, long> counts = [];

        public MeterRecorder(SagaDiagnostics diagnostics)
        {
            // Filter to the exact Meter instance owned by this diagnostics so parallel tests, which
            // each create their own SagaDiagnostics under the same meter NAME, do not cross-contaminate.
            var meter = diagnostics.Runs.Meter;

            listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter))
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };

            listener.SetMeasurementEventCallback<long>(OnMeasurement);
            listener.Start();
        }

        private void OnMeasurement(
            Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string outcome = "none";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcome = tag.Value?.ToString() ?? "none";
                }
            }

            var key = $"{instrument.Name}:{outcome}";
            lock (gate)
            {
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + measurement : measurement;
            }
        }

        public long Count(string instrumentAndOutcome)
        {
            listener.RecordObservableInstruments();
            lock (gate)
            {
                return counts.TryGetValue(instrumentAndOutcome, out var value) ? value : 0;
            }
        }

        public void Dispose() => listener.Dispose();
    }

    [Fact]
    public async Task A_successful_run_records_one_succeeded_run_and_completed_steps()
    {
        using var holder = new SagaDiagnosticsHolder();
        using var recorder = new MeterRecorder(holder.Diagnostics);

        var saga = new SagaBuilder<object>()
            .WithDiagnostics(holder.Diagnostics)
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(1, recorder.Count("orionsaga.runs:succeeded"));
        Assert.Equal(0, recorder.Count("orionsaga.runs:failed"));
        Assert.Equal(2, recorder.Count("orionsaga.steps:completed"));
        Assert.Equal(0, recorder.Count("orionsaga.steps:failed"));
        Assert.Equal(0, recorder.Count("orionsaga.compensations:compensated"));
    }

    [Fact]
    public async Task A_failed_run_records_a_failed_run_a_failed_step_and_compensations()
    {
        using var holder = new SagaDiagnosticsHolder();
        using var recorder = new MeterRecorder(holder.Diagnostics);

        var saga = new SagaBuilder<object>()
            .WithDiagnostics(holder.Diagnostics)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(1, recorder.Count("orionsaga.runs:failed"));
        Assert.Equal(0, recorder.Count("orionsaga.runs:succeeded"));
        // a and b completed, c failed.
        Assert.Equal(2, recorder.Count("orionsaga.steps:completed"));
        Assert.Equal(1, recorder.Count("orionsaga.steps:failed"));
        // a and b compensate cleanly.
        Assert.Equal(2, recorder.Count("orionsaga.compensations:compensated"));
        Assert.Equal(0, recorder.Count("orionsaga.compensations:failed"));
    }

    [Fact]
    public async Task A_compensation_failure_is_counted_separately()
    {
        using var holder = new SagaDiagnosticsHolder();
        using var recorder = new MeterRecorder(holder.Diagnostics);

        var saga = new SagaBuilder<object>()
            .WithDiagnostics(holder.Diagnostics)
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("undo-b boom"))
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        Assert.Equal(1, recorder.Count("orionsaga.compensations:compensated"));
        Assert.Equal(1, recorder.Count("orionsaga.compensations:failed"));
    }

    [Fact]
    public async Task Diagnostics_are_optional_and_a_saga_without_them_still_runs()
    {
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void The_meter_name_is_the_documented_constant()
    {
        Assert.Equal("Moongazing.OrionSaga", SagaDiagnostics.MeterName);
    }

    [Fact]
    public void Diagnostics_exposes_run_step_and_compensation_counters()
    {
        using var diagnostics = new SagaDiagnostics();

        Assert.NotNull(diagnostics.Runs);
        Assert.NotNull(diagnostics.Steps);
        Assert.NotNull(diagnostics.Compensations);
        Assert.Equal("orionsaga.runs", diagnostics.Runs.Name);
        Assert.Equal("orionsaga.steps", diagnostics.Steps.Name);
        Assert.Equal("orionsaga.compensations", diagnostics.Compensations.Name);
    }

    [Fact]
    public void Disposing_diagnostics_twice_does_not_throw()
    {
        var diagnostics = new SagaDiagnostics();

        diagnostics.Dispose();
        diagnostics.Dispose();
    }
}
