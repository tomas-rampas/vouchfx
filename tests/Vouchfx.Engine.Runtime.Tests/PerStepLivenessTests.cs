// Tests for genuine per-step / per-attempt liveness (issue #262). NO DOCKER, NO TOPOLOGY,
// NO ASPIRE — these drive CsxAssembler + RoslynScriptCompiler DIRECTLY (the exact mechanism
// #262 modifies), bypassing ScenarioRunner/SuiteTopology entirely. This is the Docker-free
// equivalent of the design's "two-TCS deadlock-style" liveness proof: a script.csharp-only
// SCENARIO still requires SuiteTopology.StartAsync (Aspire/DCP) in this repo's own testing
// convention (every RunSuiteAsync/RunAsync non-docker test short-circuits BEFORE that call —
// see ScenarioRunnerTests.cs / RunSuiteAsyncTests.cs), so a genuinely Docker-free proof of the
// SAME mechanism (CsxAssembler-emitted StepEvents calls -> RetryRunner -> LiveStepEventSink ->
// LiveEventPump) is obtained by compiling a small assembled script directly, exactly as
// MemoryProbe / ClosureProbeScript already do for the memory-leak gate.
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Test-only coordination point the hand-assembled CSX bodies below reference by fully-qualified
/// name (mirroring how <c>ClosureProbeScript</c> references harness-side stub accessors). A
/// PUBLIC static field so the emitted (collectible-ALC) script can reach it — the field itself
/// holds no reference to anything inside the collectible ALC, so this cannot pin it.
/// </summary>
public static class LivenessHarness
{
    private static TaskCompletionSource s_gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The current gate the emitted CSX awaits.</summary>
    public static TaskCompletionSource Gate => s_gate;

