// Tests for S08-T1: ParallelSuiteRunner.RunParallelAsync / RunParallelCoreAsync — scenario
// parallelism (topology-per-scenario).  NO DOCKER: every test injects a FAKE ScenarioCoreFunc
// via the internal RunParallelCoreAsync seam, so no Aspire topology is ever started.
//
// The fake core stands in for ScenarioRunner.RunScenarioOwningTopologyAsync.  It lets each
// test control:
//   • the per-scenario verdict + event buffer it returns,
//   • the wall-clock delay it incurs (to perturb completion order), and
//   • whether it throws / observes the cancellation token,
// so the determinism, concurrency-bound, complete-all, cancellation, and exception-safety
// guarantees can be proven WITHOUT a container.
//
// Coverage (design §"Tests"):
//   (a) determinism      — randomised per-scenario delays, two different seeds → BYTE-IDENTICAL
//                          rendered output == declaration-order concatenation.
//   (b) concurrency bound — fake records max observed concurrency; assert ≤ degree ∈ {1,2,3}.
//   (c) verdict matrix    — per verdict combo → aggregate = Elevate-fold + per-scenario list in
//                          declaration order.
//   (d) complete-all      — one fake Fails fast, siblings delayed → all slots present, all ran.
//   (e) external cancel   — cancelled scenarios → Inconclusive, all launched tasks awaited.
//   (f) exception escapes — a fake that throws → EnvironmentError slot, no crash.
//   (g) arg validation    — empty → Pass; length mismatch → ArgumentException;
//                          maxConcurrency = 0 → ArgumentException.

