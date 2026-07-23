// Tests for LiveStepEventSink (issue #262). NO DOCKER — builds a scenario AST via the normal
// parse/build pipeline (no topology, no Roslyn compile), constructs the sink directly with a
// collecting LiveEventPump, and drives its three IStepEventSink members by hand.
//
// The "parity" angle: every assertion compares the sink's live-posted line against
// StepEventBuilder's own direct output for the SAME inputs (modulo the `ts` field) — proving
// the live path and the (separately tested) reconstruction path share the identical
// construction, which is the whole point of extracting StepEventBuilder.
using System.Text.Json.Nodes;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class LiveStepEventSinkTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    private const string Yaml = """
        steps:
          - id: fetch
            type: http.rest
            target: whoami
            method: GET
            path: /
            expect:
              status: 200
            capture:
              hostname: "$.hostname"
          - id: verify
            type: http.rest
            target: whoami
            method: GET
            path: /health
            expect:
              status: 200
        """;

    private static ScenarioAst BuildAst()
    {
        var doc = YamlDocumentParser.Parse(Yaml);
        return AstBuilder.Build(doc, Registry);
    }

    private static (LiveStepEventSink Sink, LiveEventPump Pump, List<string> Collected) MakeSink(ScenarioAst ast, string runId = "run-1")
    {
        var collected = new List<string>();
        var pump = new LiveEventPump(batch => collected.AddRange(batch), capacity: 64);
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);
        var sink = new LiveStepEventSink(pump, runId, ast.Steps, captureOriginMap, NullSecretAccessor.Instance);
        return (sink, pump, collected);
    }

    /// <summary>
    /// Removes the `ts` field so two lines can be compared modulo timestamp, and normalises
    /// through <see cref="JsonNode"/> so property re-serialisation is byte-stable.
    /// </summary>
    private static string NormaliseModuloTs(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("ts");
        return node.ToJsonString();
    }

    [Fact]
    public async Task OnStepStarted_KnownStep_PostsExactlyOneLine_MatchingStepEventBuilder()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepStarted("fetch");
        await pump.DisposeAsync();

        Assert.Single(collected);

        var node = ast.Steps.Single(s => s.Id == "fetch");
        var expected = StepEventBuilder.StepStartedLine("run-1", DateTimeOffset.UtcNow, node);

        Assert.Equal(NormaliseModuloTs(expected), NormaliseModuloTs(collected[0]));
        Assert.Contains("\"stepId\":\"fetch\"", collected[0]);
        Assert.Contains("\"kind\":\"http.rest\"", collected[0]);
        Assert.Contains("\"verifyMode\":\"IMMEDIATE\"", collected[0]);
    }

    [Fact]
    public async Task OnStepStarted_UnrecognisedStepId_IsANoOp_NeverThrows()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepStarted("does-not-exist");
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    /// <summary>
    /// Copilot review (issue #262): these hooks are documented never-throw, and a
    /// <c>script.csharp</c> body could call <see cref="LiveStepEventSink.OnStepStarted"/> with a
    /// <see langword="null"/> or empty stepId — <c>Dictionary&lt;TKey,TValue&gt;.TryGetValue</c>
    /// throws <see cref="ArgumentNullException"/> on a null key, so the guard must run BEFORE
    /// the step-id lookup, never after.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OnStepStarted_NullOrEmptyStepId_IsANoOp_NeverThrows(string? stepId)
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepStarted(stepId!);
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    [Fact]
    public async Task OnStepAttempt_PostsExactlyOneLine_MatchingStepEventBuilder()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        var record = new AttemptRecord(2, 15L, Verdict.Fail, "{\"matched\":false}");
        sink.OnStepAttempt("fetch", record);
        await pump.DisposeAsync();

        Assert.Single(collected);

        var expected = StepEventBuilder.StepAttemptLine(
            "run-1", DateTimeOffset.UtcNow, "fetch", record, NullSecretAccessor.Instance);

        Assert.Equal(NormaliseModuloTs(expected), NormaliseModuloTs(collected[0]));
        Assert.Contains("\"attempt\":2", collected[0]);
        Assert.Contains("\"outcome\":\"FAIL\"", collected[0]);
    }

    /// <summary>
    /// Security hardening: <see cref="LiveStepEventSink.OnStepAttempt"/> must reject an
    /// unrecognised step id exactly like its <c>OnStepStarted</c> / <c>OnStepCompleted</c>
    /// siblings — a <c>script.csharp</c> body could otherwise call it directly with an
    /// arbitrary stepId and forge step-attempt lines into the live stream.
    /// </summary>
    [Fact]
    public async Task OnStepAttempt_UnrecognisedStepId_IsANoOp_NeverThrows()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepAttempt("does-not-exist", new AttemptRecord(1, 5L, Verdict.Pass, null));
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    /// <summary>Copilot review (issue #262): see <see cref="OnStepStarted_NullOrEmptyStepId_IsANoOp_NeverThrows"/>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OnStepAttempt_NullOrEmptyStepId_IsANoOp_NeverThrows(string? stepId)
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepAttempt(stepId!, new AttemptRecord(1, 5L, Verdict.Pass, null));
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    [Fact]
    public async Task OnStepCompleted_WithCaptureAndOutcome_MatchesStepEventBuilder()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        var outcome = new StepOutcome(Verdict.Pass, 42L, "{\"status\":200}");
        sink.OnStepCompleted("fetch", outcome, captureStatus: "1");
        await pump.DisposeAsync();

        Assert.Single(collected);

        var node = ast.Steps.Single(s => s.Id == "fetch");
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);
        var expected = StepEventBuilder.StepCompletedLine(
            "run-1", DateTimeOffset.UtcNow, node, outcome, "1", captureOriginMap, NullSecretAccessor.Instance);

        Assert.Equal(NormaliseModuloTs(expected), NormaliseModuloTs(collected[0]));
        Assert.Contains("\"verdict\":\"PASS\"", collected[0]);
        Assert.Contains("\"durationMs\":42", collected[0]);
        Assert.Contains("\"name\":\"hostname\"", collected[0]);
        Assert.Contains("\"matched\":true", collected[0]);
    }

    [Fact]
    public async Task OnStepCompleted_NullOutcome_DegradesToInconclusiveZeroDuration()
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepCompleted("verify", outcome: null, captureStatus: null);
        await pump.DisposeAsync();

        Assert.Single(collected);
        Assert.Contains("\"verdict\":\"INCONCLUSIVE\"", collected[0]);
        Assert.Contains("\"durationMs\":0", collected[0]);
    }

    /// <summary>Copilot review (issue #262): see <see cref="OnStepStarted_NullOrEmptyStepId_IsANoOp_NeverThrows"/>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OnStepCompleted_NullOrEmptyStepId_IsANoOp_NeverThrows(string? stepId)
    {
        var ast = BuildAst();
        var (sink, pump, collected) = MakeSink(ast);

        sink.OnStepCompleted(stepId!, new StepOutcome(Verdict.Pass, 1L, null), captureStatus: null);
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    [Fact]
    public async Task Constructor_RejectsNullArguments()
    {
        var ast = BuildAst();
        var pump = new LiveEventPump(_ => { }, capacity: 8);
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);

        Assert.Throws<ArgumentNullException>(() =>
            new LiveStepEventSink(null!, "run-1", ast.Steps, captureOriginMap, NullSecretAccessor.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new LiveStepEventSink(pump, null!, ast.Steps, captureOriginMap, NullSecretAccessor.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new LiveStepEventSink(pump, "run-1", null!, captureOriginMap, NullSecretAccessor.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new LiveStepEventSink(pump, "run-1", ast.Steps, null!, NullSecretAccessor.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new LiveStepEventSink(pump, "run-1", ast.Steps, captureOriginMap, null!));

        await pump.DisposeAsync();
    }
}