    /// <summary>Re-arms the gate with a fresh, uncompleted <see cref="TaskCompletionSource"/>.</summary>
    public static void Rearm() => s_gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// No-docker proof that <see cref="ScriptGlobalVariables.StepEvents"/> fires per step / per
/// attempt WHILE <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> is still in progress —
/// issue #262's headline guarantee — by compiling a small two-step assembled script directly
/// and observing the live-streamed lines mid-run.
/// </summary>
public sealed class PerStepLivenessTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(ScriptCsharpProvider).Assembly };

    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // A trivially-valid AST purely for the StepNode metadata (Id/CanonicalType/VerifyMode/
    // Timeout/Capture) LiveStepEventSink needs — the ACTUAL executed CSX below is hand-built
    // via CsxAssembler, independent of what this scenario's `code:` fields say.
    private static Vouchfx.Engine.Authoring.Ast.ScenarioAst BuildMetadataAst()
    {
        const string yaml = """
            steps:
              - id: gate-check
                type: script.csharp
                code: |
                  Vars["placeholder"] = 1;
              - id: blocked
                type: script.csharp
                code: |
                  Vars["placeholder"] = 2;
            """;
        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, Registry);
    }

    private static string OutcomeKeyLiteral(string stepId) =>
        JsonSerializer.Serialize(VarKeys.Outcome(CsxFragment.SanitiseId(stepId)));

    [Fact]
    public async Task StepCompleted_IsObservable_WhileRunIsolatedAsyncStillInProgress()
    {
        LivenessHarness.Rearm();

        var ast = BuildMetadataAst();
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);

        // Step "gate-check": completes IMMEDIATELY (no blocking) — its step-completed line must
        // be observable the instant its own block finishes, long before step "blocked" (let
        // alone the whole script) resolves.
        var gateCheckBlock =
            "{\n" +
            $"    Vars[{OutcomeKeyLiteral("gate-check")}] = new Vouchfx.Engine.Abstractions.StepOutcome(" +
            "Vouchfx.Engine.Abstractions.Verdict.Pass, 0L, null);\n" +
            "}";

        // Step "blocked": awaits a TCS the TEST only completes AFTER it has observed
        // "gate-check"'s step-completed line. If StepEvents were only reconstructed AFTER
        // RunIsolatedAsync returns (the pre-#262 behaviour), this would deadlock: this step
        // can't finish until the test releases the gate, and RunIsolatedAsync can't return
        // (so nothing would ever get reconstructed) until this step finishes.
        var blockedBlock =
            "{\n" +
            "    await Vouchfx.Engine.Runtime.Tests.LivenessHarness.Gate.Task.ConfigureAwait(false);\n" +
            $"    Vars[{OutcomeKeyLiteral("blocked")}] = new Vouchfx.Engine.Abstractions.StepOutcome(" +
            "Vouchfx.Engine.Abstractions.Verdict.Pass, 0L, null);\n" +
            "}";

        var assembled = CsxAssembler.Assemble(new List<StepCompilePlan>
        {
            new("gate-check", new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), gateCheckBlock),
                Retry: false, TimeoutMs: null, PollIntervalMs: null),
            new("blocked", new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), blockedBlock),
                Retry: false, TimeoutMs: null, PollIntervalMs: null),
        });

        var additionalRefs = new[] { typeof(LivenessHarness).Assembly.Location };
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

        var collected = new List<string>();
        var collectedLock = new object();
        await using var pump = new LiveEventPump(batch =>
        {
            lock (collectedLock)
            {
                collected.AddRange(batch);
            }
        }, capacity: 64);

        var sink = new LiveStepEventSink(pump, "liveness-run", ast.Steps, captureOriginMap, NullSecretAccessor.Instance);
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            NullWebhookCaptureAccessor.Instance,
            NullTraceCaptureAccessor.Instance,
            sink);

        var runTask = RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: "liveness-test");

        // Poll for "gate-check"'s step-completed line, bounded, so a genuine regression (no
        // liveness) reports a clean test FAILURE rather than hanging the test run forever.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var observedMidRun = false;
        while (DateTime.UtcNow < deadline)
        {
            lock (collectedLock)
            {
                if (collected.Any(l =>
                    l.Contains("\"type\":\"step-completed\"", StringComparison.Ordinal) &&
                    l.Contains("\"stepId\":\"gate-check\"", StringComparison.Ordinal)))
                {
                    observedMidRun = true;
                    break;
                }
            }

            if (runTask.IsCompleted)
            {
                // The whole run finished before we ever released the gate — "blocked" cannot
                // have genuinely blocked, so this is itself already a failure; stop polling.
                break;
            }

            await Task.Delay(15);
        }

        // The critical assertion: the line was observed WHILE the run was still in flight.
        Assert.False(
            runTask.IsCompleted,
            "The run completed before the test released the gate — 'blocked' cannot have " +
            "genuinely awaited an unset TaskCompletionSource; the liveness proof requires the " +
            "run to still be in-flight at this point.");
        Assert.True(
            observedMidRun,
            "Expected to observe step 'gate-check''s step-completed line WHILE " +
            "RunIsolatedAsync was still running (per-step liveness, issue #262) — it never " +
            "appeared within the timeout, and the run never completed either (a genuine hang).");

        // Release the gate so "blocked" — and the whole run — can finish.
        LivenessHarness.Gate.TrySetResult();

        await runTask;

        // Both steps' final outcomes landed in Vars, as expected.
        Assert.True(vars.TryGetValue(VarKeys.Outcome("gate_check"), out var raw1));
        Assert.Equal(Verdict.Pass, Assert.IsType<StepOutcome>(raw1).Verdict);
        Assert.True(vars.TryGetValue(VarKeys.Outcome("blocked"), out var raw2));
        Assert.Equal(Verdict.Pass, Assert.IsType<StepOutcome>(raw2).Verdict);

        // Both steps' step-completed lines are present in the live stream by the time the
        // whole run has finished (the archive-parity concern is covered separately).
        lock (collectedLock)
        {
            Assert.Contains(collected, l =>
                l.Contains("\"stepId\":\"blocked\"", StringComparison.Ordinal) &&
                l.Contains("\"type\":\"step-completed\"", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A <c>verifyMode: RETRY</c> step must emit MULTIPLE <c>step-attempt</c> lines to the live
    /// pump WHILE the poll is still in progress (issue #262) — not only after the whole run
    /// resolves. Uses a small per-attempt delay so the polling window here has ample chances to
    /// observe more than one attempt before the retry loop itself resolves.
    /// </summary>
    [Fact]
    public async Task RetryStep_EmitsMultipleAttemptLines_WhileStillPolling()
    {
        var ast = BuildMetadataAstSingleRetryStep();
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);

        // Fails four times, then passes on the fifth attempt — each attempt sleeps briefly so
        // the whole poll takes long enough for the test to sample multiple step-attempt lines
        // before the loop resolves.
        var pollBlock =
            "{\n" +
            "    await System.Threading.Tasks.Task.Delay(40).ConfigureAwait(false);\n" +
            "    var __n = Vars.TryGetValue(\"__retry_counter\", out var __c) && __c is int __ci ? __ci : 0;\n" +
            "    __n++;\n" +
            "    Vars[\"__retry_counter\"] = __n;\n" +
            "    var __verdict = __n < 5 ? Vouchfx.Engine.Abstractions.Verdict.Fail : Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
            $"    Vars[{OutcomeKeyLiteral("poll-step")}] = new Vouchfx.Engine.Abstractions.StepOutcome(__verdict, 0L, null);\n" +
            "}";

        var assembled = CsxAssembler.Assemble(new List<StepCompilePlan>
        {
            new("poll-step", new CsxFragment(Array.Empty<string>(), Array.Empty<string>(), pollBlock),
                Retry: true, TimeoutMs: 10_000, PollIntervalMs: 30),
        });

        var additionalRefs = new[] { typeof(LivenessHarness).Assembly.Location };
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

        var collected = new List<string>();
        var collectedLock = new object();
        await using var pump = new LiveEventPump(batch =>
        {
            lock (collectedLock)
            {
                collected.AddRange(batch);
            }
        }, capacity: 64);

        var sink = new LiveStepEventSink(pump, "retry-liveness-run", ast.Steps, captureOriginMap, NullSecretAccessor.Instance);
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            NullWebhookCaptureAccessor.Instance,
            NullTraceCaptureAccessor.Instance,
            sink);

        var runTask = RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: "retry-liveness-test");

        var maxAttemptLinesSeenMidRun = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!runTask.IsCompleted && DateTime.UtcNow < deadline)
        {
            lock (collectedLock)
            {
                var count = collected.Count(l => l.Contains("\"type\":\"step-attempt\"", StringComparison.Ordinal));
                if (count > maxAttemptLinesSeenMidRun)
                {
                    maxAttemptLinesSeenMidRun = count;
                }
            }

            await Task.Delay(10);
        }

        await runTask;

        Assert.True(
            maxAttemptLinesSeenMidRun >= 2,
            $"Expected to observe at least 2 step-attempt lines WHILE the retry poll was still " +
            $"in progress; observed at most {maxAttemptLinesSeenMidRun} before completion.");

        lock (collectedLock)
        {
            var totalAttemptLines = collected.Count(l => l.Contains("\"type\":\"step-attempt\"", StringComparison.Ordinal));
            Assert.Equal(5, totalAttemptLines);
        }
    }

    private static Vouchfx.Engine.Authoring.Ast.ScenarioAst BuildMetadataAstSingleRetryStep()
    {
        const string yaml = """
            steps:
              - id: poll-step
                type: script.csharp
                verifyMode: RETRY
                code: |
                  Vars["placeholder"] = 1;
            """;
        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, Registry);
    }
}
