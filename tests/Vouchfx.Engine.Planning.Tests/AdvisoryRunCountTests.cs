using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Planning.Ingest;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test naming convention.")]
public sealed class AdvisoryRunCountTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private const string Start = """{"type":"scenario-started","ts":"2026-01-02T00:00:00Z","runId":"run-1","scenarioId":"checkout-flow"}""";
    private const string Step = """{"type":"step-completed","ts":"2026-01-02T00:01:00Z","runId":"run-1","stepId":"create-order","verdict":"PASS","durationMs":50}""";
    private const string Completed = """{"type":"scenario-completed","ts":"2026-01-02T00:02:00Z","runId":"run-1","scenarioId":"checkout-flow","verdict":"PASS"}""";

    [Theory]
    [InlineData(EventTypes.TransportNotice)]
    [InlineData(EventTypes.SuiteStarted)]
    [InlineData(EventTypes.SuiteCompleted)]
    public void NonScenarioHistory_OnlyAdvisoryOrReservedSuiteEvents_CountsZero(string type)
    {
        using var historyFile = new HistoryFile(Notice(type, "shared", Started));
        var raw = EventHistoryReader.Read(historyFile.Path);
        var history = Correlate(raw);

        Assert.Equal(0, history.RunCount);
        Assert.Empty(history.Runs);
        Assert.Empty(history.StepObservations);
        Assert.Equal(0, history.UnmatchedObservationCount);
        Assert.Equal(0, history.SkippedLines);
        Assert.Equal(Started, history.FirstTimestamp);
        Assert.Equal(Started, history.LastTimestamp);
    }

    [Theory]
    [InlineData(EventTypes.TransportNotice, 1)]
    [InlineData(EventTypes.TransportNotice, 3)]
    [InlineData(EventTypes.SuiteStarted, 1)]
    [InlineData(EventTypes.SuiteStarted, 3)]
    [InlineData(EventTypes.SuiteCompleted, 1)]
    [InlineData(EventTypes.SuiteCompleted, 3)]
    public void SharedAndScenarioScopedAdvice_InterleavedWithScenario_PreservesItsCountAndAttribution(string type, int sharedCount)
    {
        var lines = new List<string> { Start, Notice(type, "run-1", Started.AddSeconds(5)) };
        for (var i = 0; i < sharedCount; i++)
        {
            lines.Add(Notice(type, $"shared-{i}", Started.AddDays(-1)));
        }

        lines.Add(Step);
        lines.Add(Notice(type, "shared-0", Started.AddDays(1)));
        lines.Add(Completed);
        using var historyFile = new HistoryFile(lines.ToArray());
        var history = Correlate(EventHistoryReader.Read(historyFile.Path));

        Assert.Equal(1, history.RunCount);
        Assert.Equal("run-1", Assert.Single(history.Runs).RunId);
        Assert.Equal("run-1", Assert.Single(history.RunIdsByScenarioId["checkout-flow"]));
        var step = Assert.Single(history.StepObservations[("checkout-flow", "create-order")]);
        Assert.Equal("run-1", step.RunId);
        Assert.Equal(Started.AddMinutes(1), step.Timestamp);
        Assert.Equal(0, history.UnmatchedObservationCount);
        Assert.Equal(0, history.SkippedLines);
        Assert.Equal(Started.AddDays(-1), history.FirstTimestamp);
        Assert.Equal(Started.AddDays(1), history.LastTimestamp);
    }

    [Theory]
    [InlineData(EventTypes.TransportNotice, 1)]
    [InlineData(EventTypes.TransportNotice, 3)]
    [InlineData(EventTypes.SuiteStarted, 1)]
    [InlineData(EventTypes.SuiteStarted, 3)]
    [InlineData(EventTypes.SuiteCompleted, 1)]
    [InlineData(EventTypes.SuiteCompleted, 3)]
    public void PlanExport_SharedAdviceWithinExistingTimes_ProducesIdenticalReport(string type, int count)
    {
        var suiteRoot = PlannerTestFixtures.FixtureRoot("ingest/basic-suites");
        using var baseline = new HistoryFile(Start, Step, Completed);
        var lines = new List<string> { Start, Step, Completed };
        for (var i = 0; i < count; i++)
        {
            lines.Insert(1, Notice(type, $"shared-{i}", Started.AddSeconds(30)));
        }

        using var advised = new HistoryFile(lines.ToArray());
        var expected = PlannerTestFixtures.Plan(suiteRoot, baseline.Path);
        var actual = PlannerTestFixtures.Plan(suiteRoot, advised.Path);

        Assert.Equal(1, actual.Inventory.RunCount);
        Assert.Equal(PlanExport.SerializePlan(expected), PlanExport.SerializePlan(actual));
    }

    [Fact]
    public void UnknownAndCaseVariantTypes_KeepGenericEnvelopeAcceptanceAndOrdinalRunIdentity()
    {
        using var historyFile = new HistoryFile(
            Notice("future-event", "Run", Started),
            Notice("future-event", "run", Started.AddSeconds(1)),
            Notice("TRANSPORT-NOTICE", "other", Started.AddSeconds(2)),
            Notice("SUITE-STARTED", "fourth", Started.AddSeconds(3)),
            Notice("SUITE-COMPLETED", "fifth", Started.AddSeconds(4)),
            Notice("future-event", "Run", Started.AddSeconds(5)));
        var history = Correlate(EventHistoryReader.Read(historyFile.Path));

        Assert.Equal(5, history.RunCount);
        Assert.Equal(0, history.SkippedLines);
        Assert.Empty(history.Runs);
        Assert.Empty(history.StepObservations);
        Assert.Equal(Started, history.FirstTimestamp);
        Assert.Equal(Started.AddSeconds(5), history.LastTimestamp);
    }

    [Fact]
    public void MalformedLinesAndTypedScenarioPayloads_KeepPriorSkipAndTimestampRules()
    {
        using var historyFile = new HistoryFile(
            Start, Step,
            "not json", "null",
            """{"type":"future-event","runId":"missing-ts"}""",
            """{"type":"transport-notice","ts":"2026-01-05T00:00:00Z","runId":null}""",
            """{"type":"scenario-started","ts":"2026-01-05T00:00:00Z","runId":"bad-scenario","scenarioId":null}""",
            """{"type":"step-completed","ts":"2026-01-05T00:00:00Z","runId":"bad-step","stepId":"create-order","verdict":"INVALID"}""",
            "");
        var history = Correlate(EventHistoryReader.Read(historyFile.Path));

        Assert.Equal(1, history.RunCount);
        Assert.Equal(6, history.SkippedLines);
        Assert.Single(history.Runs);
        Assert.Single(history.StepObservations[("checkout-flow", "create-order")]);
        Assert.Equal(Started, history.FirstTimestamp);
        Assert.Equal(Started.AddMinutes(1), history.LastTimestamp);
    }

    [Fact]
    public void AdvisoryPayload_IsStillReadAsGenericEnvelopeWithoutAddingTypedValidation()
    {
        using var historyFile = new HistoryFile(
            """{"type":"transport-notice","ts":"2026-01-02T00:00:00Z","runId":"shared","kind":42}""",
            Notice("future-event", "future", Started.AddSeconds(1)));
        var history = Correlate(EventHistoryReader.Read(historyFile.Path));

        Assert.Equal(1, history.RunCount);
        Assert.Equal(0, history.SkippedLines);
        Assert.Equal(Started, history.FirstTimestamp);
        Assert.Equal(Started.AddSeconds(1), history.LastTimestamp);
    }

    [Fact]
    public void StepOnlyHistory_RemainsOneRunAndOneUnmatchedObservation()
    {
        using var historyFile = new HistoryFile(Step, Notice(EventTypes.TransportNotice, "shared", Started));
        var history = Correlate(EventHistoryReader.Read(historyFile.Path));

        Assert.Equal(1, history.RunCount);
        Assert.Equal(1, history.UnmatchedObservationCount);
        Assert.Empty(history.StepObservations);
    }

    [Fact]
    public void RepeatedAdviceAcrossFilesAndInterleavedScenarioIds_DoesNotCollapseOrdinalRuns()
    {
        using var first = new HistoryFile(Start, Notice(EventTypes.TransportNotice, "shared", Started), Step);
        File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(first.Path)!, "second.jsonl"), new[]
        {
            Start.Replace("run-1", "RUN-1", StringComparison.Ordinal),
            Notice(EventTypes.TransportNotice, "shared", Started),
            Step.Replace("run-1", "RUN-1", StringComparison.Ordinal),
            Notice(EventTypes.SuiteCompleted, "suite-only", Started.AddDays(1)),
        });
        var history = Correlate(EventHistoryReader.Read(System.IO.Path.GetDirectoryName(first.Path)));

        Assert.Equal(2, history.RunCount);
        Assert.Equal(2, history.Runs.Count);
        Assert.Equal(2, history.RunIdsByScenarioId["checkout-flow"].Count);
        Assert.Equal(2, history.StepObservations[("checkout-flow", "create-order")].Count);
        Assert.Equal(0, history.SkippedLines);
        Assert.Equal(Started.AddDays(1), history.LastTimestamp);
    }

    private static string Notice(string type, string runId, DateTimeOffset timestamp) =>
        type == EventTypes.TransportNotice
            ? EventStreamJson.ToLine(new TransportNoticeEvent
            {
                RunId = runId,
                Timestamp = timestamp,
                Kind = TransportNoticeKinds.NoEngineTrust,
                Service = "api",
                SelectedEndpoint = "https",
            })
            : JsonSerializer.Serialize(new { type, runId, ts = timestamp });

    private static PlanEventHistory Correlate(EventHistoryReader.RawEventHistory raw)
    {
        var suites = SuiteSetLoader.Load(PlannerTestFixtures.FixtureRoot("ingest/basic-suites"), PlannerTestFixtures.BuildCoreRegistry());
        return RunCorrelator.Correlate(suites.Suites, suites.AnalysedRoot, raw);
    }

    private sealed class HistoryFile : IDisposable
    {
        public HistoryFile(params string[] lines)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vouchfx-plan-advice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "history.jsonl");
            File.WriteAllLines(Path, lines);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
    }
}