using System.Collections.Concurrent;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Reporting;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// No-docker unit tests for <see cref="ParallelSuiteRunner"/> driven through the internal
/// <see cref="ParallelSuiteRunner.RunParallelCoreAsync"/> seam with a fake
/// <see cref="ParallelSuiteRunner.ScenarioCoreFunc"/> (no Aspire topology is started).
/// </summary>
public sealed class RunParallelAsyncTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // A diff lookup that never renders a diff — the slot buffers carry no FAIL observation,
    // so the rendered output is fully determined by the buffer contents + ordering.
    private static readonly Func<string, JsonElement, string?> NoDiff = (_, _) => null;

    // A trivially-valid AST reused for slot bookkeeping (RunParallelCoreAsync never inspects
    // the AST when a fake core is injected; it only uses the parallel lists' lengths/order).
    private static ScenarioAst MakeAst()
    {
        var doc = YamlDocumentParser.Parse(
            "steps:\n  - id: s1\n    type: http.rest\n    target: x\n    method: GET\n    path: /\n    expect:\n      status: 200\n");
        return AstBuilder.Build(doc, Registry);
    }

    // Builds N declaration-ordered scenario inputs (asts/names/yamls) for the core seam.
    private static (ScenarioAst[] Asts, string[] Names, string[] Yamls) MakeInputs(int n)
    {
        var asts = new ScenarioAst[n];
        var names = new string[n];
        var yamls = new string[n];
        for (var i = 0; i < n; i++)
        {
            asts[i] = MakeAst();
            names[i] = $"scenario-{i}";
            yamls[i] = "steps: []";
        }

        return (asts, names, yamls);
    }

    // Produces the canonical two-line event buffer the fake core returns for a scenario:
    // a scenario-started + scenario-completed line, both stamped with a FIXED runId derived
    // from the scenario name so the rendered output is deterministic regardless of timing.
    private static List<string> MakeBuffer(string scenarioName, Verdict verdict)
    {
        // A FIXED, timing-independent timestamp keeps the rendered output byte-stable across
        // runs (the real runner stamps real wall-clock times, but determinism here is proven
        // against the ORDERING contract, so the buffer content itself must be fixed).
        var ts = DateTimeOffset.UnixEpoch;
        var runId = "run-" + scenarioName;
        return new List<string>
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = ts,
                ScenarioId = scenarioName,
            }),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = ts,
                ScenarioId = scenarioName,
                Verdict = verdict,
                Counts = new VerdictCounts(),
            }),
        };
    }

    // Renders the declaration-order concatenation of the per-scenario buffers EXACTLY as the
    // runner's RenderAndAggregate tail does, so a test can assert byte-identity independently.
    private static string ExpectedRender(IReadOnlyList<string> names, Verdict verdictEach)
    {
        var all = new List<string>();
        foreach (var name in names)
        {
            all.AddRange(MakeBuffer(name, verdictEach));
        }

        var sw = new StringWriter();
        TerminalRenderer.Render(all, sw, NoDiff);
        return sw.ToString();
    }

    // ── (a) Determinism ───────────────────────────────────────────────────────

    /// <summary>
    /// With a fake core that delays each scenario by a RANDOM amount (different per seed), the
    /// rendered output must be BYTE-IDENTICAL across two seeds AND equal to the declaration-order
    /// concatenation — completion order must never leak into the report.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_RandomisedDelays_ProducesByteIdenticalDeterministicOutput()
    {
        const int n = 8;
        var (asts, names, yamls) = MakeInputs(n);

        async Task<string> RunWithSeed(int seed)
        {
            var rng = new Random(seed);
            // Pre-compute the per-scenario delay so the two runs differ ONLY in timing.
            var delays = new int[n];
            lock (rng)
            {
                for (var i = 0; i < n; i++)
                {
                    delays[i] = rng.Next(0, 25);
                }
            }

            var sw = new StringWriter();

            ParallelSuiteRunner.ScenarioCoreFunc fake =
                async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
                {
                    // Index by declaration order via the scenario name suffix.
                    var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                    await Task.Delay(delays[idx], ct).ConfigureAwait(false);
                    return (Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass));
                };

            await ParallelSuiteRunner.RunParallelCoreAsync(
                Registry, asts, names, yamls,
                appHostAssemblyName: null,
                output: sw,
                diffLookup: NoDiff,
                maxConcurrency: 4,
                runScenario: fake,
                seedBaseDirectory: null,
                ct: default);

            return sw.ToString();
        }

        var outA = await RunWithSeed(1);
        var outB = await RunWithSeed(999);

        var expected = ExpectedRender(names, Verdict.Pass);

        Assert.Equal(expected, outA);
        Assert.Equal(outA, outB);
    }

    // ── (b) Concurrency bound ─────────────────────────────────────────────────

    /// <summary>
    /// The number of scenarios running concurrently must never exceed the configured degree.
    /// A fake core increments a shared counter on entry and decrements on exit; the test
    /// asserts the observed maximum is ≤ degree for degree ∈ {1, 2, 3}.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RunParallelCoreAsync_NeverExceedsConfiguredConcurrency(int degree)
    {
        const int n = 12;
        var (asts, names, yamls) = MakeInputs(n);

        var current = 0;
        var maxObserved = 0;
        var gate = new object();

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
            {
                lock (gate)
                {
                    current++;
                    if (current > maxObserved)
                    {
                        maxObserved = current;
                    }
                }

                // Hold the slot briefly so concurrent entries overlap.
                await Task.Delay(10, ct).ConfigureAwait(false);

                lock (gate)
                {
                    current--;
                }

                return (Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass));
            };

        var sw = new StringWriter();
        await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: degree,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: default);

        Assert.True(
            maxObserved <= degree,
            $"Observed concurrency {maxObserved} exceeded the configured degree {degree}.");
        Assert.True(maxObserved >= 1, "At least one scenario must have run.");
    }

    // ── (c) Verdict matrix ────────────────────────────────────────────────────

    /// <summary>
    /// For an arbitrary per-scenario verdict combination, the aggregate must equal the
    /// Elevate-fold (EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass) AND the
    /// per-scenario list must be in declaration order with the right verdicts.
    /// </summary>
    [Theory]
    [InlineData(new[] { Verdict.Pass, Verdict.Pass }, Verdict.Pass)]
    [InlineData(new[] { Verdict.Pass, Verdict.Fail }, Verdict.Fail)]
    [InlineData(new[] { Verdict.Inconclusive, Verdict.Pass }, Verdict.Inconclusive)]
    [InlineData(new[] { Verdict.Fail, Verdict.Inconclusive }, Verdict.Fail)]
    [InlineData(new[] { Verdict.Pass, Verdict.EnvironmentError, Verdict.Fail }, Verdict.EnvironmentError)]
    public async Task RunParallelCoreAsync_FoldsVerdicts_AndPreservesDeclarationOrder(
        Verdict[] perScenario, Verdict expectedAggregate)
    {
        var n = perScenario.Length;
        var (asts, names, yamls) = MakeInputs(n);

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                var verdict = perScenario[idx];
                // Delay EARLIER scenarios more so completion order is reversed vs declaration —
                // the result list must STILL be in declaration order (fixed-slot, not append).
                await Task.Delay((n - idx) * 5, ct).ConfigureAwait(false);
                return (verdict, MakeBuffer(scenarioName, verdict));
            };

        var sw = new StringWriter();
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 4,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: default);

        Assert.Equal(expectedAggregate, result.Verdict);
        Assert.Equal(n, result.ScenarioVerdicts.Count);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(names[i], result.ScenarioVerdicts[i].ScenarioName);
            Assert.Equal(perScenario[i], result.ScenarioVerdicts[i].Verdict);
        }
    }

    // ── (d) Complete-all (no fail-fast) ───────────────────────────────────────

    /// <summary>
    /// A scenario that Fails fast must NOT cancel its siblings: every slot must be present and
    /// every fake must have run to completion (complete-all semantics).
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_OneFailsFast_AllSiblingsStillRun()
    {
        const int n = 6;
        var (asts, names, yamls) = MakeInputs(n);

        var ran = new ConcurrentDictionary<int, bool>();

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);

                if (idx == 0)
                {
                    // Fails immediately (no delay).
                    ran[idx] = true;
                    return (Verdict.Fail, MakeBuffer(scenarioName, Verdict.Fail));
                }

                // Siblings take longer — they must still complete.
                await Task.Delay(20, ct).ConfigureAwait(false);
                ran[idx] = true;
                return (Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass));
            };

        var sw = new StringWriter();
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 3,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: default);

        Assert.Equal(n, result.ScenarioVerdicts.Count);
        for (var i = 0; i < n; i++)
        {
            Assert.True(ran.ContainsKey(i), $"Scenario {i} did not run.");
        }

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Equal(Verdict.Fail, result.ScenarioVerdicts[0].Verdict);
        for (var i = 1; i < n; i++)
        {
            Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[i].Verdict);
        }
    }

    // ── (e) External cancellation ─────────────────────────────────────────────

    /// <summary>
    /// When the external token is cancelled, scenarios that observe the cancellation must be
    /// recorded as <see cref="Verdict.Inconclusive"/> (never Fail, §12.1) and EVERY launched
    /// task must still be awaited (so every topology disposes — no container leak on cancel).
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_ExternalCancellation_YieldsInconclusiveAndAwaitsAll()
    {
        const int n = 5;
        var (asts, names, yamls) = MakeInputs(n);

        using var cts = new CancellationTokenSource();
        var startedCount = 0;
        var completedCount = 0;
        var gate = new object();

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
            {
                lock (gate)
                {
                    startedCount++;
                    // Cancel once the first scenario is mid-flight.
                    if (startedCount == 1)
                    {
                        cts.Cancel();
                    }
                }

                try
                {
                    // Observe cancellation: this throws OperationCanceledException once cancelled.
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        completedCount++;
                    }
                }

                return (Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass));
            };

        var sw = new StringWriter();
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 5,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: cts.Token);

        // Every slot is present (complete-all even on cancel).
        Assert.Equal(n, result.ScenarioVerdicts.Count);

        // No slot is Fail — a cancelled scenario is Inconclusive (§12.1).
        Assert.DoesNotContain(result.ScenarioVerdicts, v => v.Verdict == Verdict.Fail);

        // At least one scenario was cancelled → Inconclusive present, aggregate ≥ Inconclusive.
        Assert.Contains(result.ScenarioVerdicts, v => v.Verdict == Verdict.Inconclusive);
        Assert.True(
            result.Verdict is Verdict.Inconclusive or Verdict.Pass,
            $"Cancelled aggregate must be Inconclusive or Pass, was {result.Verdict}.");

        // Every body that STARTED must have run its finally (proves all launched tasks awaited).
        Assert.Equal(startedCount, completedCount);
    }

    // ── (f) Exception escapes the core ────────────────────────────────────────

    /// <summary>
    /// A genuine exception escaping the core (defence-in-depth) must be synthesised into an
    /// <see cref="Verdict.EnvironmentError"/> slot — never crash the gather, never Fail.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_CoreThrows_YieldsEnvironmentErrorSlot_NoCrash()
    {
        const int n = 4;
        var (asts, names, yamls) = MakeInputs(n);

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                if (idx == 2)
                {
                    throw new InvalidOperationException("boom from core");
                }

                return Task.FromResult((Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass)));
            };

        var sw = new StringWriter();
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 4,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: default);

        Assert.Equal(n, result.ScenarioVerdicts.Count);
        Assert.Equal(Verdict.EnvironmentError, result.ScenarioVerdicts[2].Verdict);
        Assert.Equal(Verdict.EnvironmentError, result.Verdict);

        // The non-throwing siblings all pass and are in declaration order.
        Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[0].Verdict);
        Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[1].Verdict);
        Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[3].Verdict);
    }

    // ── (g) Argument validation ───────────────────────────────────────────────

    /// <summary>An empty scenario list returns <see cref="Verdict.Pass"/> immediately.</summary>
    [Fact]
    public async Task RunParallelAsync_EmptyScenarioList_ReturnsPassImmediately()
    {
        var sw = new StringWriter();

        var result = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: Array.Empty<ScenarioAst>(),
            scenarioNames: Array.Empty<string>(),
            yamlTexts: Array.Empty<string>(),
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: null,
            output: sw);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.ScenarioVerdicts);
    }

    /// <summary>Mismatched parallel-list lengths throw <see cref="ArgumentException"/>.</summary>
    [Fact]
    public async Task RunParallelAsync_MismatchedListLengths_ThrowsArgumentException()
    {
        var ast = MakeAst();
        var sw = new StringWriter();

        var oneScenario = new[] { ast };
        var oneYaml = new[] { "yaml" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ParallelSuiteRunner.RunParallelAsync(
                scenarios: oneScenario,
                scenarioNames: Array.Empty<string>(), // mismatch
                yamlTexts: oneYaml,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: null,
                output: sw));
    }

    /// <summary>A <c>maxConcurrency</c> of zero is rejected with <see cref="ArgumentException"/>.</summary>
    [Fact]
    public async Task RunParallelAsync_ZeroMaxConcurrency_ThrowsArgumentException()
    {
        var ast = MakeAst();
        var sw = new StringWriter();

        var oneScenario = new[] { ast };
        var oneName = new[] { "s0" };
        var oneYaml = new[] { "yaml" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ParallelSuiteRunner.RunParallelAsync(
                scenarios: oneScenario,
                scenarioNames: oneName,
                yamlTexts: oneYaml,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: null,
                output: sw,
                maxConcurrency: 0));
    }
}
