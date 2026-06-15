// Platform.Engine.Telemetry.Tests — aggregation correctness (S10-G-04).
//
// Proves the builder maps the buffered event stream to the allowlisted counts/timings:
// verdict counts (step + scenario), step family / provider counts, startup time and
// time-to-first-test.  All from a synthetic stream — no run, no container.

using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class TelemetryEventBuilderTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ComputesScenarioAndStepVerdictCounts_FamilyAndProviderCounts()
    {
        // Two scenarios across two runIds: scenario A passes (2 steps), scenario B fails
        // (1 fail step + 1 inconclusive step).  Step verdicts come from each scenario's
        // nested `counts`; scenario verdicts from each scenario-completed verdict.
        var lines = new List<string>
        {
            // Scenario A (run-a): http.rest + db-assert.postgres, both pass.
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(100), runId: "run-a"),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(110), runId: "run-a"),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(120), runId: "run-a"),
            SyntheticEvents.StepStarted("a2", "db-assert.postgres", T0.AddMilliseconds(130), runId: "run-a"),
            SyntheticEvents.StepCompleted("a2", Verdict.Pass, 6, T0.AddMilliseconds(140), runId: "run-a"),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 2 }, T0.AddMilliseconds(150), runId: "run-a"),

            // Scenario B (run-b): http.rest fail + mq-expect.kafka inconclusive.
            SyntheticEvents.ScenarioStarted("B", T0.AddMilliseconds(200), runId: "run-b"),
            SyntheticEvents.StepStarted("b1", "http.rest", T0.AddMilliseconds(210), runId: "run-b"),
            SyntheticEvents.StepCompleted("b1", Verdict.Fail, 7, T0.AddMilliseconds(220), runId: "run-b"),
            SyntheticEvents.StepStarted("b2", "mq-expect.kafka", T0.AddMilliseconds(230), runId: "run-b"),
            SyntheticEvents.StepCompleted("b2", Verdict.Inconclusive, 8, T0.AddMilliseconds(240), runId: "run-b"),
            SyntheticEvents.ScenarioCompleted(
                "B", Verdict.Fail, new VerdictCounts { Fail = 1, Inconclusive = 1 },
                T0.AddMilliseconds(250), runId: "run-b"),
        };

        var ev = Build(lines);

        // Scenario count + scenario verdicts.
        Assert.Equal(2, ev.ScenarioCount);
        Assert.Equal(1, ev.ScenarioVerdicts.Pass);
        Assert.Equal(1, ev.ScenarioVerdicts.Fail);
        Assert.Equal(0, ev.ScenarioVerdicts.EnvError);
        Assert.Equal(0, ev.ScenarioVerdicts.Inconclusive);

        // Step verdicts (summed from each scenario's nested counts).
        Assert.Equal(2, ev.StepVerdicts.Pass);
        Assert.Equal(1, ev.StepVerdicts.Fail);
        Assert.Equal(0, ev.StepVerdicts.EnvError);
        Assert.Equal(1, ev.StepVerdicts.Inconclusive);

        // Family counts (before the first '.').
        Assert.Equal(2, ev.StepFamilies["http"]);
        Assert.Equal(1, ev.StepFamilies["db-assert"]);
        Assert.Equal(1, ev.StepFamilies["mq-expect"]);

        // Provider counts (full family.provider).
        Assert.Equal(2, ev.StepProviders["http.rest"]);
        Assert.Equal(1, ev.StepProviders["db-assert.postgres"]);
        Assert.Equal(1, ev.StepProviders["mq-expect.kafka"]);

        // run count is always 1; versions carried through.
        Assert.Equal(1, ev.RunCount);
        Assert.Equal(TelemetryEventBuilder.CurrentSchemaVersion, ev.SchemaVersion);
    }

    [Fact]
    public void Build_ComputesStartupAndTimeToFirstTest_FromTimestamps()
    {
        // Earliest event is at +0ms; first scenario-started at +40ms; first step-completed
        // at +90ms.  startupMs = 40, timeToFirstTestMs = 90.
        var lines = new List<string>
        {
            // An earlier event (a step-started before scenario-started can't happen, but a
            // probe at +0 establishes the run-start anchor as the earliest event).
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(40)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(50)),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(90)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(100)),
        };

        var ev = Build(lines);

        // Earliest event is the scenario-started at +40, so startup (anchor→first
        // scenario-started) is 0; time-to-first-test (anchor→first step-completed) is 50.
        Assert.Equal(0, ev.StartupMs);
        Assert.Equal(50, ev.TimeToFirstTestMs);
    }

    [Fact]
    public void Build_WithEarlierAnchorEvent_ComputesPositiveStartup()
    {
        // Seed an earlier scenario-started (run anchor) at +0, a LATER scenario-started at
        // +40 — the EARLIEST scenario-started is the anchor, so this models a multi-scenario
        // run where startup is measured to the first scenario.  To exercise a positive
        // startup we put a non-scenario event earliest.
        var lines = new List<string>
        {
            // step-attempt at +0 → earliest event (run-start anchor), not a scenario-started.
            EventStreamJson.ToLine(new StepAttemptEvent
            {
                RunId = "run0000000000000000000000000000",
                Timestamp = T0,
                StepId = "warmup",
                Attempt = 1,
                TMs = 1,
            }),
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(40)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(50)),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(90)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(100)),
        };

        var ev = Build(lines);

        Assert.Equal(40, ev.StartupMs);
        Assert.Equal(90, ev.TimeToFirstTestMs);
    }

    [Fact]
    public void Build_EmptyStream_ProducesZeroedAllowlistedEvent()
    {
        var ev = Build(new List<string>());

        Assert.Equal(0, ev.ScenarioCount);
        Assert.Empty(ev.StepFamilies);
        Assert.Empty(ev.StepProviders);
        Assert.Equal(0, ev.StartupMs);
        Assert.Equal(0, ev.TimeToFirstTestMs);
        Assert.Equal(0, ev.StepVerdicts.Pass);
        Assert.Equal(0, ev.ScenarioVerdicts.Pass);
        Assert.Equal(1, ev.RunCount);
    }

    [Fact]
    public void Build_SkipsUnparseableLines_WithoutThrowing()
    {
        var lines = new List<string>
        {
            "this is not json",
            string.Empty,
            "   ",
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(10)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(20)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(30)),
        };

        var ev = Build(lines);

        Assert.Equal(1, ev.ScenarioCount);
        Assert.Equal(1, ev.StepProviders["http.rest"]);
    }

    [Fact]
    public void Build_StepKindWithoutDot_TreatsKindAsItsOwnFamily()
    {
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.StepStarted("a1", "script", T0.AddMilliseconds(10)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(20)),
        };

        var ev = Build(lines);

        Assert.Equal(1, ev.StepFamilies["script"]);
        Assert.Equal(1, ev.StepProviders["script"]);
    }

    private static TelemetryEvent Build(IReadOnlyList<string> lines) =>
        TelemetryEventBuilder.Build(
            lines,
            installId: Guid.NewGuid(),
            toolVersion: "1.2.3",
            engineVersion: "1.2.3",
            dotnetVersion: ".NET 8.0.7",
            timestamp: T0.AddSeconds(5));
}
