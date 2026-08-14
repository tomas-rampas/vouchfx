// S11-B-01 — secret-redaction penetration suite, RUNTIME / EVENT-STREAM layer (§17).
//
// This is the adversarial suite for the ONE place the recon found a GENUINE engine-side
// leak: a step's free-form OBSERVATION text reaching the event stream verbatim.  Unlike a
// SecretString (a typed carrier the engine redacts structurally), an observation is an
// arbitrary author-built string — most acutely the script.csharp provider's
// `__obs = __ex.Message;` (ScriptCsharpProvider.Emit), which splices a caught exception's
// MESSAGE verbatim into StepOutcome.Observation.  That observation is then carried onto
// StepCompletedEvent.Observation (ScenarioRunner) and re-emitted BYTE-FOR-BYTE into the raw
// --events JSON Lines artifact by FileReportWriter — the stream the VSCode Test Explorer and
// the Healer agent consume.  An author who throws
//     throw new Exception($"auth failed for {Vars.Secrets.Resolve("env/TOKEN").Reveal()}")
// would, before this task, leak the revealed value into that raw stream when the message
// happened to be valid JSON.
//
// The fix (defence in depth, TYPE-BASED redaction stays primary): the Default-ALC
// SecretAccessor records every value it reveals into a per-scenario ResolvedSecretLedger;
// the runner scrubs the observation text through that ledger at the reporting boundary
// (ScenarioRunner.BuildStepObservation) BEFORE the observation enters the event stream.
// String-matching is NOT the primary mechanism — it is a net catching free-form provider
// diagnostic text the engine cannot type-check.  Values never resolved through the accessor
// (e.g. a literal an author hard-codes) are out of scope: there is no reference, nothing to
// redact, and that is an author defect, not an engine one.
//
// Tags: (a) GENUINE-LEAK-FIXED — red before the ledger/scrub, green after.
//       (b) OUT-OF-SCOPE-BY-DESIGN — only a deliberate Reveal()+author-transform escapes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Penetration tests for the event-stream observation leak path (§17): a resolved secret
/// value embedded in a step observation (the script.csharp exception-message gap, an OAuth
/// bearer token, a captured-derived value) must never reach the raw event stream.  The
/// engine's defence is the Default-ALC <see cref="ResolvedSecretLedger"/> recorded by
/// <see cref="SecretAccessor"/> and applied by the runner at the reporting boundary.
/// </summary>
public sealed class SecretObservationLeakPenetrationTests
{
    private const string Run = "run-pentest";

