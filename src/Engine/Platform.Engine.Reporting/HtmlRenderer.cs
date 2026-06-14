// Self-contained HTML report renderer for the vouchfx structured JSON Lines event
// stream (§14, S09-G-01).
//
// Design — a sibling of TerminalRenderer, deliberately mirroring it:
//   • Same SIGNATURE: a static Render(IEnumerable<string> jsonLines, TextWriter
//     output, Func<string,JsonElement,string?>? diffLookup) plus a two-argument
//     convenience overload.  It is driven by the SAME buffered JSON Lines event
//     stream the terminal renderer consumes — "render the one stream differently,
//     never a per-audience pipeline" (§14).  This class adds no field to the event
//     contract and does not touch the runners or the CLI.
//   • Same TOLERANCE: blank / whitespace-only lines are skipped, malformed JSON is
//     caught per-line, unknown event types and unknown fields are ignored (they
//     ride in EventEnvelope.Extra and are never surfaced) — the §14
//     forward-compatibility guarantee.
//   • Same (runId, stepId) → kind cache, populated from step-started events, so an
//     aggregated multi-run stream does not cross-resolve a shared step id when a
//     diff is looked up.
//
// Differences from the terminal renderer (what the HTML report adds):
//   • Emits ONE self-contained HTML document: <!DOCTYPE html> + an inline <style>.
//     There are NO external fetches — no CDN links, no remote fonts/css/js — so the
//     report is a single portable artefact.
//   • Renders the reproducibility-envelope event (which the terminal renderer
//     ignores) as a panel of secret REFERENCE digests + fixture digests.
//   • Shows all four §12.1 verdicts DISTINCTLY with a non-colour cue (a per-verdict
//     CSS class AND a text token), satisfying WCAG AA.
//
// SECRET SAFETY (the load-bearing invariant, §17) — identical discipline to the
// terminal renderer, ported verbatim:
//   • A secret-derived substitution renders as "${secret:<ref>} (redacted)" — the
//     reference label only, never a value.  The value is never in the stream to
//     begin with; the marker makes the secret-derived provenance visible WITHOUT it.
//   • An observation is summarised by SHAPE / value-KIND only (<string>/<number>/
//     <bool> for a scalar, {keys} for an object, [n item(s)] for an array) — never
//     its literal value.
//   • EVERY dynamic string emitted (step ids, scenario names, observations,
//     references, diffs, hashes) is HTML-escaped via HtmlEscape — both to neutralise
//     HTML/script injection and so that no value can sneak through the markup.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Platform.Engine.Abstractions.Events;

namespace Platform.Engine.Reporting;

/// <summary>
/// Consumes the schema-versioned JSON Lines event stream and writes a single,
/// self-contained HTML report to a <see cref="TextWriter"/> (S09-G-01).
/// </summary>
/// <remarks>
/// <para>
/// The renderer is a sibling of <see cref="TerminalRenderer"/>: it consumes the same
/// buffered event stream and is equally tolerant of blank lines, malformed JSON,
/// unknown event types, and unknown fields (§14 forward-compatibility).  It differs
/// only in its output medium — one self-contained HTML document with an inline
/// stylesheet and <strong>no external fetches</strong> — and in that it renders the
/// <c>reproducibility-envelope</c> event (which the terminal renderer ignores).
/// </para>
/// <para>
/// <strong>Secret-safety (§17).</strong>  The report contains no resolved secret
/// value: secret-derived substitutions render the reference label plus a
/// <c>(redacted)</c> marker, observations are summarised by shape / value-kind only,
/// and every dynamic string is HTML-escaped.  This is the same discipline the
/// terminal renderer applies, ported here verbatim.
/// </para>
/// </remarks>
public sealed class HtmlRenderer
{
    /// <summary>
    /// Renders the supplied JSON Lines event stream to <paramref name="output"/> as a
    /// self-contained HTML document.
    /// </summary>
    /// <param name="jsonLines">
    /// The sequence of JSON Lines strings to render.  Blank, whitespace-only, and
    /// malformed lines are skipped silently.  The sequence is enumerated once.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered HTML.  Typical call
    /// sites pass a file writer (production) or a
    /// <see cref="System.IO.StringWriter"/> (tests).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="jsonLines"/> or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static void Render(IEnumerable<string> jsonLines, TextWriter output)
        => Render(jsonLines, output, diffLookup: null);

