// Tests for issue #566 — BLOCKING-LANE companion to ScenarioStartedTimestampAccuracyTests.
//
// WHY THIS FILE EXISTS. ScenarioStartedTimestampAccuracyTests carries
// [Trait("requires", "docker")], and build.yml's Docker lane runs with
// `continue-on-error: true` (the Docker / Aspire integration job in build.yml) — so reverting ScenarioRunner.cs's
// `scenarioStartedAt` fix back to `now9` would pass every BLOCKING check and still merge. This
// file drives the SAME production method the Docker test does, at unit speed, with no trait,
// so a regression here fails the build.
//
// THE SEAM. ScenarioRunner.RunScenarioAgainstTopologyAsync (made `internal` for this file,
// issue #566 — Runtime already grants InternalsVisibleTo to this test assembly) is the method
// BOTH `RunScenarioOwningTopologyAsync` (the real, DCP-backed entry point the Docker test drives)
// and `RunPlannedScenarioAgainstKeptTopologyAsync` (the `--watch` kept-topology entry point
// Vouchfx.Cli.Tests.WatchPreTopologyGateTests drives against Vouchfx.Cli.Tests.FakeKeptTopology)
// converge on. It takes an ALREADY-BUILT `IKeptTopology` — not a `SuiteTopology` — and never
// starts one itself. Handing it a minimal, zero-dependency double therefore drives the identical
// `RunScenarioCoreAsync` body the Docker test exercises (the three archive sites, plus the
// live-pump post, this fix touches all live inside it), without ANY topology-starting code path.
//
// NO DCP — STRUCTURAL, NOT A RUNTIME OBSERVATION. RunScenarioAgainstTopologyAsync's only
// interaction with its `suite` argument is through the plain C# `IKeptTopology` members
// (DiscoveredServices, DependencyNames — both read as data) and, only when
// `hostResourcePlan.Count > 0`, starting an in-process Kestrel/OTLP listener (never a container).
// This scenario declares no dependency and no host-resource step, so `ProviderPipeline.Compile`
// returns an EMPTY HostResourcePlan and that branch never runs at all — asserted below
// (`Assert.Empty(pipeline.HostResourcePlan)`) as the precondition that guards it. The method's
// call graph therefore contains no topology-starting code at all: it does nothing but compile and
// execute the script in this process. (This test project IS declared an Aspire host —
// its csproj sets `<IsAspireHost>true</IsAspireHost>` under the Aspire.AppHost.Sdk, the same as the Docker-gated sibling relies on; the
// guarantee here is about what THIS METHOD calls, not about what the project could start.)
//
// RED PROOF. Temporarily restoring `now9` (the pre-#566 timestamp) at the success-path archive
// `scenario-started` line reproduces the exact measured pre-fix shape: for a 414ms step, the
// archive delta collapsed to ~37ms (the reconstruction-bookkeeping gap, not the step's own work);
// for the compile-failure catch path, both the started and completed lines were stamped from the
// SAME local instant, making the delta exactly zero. Restored immediately after from a scratchpad
// backup. This file is not re-deriving that proof inline; it is the PERMANENT pin that keeps the
// blocking lane sensitive to the same regression going forward.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Drives <see cref="ScenarioRunner.RunScenarioAgainstTopologyAsync"/> directly against a
/// minimal, zero-dependency <see cref="IKeptTopology"/> double — no <c>SuiteTopology</c>, no
/// <c>HeadlessTopology</c>, no Aspire/DCP call anywhere on this path (see the file header) — so
/// the issue #566 archive-timestamp fix is pinned in the <c>requires!=docker</c> blocking lane,
/// not only in the Docker-gated <see cref="ScenarioStartedTimestampAccuracyTests"/> that
/// build.yml's <c>continue-on-error: true</c> integration job cannot block a merge on.
/// </summary>
public sealed class ScenarioAgainstTopologyStartedTimestampTests
{
    // A plain identifier, unrelated to any real suite path — ProviderPipeline.Compile's
    // `suiteNamespace` names the generated CSX class, nothing more (mirrors
    // ProviderPipelineTests.SuiteNamespace).
    private const string SuiteNamespace = "duration-blocking-lane";

    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(ScriptCsharpProvider).Assembly };

    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // No `environment:` block and no host-resource-contributing step (e.g. webhook-listen.http):
    // ProviderPipeline.Compile therefore returns an EMPTY HostResourcePlan, so
    // RunScenarioAgainstTopologyAsync's only branch that starts anything in-process (a Kestrel or
    // OTLP listener) never executes — see the file header's NO DCP argument.
    private const string SleepingScenarioYaml = """
        steps:
          - id: sleep-step
            type: script.csharp
            code: |
              await Task.Delay(TimeSpan.FromMilliseconds(400));
        """;

    // Deliberately invalid C#. ScriptCsharpProvider.Validate enforces only the 64 KiB body-size
    // limit (no syntax check), so this passes authoring validation and ProviderPipeline.Compile
    // (pipeline.Failure stays null) and fails only later, at Roslyn compile time inside
    // RunScenarioAgainstTopologyAsync's inner try — landing in the generic `catch (Exception ex)`
    // this test targets: that catch's OWN started line is a THIRD archive site the #566 fix
    // touches, distinct from the success path, and needed its own pin in the blocking lane.
    private const string InvalidCompileScenarioYaml = """
        steps:
          - id: broken-script
            type: script.csharp
            code: |
              this is not ; valid C# &&& at all !!!
        """;

    /// <summary>
    /// The blocking-lane pin: same assertion as
    /// <see cref="ScenarioStartedTimestampAccuracyTests"/>'s Docker-gated test, reached through
    /// the shared <c>RunScenarioAgainstTopologyAsync</c> → <c>RunScenarioCoreAsync</c> core
    /// instead of the DCP-backed <c>RunScenarioOwningTopologyAsync</c> entry point.
    /// </summary>
    [Fact]
    public async Task RunScenarioAgainstTopologyAsync_ScriptSleeps400Ms_ArchiveStartedTimestampPredatesStepWork()
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(SleepingScenarioYaml), Registry);
        var pipeline = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.Null(pipeline.Failure);
        Assert.NotNull(pipeline.Assembled);
        Assert.Empty(pipeline.HostResourcePlan); // the NO DCP precondition this test relies on.

        var buffer = new List<string>();
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var verdict = await ScenarioRunner.RunScenarioAgainstTopologyAsync(
            ast,
            scenarioName: "duration-archive-sleep-no-dcp",
            runId: Guid.NewGuid().ToString("n"),
            suite: new NoDependencyKeptTopology(),
            assembled: pipeline.Assembled!,
            compileReferencePaths: pipeline.CompileReferencePaths,
            hostResourcePlan: pipeline.HostResourcePlan,
            buffer: buffer,
            isolation: new NullScenarioIsolation(),
            output: output,
            seedBaseDirectory: null,
            cancellationToken: cts.Token);

        Assert.Equal(Verdict.Pass, verdict);
        Assert.NotEmpty(buffer);

        var scenarioStartedTs = SingleEventTimestamp(buffer, "scenario-started");
        var scenarioCompletedTs = SingleEventTimestamp(buffer, "scenario-completed");
        var stepDurationMs = SingleStepDurationMs(buffer, "sleep-step");

        // RED on the pre-#566 tree (now9 taken after RunIsolatedAsync returns): this delta
        // collapses to ~tens of milliseconds while stepDurationMs is ~400+.
        var scenarioDeltaMs = (scenarioCompletedTs - scenarioStartedTs).TotalMilliseconds;
        Assert.True(
            scenarioDeltaMs >= stepDurationMs,
            $"scenario-completed.ts - scenario-started.ts = {scenarioDeltaMs}ms, which is LESS than "
            + $"the step's own durationMs={stepDurationMs}ms — the archive scenario-started line was "
            + "stamped after the step already ran, discarding the time the scenario's own step took "
            + "(issue #566).");
    }

    /// <summary>
    /// Pins the generic-catch archive site the #566 fix touches: the <c>catch (Exception ex)</c>
    /// around the compile+run call. The sibling <c>catch (SecretResolutionException)</c> site
    /// stays unpinned, because no Core provider can reach it: <c>script.csharp</c> wraps every
    /// step in its own <c>catch (System.Exception)</c>, so a secret-resolution failure never
    /// escapes a step to that outer catch. A step whose <c>code:</c> fails to compile reaches the
    /// generic <c>catch (Exception ex)</c> in <c>RunScenarioCoreAsync</c>, which is reached AFTER
    /// <c>scenarioStartedAt</c> is captured (at the top of the method, well before
    /// <c>RoslynScriptCompiler.CompileOnce</c> is ever called) — so this catch path is reachable
    /// with the fix in scope, not merely a documentation claim.
    /// </summary>
    /// <remarks>
    /// Pre-#566 this catch stamped BOTH its <c>scenario-started</c> AND <c>scenario-completed</c>
    /// lines from the same local <c>nowCE</c> instant, making
    /// <c>completed.ts − started.ts</c> exactly <c>0</c> there, always — RED on that tree.
    /// </remarks>
    [Fact]
    public async Task RunScenarioAgainstTopologyAsync_CompileFailure_ArchiveStartedPredatesCompletedTimestamp()
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(InvalidCompileScenarioYaml), Registry);
        var pipeline = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        // Validate enforces only the 64 KiB size limit (see the YAML constant's own comment), so
        // the invalid C# body passes authoring validation and reaches the Roslyn compile step.
        Assert.Null(pipeline.Failure);
        Assert.NotNull(pipeline.Assembled);
        Assert.Empty(pipeline.HostResourcePlan);

        var buffer = new List<string>();
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var verdict = await ScenarioRunner.RunScenarioAgainstTopologyAsync(
            ast,
            scenarioName: "duration-archive-compile-failure",
            runId: Guid.NewGuid().ToString("n"),
            suite: new NoDependencyKeptTopology(),
            assembled: pipeline.Assembled!,
            compileReferencePaths: pipeline.CompileReferencePaths,
            hostResourcePlan: pipeline.HostResourcePlan,
            buffer: buffer,
            isolation: new NullScenarioIsolation(),
            output: output,
            seedBaseDirectory: null,
            cancellationToken: cts.Token);

        // The generic `catch (Exception ex)` maps a compile failure to Inconclusive (a CSX
        // compilation failure is an authoring error, never a product defect).
        Assert.Equal(Verdict.Inconclusive, verdict);

        // SingleEventTimestamp itself asserts exactly one line of each type exists — the "archive
        // has exactly one scenario-started and one scenario-completed" requirement.
        var scenarioStartedTs = SingleEventTimestamp(buffer, "scenario-started");
        var scenarioCompletedTs = SingleEventTimestamp(buffer, "scenario-completed");

        Assert.True(
            scenarioCompletedTs > scenarioStartedTs,
            $"scenario-completed.ts ({scenarioCompletedTs:o}) must be AFTER scenario-started.ts "
            + $"({scenarioStartedTs:o}) on the compile-failure catch path — pre-#566 this catch "
            + "stamped both lines from the SAME instant, making the delta exactly zero "
            + "(issue #566).");
    }

    // ── Pre-topology refusal doors: schema (Step 2) and parse/AST-build (Step 3) ──
    //
    // A refusal at either door executes NOTHING — no compile, no script, no topology — so its
    // archive pair must record ONE instant, not two separate DateTimeOffset.UtcNow reads
    // straddling the EventStreamJson.ToLine/ScenarioCompletedLine calls (the gap being only the
    // first line's own JSON serialisation: measured at ~10 ms under --parallel contention, where
    // a refusal rendered `time="0.010"`, and 22.6 ms in the red proof of these tests).
    // RunScenarioOwningTopologyAsync has no injectable topology factory: reading its source shows
    // both doors `return` (the schema door inside its `if (!validationResult.IsValid)` block; the
    // parse door inside its `catch (Exception ex)` around Step 3) hundreds of lines before the
    // first topology-starting call (`SuiteTopology suite;` / `.StartAsync(...)`, first reached only
    // once BOTH doors have passed) — so there is nothing to inject and assert-not-invoked; the
    // refusal shapes below are proof by construction that this call cannot reach DCP.

    // Missing the required `id` field — DocumentValidator.Validate (Step 2) refuses this before
    // any AST is built.
    private const string SchemaInvalidScenarioYaml = """
        steps:
          - type: script.csharp
            code: |
              Vars.Set("k", "v");
        """;

    // Schema-VALID (JSON Schema has no step-id-uniqueness rule) but AstBuilder.Build (Step 3)
    // throws AstBuildException for the duplicate id, landing in RunScenarioOwningTopologyAsync's
    // parse-door `catch (Exception ex)`.
    private const string UnbuildableDuplicateIdScenarioYaml = """
        steps:
          - id: dup
            type: script.csharp
            code: |
              Vars.Set("k", "one");
          - id: dup
            type: script.csharp
            code: |
              Vars.Set("k", "two");
        """;

    [Fact]
    public async Task RunScenarioOwningTopologyAsync_SchemaInvalidDocument_ArchiveStartedEqualsCompletedTimestamp()
    {
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
            registry: Registry,
            yamlText: SchemaInvalidScenarioYaml,
            scenarioName: "duration-archive-schema-invalid",
            declaredTargets: ScenarioRunner.DeclaredTargetsOf(SchemaInvalidScenarioYaml, Registry),
            appHostAssemblyName: "Vouchfx.Engine.Runtime.Tests",
            output: output,
            seedBaseDirectory: null,
            livePump: null,
            cancellationToken: cts.Token);

        Assert.Equal(Verdict.Inconclusive, result.Verdict);

        // Door identity: the schema door prints the validation errors, never the parse door's
        // "Parse / AST error:" prefix. Pinned so this test cannot drift onto the other door.
        Assert.DoesNotContain("Parse / AST error", output.ToString(), StringComparison.Ordinal);

        var startedTs = SingleEventTimestamp(result.Buffer, "scenario-started");
        var completedTs = SingleEventTimestamp(result.Buffer, "scenario-completed");

        // RED with a double UtcNow read at this door: the two lines carry distinct instants
        // (measured under --parallel contention: time="0.010" instead of "0.000").
        Assert.Equal(startedTs, completedTs);
    }

    [Fact]
    public async Task RunScenarioOwningTopologyAsync_UnbuildableDocument_ArchiveStartedEqualsCompletedTimestamp()
    {
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
            registry: Registry,
            yamlText: UnbuildableDuplicateIdScenarioYaml,
            scenarioName: "duration-archive-unbuildable",
            declaredTargets: ScenarioRunner.DeclaredTargetsOf(UnbuildableDuplicateIdScenarioYaml, Registry),
            appHostAssemblyName: "Vouchfx.Engine.Runtime.Tests",
            output: output,
            seedBaseDirectory: null,
            livePump: null,
            cancellationToken: cts.Token);

        Assert.Equal(Verdict.Inconclusive, result.Verdict);

        // Door identity: two steps sharing an id pass the schema (its only uniqueItems rule is
        // on ports) and are refused by AstBuilder, so the parse door's "Parse / AST error:"
        // prefix must be what reached the terminal. If a future schema rule caught duplicate
        // ids, this test would otherwise drift onto the schema door and leave the parse door
        // unpinned.
        Assert.Contains("Parse / AST error", output.ToString(), StringComparison.Ordinal);

        var startedTs = SingleEventTimestamp(result.Buffer, "scenario-started");
        var completedTs = SingleEventTimestamp(result.Buffer, "scenario-completed");

        Assert.Equal(startedTs, completedTs);
    }

    /// <summary>
    /// Reads the <c>ts</c> field off the single archive line of the given event
    /// <paramref name="eventType"/>. Fails the test (rather than throwing an unhelpful
    /// <see cref="InvalidOperationException"/> from <c>Single()</c>) when the buffer carries zero
    /// or more than one such line. Disposes each parsed <see cref="JsonDocument"/> before moving
    /// to the next line.
    /// </summary>
    private static DateTimeOffset SingleEventTimestamp(IReadOnlyList<string> buffer, string eventType)
    {
        DateTimeOffset? found = null;
        var matchCount = 0;

        foreach (var line in buffer)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() != eventType)
            {
                continue;
            }

            matchCount++;
            found = doc.RootElement.GetProperty("ts").GetDateTimeOffset();
        }

        Assert.True(
            matchCount == 1,
            $"Expected exactly one '{eventType}' line in the archive buffer, found {matchCount}. "
            + $"Buffer event types: {string.Join(", ", buffer.Select(EventTypeOf))}.");

        return found!.Value;
    }

    /// <summary>
    /// Reads the <c>durationMs</c> field off the single <c>step-completed</c> line for
    /// <paramref name="stepId"/>. Disposes each parsed <see cref="JsonDocument"/> before moving
    /// to the next line.
    /// </summary>
    private static long SingleStepDurationMs(IReadOnlyList<string> buffer, string stepId)
    {
        long? found = null;
        var matchCount = 0;

        foreach (var line in buffer)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() != "step-completed"
                || !doc.RootElement.TryGetProperty("stepId", out var idEl)
                || idEl.GetString() != stepId)
            {
                continue;
            }

            matchCount++;
            found = doc.RootElement.GetProperty("durationMs").GetInt64();
        }

        Assert.True(
            matchCount == 1,
            $"Expected exactly one 'step-completed' line for step '{stepId}', found {matchCount}.");

        return found!.Value;
    }

    private static string EventTypeOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("type", out var typeEl)
            ? typeEl.GetString() ?? "(no type)"
            : "(no type)";
    }

    /// <summary>
    /// A minimal <see cref="IKeptTopology"/> double declaring ZERO dependencies and ZERO
    /// discovered services — the same "empty topology" shape
    /// <c>SuiteTopology.StartAsync</c> documents for a null environment, but built here as a
    /// plain in-memory object with no Aspire/DCP call of any kind. Mirrors
    /// <c>Vouchfx.Cli.Tests.FakeKeptTopology</c>'s shape (that type is internal to a different
    /// assembly and Vouchfx.TestSupport is BCL-only with no ProjectReferences, so it cannot be
    /// shared).
    /// </summary>
    private sealed class NoDependencyKeptTopology : IKeptTopology
    {
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations { get; } =
            Array.Empty<SecurityConfirmation>();

        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices { get; } =
            Array.Empty<EndpointSelectionNotice>();

        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices { get; } =
            Array.Empty<EndpointTrustNotice>();

        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } =
            new Dictionary<string, object>(StringComparer.Ordinal);

        public IReadOnlyList<string> DependencyNames { get; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> DependencyTypes { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task ReseedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
