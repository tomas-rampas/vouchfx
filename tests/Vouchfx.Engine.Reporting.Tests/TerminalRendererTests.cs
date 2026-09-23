// Tests for TerminalRenderer — the minimal terminal renderer stub (S02-G-02).
//
// Strategy: build recorded event streams from typed payload records via
// EventStreamJson.ToLine<T>, then render to a StringWriter and assert on the
// textual output.  Each test targets a specific tolerance requirement from the
// task specification.

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

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
        // Vouchfx.Engine.Orchestration from this test project.
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

    // -------------------------------------------------------------------------
    // Issue #266, Item 4: an EnvironmentError event's resourceName/registryHost/detail
    // fields can ultimately reflect untrusted content (e.g. an author-declared dependency
    // or image name); an embedded control character / ANSI escape sequence must be
    // rendered inert rather than corrupting/spoofing the terminal. The JSON \u001b escape
    // decodes to a raw ESC byte in the DESERIALISED string value (GetStr's input) — this
    // is standard JSON syntax, not a hostile-input trick in itself; the point under test
    // is that GetStr's DisplaySanitiser.SanitiseForDisplay call neutralises it before the
    // renderer ever writes it out.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_EnvironmentErrorEvent_HostileFieldsWithControlSequence_AreRenderedInert()
    {
        const string envErrorLine =
            """{"v":1,"schemaVersion":"v1","type":"environment-error","ts":"2026-01-01T00:00:00Z","runId":"run-3","verdict":"ENV_ERROR","errorKind":"ImagePull","resourceName":"evil\u001b[31mresource","registryHost":"evil\u001b]0;pwned\u0007host","authStatus":"unauthenticated","detail":"evil\u001b[2J\u001b[Hdetail"}""";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(new[] { envErrorLine }, writer));

        Assert.Null(ex);

        var output = writer.ToString();

        // The surrounding text survives sanitisation intact…
        Assert.Contains("evilresource", output, StringComparison.Ordinal);
        Assert.Contains("evilhost", output, StringComparison.Ordinal);
        Assert.Contains("evildetail", output, StringComparison.Ordinal);
        // …but no raw ESC byte reaches the rendered output.
        Assert.DoesNotContain((char)0x1B, output);
    }

    // -------------------------------------------------------------------------
    // Test 8 (S03-G-01): step-completed must include the duration in ms.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_StepCompleted_PrintsVerdictAndDuration()
    {
        // A single step-completed event with a known duration.
        var lines = new[]
        {
            Line(new StepCompletedEvent
            {
                RunId      = "run-d1",
                StepId     = "ping",
                Verdict    = Verdict.Pass,
                DurationMs = 42,
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Step id, verdict token, duration value, and the unit suffix must all appear.
        Assert.Contains("ping", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.Contains("42", output, StringComparison.Ordinal);
        Assert.Contains(" ms", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 9 (S03-G-01): a step-completed line missing durationMs must not throw,
    //                    and must still render the verdict.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_StepCompleted_MissingDuration_DoesNotThrow()
    {
        // Hand-crafted line that omits durationMs entirely — the renderer must
        // fall back gracefully rather than propagating a missing-key error.
        const string noDurationLine =
            """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:00:00Z","runId":"run-d2","stepId":"no-dur-step","verdict":"FAIL"}""";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(new[] { noDurationLine }, writer));

        // Must not throw.
        Assert.Null(ex);

        var output = writer.ToString();

        // Verdict must still be present even without a duration field.
        Assert.Contains("no-dur-step", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 10 (S03-G-01 acceptance): a small recorded single-step suite must
    //         produce a legible verdict line that includes the step id, the
    //         verdict token, and the duration.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_SingleStepSuite_PrintsLegibleVerdictLine()
    {
        // Recorded stream: scenario-started → step-started → step-completed → scenario-completed.
        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId      = "run-g01",
                ScenarioId = "health-check",
            }),
            Line(new StepStartedEvent
            {
                RunId  = "run-g01",
                StepId = "http-get-health",
                Kind   = "http.rest",
            }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-g01",
                StepId     = "http-get-health",
                Verdict    = Verdict.Pass,
                DurationMs = 42,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId      = "run-g01",
                ScenarioId = "health-check",
                Verdict    = Verdict.Pass,
                Counts     = new VerdictCounts { Pass = 1, Fail = 0, EnvError = 0, Inconclusive = 0 },
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Scenario header present.
        Assert.Contains("health-check", output, StringComparison.Ordinal);

        // Step verdict line contains step id, verdict token, duration value, and unit.
        Assert.Contains("http-get-health", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.Contains("42", output, StringComparison.Ordinal);
        Assert.Contains(" ms", output, StringComparison.Ordinal);

        // Scenario summary present.
        Assert.Contains("pass=1", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Issue #569: scenario-completed's ` total=N ms` suffix, absent since the frozen
    // v1 wire contract never gave ScenarioCompletedEvent a durationMs field, is now
    // DERIVED from the scenario-started/scenario-completed timestamp gap — mirroring
    // the #566 fix already applied to JunitXmlRenderer and HtmlRenderer.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_ScenarioCompleted_DerivesTotalFromTimestampDelta()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(1234);

        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId      = "run-dur1",
                ScenarioId = "duration-scenario",
                Timestamp  = startedAt,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId      = "run-dur1",
                ScenarioId = "duration-scenario",
                Verdict    = Verdict.Pass,
                Counts     = new VerdictCounts { Pass = 1 },
                Timestamp  = completedAt,
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains(" total=1234 ms)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ScenarioCompleted_WireDurationMsTakesPrecedenceOverDerivedDelta()
    {
        // scenario-started at t0; scenario-completed 1234ms later would derive 1234,
        // but ScenarioCompletedEvent carries no durationMs field on the typed record
        // (frozen v1 contract), so the wire value is hand-written here — a wire
        // durationMs, were a future contract to add one, must win over the derivation.
        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId      = "run-dur2",
                ScenarioId = "wire-wins-scenario",
                Timestamp  = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }),
            """{"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-01-01T00:00:01.234Z","runId":"run-dur2","scenarioId":"wire-wins-scenario","verdict":"PASS","counts":{"pass":1,"fail":0,"envError":0,"inconclusive":0},"durationMs":42}""",
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains(" total=42 ms)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" total=1234 ms)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NegativeDurations_AreClampedToZero_NeverNegative()
    {
        // A hostile or malformed stream can carry a negative durationMs/tMs on any of
        // the three duration-bearing event types this renderer reads; none may ever
        // render as a negative value (issue #569).  Each clamp is asserted below by its
        // own exact rendered fragment (proving the value is "0", the positive claim),
        // rather than by a whole-output DoesNotContain("-") (the negative claim) — the
        // latter fails SAFE, not UNSAFE: a stray hyphen anywhere in fixed surrounding
        // text (a future step id, a future format change) would give a spurious FAILURE,
        // never a false PASS, so it proves nothing an exact Contains doesn't already.
        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId      = "runNeg",
                ScenarioId = "negativeScenario",
            }),
            Line(new StepStartedEvent
            {
                RunId      = "runNeg",
                StepId     = "negativeStep",
                Kind       = "http.rest",
                VerifyMode = "RETRY",
            }),
            Line(new StepAttemptEvent
            {
                RunId   = "runNeg",
                StepId  = "negativeStep",
                Attempt = 1,
                TMs     = -3,
                Outcome = Verdict.Inconclusive,
            }),
            Line(new StepCompletedEvent
            {
                RunId      = "runNeg",
                StepId     = "negativeStep",
                Verdict    = Verdict.Fail,
                DurationMs = -7,
            }),
            // ScenarioCompletedEvent has no typed DurationMs field (frozen v1
            // contract), so the negative wire value is hand-written.
            """{"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-01-01T00:00:00Z","runId":"runNeg","scenarioId":"negativeScenario","verdict":"FAIL","counts":{"pass":0,"fail":1,"envError":0,"inconclusive":0},"durationMs":-5}""",
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // step-attempt tMs=-3 clamps to an elapsed of 0.0s.
        Assert.Contains("t=  0.0s   attempt 1   INCONCLUSIVE", output, StringComparison.Ordinal);
        // step-completed durationMs=-7 clamps to " (0 ms)".
        Assert.Contains(" (0 ms)", output, StringComparison.Ordinal);
        // scenario-completed durationMs=-5 clamps to " total=0 ms)".
        Assert.Contains(" total=0 ms)", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Issue #569 (m1): the scenario-started recording rule keys UNCONDITIONALLY on
    // envelope.RunId and GetStr(envelope, "scenarioId") ?? "(unknown)" — no runId/
    // scenarioId presence guard — matching JunitXmlRenderer/HtmlRenderer's key shape
    // exactly.  These two tests pin the absence of any runId/scenarioId presence guard
    // on the recording (and the matching lookup): an empty runId, and a scenario-started/-completed
    // pair that both omit scenarioId entirely (so the key is the shared "(unknown)"
    // fallback).  Both must still derive the total from the timestamp delta.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_ScenarioStarted_EmptyRunId_StillDerivesTotal()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(1234);

        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId      = string.Empty,
                ScenarioId = "empty-run-scenario",
                Timestamp  = startedAt,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId      = string.Empty,
                ScenarioId = "empty-run-scenario",
                Verdict    = Verdict.Pass,
                Counts     = new VerdictCounts { Pass = 1 },
                Timestamp  = completedAt,
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains(" total=1234 ms)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ScenarioStarted_MissingScenarioId_StillDerivesTotal()
    {
        // scenarioId is `required` on the typed record, so a missing wire property is
        // simulated with hand-written JSON — both events omit it, so both resolve to
        // the shared "(unknown)" fallback key.
        var lines = new[]
        {
            """{"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-01-01T00:00:00Z","runId":"run-noscenid"}""",
            """{"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-01-01T00:00:01.234Z","runId":"run-noscenid","verdict":"PASS","counts":{"pass":1,"fail":0,"envError":0,"inconclusive":0}}""",
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains(" total=1234 ms)", output, StringComparison.Ordinal);
    }
}
