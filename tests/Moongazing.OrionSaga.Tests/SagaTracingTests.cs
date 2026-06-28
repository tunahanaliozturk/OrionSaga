namespace Moongazing.OrionSaga.Tests;

using System.Diagnostics;

using Moongazing.OrionSaga.Diagnostics;
using Moongazing.OrionSaga.Orchestration;

using Xunit;

/// <summary>
/// Covers the v0.6.0 tracing surface: an <see cref="Activity"/> span per saga run, per step, and per
/// compensation, emitted from the <see cref="SagaActivitySource"/>. Assertions capture spans via an
/// <see cref="ActivityListener"/> scoped to the source name, and verify nesting, tags, and outcome. The
/// no-listener case proves the happy path starts no activity. Concurrency in the parallel-group case is
/// proven by the captured span set, not by timing.
/// </summary>
public sealed class SagaTracingTests
{
    // A dedicated source the test wraps each run in, so the run span becomes a child of a test-owned
    // root span and shares its TraceId. The recorder filters captured spans to that TraceId, isolating
    // a single run from the spans of other tests running in parallel in the same process. The name is a
    // plain const (not a reference to the field) so the listener's ShouldListenTo callback never touches
    // a static field that may still be initializing on a parallel test thread.
    private const string TestRootSourceName = "Moongazing.OrionSaga.Tests.Root";
    private static readonly ActivitySource TestRootSource = new(TestRootSourceName);

    /// <summary>
    /// Captures every <see cref="Activity"/> started and stopped on the OrionSaga source, recording
    /// them in completion order with their tags intact. Filters to a single run by starting a test-owned
    /// root span and keeping only OrionSaga spans that share its <see cref="ActivityTraceId"/>, so the
    /// spans of other tests running concurrently in the same process do not leak in.
    /// </summary>
    private sealed class ActivityRecorder : IDisposable
    {
        private readonly ActivityListener listener;
        private readonly object gate = new();
        private readonly Activity root;

        public ActivityRecorder()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source =>
                    source.Name == SagaActivitySource.Name || source.Name == TestRootSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnStopped,
            };

            ActivitySource.AddActivityListener(listener);