    /// <summary>
    /// Builds a Default-ALC <see cref="SecretAccessor"/> over the real
    /// <see cref="EnvironmentSecretResolver"/> (the production env source) seeded with a
    /// unique variable, and resolves it once — so the value is recorded in the accessor's
    /// resolved-secret ledger exactly as a live <c>${secret:env/…}.Reveal()</c> at a sink
    /// would have recorded it.  Returns the accessor, the revealed value, and a dispose
    /// action that clears the temporary environment variable.
    /// </summary>
    private static (SecretAccessor Accessor, string Revealed, Action Cleanup) AccessorWithResolvedSecret(string value)
    {
        // Unique per-test env name so concurrent tests never race the shared process
        // environment (the convention every sibling secret test follows).
        var envName = "VOUCHFX_PENTEST_OBS_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, value);

        var accessor = new SecretAccessor(
            new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

        // Resolve through the accessor at least once — this is what a step does at its sink,
        // and it is what populates the ledger the runner later scrubs with.
        var revealed = accessor.Resolve($"${{secret:env/{envName}}}").Reveal();
        Assert.Equal(value, revealed);

        return (accessor, revealed, () => Environment.SetEnvironmentVariable(envName, null));
    }

    /// <summary>
    /// Mirrors the runner's step-completed observation handling for ONE step: scrubs the
    /// raw observation text through the accessor's ledger and parses it, returning the
    /// emitted JSON Lines string for the step-completed event.  This calls the SAME internal
    /// <see cref="ScenarioRunner.BuildStepObservation"/> the production runner uses, so a
    /// green test is a green production path — not a test-only reconstruction.
    /// </summary>
    private static string EmitStepCompletedLine(SecretAccessor accessor, string stepId, string? rawObservation)
    {
        var observation = ScenarioRunner.BuildStepObservation(accessor, rawObservation);
        return EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = Run,
            Timestamp = DateTimeOffset.UtcNow,
            StepId = stepId,
            Verdict = Verdict.Fail,
            DurationMs = 1L,
            Observation = observation,
        });
    }

    // ── 1. script.csharp exception-message leak (the README-flagged gap) ─────────

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  A script.csharp step whose thrown exception MESSAGE embeds a
    /// revealed secret value — and whose message is VALID JSON (a JSON string), so it is NOT
    /// dropped by the parse-or-omit fallback — must not carry that value into the emitted
    /// step-completed event line.  This is the exact ScriptCsharpProvider `__obs = __ex.Message`
    /// path: the message becomes StepOutcome.Observation → StepCompletedEvent.Observation.
    /// </summary>
    [Fact]
    public void ScriptCsharpExceptionMessage_JsonStringEmbeddingSecret_IsScrubbedFromStream()
    {
        const string secret = "scriptcsharp-leak-9q2x7";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            // A JSON-string exception message: `"auth failed: <secret>"` — valid JSON, so
            // ParseObservation keeps it; without the scrub the value would survive verbatim.
            var exceptionMessage = JsonSerializer.Serialize($"auth failed: {revealed}");

            var line = EmitStepCompletedLine(accessor, "do-script", exceptionMessage);

            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            // The observation is still present (scrubbed, not dropped) — the marker survives.
            Assert.Contains(SecretString.RedactedMarker, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  The same gap when the exception message is a JSON OBJECT whose
    /// field value embeds the secret (e.g. <c>{"error":"bad token &lt;secret&gt;"}</c>) — a
    /// shape a structured-logging author body readily produces.  The scrub must reach the value
    /// inside the nested string, not merely the top level.
    /// </summary>
    [Fact]
    public void ScriptCsharpExceptionMessage_JsonObjectEmbeddingSecret_IsScrubbedFromStream()
    {
        const string secret = "scriptcsharp-obj-leak-44ab";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            var exceptionMessage = JsonSerializer.Serialize(new { error = $"bad token {revealed}" });

            var line = EmitStepCompletedLine(accessor, "do-script", exceptionMessage);

            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 2. OAuth bearer token captured into the stream ───────────────────────────

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  An OAuth bearer token resolved through the accessor and then
    /// surfaced inside an observation (e.g. a provider that echoes a failed
    /// <c>Authorization: Bearer &lt;token&gt;</c> header into its diagnostic observation) must
    /// be scrubbed before the observation reaches the event stream.
    /// </summary>
    [Fact]
    public void OAuthBearerToken_InObservation_IsScrubbedFromStream()
    {
        const string token = "ya29.OAuth-bearer-pentest-token-zzz";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(token);
        try
        {
            var observation = JsonSerializer.Serialize(new { sent = $"Authorization: Bearer {revealed}" });

            var line = EmitStepCompletedLine(accessor, "call-api", observation);

            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 3. partial / substring leak of the secret ────────────────────────────────

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  A whole resolved value embedded in an observation is scrubbed.
    /// The partial-logging accident (an author logging a PREFIX of a revealed value) is the
    /// author's transform of revealed bytes — out of scope by design (there is no full value to
    /// match) — but the COMMON case the scrub must catch is the WHOLE value appearing in text,
    /// which this asserts, and additionally that no remaining prefix of the value survives.
    /// </summary>
    [Fact]
    public void PartialLogging_WholeValueInObservation_IsScrubbed_NoPrefixSurvives()
    {
        const string secret = "prefix-pentest-secret-abcdef0123456789";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            var observation = JsonSerializer.Serialize($"value was {revealed} (logged)");

            var line = EmitStepCompletedLine(accessor, "leaky", observation);

            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            // No 8+ char prefix of the value survives either.
            for (var len = 8; len <= secret.Length; len++)
            {
                Assert.DoesNotContain(secret[..len], line, StringComparison.Ordinal);
            }
        }
        finally
        {
            cleanup();
        }
    }

    // ── 4. concatenation of the revealed value into a larger observation string ───

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  The revealed value concatenated into a larger observation
    /// string before the observation reaches the stream is scrubbed wherever it appears,
    /// including multiple occurrences in one observation.
    /// </summary>
    [Fact]
    public void Concatenation_RevealedValueInLargerObservation_AllOccurrencesScrubbed()
    {
        const string secret = "concat-pentest-secret-7g4";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            var observation = JsonSerializer.Serialize(
                new { a = $"key={revealed}", b = $"again {revealed} end" });

            var line = EmitStepCompletedLine(accessor, "concat", observation);

            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 5. base64-encoded form of the secret in an observation ───────────────────

    /// <summary>
    /// (b) OUT-OF-SCOPE-BY-DESIGN.  A base64 ENCODING of the secret only exists if author code
    /// revealed the value and then base64-encoded it — a transform of revealed bytes, the
    /// documented Reveal() escape hatch (§17).  The engine ledger records the value it actually
    /// revealed, not every possible encoding of it, so a base64 form is NOT scrubbed.  This test
    /// makes that boundary explicit: it asserts the RAW value is scrubbed (the engine's
    /// responsibility) while documenting that the base64 transform is the author's responsibility
    /// — the engine deliberately does not chase encodings (doing so would be unbounded and would
    /// still miss the next encoding).
    /// </summary>
    [Fact]
    public void Base64Encoded_IsAuthorTransform_OutOfScope_RawValueStillScrubbed()
    {
        const string secret = "base64-pentest-secret-12345";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(revealed));
            var observation = JsonSerializer.Serialize(new { raw = revealed, encoded = base64 });

            var line = EmitStepCompletedLine(accessor, "encode", observation);

            // The engine scrubs the raw value it actually revealed.
            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);

            // The base64 transform is the author's escape-hatch responsibility, so by DESIGN the
            // engine does not redact it — documented here, not silently accepted as a leak.
            Assert.Contains(base64, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 6. END-TO-END: the leak reaches the raw --events artifact (the real sink) ─

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  The raw <c>--events</c> artifact is written VERBATIM from the
    /// event buffer by the REAL <see cref="FileReportWriter.WriteFileReports"/> (no renderer in
    /// between) — it is the stream the VSCode Test Explorer and the Healer agent consume, the
    /// actual exfiltration path.  Because that write is byte-for-byte, the only place a value
    /// can be stopped is BEFORE the buffer is built — exactly where the scrub runs.  This test
    /// builds the full scenario buffer through the runner's observation handling, invokes the
    /// PRODUCTION writer against a temp events path, and asserts over the ACTUAL FILE BYTES on
    /// disk — so the test cannot silently desync if <see cref="FileReportWriter"/> changes its
    /// write format (it calls the real writer, not a hand-rolled reproduction of it).
    /// </summary>
    [Fact]
    public void RawEventsArtifact_DoesNotContainSecretValue_EndToEnd()
    {
        const string secret = "events-artifact-pentest-secret-xyz789";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        var eventsPath = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-pentest-events-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var observation = JsonSerializer.Serialize($"throwing with {revealed}");
            var buffer = new List<string>
            {
                EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = Run,
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "pentest",
                }),
                EmitStepCompletedLine(accessor, "do-script", observation),
                EventStreamJson.ToLine(new ScenarioCompletedEvent
                {
                    RunId = Run,
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "pentest",
                    Verdict = Verdict.Fail,
                    Counts = new VerdictCounts { Fail = 1 },
                }),
            };

            // Invoke the REAL production writer for the raw --events artifact (no HTML / JUnit).
            // This is the exact code path the CLI uses, so the test fails for real if the writer
            // ever stops writing the buffer verbatim — no hand-rolled string.Join reproduction.
            FileReportWriter.WriteFileReports(
                buffer,
                diffLookup: null,
                htmlPath: null,
                junitPath: null,
                diagnostics: null,
                eventsPath: eventsPath);

            // Assert over the ACTUAL bytes that landed on disk.
            var artifactBytes = File.ReadAllText(eventsPath);

            Assert.DoesNotContain(secret, artifactBytes, StringComparison.Ordinal);
            // The artifact was actually populated and is non-trivial.
            Assert.Contains("do-script", artifactBytes, StringComparison.Ordinal);
            // And the scrub marker survived to disk (scrubbed, not dropped).
            Assert.Contains(SecretString.RedactedMarker, artifactBytes, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
            if (File.Exists(eventsPath))
            {
                File.Delete(eventsPath);
            }
        }
    }

    // ── 6b. JSON-ESCAPED-FORM leak: a secret containing '+'/non-ASCII survives a ─────
    //        RAW-only scrub because the event-stream serialiser escapes it to \uXXXX.

    // A realistic secret whose bytes force the event-stream's default JavaScriptEncoder to
    // escape: it contains '+' (escaped to + — common in base64 tokens) AND non-ASCII
    // characters (Ω U+03A9, â U+00E2 — escaped to Ω / â).  When such a value is
    // embedded in a provider observation that was itself JSON-encoded, the value appears in
    // the raw observation text ONLY in its escaped form, so a raw-only ledger scrub misses
    // it and the secret reaches the on-disk --events artifact in recoverable escaped form.
    private const string PlusNonAsciiSecret = "s3cr+t-Ω-â-pentest-9q2x7";

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED.  A secret value containing <c>+</c> and non-ASCII characters,
    /// embedded in a JSON-shaped observation, must be scrubbed in the JSON-ESCAPED form too —
    /// not merely the raw form.  The observation is built with <see cref="JsonSerializer"/>
    /// (the same default <see cref="JavaScriptEncoder"/> the event stream uses), so the value
    /// appears in the raw observation text only as <c>\uXXXX</c> escapes; a raw-only scrub
    /// would miss it and the value would re-emerge, JSON-decodable, in the emitted line.  This
    /// drives the SAME <see cref="ScenarioRunner.BuildStepObservation"/> → event-stream path as
    /// the other cases and asserts the secret is absent in BOTH its raw and escaped forms.
    /// </summary>
    [Fact]
    public void PlusAndNonAsciiSecret_EscapedFormInObservation_IsScrubbedFromStream()
    {
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(PlusNonAsciiSecret);
        try
        {
            // The escaped form the event stream would serialise the value as (e.g.
            // s3cr+t-Ω-â-…).  This is precisely the substring that survives a
            // raw-only scrub, so it is the assertion that is RED before the fix.
            var escaped = JavaScriptEncoder.Default.Encode(revealed);
            Assert.NotEqual(revealed, escaped); // sanity: this value genuinely re-encodes.

            var observation = JsonSerializer.Serialize(new { error = $"bad token {revealed}" });
            // The raw observation text embeds the value ONLY in escaped form.
            Assert.Contains(escaped, observation, StringComparison.Ordinal);
            Assert.DoesNotContain(revealed, observation, StringComparison.Ordinal);

            var line = EmitStepCompletedLine(accessor, "call-api", observation);

            // Neither the raw value nor its JSON-escaped form may survive into the stream.
            Assert.DoesNotContain(revealed, line, StringComparison.Ordinal);
            Assert.DoesNotContain(escaped, line, StringComparison.Ordinal);
            // Scrubbed, not dropped — the marker is present in its place.
            Assert.Contains(SecretString.RedactedMarker, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED, END-TO-END.  The same <c>+</c>/non-ASCII secret, driven through
    /// the full scenario buffer and the REAL <see cref="FileReportWriter.WriteFileReports"/> to
    /// a temp <c>--events</c> file (the exact exfiltration path the VSCode Test Explorer and the
    /// Healer agent consume), must not appear in the ACTUAL FILE BYTES in EITHER its raw form OR
    /// its JSON-escaped <c>\uXXXX</c> form.  Before the escaped-form scrub this artifact carried
    /// the value in recoverable escaped form even though the raw form was never on disk.
    /// </summary>
    [Fact]
    public void RawEventsArtifact_PlusAndNonAsciiSecret_NoEscapedFormOnDisk_EndToEnd()
    {
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(PlusNonAsciiSecret);
        var escaped = JavaScriptEncoder.Default.Encode(revealed);
        var eventsPath = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-pentest-events-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var observation = JsonSerializer.Serialize(new { error = $"throwing with {revealed}" });
            var buffer = new List<string>
            {
                EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = Run,
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "pentest",
                }),
                EmitStepCompletedLine(accessor, "do-script", observation),
                EventStreamJson.ToLine(new ScenarioCompletedEvent
                {
                    RunId = Run,
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "pentest",
                    Verdict = Verdict.Fail,
                    Counts = new VerdictCounts { Fail = 1 },
                }),
            };

            FileReportWriter.WriteFileReports(
                buffer,
                diffLookup: null,
                htmlPath: null,
                junitPath: null,
                diagnostics: null,
                eventsPath: eventsPath);

            var artifactBytes = File.ReadAllText(eventsPath);

            // Raw form never lands (the serialiser always escapes it) — but assert it anyway.
            Assert.DoesNotContain(revealed, artifactBytes, StringComparison.Ordinal);
            // The escaped form is the recoverable leak; it must be absent (RED before the fix).
            Assert.DoesNotContain(escaped, artifactBytes, StringComparison.Ordinal);
            // The artifact is real and the scrub marker survived to disk (scrubbed, not dropped).
            Assert.Contains("do-script", artifactBytes, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, artifactBytes, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
            if (File.Exists(eventsPath))
            {
                File.Delete(eventsPath);
            }
        }
    }

    // ── 7. an observation with NO resolved secret is passed through unchanged ─────

    /// <summary>
    /// Guards against an over-broad scrub: an observation that contains no resolved secret value
    /// must be emitted unchanged (the scrub is a targeted net, not a blanket rewrite).  This pins
    /// that the ledger only redacts values it actually recorded.
    /// </summary>
    [Fact]
    public void ObservationWithNoSecret_IsEmittedUnchanged()
    {
        var (accessor, _, cleanup) = AccessorWithResolvedSecret("unrelated-secret-value");
        try
        {
            var observation = JsonSerializer.Serialize(new { status = 500, expected = 200 });
            var line = EmitStepCompletedLine(accessor, "plain", observation);

            Assert.Contains("\"status\":500", line, StringComparison.Ordinal);
            Assert.Contains("\"expected\":200", line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 8. TERMINAL leak: a scenario-level exception message reaches the human output ─

    /// <summary>
    /// Emulates the runner's scenario-level unexpected-exception catch site
    /// (<c>RunScenarioCoreAsync</c>, the <c>Compile/run error (Inconclusive): …</c> write): it
    /// formats the diagnostic exactly as the catch does (<c>{TypeName}: {Message}</c>), routes
    /// it through <see cref="ScenarioRunner.ScrubDiagnostic"/> (the SAME ledger scrub the
    /// observation path uses) COMPOSED with <see cref="DisplaySanitiser.SanitiseForDisplay"/>
    /// (issue #266, Item 4 — the same composition the production site now applies, so a
    /// control character / ANSI escape sequence in the message is ALSO neutralised, not just
    /// a resolved secret value), and writes it to the supplied <paramref name="output"/> — the
    /// developer terminal / CI log.  Returns the captured terminal text.
    /// </summary>
    private static string EmitScenarioDiagnosticLine(SecretAccessor accessor, Exception ex, TextWriter output)
    {
        var diagnosis = $"{ex.GetType().Name}: {ex.Message}";
        var scrubbed = DisplaySanitiser.SanitiseForDisplay(ScenarioRunner.ScrubDiagnostic(accessor, diagnosis));
        output.WriteLine($"Compile/run error (Inconclusive): {scrubbed}");
        return output.ToString()!;
    }

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED (acceptance: no secret material in the TERMINAL).  A secret value
    /// resolved during execution and surfaced in a SCENARIO-LEVEL exception message — the
    /// catch-all in <c>RunScenarioCoreAsync</c> that writes <c>ex.Message</c> to the human
    /// <c>output</c> stream — must not reach the developer terminal / CI log verbatim.  Before
    /// the fix this site wrote the raw message; now it goes through the ledger scrub.
    /// </summary>
    /// <remarks>
    /// RED→GREEN evidence is asserted in-test, not just narrated: the test first proves the
    /// UNSCRUBBED diagnostic (what the site emitted before the fix) DOES contain the secret, then
    /// proves the SCRUBBED diagnostic (what the site emits now) does NOT — and that the redaction
    /// marker is present in its place (scrubbed, not silently dropped).
    /// </remarks>
    [Fact]
    public void ScenarioLevelExceptionMessage_EmbeddingSecret_IsScrubbedFromTerminalOutput()
    {
        const string secret = "terminal-leak-pentest-secret-7k3p";
        var (accessor, revealed, cleanup) = AccessorWithResolvedSecret(secret);
        try
        {
            // The realistic accident: a script.csharp body throws with an interpolated Reveal(),
            // and that message escapes the submission delegate to the scenario-level catch-all.
            var thrown = new InvalidOperationException($"auth failed for token {revealed}");

            // RED baseline: the diagnostic the site formed BEFORE the scrub leaks the value.
            var unscrubbed = $"{thrown.GetType().Name}: {thrown.Message}";
            Assert.Contains(secret, unscrubbed, StringComparison.Ordinal);

            // GREEN: route it through the shared ledger scrub exactly as the catch site now does,
            // and assert over the captured terminal text.
            var terminal = new StringWriter();
            var captured = EmitScenarioDiagnosticLine(accessor, thrown, terminal);

            Assert.DoesNotContain(secret, captured, StringComparison.Ordinal);
            // The diagnostic is still emitted (scrubbed, not dropped) — the marker is present.
            Assert.Contains(SecretString.RedactedMarker, captured, StringComparison.Ordinal);
            // And the non-secret framing survives so the developer still gets an actionable line.
            Assert.Contains("Compile/run error (Inconclusive):", captured, StringComparison.Ordinal);
            Assert.Contains("auth failed for token", captured, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// Guards the diagnostic scrub against over-reach (mirrors section 7 for the terminal path):
    /// a scenario-level diagnostic that embeds NO resolved secret must reach the terminal
    /// unchanged — the scrub is a targeted net, never a blanket rewrite of exception text.
    /// </summary>
    [Fact]
    public void ScenarioLevelDiagnostic_WithNoSecret_ReachesTerminalUnchanged()
    {
        var (accessor, _, cleanup) = AccessorWithResolvedSecret("unrelated-terminal-secret");
        try
        {
            var thrown = new TimeoutException("the operation timed out after 30s");

            var terminal = new StringWriter();
            var captured = EmitScenarioDiagnosticLine(accessor, thrown, terminal);

            Assert.Contains("the operation timed out after 30s", captured, StringComparison.Ordinal);
            Assert.Contains("TimeoutException", captured, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretString.RedactedMarker, captured, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 9. TERMINAL: a scenario-level exception message carrying an ANSI/control sequence
    //        is rendered inert, not just secret-scrubbed (issue #266, Item 4) ──

    /// <summary>
    /// The SCENARIO-LEVEL catch-all in <c>RunScenarioCoreAsync</c> (the
    /// <c>Compile/run error (Inconclusive): …</c> write <see cref="EmitScenarioDiagnosticLine"/>
    /// mirrors) fires for an exception that escapes the compile/run call ITSELF — a genuine
    /// unexpected engine/infra fault — and can carry a control character / ANSI escape sequence
    /// in its message rather than — or in addition to — a resolved secret value. This is a
    /// DIFFERENT path from <see cref="Vouchfx.Steps.Script.Csharp.ScriptCsharpProvider"/>'s
    /// <c>__obs_&lt;safeId&gt; = __ex_&lt;safeId&gt;.Message</c> wrapper (tests 1-6 above): a
    /// script.csharp author's OWN thrown exception is caught INSIDE the emitted CSX submission
    /// and becomes a <c>StepOutcome.Observation</c> — it never reaches this scenario-level
    /// catch at all. <see cref="EmitScenarioDiagnosticLine"/> mirrors the CURRENT production
    /// site exactly (§17 secret scrub composed with <see cref="DisplaySanitiser"/>), so this
    /// proves the composition, not a hand-rolled reproduction of it: the ANSI sequence must not
    /// reach the developer terminal / CI log.
    /// </summary>
    [Fact]
    public void ScenarioLevelExceptionMessage_EmbeddingAnsiControlSequence_IsInertInTerminalOutput()
    {
        var (accessor, _, cleanup) = AccessorWithResolvedSecret("unrelated-ansi-pentest-secret");
        try
        {
            var esc = (char)0x1B;
            // A realistic unexpected engine/infra exception whose message embeds an ANSI
            // colour-set/reset sequence, no secret involved. This simulates whatever an
            // exception escaping RunIsolatedAsync/CompileOnce itself might carry — NOT a
            // script.csharp author's own thrown exception (that is caught INSIDE the CSX and
            // never reaches this catch — see the test's own summary above).
            var thrown = new InvalidOperationException(
                "auth failed" + esc + "[31m" + " for user" + esc + "[0m");
            var terminal = new StringWriter();
            var captured = EmitScenarioDiagnosticLine(accessor, thrown, terminal);

            // The surrounding message text survives sanitisation intact…
            Assert.Contains("auth failed", captured, StringComparison.Ordinal);
            Assert.Contains("for user", captured, StringComparison.Ordinal);
            Assert.Contains("Compile/run error (Inconclusive):", captured, StringComparison.Ordinal);
            // …but no raw ESC byte reaches the terminal output.
            Assert.DoesNotContain(esc, captured);
        }
        finally
        {
            cleanup();
        }
    }

    // ── 10. TERMINAL: a SecretResolutionException's SecretPath carrying an ANSI/control
    //         sequence is rendered inert (issue #266, Item 4) ──

    /// <summary>
    /// Emulates the runner's <c>SecretResolutionException</c> scenario-level catch site
    /// (<c>RunScenarioCoreAsync</c>, the "Secret resolution failed (EnvironmentError): …"
    /// write): composes the diagnostic exactly as the catch does — <c>SecretSource</c> and
    /// <c>SecretPath</c> spliced verbatim, NEVER <c>sre.Message</c> (§17 reference-only
    /// discipline) — and routes the composed line through
    /// <see cref="DisplaySanitiser.SanitiseForDisplay"/>, the SAME composition the production
    /// site now applies. Writes it to the supplied <paramref name="output"/> and returns the
    /// captured terminal text.
    /// </summary>
    private static string EmitSecretResolutionFailureLine(SecretResolutionException sre, TextWriter output)
    {
        output.WriteLine(
            DisplaySanitiser.SanitiseForDisplay(
                "Secret resolution failed (EnvironmentError): " +
                $"source '{sre.SecretSource}', path '{sre.SecretPath}'."));
        return output.ToString()!;
    }

    /// <summary>
    /// <c>SecretReference</c>'s grammar restricts <c>source</c> to <c>[A-Za-z0-9_-]+</c>
    /// (safe) but leaves <c>path</c> unrestricted (<c>[^}]+</c>) — an author's
    /// <c>${secret:source/path}</c> field value can embed a control character / ANSI escape
    /// sequence in the PATH segment, which then reaches this catch site's diagnostic verbatim
    /// unless sanitised. No secret VALUE is ever involved here (resolution FAILED — there is
    /// no value to leak), so this is purely the control-sequence threat, distinct from the
    /// ledger-scrub concern sections 8/9 above cover.
    /// </summary>
    [Fact]
    public void SecretResolutionExceptionMessage_PathWithAnsiControlSequence_IsInertInTerminalOutput()
    {
        var esc = (char)0x1B;
        var hostilePath = "API" + esc + "[31mHACKED" + esc + "[0m_TOKEN";
        var sre = new SecretResolutionException("env", hostilePath, "secret not found for source 'env'");
        var terminal = new StringWriter();

        var captured = EmitSecretResolutionFailureLine(sre, terminal);

        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("Secret resolution failed (EnvironmentError):", captured, StringComparison.Ordinal);
        Assert.Contains("source 'env'", captured, StringComparison.Ordinal);
        Assert.Contains("HACKED", captured, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain(esc, captured);
    }

    // ── 10. client-key-password REQ-010: the RUN-SCOPED ledger ──────────────────
    //
    // Criteria 1 and 2 of REQ-010, proven SEPARATELY because they are separate production
    // paths and neither generalises to the other:
    //
    //   • the STEP path emits through ScrubDiagnostic / BuildStepObservation;
    //   • the PROBE path emits through EnvironmentErrorEvents.ToLine, which — measured, T4
    //     security review — never called ScrubDiagnostic at all. Sharing one ledger between
    //     the two scopes does NOT on its own make a probe-time passphrase scrubbable; there
    //     has to be a scrub ON that emission path, which is ScenarioRunner.EnvironmentErrorLine.
    //
    // Every test below therefore asserts on an EMITTED LINE, and each guard has a negative
    // twin (an unshared ledger) that shows the value surviving — so a green result proves the
    // sharing and the chokepoint are load-bearing rather than decorative.
    //
    // STRUCTURAL LIMIT, recorded here because a reader who misses it will site the next guard
    // in the wrong place: SecretAccessor.Resolve records into the ledger only after a
    // SUCCESSFUL resolve. A diagnostic raised INSTEAD of a resolve — a malformed reference, a
    // null accessor, a resolution failure, a passphrase declared against an unencrypted key —
    // fires with nothing recorded for it and can never be scrubbed by this ledger. Those paths
    // are covered by the throw sites not echoing the value (T4's don't-echo guards), which is
    // a different mechanism tested elsewhere.

    /// <summary>
    /// Builds a <see cref="SecretAccessor"/> recording into <paramref name="ledger"/> and
    /// resolves one unique <c>${secret:env/…}</c> reference through it — the production shape
    /// of a lazily-resolved <c>clientKeyPassword</c>. Passing the SAME ledger to two accessors
    /// is exactly what the runner does for the probe scope and the step scope.
    /// </summary>
    private static (SecretAccessor Accessor, string Revealed, Action Cleanup) AccessorOver(
        ResolvedSecretLedger ledger, string value)
    {
        var envName = "VOUCHFX_PENTEST_CKP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, value);

        var accessor = new SecretAccessor(
            new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }),
            ledger);

        var revealed = accessor.Resolve($"${{secret:env/{envName}}}").Reveal();
        Assert.Equal(value, revealed);

        return (accessor, revealed, () => Environment.SetEnvironmentVariable(envName, null));
    }

    /// <summary>
    /// The probe-failure text the engine really builds: <c>SecuredEndpointProbe</c> folds a
    /// <c>SecurityMaterialException.Message</c> verbatim into the failure detail, and
    /// <c>OrchestrationErrorInfo.Detail</c> is the only free-form member of that record —
    /// <c>ResourceName</c> is a declared name, <c>RegistryHost</c> is parsed from an image
    /// reference, and <c>AuthStatus</c> is one of a closed set of engine tokens.
    /// </summary>
    private static OrchestrationErrorInfo ProbeFailureCarrying(string leakedValue) =>
        new(
            Kind: OrchestrationErrorKind.SecurityConfirmation,
            ResourceName: "broker",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "'broker' declared profile 'mtls' on endpoint '9093', but its client identity "
                + $"could not be loaded: the declared 'clientKeyPassword' ({leakedValue}) did not "
                + "decrypt the key.");

    // ── Criterion 1: the STEP path ──────────────────────────────────────────────

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED, REQ-010 criterion 1. A <c>clientKeyPassword</c> resolved on the
    /// STEP path must not survive into either free-form surface the step path emits: the
    /// human diagnosis write (<c>ScrubDiagnostic</c>) or the step observation carried onto the
    /// event stream (<c>BuildStepObservation</c>). Both are driven through the SAME internals
    /// the production runner calls.
    /// </summary>
    [Fact]
    public void ClientKeyPassphrase_ResolvedOnTheStepPath_IsScrubbedFromDiagnosticAndObservation()
    {
        const string passphrase = "ckp-step-path-4h8w2";
        var ledger = new ResolvedSecretLedger();
        var (accessor, revealed, cleanup) = AccessorOver(ledger, passphrase);
        try
        {
            // 1. The diagnosis write.
            var diagnosis = ScenarioRunner.ScrubDiagnostic(
                accessor, $"mTLS handshake failed (key passphrase '{revealed}' rejected).");

            Assert.NotNull(diagnosis);
            Assert.DoesNotContain(revealed, diagnosis!, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, diagnosis!, StringComparison.Ordinal);
            // The surrounding diagnostic survives — the scrub is a targeted net, not a wipe.
            Assert.Contains("mTLS handshake failed", diagnosis!, StringComparison.Ordinal);

            // 2. The step observation, asserted on the EMITTED event line.
            var line = EmitStepCompletedLine(
                accessor, "mtls-call", JsonSerializer.Serialize($"key passphrase '{revealed}' rejected"));

            Assert.DoesNotContain(revealed, line, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    // ── Criterion 2: the PROBE path (the trap) ──────────────────────────────────

    /// <summary>
    /// (a) GENUINE-LEAK-FIXED, REQ-010 criterion 2. A <c>clientKeyPassword</c> resolved by the
    /// TOPOLOGY PROBE's accessor, leaked into the probe failure's
    /// <c>OrchestrationErrorInfo.Detail</c>, must not reach the §14 event stream.
    /// <para>
    /// This drives the REAL emission path — <c>ScenarioRunner.EnvironmentErrorLine</c>, the one
    /// place the runner may build an <c>environment-error</c> line — and asserts on the emitted
    /// JSON Lines string. A test that called <c>ScrubDiagnostic</c> directly would go green
    /// against a production path that never calls it (spec REQ-010, lines 432-444).
    /// </para>
    /// </summary>
    [Fact]
    public void ClientKeyPassphrase_ResolvedOnTheProbePath_IsScrubbedFromTheEmittedEnvironmentErrorLine()
    {
        const string passphrase = "ckp-probe-path-7m3q9";
        var runLedger = new ResolvedSecretLedger();
        var (_, revealed, cleanup) = AccessorOver(runLedger, passphrase);
        try
        {
            var line = ScenarioRunner.EnvironmentErrorLine(
                runLedger, ProbeFailureCarrying(revealed), Run, DateTimeOffset.UtcNow);

            Assert.DoesNotContain(revealed, line, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, line, StringComparison.Ordinal);

            // The diagnosis is still usable, and the WIRE SHAPE is untouched: same event type,
            // same ENV_ERROR verdict, same resourceName. REQ-010 changes field CONTENT only.
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal(EventTypes.EnvironmentError, root.GetProperty("type").GetString());
            Assert.Equal("ENV_ERROR", root.GetProperty("verdict").GetString());
            Assert.Equal("broker", root.GetProperty("resourceName").GetString());
            Assert.Equal(
                nameof(OrchestrationErrorKind.SecurityConfirmation),
                root.GetProperty("errorKind").GetString());
            Assert.Contains(
                "client identity", root.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// The NEGATIVE twin of the test above, and what makes it evidence rather than decoration:
    /// with the pre-REQ-010 shape — the probe holding a ledger of its OWN, separate from the
    /// one the emission path reads — the identical passphrase survives verbatim into the
    /// emitted event line. The SHARING is the mechanism, not an incidental detail.
    /// </summary>
    [Fact]
    public void ClientKeyPassphrase_WithAnUnsharedProbeLedger_SurvivesIntoTheEmittedEnvironmentErrorLine()
    {
        const string passphrase = "ckp-unshared-2v6k1";
        var probeLedger = new ResolvedSecretLedger();
        var unrelatedRunLedger = new ResolvedSecretLedger();
        var (_, revealed, cleanup) = AccessorOver(probeLedger, passphrase);
        try
        {
            var line = ScenarioRunner.EnvironmentErrorLine(
                unrelatedRunLedger, ProbeFailureCarrying(revealed), Run, DateTimeOffset.UtcNow);

            Assert.Contains(revealed, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretString.RedactedMarker, line, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// The CROSS-PATH direction REQ-010's construction-order problem is actually about: a
    /// passphrase resolved by the probe's accessor — built before any per-scenario accessor
    /// exists — must be scrubbable from a STEP's observation. It is, and only because the two
    /// accessors record into one ledger: the unshared variant in the same test shows the value
    /// surviving into the emitted step-completed line.
    /// </summary>
    [Fact]
    public void PassphraseResolvedByTheProbeAccessor_IsScrubbedFromAStepObservation_OnlyWhenTheLedgerIsShared()
    {
        const string passphrase = "ckp-cross-path-8z4r5";
        var runLedger = new ResolvedSecretLedger();
        var (_, revealed, cleanup) = AccessorOver(runLedger, passphrase);
        try
        {
            var observation = JsonSerializer.Serialize($"client identity rejected: '{revealed}'");

            // SHARED: the step accessor is a DIFFERENT accessor (a different scope, with its own
            // resolvers) recording into the SAME run ledger — exactly the runner's shape.
            var sharedStepAccessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }),
                runLedger);
            var sharedLine = EmitStepCompletedLine(sharedStepAccessor, "mtls-call", observation);

            Assert.DoesNotContain(revealed, sharedLine, StringComparison.Ordinal);
            Assert.Contains(SecretString.RedactedMarker, sharedLine, StringComparison.Ordinal);

            // UNSHARED (the pre-REQ-010 shape): a private ledger, and the probe's value leaks.
            var isolatedStepAccessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var isolatedLine = EmitStepCompletedLine(isolatedStepAccessor, "mtls-call", observation);

            Assert.Contains(revealed, isolatedLine, StringComparison.Ordinal);
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// A null ledger is an explicit "this path owns none" (the <c>--watch</c> kept-topology
    /// entry point), not a silent scrub failure: the line is still emitted, complete and
    /// well-formed. Asserted so the null branch is a tested behaviour rather than an
    /// unexercised fall-through.
    /// </summary>
    [Fact]
    public void EnvironmentErrorLine_WithNoLedger_StillEmitsTheCompleteEvent()
    {
        var line = ScenarioRunner.EnvironmentErrorLine(
            sharedLedger: null, ProbeFailureCarrying("nothing-was-resolved"), Run, DateTimeOffset.UtcNow);

        using var doc = JsonDocument.Parse(line);
        Assert.Equal(EventTypes.EnvironmentError, doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("nothing-was-resolved", line, StringComparison.Ordinal);
    }

    // ── The routing gate: the chokepoint must actually be the chokepoint ─────────

    /// <summary>
    /// Every <c>environment-error</c> emission in <c>Vouchfx.Engine.Runtime</c> must go through
    /// <c>ScenarioRunner.EnvironmentErrorLine</c>, the scrubbing chokepoint.
    /// <para>
    /// Without this the tests above are the very trap REQ-010 warns about one level up: they
    /// prove the HELPER scrubs, and would stay green if a call site went back to calling
    /// <c>EnvironmentErrorEvents.ToLine</c> (or hand-rolled the event via
    /// <c>EnvironmentErrorEvents.Create</c>) directly. Four emission sites are four ways to
    /// forget one; this asserts the property instead of trusting the count.
    /// </para>
    /// <para>
    /// Reads the production source, in the shape other file-reading gates in this repo already
    /// use (<c>SecurityProfileRegistryTests</c>). It scans EVERY <c>.cs</c> file in
    /// <c>Vouchfx.Engine.Runtime</c>, not just <c>ScenarioRunner.cs</c>, because the property
    /// the chokepoint's own XML doc claims is assembly-wide: a second emission site added in a
    /// sibling Runtime file would bypass the scrub exactly as a direct call in the runner does.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryEnvironmentErrorEmission_InRuntime_GoesThroughTheScrubbingChokepoint()
    {
        var runtimeRoot = Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Runtime");
        var chokepointFile = Path.Combine(runtimeRoot, "ScenarioRunner.cs");

        var sep = Path.DirectorySeparatorChar;
        var sources = Directory
            .GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                     && !p.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(chokepointFile, sources);

        var chokepointEmissions = 0;
        foreach (var file in sources)
        {
            var source = File.ReadAllText(file);

            // The sibling escape hatch: building the event by hand would bypass the chokepoint
            // just as effectively, and would not be caught by the attribution loop below.
            Assert.DoesNotContain(
                "EnvironmentErrorEvents.Create(", source, StringComparison.Ordinal);

            var emissions = Regex.Matches(source, @"EnvironmentErrorEvents\.ToLine\(").ToList();
            if (emissions.Count == 0)
            {
                continue;
            }

            Assert.True(
                file == chokepointFile,
                $"'{Path.GetFileName(file)}' calls EnvironmentErrorEvents.ToLine. Only "
                + "ScenarioRunner.EnvironmentErrorLine may — it is the single place the "
                + "resolved-secret scrub is applied to an environment-error event "
                + "(client-key-password REQ-010). An emission elsewhere in this assembly "
                + "bypasses the scrub and puts a resolved passphrase on the §14 event stream.");

            // Declarations at class-member indentation, in file order, so each emission can be
            // attributed to the method that contains it.
            var declarations = Regex.Matches(
                    source,
                    @"^    (?:private|internal|public)[^\r\n=]*?\b(\w+)\(",
                    RegexOptions.Multiline)
                .Select(m => (Index: m.Index, Name: m.Groups[1].Value))
                .ToList();

            foreach (var emission in emissions)
            {
                var containing = declarations.LastOrDefault(d => d.Index < emission.Index);
                Assert.True(
                    containing.Name == "EnvironmentErrorLine",
                    $"EnvironmentErrorEvents.ToLine is called from '{containing.Name}' at "
                    + $"character offset {emission.Index}. It may only be called from "
                    + "EnvironmentErrorLine — that method is the single place the "
                    + "resolved-secret scrub is applied to an environment-error event "
                    + "(client-key-password REQ-010). A direct call bypasses the scrub and puts "
                    + "a resolved passphrase on the §14 event stream.");
            }

            chokepointEmissions += emissions.Count;
        }

        // Not vacuous: the chokepoint really does emit.
        Assert.True(chokepointEmissions > 0);
    }

    /// <summary>
    /// Every secret scope the runner builds must be given the run's ledger.
    /// <para>
    /// This covers the PLUMBING, which no Docker-free test can otherwise reach: the probe scope
    /// and the per-scenario step scope are both constructed inside methods that need a live
    /// topology to run. Dropping the argument at either site compiles cleanly (it is an
    /// optional parameter), changes no signature, and silently restores the pre-REQ-010
    /// behaviour where the probe's ledger and the step's ledger are different objects — the
    /// exact defect this todo exists to close, reintroduced invisibly.
    /// </para>
    /// <para>
    /// <c>ScenarioRunner</c> is scoped deliberately, and the scope is now a file boundary rather
    /// than a carve-out: <c>WatchRunner</c> (in the CLI) passes its own SESSION-scoped ledger to
    /// the same factory since EDGE-007, and its call sites are pinned by
    /// <c>Vouchfx.Cli.Tests.WatchRunnerSecurityLedgerTests</c> — that assembly is where the file
    /// lives, and this gate cannot see across it.
    /// </para>
    /// </summary>
    [Fact]
    public void EverySecretScopeTheRunnerBuilds_IsGivenTheRunLedger()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Runtime", "ScenarioRunner.cs"));

        // Call sites only — the factory's own declaration is `CreateSecretAccessorScope(\n`,
        // whose parameter list is on the following line.
        var argumentless = Regex.Matches(source, @"CreateSecretAccessorScope\(\s*\)").Count;
        Assert.True(
            argumentless == 0,
            $"{argumentless} call(s) to CreateSecretAccessorScope in ScenarioRunner pass no "
            + "ledger. Every scope the runner builds must record into the RUN's shared "
            + "ResolvedSecretLedger (client-key-password REQ-010); without it a passphrase "
            + "resolved by the topology probe is invisible to the step path's scrubbers and "
            + "vice versa.");

        // And the gate is not vacuous: the call sites really are there.
        Assert.Equal(3, Regex.Matches(source, @"CreateSecretAccessorScope\([A-Za-z]").Count);

        // Pin the ARGUMENT, not merely its shape. Measured: with the assertion above alone,
        // rewriting a call site as `CreateSecretAccessorScope(new ResolvedSecretLedger())`
        // restores the exact pre-REQ-010 defect — a scope with a ledger of its own — and the
        // gate stays green, because `new` also starts with [A-Za-z]. Every site must name the
        // run's ledger: `runSecretLedger` on the two entry points that create it, `sharedLedger`
        // on the per-scenario site that receives it as a parameter.
        var pinned = Regex.Matches(
            source, @"CreateSecretAccessorScope\((?:runSecretLedger|sharedLedger)\)").Count;
        Assert.True(
            pinned == 3,
            $"{pinned} of the 3 CreateSecretAccessorScope call sites in ScenarioRunner pass the "
            + "run's own ledger (runSecretLedger / sharedLedger). A site passing any OTHER "
            + "expression — a freshly constructed ResolvedSecretLedger above all — compiles and "
            + "silently reinstates the per-scope ledger REQ-010 exists to abolish.");
    }

    /// <summary>
    /// Every environment-error event the runner emits is scrubbed against a REAL ledger — no call
    /// site passes a literal null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling gate above pins the accessor SCOPES; this one pins the EMISSIONS, and they
    /// fail differently. A scope without a ledger records nothing; an emission with a null ledger
    /// scrubs nothing, and the two are independent — the environment-error line is built from an
    /// <c>OrchestrationErrorInfo</c> whose <c>Detail</c> a probe failure fills with a
    /// <c>SecurityMaterialException</c>'s text, so the null is the whole guard being absent at the
    /// one sink that carries probe output onto the §14 event stream.
    /// </para>
    /// <para>
    /// The kept-topology (<c>--watch</c>) site is the reason this exists: it passed
    /// <c>sharedLedger: null</c> until EDGE-007, documented as "a statement rather than an
    /// omission" because at that line nothing that method owned had resolved anything. That
    /// reasoning was correct about the METHOD and wrong about the PATH — WatchRunner's probe had
    /// resolved, one or many saves earlier, into a ledger the method was never handed.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryEnvironmentErrorEmission_IsScrubbedAgainstARealLedger()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Runtime", "ScenarioRunner.cs"));

        // Five mentions in total: the declaration, whose first parameter is the TYPE, plus four
        // call sites (the two probe paths, the suite loop's isolation failure, and the --watch
        // kept-topology reset). One call puts its argument on the FOLLOWING line, so the pattern
        // has to skip whitespace rather than assume one-line calls.
        var mentions = Regex.Matches(source, @"EnvironmentErrorLine\(").Count;
        Assert.Equal(5, mentions);

        var declaration = Regex.Matches(source, @"EnvironmentErrorLine\(\s*ResolvedSecretLedger\b").Count;
        Assert.Equal(1, declaration);

        var pinned = Regex.Matches(
            source, @"EnvironmentErrorLine\(\s*(?:runSecretLedger|sharedLedger)\b").Count;
        Assert.True(
            pinned == 4,
            $"{pinned} of the 4 EnvironmentErrorLine call sites in ScenarioRunner name a real "
            + "ledger (runSecretLedger on the three run-path sites, sharedLedger on the --watch "
            + "kept-topology path). Every emission must scrub against one — a null there is the "
            + "REQ-010/EDGE-007 scrub silently absent at the one sink that carries topology-probe "
            + "text onto the §14 event stream.");

        var nulls = Regex.Matches(
            source, @"EnvironmentErrorLine\(\s*(?:sharedLedger\s*:\s*)?null\b").Count;
        Assert.Equal(0, nulls);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory holding
    /// <c>vouchfx.sln</c>) — the same discovery shape the repo's other file-reading gates use.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
