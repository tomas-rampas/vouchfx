// Tests for issue #566: the archive `scenario-started` timestamp must reflect the
// scenario's REAL start, not the instant reconstruction bookkeeping happened to run.
//
// MEASURED defect (this tree's CLI, one script.csharp step sleeping 1.5s, before the
// ScenarioRunner.RunScenarioCoreAsync fix): step durationMs 1515; archive
// scenario-started ts 15:12:44.6097; scenario-completed ts 15:12:44.6378 — a 28ms
// delta for a scenario whose own step took 1515ms. Cause: the archive
// `scenario-started` line was stamped at `now9`, taken only AFTER RunIsolatedAsync had
// already returned, discarding the wall-clock time the scenario's steps actually took.
// JunitXmlRenderer / HtmlRenderer both derive the rendered scenario duration from this
// (started, completed) `ts` pair (issue #566's renderer-side fix), so a mis-stamped
// started line silently reports "time assembly", not "time execution".
//
// [Trait("requires", "docker")]: a genuinely-executing scenario reaches
// ScenarioRunner.RunScenarioOwningTopologyAsync's real-topology door and starts a REAL
// (zero-resource, since this scenario declares no `environment:`) DCP application —
// the same reasoning MixedSuiteEngineFaultTaxonomyTests' passing-sibling row and
// Sprint07CapstoneTests document; the trait is about the lane, not about a container
// actually being pulled (none is: no `environment:` block is declared).
//
// The sleep is 400ms — long enough to dominate the ~30ms archive-reconstruction
// bookkeeping this defect was measured to lose, short enough to keep this test cheap
// even though it is Docker-gated.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Proves the archive event buffer's <c>scenario-started</c> timestamp reflects the
/// scenario's real start — the fix for issue #566 (a JUnit/HTML rendered duration that
/// silently reported engine bookkeeping instead of the scenario's own wall-clock time).
/// </summary>
public sealed class ScenarioStartedTimestampAccuracyTests
{
    // The short name of this test assembly — the one whose AssemblyInfo carries
    // dcpclipath (CLAUDE.md §Aspire, R-1).
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(ScriptCsharpProvider).Assembly };

    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // No `environment:` block: the only resource this scenario needs is none — a
    // zero-resource topology still starts (see the file header), so the trait is
    // present, but nothing is pulled and nothing is health-gated.
    private const string SleepingScenarioYaml = """
        steps:
          - id: sleep-step
            type: script.csharp
            code: |
              await Task.Delay(TimeSpan.FromMilliseconds(400));
        """;

    /// <summary>
    /// Runs a scenario with a single <c>script.csharp</c> step that sleeps 400ms, then
    /// asserts that <c>scenario-completed.ts − scenario-started.ts</c> is at least the
    /// step's own <c>durationMs</c> in the ARCHIVE event buffer (the lines
    /// <c>FileReportWriter.WriteFileReports</c> — and therefore <c>--junit</c>/<c>--html</c>
    /// — render, not the separate <c>--events-stream</c> live-pump lines): the scenario
    /// cannot be recorded as having taken LESS wall-clock time than the one step inside it
    /// took.
    /// </summary>
    /// <remarks>
    /// Before the fix this failed: the archive scenario-started line was stamped at
    /// reconstruction time (after the 400ms sleep had already elapsed and the step had
    /// already completed), so the recorded delta was only the ~tens-of-milliseconds
    /// bookkeeping gap, not the 400ms the scenario actually spent.
    /// <para>
    /// <strong>Deliberately NOT also asserted here:</strong> "scenario-started.ts is at or
    /// before every step-started.ts". Pre-fix, the archive scenario-started line and every
    /// step-started line were stamped from the SAME <c>now9</c> instant, so that comparison
    /// was <c>≤</c> (equal) and passed on the UNFIXED tree too — it does not distinguish the
    /// defect from the fix and would only be a same-buffer internal-consistency check, which
    /// belongs with a claim to pin something, not appended to a test whose name says it
    /// proves the fix.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RunScenarioOwningTopologyAsync_ScriptSleeps400Ms_ArchiveStartedTimestampPredatesStepWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var output = new StringWriter();

        var result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
            registry: Registry,
            yamlText: SleepingScenarioYaml,
            scenarioName: "duration-archive-sleep",
            declaredTargets: ScenarioRunner.DeclaredTargetsOf(SleepingScenarioYaml, Registry),
            appHostAssemblyName: AppHostAssemblyName,
            output: output,
            seedBaseDirectory: null,
            livePump: null,
            cancellationToken: cts.Token);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.NotEmpty(result.Buffer);

        var scenarioStartedTs = SingleEventTimestamp(result.Buffer, "scenario-started");
        var scenarioCompletedTs = SingleEventTimestamp(result.Buffer, "scenario-completed");
        var stepDurationMs = SingleStepDurationMs(result.Buffer, "sleep-step");

        // The scenario's recorded wall-clock span must be AT LEAST the one step's own
        // recorded duration — it can never be recorded as shorter than the work it
        // contains. RED on the pre-fix tree: the archive started line was stamped after
        // the step had already run, so this delta collapses to ~tens of milliseconds
        // while stepDurationMs is ~400+.
        var scenarioDeltaMs = (scenarioCompletedTs - scenarioStartedTs).TotalMilliseconds;
        Assert.True(
            scenarioDeltaMs >= stepDurationMs,
            $"scenario-completed.ts - scenario-started.ts = {scenarioDeltaMs}ms, which is LESS than "
            + $"the step's own durationMs={stepDurationMs}ms — the archive scenario-started line was "
            + "stamped after the step already ran, discarding the time the scenario's own step took "
            + "(issue #566).");
    }

    /// <summary>
    /// Reads the <c>ts</c> field off the single archive line of the given event
    /// <paramref name="eventType"/>. Fails the test (rather than throwing an unhelpful
    /// <see cref="InvalidOperationException"/> from <c>Single()</c>) when the buffer
    /// carries zero or more than one such line, naming the buffer's own event-type
    /// tally so a failure here is diagnosable without re-running under a debugger.
    /// </summary>
    /// <remarks>
    /// Parses each line inside a <see langword="using"/> so every <see cref="JsonDocument"/>
    /// is disposed before the next line is parsed, rather than projecting a LINQ sequence of
    /// live, undisposed documents.
    /// </remarks>
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
    /// <paramref name="stepId"/>. Disposes each parsed <see cref="JsonDocument"/> before
    /// moving to the next line (see <see cref="SingleEventTimestamp"/>'s remarks).
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
}