            // Start the root once a listener is attached so it samples; the run span the test starts
            // next nests under it and shares this TraceId.
            root = TestRootSource.StartActivity("test-root", ActivityKind.Internal)!;
        }

        // Spans in the order they completed (stopped), so a child step span appears before the run span.
        // Only OrionSaga spans from this run's trace are kept.
        public List<Activity> Stopped { get; } = [];

        private void OnStopped(Activity activity)
        {
            if (activity.Source.Name != SagaActivitySource.Name || activity.TraceId != root.TraceId)
            {
                return;
            }

            lock (gate)
            {
                Stopped.Add(activity);
            }
        }

        public List<Activity> ByName(string name)
        {
            lock (gate)
            {
                return Stopped.Where(a => a.OperationName == name).ToList();
            }
        }

        public Activity Run
        {
            get
            {
                lock (gate)
                {
                    return Stopped.Single(a => a.OperationName == SagaActivitySource.RunActivityName);
                }
            }
        }

        public void Dispose()
        {
            root.Dispose();
            listener.Dispose();
        }
    }

    private static string? Tag(Activity activity, string key) =>
        activity.GetTagItem(key)?.ToString();

    [Fact]
    public void The_activity_source_name_matches_the_meter_name()
    {
        // Both traces and metrics are subscribed with one string, so they must stay equal.
        Assert.Equal(SagaDiagnostics.MeterName, SagaActivitySource.Name);
    }

    [Fact]
    public async Task A_successful_run_produces_a_run_span_with_a_nested_step_span_per_step()
    {
        using var recorder = new ActivityRecorder();

        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask)
            .Build();

        await saga.RunAsync(new object());

        var run = recorder.Run;
        var steps = recorder.ByName(SagaActivitySource.StepActivityName);

        Assert.Equal(2, steps.Count);
        // Each step span is a child of the run span, so traces show the orchestration shape.
        Assert.All(steps, s => Assert.Equal(run.SpanId, s.ParentSpanId));
        Assert.Equal(run.TraceId, steps[0].TraceId);

        // Tags carry the step name, ordinal, and outcome.
        Assert.Equal("a", Tag(steps[0], SagaActivitySource.StepNameTag));
        Assert.Equal("1", Tag(steps[0], SagaActivitySource.StepOrdinalTag));
        Assert.Equal("completed", Tag(steps[0], SagaActivitySource.OutcomeTag));
        Assert.Equal("b", Tag(steps[1], SagaActivitySource.StepNameTag));
        Assert.Equal("2", Tag(steps[1], SagaActivitySource.StepOrdinalTag));

        // The run span is tagged succeeded and outlives its children (recorded last).
        Assert.Equal("succeeded", Tag(run, SagaActivitySource.OutcomeTag));
        Assert.Same(run, recorder.Stopped[^1]);
    }

    [Fact]
    public async Task Step_spans_have_a_positive_duration_reflecting_their_forward_action()
    {
        using var recorder = new ActivityRecorder();

        // A gate the step awaits so its span has measurable, non-trivial duration without a fixed sleep.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var saga = new SagaBuilder<object>()
            .AddStep("slow", async (_, ct) => await release.Task.WaitAsync(ct))
            .Build();

        var run = saga.RunAsync(new object());
        release.SetResult();
        await run;

        var step = Assert.Single(recorder.ByName(SagaActivitySource.StepActivityName));
        Assert.True(step.Duration > TimeSpan.Zero);
        Assert.True(recorder.Run.Duration >= step.Duration);
    }

    [Fact]
    public async Task A_failed_run_tags_the_failed_step_and_produces_compensation_spans_nested_under_the_run()
    {
        using var recorder = new ActivityRecorder();

        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("b", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask)
            .AddStep("c", (_, _) => throw new InvalidOperationException("boom"))
            .Build();

        await saga.RunAsync(new object());

        var run = recorder.Run;
        Assert.Equal("failed", Tag(run, SagaActivitySource.OutcomeTag));

        // Three step spans: a and b completed, c failed.
        var steps = recorder.ByName(SagaActivitySource.StepActivityName);
        Assert.Equal(3, steps.Count);
        var failed = Assert.Single(steps, s => Tag(s, SagaActivitySource.StepNameTag) == "c");
        Assert.Equal("failed", Tag(failed, SagaActivitySource.OutcomeTag));

        // Two compensation spans (b then a, reverse order), each nested under the run span, not the
        // faulted step span.
        var comps = recorder.ByName(SagaActivitySource.CompensationActivityName);
        Assert.Equal(2, comps.Count);
        Assert.All(comps, c => Assert.Equal(run.SpanId, c.ParentSpanId));
        Assert.Equal(["b", "a"], comps.Select(c => Tag(c, SagaActivitySource.StepNameTag)));
        Assert.All(comps, c => Assert.Equal("compensated", Tag(c, SagaActivitySource.OutcomeTag)));
    }

    [Fact]
    public async Task A_step_timeout_tags_the_run_and_step_spans_timedout()
    {
        using var recorder = new ActivityRecorder();

        var saga = new SagaBuilder<object>()
            .AddStep(
                "slow",
                async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
                timeout: TimeSpan.FromMilliseconds(20))
            .Build();

        var result = await saga.RunAsync(new object());

        Assert.True(result.TimedOut);
        var step = Assert.Single(recorder.ByName(SagaActivitySource.StepActivityName));
        Assert.Equal("timedout", Tag(step, SagaActivitySource.OutcomeTag));
        Assert.Equal("timedout", Tag(recorder.Run, SagaActivitySource.OutcomeTag));
    }

    [Fact]
    public async Task Parallel_group_member_spans_nest_under_the_group_step_span()
    {
        using var recorder = new ActivityRecorder();

        // A barrier so both members are in flight together, proving concurrency through the captured
        // span set rather than timing: each member signals arrival and waits for the other.
        var arrived = new CountdownArrival(2);

        var saga = new SagaBuilder<object>()
            .AddParallelGroup("group", g => g
                .AddStep("m1", async (_, ct) => await arrived.ArriveAndWaitAsync(ct))
                .AddStep("m2", async (_, ct) => await arrived.ArriveAndWaitAsync(ct)))
            .Build();

        var result = await saga.RunAsync(new object());
        Assert.True(result.Succeeded);

        var run = recorder.Run;
        var stepSpans = recorder.ByName(SagaActivitySource.StepActivityName);

        // The group slot span plus one span per member: three step-named spans in total.
        var groupSlot = Assert.Single(stepSpans, s => Tag(s, SagaActivitySource.StepNameTag) == "group");
        Assert.Equal(run.SpanId, groupSlot.ParentSpanId);

        var members = stepSpans
            .Where(s => Tag(s, SagaActivitySource.StepNameTag) is "group/m1" or "group/m2")
            .ToList();
        Assert.Equal(2, members.Count);
        // Both member spans nest under the group slot span, not directly under the run span.
        Assert.All(members, m => Assert.Equal(groupSlot.SpanId, m.ParentSpanId));
        Assert.All(members, m => Assert.Equal("completed", Tag(m, SagaActivitySource.OutcomeTag)));
    }

    [Fact]
    public async Task With_no_listener_no_activity_is_started_and_behaviour_is_unchanged()
    {
        // No ActivityRecorder is registered. Capture whatever activity the run leaves current; the run
        // must start none, so Activity.Current stays exactly what it was before the call.
        var before = Activity.Current;

        Activity? observedInsideStep = null;
        var saga = new SagaBuilder<object>()
            .AddStep("a", (_, _) => { observedInsideStep = Activity.Current; return Task.CompletedTask; })
            .AddStep("b", (_, _) => throw new InvalidOperationException("boom"), (_, _) => Task.CompletedTask)
            .Build();

        var result = await saga.RunAsync(new object());

        // Behaviour is unchanged: the failure still rolled back cleanly.
        Assert.True(result.Failed);
        Assert.True(result.RolledBackCleanly);

        // No span was started for the source: the step saw no OrionSaga activity as current, and the
        // ambient activity is untouched after the run.
        Assert.Same(before, Activity.Current);
        Assert.Null(observedInsideStep);
    }
}

/// <summary>
/// A small reusable arrival barrier: members call <see cref="ArriveAndWaitAsync"/> and none proceeds
/// until the expected count has arrived. Used to force genuine concurrency deterministically (a
/// sequential implementation would deadlock and time out rather than pass).
/// </summary>
internal sealed class CountdownArrival
{
    private readonly TaskCompletionSource allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int expected;
    private int arrived;

    public CountdownArrival(int expected) => this.expected = expected;

    public async Task ArriveAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref arrived) == expected)
        {
            allArrived.TrySetResult();
        }

        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }
}
