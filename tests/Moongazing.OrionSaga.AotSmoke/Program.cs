// NativeAOT smoke test. Publishing this with PublishAot=true must produce zero trim/AOT warnings,
// and running it must exit 0 - OrionSaga's AOT exit criterion. Runtime checks, not a framework:
// the point is to prove saga orchestration (build, run, compensate) survives trimming natively.
using Moongazing.OrionSaga.Orchestration;

// Success path: two steps run to completion, no compensation.
var okLog = new List<string>();
var okSaga = new SagaBuilder<List<string>>()
    .AddStep("step-1", (ctx, ct) => { ctx.Add("s1"); return Task.CompletedTask; })
    .AddStep("step-2", (ctx, ct) => { ctx.Add("s2"); return Task.CompletedTask; })
    .Build();
var ok = await okSaga.RunAsync(okLog);
Check(ok.Succeeded, $"success saga should succeed, was {ok.Outcome}");
Check(okLog is ["s1", "s2"], "both steps should have run in order");

// Failure path: step-2 throws, so step-1's compensation runs.
var failLog = new List<string>();
var failSaga = new SagaBuilder<List<string>>()
    .AddStep(
        "step-1",
        (ctx, ct) => { ctx.Add("do-1"); return Task.CompletedTask; },
        (ctx, ct) => { ctx.Add("undo-1"); return Task.CompletedTask; })
    .AddStep("step-2", (ctx, ct) => throw new InvalidOperationException("boom"))
    .Build();
var failed = await failSaga.RunAsync(failLog);
Check(failed.Failed, $"failing saga should fail, was {failed.Outcome}");
Check(failLog.Contains("undo-1"), "step-1 should have compensated after step-2 failed");

Console.WriteLine("OrionSaga AOT smoke test passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"AOT smoke test failed: {message}");
        Environment.Exit(1);
    }
}