    /// <summary>
    /// Renders the supplied JSON Lines event stream to <paramref name="output"/> as a
    /// self-contained HTML document, optionally drawing a provider-specific
    /// expected-vs-observed diff under each failed step.
    /// </summary>
    /// <param name="jsonLines">
    /// The sequence of JSON Lines strings to render.  Blank, whitespace-only, and
    /// malformed lines are skipped silently.  The sequence is enumerated once.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered HTML.
    /// </param>
    /// <param name="diffLookup">
    /// An optional delegate that, given a step <c>kind</c> (e.g.
    /// <c>"db-assert.postgres"</c>) and the step's structured observation, returns the
    /// rendered diff text for that observation, or <see langword="null"/> when no diff
    /// is applicable.  Invoked only for a <c>step-completed</c> event whose verdict is
    /// <c>FAIL</c> and which carries an <c>observation</c>.  The delegate is a plain
    /// <see cref="Func{T1, T2, TResult}"/> over <see cref="JsonElement"/> so this
    /// assembly stays decoupled from <c>Platform.Sdk</c> and the
    /// <c>IStepDiffRenderer</c> type.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="jsonLines"/> or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static void Render(
        IEnumerable<string> jsonLines,
        TextWriter output,
        Func<string, JsonElement, string?>? diffLookup)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        ArgumentNullException.ThrowIfNull(output);

        // (runId, stepId) → kind map, populated from step-started events.  Keying by
        // (runId, stepId) — not stepId alone — disambiguates an aggregated multi-run
        // stream where two runs reuse the same step id, so a later run's step-started
        // cannot overwrite an earlier run's kind and make its step-completed resolve the
        // wrong diff.  This mirrors the terminal renderer's cache verbatim.
        var stepKinds = new Dictionary<(string RunId, string StepId), string>();

        // Materialise the stream into an in-memory model so the HTML can be laid out
        // top-down (head → body → per-scenario sections → run summary).  The buffer is
        // already in memory at the call site (it is the same buffered stream the
        // terminal renderer renders), so a second pass over parsed envelopes is cheap
        // and keeps the markup generation a pure function of the model.
        var model = BuildModel(jsonLines, stepKinds);

        WriteDocument(output, model, stepKinds, diffLookup);
    }

    // -------------------------------------------------------------------------
    // Model construction — fold the stream into scenarios → steps.
    // -------------------------------------------------------------------------

    private static ReportModel BuildModel(
        IEnumerable<string> jsonLines,
        IDictionary<(string RunId, string StepId), string> stepKinds)
    {
        var model = new ReportModel();

        foreach (var line in jsonLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // The parse AND the per-line field extraction share one try/catch so a single
            // unreadable line is skipped without aborting the whole render.  A lone /
            // unpaired UTF-16 surrogate in a string VALUE (e.g. the JSON escape "\uD800"
            // with no low-surrogate partner) PARSES fine — JsonDocument.Parse succeeds and
            // the value lands in Extra as a JsonElement of kind String — but throws
            // InvalidOperationException ("Cannot read incomplete UTF-16…") LATER, when
            // GetString() reads it during model-building.  That read happens here, in the
            // switch, so the guard must span the extraction too; catching only JsonException
            // (which fires at parse time) would let the InvalidOperationException escape and
            // abort the entire report.  Both are tolerated per-line per §14 — the offending
            // line is skipped and the rest of the stream still renders.
            try
            {
                var envelope = EventStreamJson.FromLine(line);

                switch (envelope.Type)
                {
                    case EventTypes.ScenarioStarted:
                        {
                            var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                            model.GetOrAddScenario(envelope.RunId, scenarioId);
                            break;
                        }

                    case EventTypes.StepStarted:
                        {
                            var startedRunId = envelope.RunId;
                            var startedStepId = GetStr(envelope, "stepId");
                            var startedKind = GetStr(envelope, "kind");
                            if (!string.IsNullOrEmpty(startedRunId)
                                && startedStepId is not null
                                && startedKind is not null)
                            {
                                stepKinds[(startedRunId, startedStepId)] = startedKind;
                            }

                            break;
                        }

                    case EventTypes.StepAttempt:
                        {
                            var stepId = GetStr(envelope, "stepId");
                            if (stepId is null)
                            {
                                break;
                            }

                            var step = model.GetOrAddStep(envelope.RunId, stepId);
                            step.Attempts.Add(new AttemptRow(
                                Attempt: GetInt(envelope, "attempt"),
                                TMs: GetLong(envelope, "tMs"),
                                Outcome: GetStr(envelope, "outcome"),
                                ObservationSummary: SummariseObservation(envelope)));
                            break;
                        }

                    case EventTypes.StepCompleted:
                        {
                            var stepId = GetStr(envelope, "stepId");
                            if (stepId is null)
                            {
                                break;
                            }

                            var step = model.GetOrAddStep(envelope.RunId, stepId);
                            step.Verdict = GetStr(envelope, "verdict");
                            step.DurationMs = GetLong(envelope, "durationMs");
                            step.Completed = envelope;
                            break;
                        }

                    case EventTypes.ScenarioCompleted:
                        {
                            var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                            var scenario = model.GetOrAddScenario(envelope.RunId, scenarioId);
                            scenario.Verdict = GetStr(envelope, "verdict");
                            scenario.DurationMs = GetLong(envelope, "durationMs");
                            scenario.Counts = ReadCounts(envelope);
                            break;
                        }

                    case EventTypes.EnvironmentError:
                        {
                            model.EnvironmentErrors.Add(new EnvironmentErrorRow(
                                ResourceName: GetStr(envelope, "resourceName") ?? "(unknown)",
                                ErrorKind: GetStr(envelope, "errorKind") ?? "(unknown)",
                                RegistryHost: GetStr(envelope, "registryHost"),
                                Detail: GetStr(envelope, "detail")));
                            break;
                        }

                    case EventTypes.ReproducibilityEnvelope:
                        {
                            model.Envelopes.Add(new EnvelopeRow(
                                ScenarioId: GetStr(envelope, "scenarioId") ?? "(unknown)",
                                EnvSchemaVersion: GetStr(envelope, "envSchemaVersion"),
                                SecretReferences: ReadArray(envelope, "secretReferences"),
                                Fixtures: ReadArray(envelope, "fixtures")));
                            break;
                        }

                    // Unknown / unrendered event types (suite-started, suite-completed, and
                    // any type a future engine release introduces) are silently ignored —
                    // the core §14 forward-compatibility guarantee.
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Malformed JSON (JsonException at parse) OR an unreadable string value such
                // as a lone surrogate (InvalidOperationException at GetString) — skip this
                // line and continue, exactly like the terminal renderer (§14 tolerance).
                // This ALSO tolerates a line whose EventStreamJson.FromLine itself throws
                // InvalidOperationException — a null / non-object / missing-required-field
                // line — which is skipped here just like malformed JSON.
                continue;
            }
        }

        return model;
    }

    // -------------------------------------------------------------------------
    // Document writing — head (inline style) + body (sections + summary).
    // -------------------------------------------------------------------------

    private static void WriteDocument(
        TextWriter output,
        ReportModel model,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        output.WriteLine("<!DOCTYPE html>");
        output.WriteLine("<html lang=\"en\">");
        output.WriteLine("<head>");
        output.WriteLine("<meta charset=\"utf-8\">");
        output.WriteLine("<title>vouchfx report</title>");
        WriteStyle(output);
        output.WriteLine("</head>");
        output.WriteLine("<body>");

        output.WriteLine("<h1>vouchfx report</h1>");

        WriteRunSummary(output, model);

        foreach (var scenario in model.Scenarios)
        {
            WriteScenario(output, scenario, stepKinds, diffLookup);
        }

        WriteEnvironmentErrors(output, model);
        WriteReproducibilityEnvelopes(output, model);

        output.WriteLine("</body>");
        output.WriteLine("</html>");
    }

    /// <summary>
    /// Writes the inline stylesheet.  Each verdict gets its OWN class carrying a
    /// non-colour cue (a left border, a weight, AND a colour) so the four verdicts are
    /// distinguishable without relying on colour alone (WCAG AA).  The CSS contains no
    /// remote URL — no <c>@import</c>, no web-font fetch — keeping the document
    /// self-contained.
    /// </summary>
    private static void WriteStyle(TextWriter output)
    {
        // No external URLs anywhere in this block: a colon-slash-slash scheme or an
        // off-document resource reference would break the self-contained guarantee.
        output.WriteLine("<style>");
        output.WriteLine("body { font-family: sans-serif; margin: 1.5rem; color: #1a1a1a; background: #ffffff; }");
        output.WriteLine("h1 { font-size: 1.5rem; }");
        output.WriteLine("h2 { font-size: 1.2rem; margin-top: 1.5rem; }");
        output.WriteLine(".scenario { border: 1px solid #cccccc; border-radius: 6px; padding: 0.75rem 1rem; margin: 1rem 0; }");
        output.WriteLine(".step { padding: 0.4rem 0.6rem; margin: 0.4rem 0; border-left: 6px solid #888888; }");
        output.WriteLine(".verdict { font-weight: 700; font-family: monospace; padding: 0.05rem 0.4rem; border-radius: 3px; }");
        // verdict-pass: solid left rule + check symbol via ::before, dark-on-light text.
        output.WriteLine(".verdict-pass { border-left-color: #1b7f3b; }");
        output.WriteLine(".verdict-pass .verdict { color: #0f5d29; background: #e6f4ea; }");
        output.WriteLine(".verdict-pass .verdict::before { content: \"\\2714  \"; }");
        // verdict-fail: heavy left rule + cross symbol.
        output.WriteLine(".verdict-fail { border-left-color: #b00020; }");
        output.WriteLine(".verdict-fail .verdict { color: #8a0017; background: #fce8e6; }");
        output.WriteLine(".verdict-fail .verdict::before { content: \"\\2716  \"; }");
        // verdict-env-error: dashed left rule + warning symbol (distinct shape cue).
        output.WriteLine(".verdict-env-error { border-left-style: dashed; border-left-color: #8a5a00; }");
        output.WriteLine(".verdict-env-error .verdict { color: #6b4600; background: #fff4e5; }");
        output.WriteLine(".verdict-env-error .verdict::before { content: \"\\26A0  \"; }");
        // verdict-inconclusive: dotted left rule + question symbol.
        output.WriteLine(".verdict-inconclusive { border-left-style: dotted; border-left-color: #4a4a8a; }");
        output.WriteLine(".verdict-inconclusive .verdict { color: #333366; background: #ececf7; }");
        output.WriteLine(".verdict-inconclusive .verdict::before { content: \"\\003F  \"; }");
        // verdict-unknown: an absent / unrecognised verdict (a partial buffer with no
        // step-completed, or a future verdict token).  Neutral grey — "unknown" is NOT
        // one of the four §12.1 verdicts — with its OWN non-colour cues: a double left
        // rule (a shape distinct from the solid/dashed/dotted styles above) and an
        // em-dash glyph (distinct from the check/cross/warning/question symbols).
        output.WriteLine(".verdict-unknown { border-left-style: double; border-left-color: #888888; }");
        output.WriteLine(".verdict-unknown .verdict { color: #4a4a4a; background: #eeeeee; }");
        output.WriteLine(".verdict-unknown .verdict::before { content: \"\\2014  \"; }");
        output.WriteLine(".summary { display: flex; gap: 1rem; flex-wrap: wrap; margin: 0.5rem 0; }");
        output.WriteLine(".summary span { padding: 0.2rem 0.6rem; border: 1px solid #cccccc; border-radius: 3px; }");
        output.WriteLine(".timeline { font-family: monospace; font-size: 0.9rem; margin: 0.3rem 0 0.3rem 1rem; }");
        output.WriteLine(".provenance { margin: 0.3rem 0 0.3rem 1rem; font-size: 0.9rem; }");
        output.WriteLine(".redacted { font-style: italic; color: #6b4600; }");
        output.WriteLine(".diff { font-family: monospace; white-space: pre-wrap; background: #f5f5f5; padding: 0.4rem 0.6rem; margin: 0.3rem 0 0.3rem 1rem; border-radius: 3px; }");
        output.WriteLine(".envelope { border: 1px solid #cccccc; border-radius: 6px; padding: 0.75rem 1rem; margin: 1rem 0; }");
        output.WriteLine(".mono { font-family: monospace; }");
        output.WriteLine("</style>");
    }

    private static void WriteRunSummary(TextWriter output, ReportModel model)
    {
        // Aggregate the per-scenario verdict counts into a single run-level tally so the
        // reader sees the whole-run shape at a glance, kept separate by verdict (§12.1).
        var pass = 0;
        var fail = 0;
        var envError = 0;
        var inconclusive = 0;
        foreach (var scenario in model.Scenarios)
        {
            pass += scenario.Counts.Pass;
            fail += scenario.Counts.Fail;
            envError += scenario.Counts.EnvError;
            inconclusive += scenario.Counts.Inconclusive;
        }

        output.WriteLine("<section class=\"summary-block\">");
        output.WriteLine("<h2>Run summary</h2>");
        output.WriteLine("<div class=\"summary\">");
        output.WriteLine(FormatCount("verdict-pass", "PASS", pass));
        output.WriteLine(FormatCount("verdict-fail", "FAIL", fail));
        output.WriteLine(FormatCount("verdict-env-error", "ENV_ERROR", envError));
        output.WriteLine(FormatCount("verdict-inconclusive", "INCONCLUSIVE", inconclusive));
        output.WriteLine("</div>");
        output.WriteLine("</section>");
    }

    private static string FormatCount(string verdictClass, string label, int count)
        => string.Format(
            CultureInfo.InvariantCulture,
            "<span class=\"{0}\"><span class=\"verdict\">{1}</span> {2}</span>",
            verdictClass,
            label,
            count);

    private static void WriteScenario(
        TextWriter output,
        ScenarioModel scenario,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        var verdictClass = VerdictClass(scenario.Verdict);
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<section class=\"scenario {0}\">",
            verdictClass));

        var durationSuffix = scenario.DurationMs.HasValue
            ? string.Format(CultureInfo.InvariantCulture, " ({0} ms)", scenario.DurationMs.Value)
            : string.Empty;

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<h2>Scenario: {0} <span class=\"verdict\">{1}</span>{2}</h2>",
            HtmlEscape(scenario.ScenarioId),
            HtmlEscape(scenario.Verdict ?? "(unknown)"),
            HtmlEscape(durationSuffix)));

        foreach (var step in scenario.Steps)
        {
            WriteStep(output, scenario.RunId, step, stepKinds, diffLookup);
        }

        output.WriteLine("</section>");
    }

    private static void WriteStep(
        TextWriter output,
        string runId,
        StepModel step,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        var verdict = step.Verdict ?? "(unknown)";
        var verdictClass = VerdictClass(step.Verdict);

        var durationSuffix = step.DurationMs.HasValue
            ? string.Format(CultureInfo.InvariantCulture, " ({0} ms)", step.DurationMs.Value)
            : string.Empty;

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<div class=\"step {0}\">",
            verdictClass));

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<span class=\"mono\">{0}</span>: <span class=\"verdict\">{1}</span>{2}",
            HtmlEscape(step.StepId),
            HtmlEscape(verdict),
            HtmlEscape(durationSuffix)));

        WriteAttemptTimeline(output, step);
        WriteStepDiff(output, runId, step, verdict, stepKinds, diffLookup);
        WriteProvenanceThread(output, step);

        output.WriteLine("</div>");
    }

    /// <summary>
    /// Writes the per-attempt polling timeline for a step from its
    /// <c>step-attempt</c> events (the timeline the terminal renderer draws, in HTML).
    /// The optional observation summary is shape / value-kind only (§17).
    /// </summary>
    private static void WriteAttemptTimeline(TextWriter output, StepModel step)
    {
        if (step.Attempts.Count == 0)
        {
            return;
        }

        output.WriteLine("<div class=\"timeline\"><strong>timeline:</strong><ul>");
        foreach (var attempt in step.Attempts)
        {
            var elapsed = attempt.TMs.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", attempt.TMs.Value / 1000.0)
                : "?s";
            var outcome = attempt.Outcome ?? "(pending)";

            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "<li>t={0} attempt {1} <span class=\"verdict\">{2}</span>{3}</li>",
                HtmlEscape(elapsed),
                attempt.Attempt,
                HtmlEscape(outcome),
                HtmlEscape(attempt.ObservationSummary)));
        }

        output.WriteLine("</ul></div>");
    }

    /// <summary>
    /// Writes a provider-specific expected-vs-observed diff under a FAILED step when a
    /// <paramref name="diffLookup"/> is supplied and the step carries a structured
    /// observation.  A no-op otherwise.  The diff is computed at render time — the
    /// stream carries only the structured observation, never rendered text (§14).
    /// </summary>
    private static void WriteStepDiff(
        TextWriter output,
        string runId,
        StepModel step,
        string verdict,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        if (diffLookup is null
            || !string.Equals(verdict, "FAIL", StringComparison.Ordinal)
            || step.Completed is null)
        {
            return;
        }

        var envelope = step.Completed;
        if (envelope.Extra is null
            || !envelope.Extra.TryGetValue("observation", out var observation)
            || observation.ValueKind == JsonValueKind.Null
            || observation.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        if (string.IsNullOrEmpty(runId)
            || !stepKinds.TryGetValue((runId, step.StepId), out var kind))
        {
            return;
        }

        // The diff is computed at EMIT time — OUTSIDE the per-line build guard — and the
        // provider delegate reads string values out of the stored observation.  If the
        // observation carries a string value with a lone / unpaired UTF-16 surrogate (a
        // SUT-derived observation can), the delegate's GetString() throws
        // InvalidOperationException; left unguarded that would abort WriteDocument
        // mid-stream and truncate the HTML file.  Treat an unreadable observation like a
        // missing one — OMIT the diff fragment — so the rest of the document still renders
        // to completion.  JsonException is caught for safety.
        string? diff;
        try
        {
            diff = diffLookup(kind, observation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return;
        }

        if (string.IsNullOrEmpty(diff))
        {
            return;
        }

        // The provider's diff text is dynamic and HTML-escaped before it is placed into
        // the document — markup-injection is neutralised and no value can slip through.
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<div class=\"diff\">{0}</div>",
            HtmlEscape(diff)));
    }

    /// <summary>
    /// Writes the captured-variable provenance thread for a step — the values it
    /// captured and the <c>{placeholder}</c> / <c>${secret:…}</c> references
    /// substituted into it — exactly as the terminal renderer does, in HTML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>§17 secret-safety.</strong>  A substitution whose <c>secretDerived</c>
    /// flag is <see langword="true"/> renders the non-sensitive reference label plus a
    /// <c>(redacted)</c> marker — never a value (the value is never in the stream).
    /// Captured variable names and JSONPaths are metadata (safe to show); captured
    /// values are not in the stream and are never invented.  Every dynamic field is
    /// HTML-escaped.
    /// </para>
    /// </remarks>
    private static void WriteProvenanceThread(TextWriter output, StepModel step)
    {
        if (step.Completed is null)
        {
            return;
        }

        var captured = ReadArray(step.Completed, "captured");
        var substitutions = ReadArray(step.Completed, "substitutions");

        if (captured.Length == 0 && substitutions.Length == 0)
        {
            return;
        }

        output.WriteLine("<div class=\"provenance\"><strong>provenance:</strong><ul>");

        foreach (var capture in captured)
        {
            if (capture.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetStrFromObject(capture, "name") ?? "(unknown)";
            var path = GetStrFromObject(capture, "path") ?? "(unknown)";
            var matched = GetBoolFromObject(capture, "matched");
            var matchSuffix = matched ? string.Empty : " (no match)";

            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "<li>captured <span class=\"mono\">{0}</span> &lt;- step <span class=\"mono\">{1}</span> ({2}){3}</li>",
                HtmlEscape(name),
                HtmlEscape(step.StepId),
                HtmlEscape(path),
                HtmlEscape(matchSuffix)));
        }

        foreach (var substitution in substitutions)
        {
            if (substitution.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var placeholder = GetStrFromObject(substitution, "placeholder") ?? "(unknown)";
            var secretDerived = GetBoolFromObject(substitution, "secretDerived");
            var originStepId = GetStrFromObject(substitution, "originStepId");

            if (secretDerived)
            {
                // §17: the placeholder field carries the non-sensitive secret REFERENCE
                // label; render it with a redaction marker and never a value.  The
                // marker makes the secret-derived provenance visible WITHOUT it.
                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "<li>substituted <span class=\"mono\">${{secret:{0}}}</span> <span class=\"redacted\">(redacted)</span> -&gt; step <span class=\"mono\">{1}</span></li>",
                    HtmlEscape(placeholder),
                    HtmlEscape(step.StepId)));
            }
            else
            {
                var origin = string.IsNullOrEmpty(originStepId)
                    ? "(variables/untraced)"
                    : string.Format(CultureInfo.InvariantCulture, "step '{0}'", originStepId);

                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "<li>substituted <span class=\"mono\">{{{0}}}</span> (from {1}) -&gt; step <span class=\"mono\">{2}</span></li>",
                    HtmlEscape(placeholder),
                    HtmlEscape(origin),
                    HtmlEscape(step.StepId)));
            }
        }

        output.WriteLine("</ul></div>");
    }

    private static void WriteEnvironmentErrors(TextWriter output, ReportModel model)
    {
        if (model.EnvironmentErrors.Count == 0)
        {
            return;
        }

        output.WriteLine("<section class=\"scenario verdict-env-error\">");
        output.WriteLine("<h2>Environment errors</h2><ul>");
        foreach (var error in model.EnvironmentErrors)
        {
            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "<li><span class=\"verdict\">ENV_ERROR</span> on <span class=\"mono\">{0}</span> [{1}] registry={2}: {3}</li>",
                HtmlEscape(error.ResourceName),
                HtmlEscape(error.ErrorKind),
                HtmlEscape(error.RegistryHost ?? "(none)"),
                HtmlEscape(error.Detail ?? "(no detail)")));
        }

        output.WriteLine("</ul></section>");
    }

    /// <summary>
    /// Writes the reproducibility-envelope panel — the secret REFERENCE digests and
    /// the seed-fixture digests (§17, docs/02 §3.2.2).  This is the panel the terminal
    /// renderer does not draw.  Every value here is a non-sensitive reference, source
    /// id, or content hash by construction (the secret resolver is never invoked to
    /// build the envelope); each is still HTML-escaped on the way out.
    /// </summary>
    private static void WriteReproducibilityEnvelopes(TextWriter output, ReportModel model)
    {
        if (model.Envelopes.Count == 0)
        {
            return;
        }

        output.WriteLine("<section class=\"envelope\">");
        output.WriteLine("<h2>Reproducibility envelope</h2>");

        foreach (var envelope in model.Envelopes)
        {
            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "<h3>Scenario: {0} <span class=\"mono\">(schema {1})</span></h3>",
                HtmlEscape(envelope.ScenarioId),
                HtmlEscape(envelope.EnvSchemaVersion ?? "(unknown)")));

            output.WriteLine("<h4>Secret references</h4><ul>");
            foreach (var reference in envelope.SecretReferences)
            {
                if (reference.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var source = GetStrFromObject(reference, "source") ?? "(unknown)";
                var hash = GetStrFromObject(reference, "referenceHash") ?? "(unknown)";

                // The source id and the reference HASH are surfaced — never a value.
                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "<li>source <span class=\"mono\">{0}</span> reference-hash <span class=\"mono\">{1}</span></li>",
                    HtmlEscape(source),
                    HtmlEscape(hash)));
            }

            output.WriteLine("</ul>");

            output.WriteLine("<h4>Fixtures</h4><ul>");
            foreach (var fixture in envelope.Fixtures)
            {
                if (fixture.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var reference = GetStrFromObject(fixture, "reference") ?? "(unknown)";
                var contentHash = GetStrFromObject(fixture, "contentHash") ?? "(absent)";

                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "<li><span class=\"mono\">{0}</span> content-hash <span class=\"mono\">{1}</span></li>",
                    HtmlEscape(reference),
                    HtmlEscape(contentHash)));
            }

            output.WriteLine("</ul>");
        }

        output.WriteLine("</section>");
    }

    // -------------------------------------------------------------------------
    // Secret-safe observation summarisation — ported verbatim from the terminal
    // renderer.  Scalars summarise to a value-KIND hint; objects/arrays to a SHAPE
    // hint.  Never the literal value (§17).
    // -------------------------------------------------------------------------

    private static string SummariseObservation(EventEnvelope envelope)
    {
        if (envelope.Extra is null
            || !envelope.Extra.TryGetValue("observation", out var observation)
            || observation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var summary = observation.ValueKind switch
        {
            JsonValueKind.String => "<string>",
            JsonValueKind.Number => "<number>",
            JsonValueKind.True or JsonValueKind.False => "<bool>",
            JsonValueKind.Object => DescribeObject(observation),
            JsonValueKind.Array => string.Format(
                CultureInfo.InvariantCulture,
                "[{0} item(s)]",
                observation.GetArrayLength()),
            _ => string.Empty,
        };

        return string.IsNullOrEmpty(summary)
            ? string.Empty
            : "   " + summary;
    }

    private static string DescribeObject(JsonElement observation)
    {
        var keys = observation
            .EnumerateObject()
            .Select(static p => p.Name);

        return "{" + string.Join(", ", keys) + "}";
    }

    // -------------------------------------------------------------------------
    // HTML escaping — the load-bearing safety helper.  Every dynamic string the
    // renderer emits passes through here, both to neutralise HTML/script injection
    // and so no value can sneak through the markup (§17).
    // -------------------------------------------------------------------------

    /// <summary>
    /// HTML-escapes the five markup-significant characters in <paramref name="value"/>:
    /// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, and <c>'</c>.  The ampersand is
    /// replaced FIRST so that the entity replacements introduced for the other
    /// characters are not themselves re-escaped.
    /// </summary>
    /// <param name="value">The dynamic string to escape; <see langword="null"/> yields the empty string.</param>
    /// <returns>The HTML-safe form of <paramref name="value"/>.</returns>
    private static string HtmlEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    // -------------------------------------------------------------------------
    // Verdict → CSS class.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maps a wire verdict token (<c>PASS</c>/<c>FAIL</c>/<c>ENV_ERROR</c>/
    /// <c>INCONCLUSIVE</c>) to its non-colour-cue CSS class.  An unknown / absent
    /// verdict falls back to <c>verdict-unknown</c> so the markup is always well-formed.
    /// </summary>
    private static string VerdictClass(string? verdict)
        => verdict switch
        {
            "PASS" => "verdict-pass",
            "FAIL" => "verdict-fail",
            "ENV_ERROR" => "verdict-env-error",
            "INCONCLUSIVE" => "verdict-inconclusive",
            _ => "verdict-unknown",
        };

    // -------------------------------------------------------------------------
    // Extra-field accessors — all defensive; never throw.  Ported from the
    // terminal renderer so both renderers read the flat wire shape identically.
    // -------------------------------------------------------------------------

    private static string? GetStr(EventEnvelope envelope, string key)
    {
        if (envelope.Extra is not null
            && envelope.Extra.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            // Defensive read — uniform with GetStrFromObject.  A string VALUE carrying a
            // lone / unpaired UTF-16 surrogate parses fine but throws
            // InvalidOperationException at GetString().  BuildModel already calls this
            // INSIDE its per-line guard (so such a line is skipped there), but reading
            // defensively here too keeps both string-extraction helpers identical and
            // safe should any future EMIT-time caller route through this overload.
            try
            {
                return element.GetString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static int GetInt(EventEnvelope envelope, string key)
    {
        if (envelope.Extra is not null
            && envelope.Extra.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value))
        {
            return value;
        }

        return 0;
    }

    private static long? GetLong(EventEnvelope envelope, string key)
    {
        if (envelope.Extra is not null
            && envelope.Extra.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var value))
        {
            return value;
        }

        return null;
    }

    private static (int Pass, int Fail, int EnvError, int Inconclusive) ReadCounts(EventEnvelope envelope)
    {
        if (envelope.Extra is null
            || !envelope.Extra.TryGetValue("counts", out var countsEl)
            || countsEl.ValueKind != JsonValueKind.Object)
        {
            return (0, 0, 0, 0);
        }

        return (
            Pass: GetIntFromObject(countsEl, "pass"),
            Fail: GetIntFromObject(countsEl, "fail"),
            EnvError: GetIntFromObject(countsEl, "envError"),
            Inconclusive: GetIntFromObject(countsEl, "inconclusive"));
    }

    private static int GetIntFromObject(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value))
        {
            return value;
        }

        return 0;
    }

    private static JsonElement[] ReadArray(EventEnvelope envelope, string key)
    {
        if (envelope.Extra is not null
            && envelope.Extra.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().ToArray();
        }

        return Array.Empty<JsonElement>();
    }

    private static string? GetStrFromObject(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            // Defensive read.  Unlike BuildModel's guarded reads, the nested-object
            // string fields surfaced here (provenance name/path/placeholder/originStepId,
            // reproducibility source/referenceHash/reference/contentHash) are read at EMIT
            // time inside WriteDocument — OUTSIDE the per-line try/catch — because the
            // renderer stores the whole step-completed / envelope and defers their reads to
            // write time.  A string VALUE carrying a lone / unpaired UTF-16 surrogate (the
            // JSON escape "\uD800" with no low-surrogate partner) PARSES fine but throws
            // InvalidOperationException ("Cannot read incomplete UTF-16…") at GetString().
            // If that escaped out here it would abort WriteDocument mid-stream and leave a
            // TRUNCATED HTML file (the writer streams straight to the output).  Treat an
            // unreadable value EXACTLY like a missing field — return null — so the single
            // affected provenance row / reproducibility entry is OMITTED while the rest of
            // the document renders to completion (all closing tags present).  Per §17 an
            // omitted-because-unreadable field leaks nothing: it simply disappears.
            // JsonException is caught for safety though no read here parses fresh JSON.
            try
            {
                return prop.GetString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool GetBoolFromObject(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return prop.GetBoolean();
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // In-memory report model — a light fold of the stream used to lay the HTML out
    // top-down.  All collections preserve first-seen order so the report reads in
    // event order.
    // -------------------------------------------------------------------------

    private sealed class ReportModel
    {
        private readonly Dictionary<(string RunId, string ScenarioId), ScenarioModel> _scenarioIndex = new();
        private readonly Dictionary<(string RunId, string StepId), StepModel> _stepIndex = new();

        // The most-recently-INSERTED scenario per run, kept O(1) so attaching a
        // newly-seen step does not re-scan the whole Scenarios list.  Updated ONLY
        // when a brand-new scenario is added (never when an existing scenario is
        // returned), which preserves the "last by insertion order, not last-touched"
        // semantics the step-attachment logic relies on.
        private readonly Dictionary<string, ScenarioModel> _lastScenarioByRun = new();

        public List<ScenarioModel> Scenarios { get; } = new();

        public List<EnvironmentErrorRow> EnvironmentErrors { get; } = new();

        public List<EnvelopeRow> Envelopes { get; } = new();

        public ScenarioModel GetOrAddScenario(string runId, string scenarioId)
        {
            var key = (runId, scenarioId);
            if (!_scenarioIndex.TryGetValue(key, out var scenario))
            {
                scenario = new ScenarioModel(runId, scenarioId);
                _scenarioIndex[key] = scenario;
                Scenarios.Add(scenario);

                // Only a freshly-inserted scenario advances the per-run "last seen"
                // pointer — returning an already-indexed scenario must NOT, or the
                // pointer would track last-touched rather than last-inserted.
                _lastScenarioByRun[runId] = scenario;
            }

            return scenario;
        }

        public StepModel GetOrAddStep(string runId, string stepId)
        {
            var key = (runId, stepId);
            if (!_stepIndex.TryGetValue(key, out var step))
            {
                step = new StepModel(stepId);
                _stepIndex[key] = step;

                // Attach the step to the scenario currently open for this run.  When no
                // scenario-started has been seen for the run (e.g. a step-only test
                // stream), synthesise an implicit scenario so no step is dropped.
                var scenario = LastScenarioForRun(runId)
                    ?? GetOrAddScenario(runId, "(scenario)");
                scenario.Steps.Add(step);
            }

            return step;
        }

        private ScenarioModel? LastScenarioForRun(string runId)
            => _lastScenarioByRun.TryGetValue(runId, out var scenario) ? scenario : null;
    }

    private sealed class ScenarioModel
    {
        public ScenarioModel(string runId, string scenarioId)
        {
            RunId = runId;
            ScenarioId = scenarioId;
        }

        public string RunId { get; }

        public string ScenarioId { get; }

        public string? Verdict { get; set; }

        public long? DurationMs { get; set; }

        public (int Pass, int Fail, int EnvError, int Inconclusive) Counts { get; set; }

        public List<StepModel> Steps { get; } = new();
    }

    private sealed class StepModel
    {
        public StepModel(string stepId) => StepId = stepId;

        public string StepId { get; }

        public string? Verdict { get; set; }

        public long? DurationMs { get; set; }

        public EventEnvelope? Completed { get; set; }

        public List<AttemptRow> Attempts { get; } = new();
    }

    private sealed record AttemptRow(int Attempt, long? TMs, string? Outcome, string ObservationSummary);

    private sealed record EnvironmentErrorRow(string ResourceName, string ErrorKind, string? RegistryHost, string? Detail);

    private sealed record EnvelopeRow(
        string ScenarioId,
        string? EnvSchemaVersion,
        JsonElement[] SecretReferences,
        JsonElement[] Fixtures);
}
