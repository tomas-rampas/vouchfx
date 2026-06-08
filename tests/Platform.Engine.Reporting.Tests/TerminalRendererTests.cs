// Tests for TerminalRenderer — the minimal terminal renderer stub (S02-G-02).
//
// Strategy: build recorded event streams from typed payload records via
// EventStreamJson.ToLine<T>, then render to a StringWriter and assert on the
// textual output.  Each test targets a specific tolerance requirement from the
// task specification.

using System.IO;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class TerminalRendererTests
{

    // -------------------------------------------------------------------------
    // Helper — build a minimal JSON line from a typed payload.
    // -------------------------------------------------------------------------

    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // -------------------------------------------------------------------------
    // Test 1: nominal rendering of a complete scenario recording.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_PrintsPerStepVerdicts()
    {
        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId     = "run-1",
                ScenarioId = "order-flow",
            }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-1",
                StepId     = "step-create-order",
                Verdict    = Verdict.Pass,
                DurationMs = 42,
            }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-1",
                StepId     = "step-cancel-order",
                Verdict    = Verdict.Fail,
                DurationMs = 99,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId      = "run-1",
                ScenarioId = "order-flow",
                Verdict    = Verdict.Fail,
                Counts     = new VerdictCounts { Pass = 1, Fail = 1, EnvError = 0, Inconclusive = 0 },
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Scenario header must appear.
        Assert.Contains("order-flow", output);

        // Both step IDs and their verdict tokens must be present.
        Assert.Contains("step-create-order", output);
        Assert.Contains("PASS", output);
        Assert.Contains("step-cancel-order", output);
        Assert.Contains("FAIL", output);

        // Scenario summary must include counts.
        Assert.Contains("pass=1", output);
        Assert.Contains("fail=1", output);
    }

    // -------------------------------------------------------------------------
    // Test 2: an unknown event type must be silently skipped; known events in
    //         the same stream must still render.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_UnknownEventType_DoesNotThrowAndIsIgnored()
    {
        // A hand-crafted line with an unknown type AND an unknown extra field.
        const string unknownLine =
            """{"v":1,"schemaVersion":"v1","type":"future-event-2099","ts":"2025-01-01T00:00:00Z","runId":"run-x","somethingNew":{"x":1}}""";

        var lines = new[]
        {
            unknownLine,
            Line(new StepCompletedEvent
            {
                RunId      = "run-x",
                StepId     = "step-known",
                Verdict    = Verdict.Pass,
                DurationMs = 10,
            }),
        };

        using var writer = new StringWriter();
        // Must not throw.
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        // Known event must still appear.
        Assert.Contains("step-known", output);
        Assert.Contains("PASS", output);
    }

    // -------------------------------------------------------------------------
    // Test 3: unknown fields on a known event type must not cause an error and
    //         the verdict must still be rendered.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_UnknownFieldsOnKnownEvent_AreTolerated()
    {
        // Inject an extra field "providerDetail" that the renderer does not know.
        const string lineWithExtraField =
            """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2025-01-01T00:00:00Z","runId":"run-y","stepId":"step-alpha","verdict":"PASS","durationMs":5,"providerDetail":{"k":"v"}}""";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(new[] { lineWithExtraField }, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        Assert.Contains("step-alpha", output);
        Assert.Contains("PASS", output);
    }

    // -------------------------------------------------------------------------
    // Test 4: a malformed (non-JSON) line must be skipped; the rest of the
    //         stream must continue rendering normally.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_MalformedJsonLine_IsSkippedNotThrown()
    {
        var lines = new[]
        {
            "{ this is not json",
            Line(new StepCompletedEvent
            {
                RunId      = "run-z",
                StepId     = "step-after-bad-line",
                Verdict    = Verdict.Pass,
                DurationMs = 1,
            }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        // The valid line after the malformed one must still render.
        Assert.Contains("step-after-bad-line", output);
        Assert.Contains("PASS", output);
    }

    // -------------------------------------------------------------------------
    // Test 5: blank / whitespace-only lines must be skipped without error.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_BlankLines_AreSkipped()
    {
        var lines = new[]
        {
            "",
            "   ",
            Line(new StepCompletedEvent
            {
                RunId      = "run-b",
                StepId     = "step-after-blanks",
                Verdict    = Verdict.Pass,
                DurationMs = 2,
            }),
            "",
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        Assert.Contains("step-after-blanks", output);
        Assert.Contains("PASS", output);
    }

    // -------------------------------------------------------------------------
    // Test 6: environment-error events render the resource name and error kind
    //         (Fix 6, S02-G-02).  The JSON is built by hand so the Reporting
    //         test project does not need a reference to Orchestration.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_EnvironmentErrorEvent_PrintsResourceNameAndErrorKind()
    {
        // Build the environment-error JSON line by hand — flat wire shape that
        // mirrors the EnvironmentErrorEvent record without referencing
        // Platform.Engine.Orchestration from this test project.
        const string envErrorLine =
            """{"v":1,"schemaVersion":"v1","type":"environment-error","ts":"2026-01-01T00:00:00Z","runId":"run-1","verdict":"ENV_ERROR","errorKind":"ImagePull","resourceName":"myapp","registryHost":"registry.example.com","authStatus":"unauthenticated","detail":"manifest not found"}""";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(new[] { envErrorLine }, writer));

        // Must not throw.
        Assert.Null(ex);

        var output = writer.ToString();

        // Resource name and error kind must appear in the rendered output.
        Assert.Contains("myapp", output, StringComparison.Ordinal);
        Assert.Contains("ImagePull", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EnvironmentErrorEvent_MissingOptionalFields_DoesNotThrow()
    {
        // Minimal environment-error line with only the mandatory fields present.
        // registryHost, authStatus, and detail are absent — the renderer must
        // tolerate missing optional fields via the defensive GetStr helper.
        const string minimalEnvErrorLine =
            """{"v":1,"schemaVersion":"v1","type":"environment-error","ts":"2026-01-01T00:00:00Z","runId":"run-2","verdict":"ENV_ERROR","errorKind":"HealthGate","resourceName":"appdb"}""";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(new[] { minimalEnvErrorLine }, writer));

        // Must not throw even when optional fields are absent.
        Assert.Null(ex);

        var output = writer.ToString();

        // Resource name and error kind must still appear.
        Assert.Contains("appdb", output, StringComparison.Ordinal);
        Assert.Contains("HealthGate", output, StringComparison.Ordinal);
    }
}
