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
                async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
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
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
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
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
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

    // ── (c2) REQ-018's security signal folds across slots ─────────────────────
    //
    // WHY THIS EXISTS. `ScenarioCoreResult` was introduced for exactly ONE reason: the tuple the
    // fake cores here return cannot carry the security assurance, and `ParallelSuiteRunner`'s
    // fold is the only site that reads the per-slot array. Every fake below the fold still returns
    // the tuple shape (via the implicit conversion, which defaults the assurance to None), so before
    // these two cases the ONE property the record exists for had no test on its ONE consumer — a
    // seam the implicit conversion makes silently plausible either way.

    /// <summary>
    /// One scenario that could not have its declared security confirmed is enough to set the
    /// suite-level signal, whichever slot it lands in and whatever the slots' completion order.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RunParallelCoreAsync_OneSlotFailsSecurityConfirmation_FoldsToTheSuite(int failingSlot)
    {
        const int n = 5;
        var (asts, names, yamls) = MakeInputs(n);

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);

                // Reverse completion order against declaration order, so the fold cannot pass by
                // reading whichever slot happened to finish first.
                await Task.Delay((n - idx) * 5, ct).ConfigureAwait(false);

                return idx == failingSlot
                    ? new ScenarioCoreResult(
                        Verdict.EnvironmentError, MakeBuffer(scenarioName, Verdict.EnvironmentError))
                    {
                        // A failed PROBE — the refusal kind that raises on its own evidence, so
                        // this case does not depend on what the fake scenarios declare.
                        Assurance = SecurityAssurance.None.Refusing(
                            SecurityAbortKind.ProbeUnconfirmed),
                    }
                    : (Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass));
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

        Assert.True(result.Assurance.Unconfirmed);

        // The verdict itself is unchanged by the carve-out (§12.1): a security-confirmation
        // failure is still an ordinary EnvironmentError, and only the EXIT CODE differs.
        Assert.Equal(Verdict.EnvironmentError, result.Verdict);
    }

    /// <summary>
    /// The all-false mirror: with no slot reporting a security-confirmation failure the suite-level
    /// signal stays off, even when scenarios genuinely fail. Without this, a fold hard-wired to
    /// <see langword="true"/> would pass the case above — and REQ-018's carve-out would stop being
    /// narrow, breaking CI on every ordinary environment error.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_NoSlotFailsSecurityConfirmation_LeavesTheSignalOff()
    {
        const int n = 4;
        var (asts, names, yamls) = MakeInputs(n);

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                var verdict = idx == 1 ? Verdict.EnvironmentError : Verdict.Pass;
                return Task.FromResult<ScenarioCoreResult>((verdict, MakeBuffer(scenarioName, verdict)));
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

        Assert.False(result.Assurance.Unconfirmed);
        Assert.Equal(Verdict.EnvironmentError, result.Verdict);
    }

    /// <summary>
    /// The REAL core — the one the fakes above stand in for — must record the refusal the fold reads.
    /// Two doors reach it before any container starts, and both are exercised here: a preflight
    /// rejection of a declared artefact path (REQ-003/REQ-004) and a root-schema rejection of the
    /// declaration itself (REQ-021's per-kind narrowing).
    /// </summary>
    /// <remarks>
    /// Non-Docker by construction: both return from
    /// <c>RunScenarioOwningTopologyAsync</c> before <c>SuiteTopology.StartAsync</c> is called at
    /// all. Without this the fold's input was tested only through fakes that always supplied it.
    /// </remarks>
    // Both documents are written out in full rather than assembled from an interpolated
    // `securityBlock` fragment. That assembly is how the first version of this test silently stopped
    // testing anything: a MULTI-LINE interpolation value is spliced into a raw string literal
    // VERBATIM — the compiler re-indents the literal's own lines to strip the common prefix, but
    // never re-indents an interpolated value to its hole's column. The fragment therefore landed one
    // level too shallow and declared a second service (and a second dependency) literally NAMED
    // `security`, instead of a `security` block on `api`/`cache`. Measured: the suites produced
    // errors at `/environment/services/security/profile` and `/environment/dependencies/security`,
    // and NEITHER door this theory names was ever reached. It passed regardless, because the
    // classifier it fed asked `InstanceLocation.Contains("/security")` — so a broken fixture and an
    // over-matching classifier agreed, and each hid the other.

    /// <summary>The preflight door: a declared <c>clientCert</c> that does not exist (REQ-003/004).</summary>
    private const string ServiceWithAnAbsentClientCert = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              security:
                profile: mtls
                endpoint: 8443
                clientCert: ./certs/absent.pem
                clientKey: ./certs/absent-key.pem
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    /// <summary>The schema door: <c>profile: mtls</c> on a redis dependency (REQ-021's narrowing).</summary>
    private const string RedisDependencyDeclaringMtls = """
        environment:
          services:
            api:
              image: myorg/api:1.0
          dependencies:
            cache:
              type: redis
              security:
                profile: mtls
                endpoint: 6380
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    [Theory]
    // A declared clientCert that does not exist → EnvironmentSecurityValidator (preflight door).
    [InlineData("services", "clientCert")]
    // profile: mtls on a redis dependency → the root schema's per-kind narrowing (schema door).
    [InlineData("dependencies", "redis")]
    public async Task RunScenarioOwningTopologyAsync_SecurityDeclarationRejected_SetsTheSignal(
        string section, string expectedInDiagnostic)
    {
        var yaml = section == "services"
            ? ServiceWithAnAbsentClientCert
            : RedisDependencyDeclaringMtls;

        var suiteDirectory = Directory.CreateTempSubdirectory("vouchfx-core-security").FullName;
        try
        {
            var sw = new StringWriter();
            var result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
                Registry,
                yaml,
                "rejected-security",
                appHostAssemblyName: null,
                output: sw,
                seedBaseDirectory: suiteDirectory,
                livePump: null,
                cancellationToken: default);

            // Pin WHICH door opened, not merely that the assurance reads unconfirmed. Without this the theory
            // passes for any rejection whatsoever — which is exactly how the mis-indented fixture
            // above went unnoticed while reaching neither door it names.
            Assert.Contains(expectedInDiagnostic, sw.ToString(), StringComparison.Ordinal);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.True(result.Assurance.Unconfirmed);
        }
        finally
        {
            Directory.Delete(suiteDirectory, recursive: true);
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
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
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
            async (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
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
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
            {
                var idx = int.Parse(scenarioName.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                if (idx == 2)
                {
                    throw new InvalidOperationException("boom from core");
                }

                return Task.FromResult<ScenarioCoreResult>((Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass)));
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

    /// <summary>
    /// Issue #266, Item 4: when the core throws, <c>RunOneSlotAsync</c> writes a diagnostic
    /// straight to the scenario's raw <see cref="StringWriter"/>
    /// (<c>"[environment-error] scenario '{scenarioName}' did not complete: ..."</c>), which
    /// <c>RenderAndAggregate</c> then flushes to the terminal VERBATIM
    /// (<c>output.Write(raw)</c>) — bypassing <c>TerminalRenderer</c>'s own
    /// <c>GetStr</c>/<c>GetStrFromObject</c> sanitisation choke entirely. Since
    /// <c>scenarioName</c> is author-controlled (<c>metadata.name</c>), an embedded ANSI
    /// escape sequence must still be rendered inert. The assertion isolates the RAW
    /// writer's OWN line (via its unique "did not complete" phrase, which never appears in
    /// TerminalRenderer's own event-based rendering) so this test cannot pass merely because
    /// a DIFFERENT, already-sanitised path (e.g. the scenarioId TerminalRenderer separately
    /// renders from the structured event) happens to be safe.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_CoreThrows_ScenarioNameWithAnsiSequence_RawDiagnosticRendersInert()
    {
        const int n = 2;
        var (asts, _, yamls) = MakeInputs(n);
        var esc = (char)0x1B;
        var hostileName = "scenario-hostile" + esc + "[31mHACKED" + esc + "[0m";
        var names = new[] { "scenario-0", hostileName };

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
            {
                if (string.Equals(scenarioName, hostileName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("boom from core");
                }

                return Task.FromResult<ScenarioCoreResult>((Verdict.Pass, MakeBuffer(scenarioName, Verdict.Pass)));
            };

        var sw = new StringWriter();
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry, asts, names, yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 2,
            runScenario: fake,
            seedBaseDirectory: null,
            ct: default);

        Assert.Equal(Verdict.EnvironmentError, result.ScenarioVerdicts[1].Verdict);

        var rendered = sw.ToString();
        var rawDiagnosticLine = rendered
            .Split('\n')
            .Single(l => l.Contains("did not complete", StringComparison.Ordinal));

        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rawDiagnosticLine, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches this specific raw-writer line.
        Assert.DoesNotContain(esc, rawDiagnosticLine);
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
        Assert.Null(result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// The empty-<c>scenarios</c> arm answers from the unbuilt documents it was handed, exactly as
    /// <c>ScenarioRunner.RunSuiteAsync</c>'s does (Copilot, PR #416) — it used to discard them and
    /// return a default <see cref="SecurityAssurance.None"/>. The fake core is deliberately one
    /// that throws: this arm must return before any slot runs.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_NoScenariosBesideASecuredUnbuiltDocument_AnswersFromTheDocument()
    {
        const string yaml =
            "environment:\n"
            + "  services:\n"
            + "    legacy:\n"
            + "      image: myorg/legacy:1.0\n"
            + "      security:\n"
            + "        profile: mtls\n"
            + "        endpoint: 8443\n"
            + "        clientCert: ./client.pem\n"
            + "        clientKey: ./client.key\n"
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        var sw = new StringWriter();
        var unbuilt = new[] { new UnbuiltDocument(yaml, YamlDocumentParser.Parse(yaml)) };

        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry,
            Array.Empty<ScenarioAst>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 1,
            runScenario: (_, _, _, _, _, _, _, _) =>
                throw new InvalidOperationException("no slot may run on the empty arm"),
            seedBaseDirectory: null,
            unbuiltDocuments: unbuilt);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.ScenarioVerdicts);

        // A local rather than an inline array literal: CA1861 on a repeated constant argument.
        // Issue #415 retyped `Declared` from names to identities, so the same claim — this arm
        // declared exactly `legacy` and nothing else — is now asserted through the Name projection.
        // Unchanged in strength: still an ordered equality against a one-element expectation.
        var expectedDeclared = new[] { "legacy" };
        Assert.Equal(expectedDeclared, result.Assurance.Declared.Select(identity => identity.Name));
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>EDGE-003 on the PARALLEL arm: several unbuilt documents, mixed.</strong> The suite
    /// raises, the refusal recorded is the highest-precedence one, and the assurance reported is the
    /// winning document's own — never one document's declaration beside another's refusal.
    /// </summary>
    /// <remarks>
    /// This path always folded whole per-document values, so this row is not a fix's regression test
    /// but the OTHER side of the agreement: the sequential path's
    /// <c>RunSuiteAsyncTests.RunSuiteAsync_SeveralUnbuiltDocuments_*</c> rows now assert the same
    /// two properties against the same document shapes, so a future change that reopens the union on
    /// either path turns one of the pair red. It lives here rather than in the sequential file
    /// because the harness for a Docker-free parallel run — the throwing fake core on the
    /// empty-<c>scenarios</c> arm — is already here.
    /// </remarks>
    [Fact]
    public async Task RunParallelCoreAsync_SeveralUnbuiltDocuments_RecordTheHighestPrecedenceRefusal()
    {
        const string securedYaml =
            "environment:\n"
            + "  services:\n"
            + "    legacy:\n"
            + "      image: myorg/legacy:1.0\n"
            + "      security:\n"
            + "        profile: mtls\n"
            + "        endpoint: 8443\n"
            + "        clientCert: ./client.pem\n"
            + "        clientKey: ./client.key\n"
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        // A `security` node the SCHEMA rejects: it binds no SecuritySpec, so it declares nothing the
        // walk can see and SecurityDeclarationRejected — which outranks AuthoringFault — carries it.
        const string rejectedYaml =
            "environment:\n"
            + "  services:\n"
            + "    broken:\n"
            + "      image: myorg/broken:1.0\n"
            + "      security: mtls\n"
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        var sw = new StringWriter();
        var unbuilt = new[]
        {
            new UnbuiltDocument(securedYaml, YamlDocumentParser.Parse(securedYaml)),
            new UnbuiltDocument(rejectedYaml, YamlDocumentParser.Parse(rejectedYaml)),
        };

        var result = await ParallelSuiteRunner.RunParallelCoreAsync(
            Registry,
            Array.Empty<ScenarioAst>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            appHostAssemblyName: null,
            output: sw,
            diffLookup: NoDiff,
            maxConcurrency: 1,
            runScenario: (_, _, _, _, _, _, _, _) =>
                throw new InvalidOperationException("no slot may run on the empty arm"),
            seedBaseDirectory: null,
            unbuiltDocuments: unbuilt);

        Assert.Equal(
            SecurityAbortKind.SecurityDeclarationRejected, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);

        // The winning document declared nothing the walk could see, so its whole assurance carries
        // an empty declaration — the mechanical form of "no union with the secured sibling's".
        Assert.Empty(result.Assurance.Declared);
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

    /// <summary>
    /// <see cref="ParallelSuiteRunner.RunParallelCoreAsync"/> throws <see cref="ArgumentException"/>
    /// when the optional <c>seedBaseDirectories</c> list (issue #268) is supplied but its length
    /// does not match <c>scenarios</c>. This guard fires immediately after the empty-list
    /// short-circuit and BEFORE any scenario slot is launched — the injected fake core below must
    /// NEVER be invoked, proving the guard is reached (and the exception thrown) with no topology
    /// — and therefore no Docker — ever touched.
    /// </summary>
    [Fact]
    public async Task RunParallelCoreAsync_MismatchedSeedBaseDirectoriesLength_ThrowsArgumentException()
    {
        var (asts, names, yamls) = MakeInputs(1);

        ParallelSuiteRunner.ScenarioCoreFunc fake =
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, livePump, ct) =>
                throw new InvalidOperationException(
                    "The scenario core must never be invoked: the length-mismatch guard should "
                    + "fire before any scenario slot is launched.");

        var sw = new StringWriter();
        var twoBaseDirectories = new string?[] { "dir-a", "dir-b" }; // mismatch: 2 dirs for 1 scenario

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ParallelSuiteRunner.RunParallelCoreAsync(
                Registry, asts, names, yamls,
                appHostAssemblyName: null,
                output: sw,
                diffLookup: NoDiff,
                maxConcurrency: 4,
                runScenario: fake,
                seedBaseDirectory: null,
                seedBaseDirectories: twoBaseDirectories,
                ct: default));
    }
}
