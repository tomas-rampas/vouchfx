// JUnit XML output renderer for the vouchfx structured JSON Lines event stream
// (§14, S09-G-02).
//
// Design — a sibling of TerminalRenderer / HtmlRenderer, deliberately mirroring them:
//   • Driven by the SAME buffered JSON Lines event stream the other renderers consume
//     — "render the one stream differently, never a per-audience pipeline" (§14).  This
//     class adds no field to the event contract: it is a pure stream→XML transform.
//     The CLI (--junit) merely selects this renderer and feeds it the buffered stream;
//     the renderer itself neither reads nor mutates the runners or the CLI.
//   • Same TOLERANCE: blank / whitespace-only lines are skipped, malformed JSON is
//     caught per-line, unknown event types and unknown fields are ignored (they ride in
//     EventEnvelope.Extra and are never surfaced) — the §14 forward-compatibility
//     guarantee.
//   • Same (runId, scenarioId) keying so an aggregated multi-run stream does not
//     cross-resolve a shared scenario id.
//
// What this renderer adds (the JUnit-XML shape CI systems ingest):
//   • Granularity is PER SCENARIO: each scenario is one <testcase> (so CI shows
//     per-scenario results), wrapped in <testsuites> → <testsuite name="vouchfx" …>.
//   • THE non-negotiable mapping — the four §12.1 verdicts must NOT collapse onto a
//     single JUnit primitive:
//       Pass             → <testcase> with no child element.
//       Fail             → <testcase><failure …>   (a genuine assertion failure).
//       EnvironmentError → <testcase><error …>      (JUnit's DISTINCT "infrastructure
//                          broke" primitive — NOT an assertion failure).
//       Inconclusive     → <testcase><skipped …>    (the faithful JUnit primitive;
//                          NOT a failure).
//     AND every <testcase> additionally carries the original vouchfx verdict as a
//     <property name="vouchfx.verdict" value="…"/> so a consumer can recover the EXACT
//     taxonomy (EnvError vs Inconclusive vs Fail vs Pass), which the lossy JUnit
//     primitive mapping would otherwise erase.  This is the "the four verdicts survive
//     the mapping" requirement.
//   • The <testsuite>/<testsuites> aggregate attributes (tests, failures, errors,
//     skipped) are computed CONSISTENTLY with that mapping (Fail→failures,
//     EnvError→errors, Inconclusive→skipped).
//
// SECRET SAFETY (the load-bearing invariant, §17) — identical discipline to the
// terminal / HTML renderers:
//   • No resolved secret value ever appears.  Messages are kept to the verdict + the
//     scenario id + a redacted, shape-only summary; no observation or captured value is
//     dumped verbatim, and only NAMED Extra fields are read (Extra is never dumped).
//   • A secret-derived substitution is summarised by its non-sensitive REFERENCE label
//     plus a (redacted) marker — never a value (the value is never in the stream).
//   • EVERY dynamic string emitted is XML-escaped via XmlEscape (& FIRST, then < > " '),
//     both to keep the document well-formed and so no value can sneak through the markup.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;

namespace Vouchfx.Engine.Reporting;

/// <summary>
/// Consumes the schema-versioned JSON Lines event stream and writes a JUnit XML report
/// to a <see cref="TextWriter"/> so CI systems can ingest vouchfx results (S09-G-02).
/// </summary>
/// <remarks>
/// <para>
/// The renderer is a sibling of <see cref="TerminalRenderer"/> and
/// <see cref="HtmlRenderer"/>: it consumes the same buffered event stream and is
/// equally tolerant of blank lines, malformed JSON, unknown event types, and unknown
/// fields (§14 forward-compatibility).  It differs only in its output medium — a single
/// well-formed JUnit XML document.
/// </para>
/// <para>
/// <strong>The four-verdict mapping (§12.1).</strong>  Each scenario becomes one
/// <c>&lt;testcase&gt;</c>.  <c>Pass</c> has no child element; <c>Fail</c> carries a
/// <c>&lt;failure&gt;</c>; <c>EnvironmentError</c> carries an <c>&lt;error&gt;</c> (the
/// distinct JUnit "infrastructure broke" primitive); <c>Inconclusive</c> carries a
/// <c>&lt;skipped&gt;</c>.  The verdicts are deliberately kept separate so an
/// environment error is never conflated with a defect.  Every testcase ALSO carries a
/// <c>&lt;property name="vouchfx.verdict"&gt;</c> with the exact taxonomy token, so a
/// consumer can recover the original verdict that the lossy JUnit primitive mapping
/// would otherwise erase.
/// </para>
/// <para>
/// <strong>Secret-safety (§17).</strong>  The report contains no resolved secret value:
/// messages are kept to the verdict plus the scenario / step id plus a redacted,
/// shape-only summary, only named <c>Extra</c> fields are read (never dumped verbatim),
/// secret-derived substitutions render the reference label plus a <c>(redacted)</c>
/// marker, and every dynamic string is XML-escaped.
/// </para>
/// </remarks>
public sealed class JunitXmlRenderer
{
    /// <summary>The <c>name</c> attribute of the single emitted <c>&lt;testsuite&gt;</c>.</summary>
    private const string SuiteName = "vouchfx";

    /// <summary>
    /// Renders the supplied JSON Lines event stream to <paramref name="output"/> as a
    /// JUnit XML document.
    /// </summary>
    /// <param name="jsonLines">
    /// The sequence of JSON Lines strings to render.  Blank, whitespace-only, and
    /// malformed lines are skipped silently.  The sequence is enumerated once.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered XML.  Typical call sites
    /// pass a file writer (production) or a <see cref="System.IO.StringWriter"/> (tests).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="jsonLines"/> or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static void Render(IEnumerable<string> jsonLines, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        ArgumentNullException.ThrowIfNull(output);

        var model = BuildModel(jsonLines);
        WriteDocument(output, model);
    }

    // -------------------------------------------------------------------------
    // Model construction — fold the stream into scenarios (each a testcase).
    // -------------------------------------------------------------------------

    private static ReportModel BuildModel(IEnumerable<string> jsonLines)
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
                            var scenario = model.GetOrAddScenario(envelope.RunId, scenarioId);
                            scenario.File ??= GetStr(envelope, "file");
                            break;
                        }

                    case EventTypes.ScenarioCompleted:
                        {
                            var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                            var scenario = model.GetOrAddScenario(envelope.RunId, scenarioId);
                            scenario.Verdict = GetStr(envelope, "verdict");
                            scenario.DurationMs = GetLong(envelope, "durationMs");
                            scenario.Counts = ReadCounts(envelope);

                            // #372. Absent on every ordinary pass and on any stream written by an
                            // older engine, which is why it is read tolerantly (§14) rather than
                            // required — GetStr returns null and the summary below is unchanged.
                            scenario.Message = GetStr(envelope, "message");
                            break;
                        }

                    // Step and other events do not change the per-scenario JUnit shape (JUnit
                    // carries verdicts + messages, not per-step rows or rendered diffs), so
                    // they are ignored here — along with every unknown / future event type,
                    // the core §14 forward-compatibility guarantee.  Crucially this means no
                    // step observation or captured value is ever read, keeping the report
                    // secret-safe by construction.
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Malformed JSON (JsonException at parse) OR an unreadable string value such
                // as a lone surrogate (InvalidOperationException at GetString) — skip this
                // line and continue, exactly like the sibling renderers (§14 tolerance).
                // This ALSO tolerates a line whose EventStreamJson.FromLine itself throws
                // InvalidOperationException — a null / non-object / missing-required-field
                // line — which is skipped here just like malformed JSON.
                continue;
            }
        }

        return model;
    }

    // -------------------------------------------------------------------------
    // Document writing — <testsuites> → <testsuite> → per-scenario <testcase>.
    // -------------------------------------------------------------------------

    private static void WriteDocument(TextWriter output, ReportModel model)
    {
        // Aggregate the per-scenario verdicts into the four JUnit-relevant tallies,
        // consistent with the mapping: Fail→failures, EnvError→errors,
        // Inconclusive→skipped.  Pass contributes only to the total `tests` count.
        var tests = model.Scenarios.Count;
        var failures = 0;
        var errors = 0;
        var skipped = 0;
        var totalMs = 0L;

        foreach (var scenario in model.Scenarios)
        {
            switch (scenario.Verdict)
            {
                case "FAIL":
                    failures++;
                    break;
                case "ENV_ERROR":
                    errors++;
                    break;
                case "INCONCLUSIVE":
                    skipped++;
                    break;
                default:
                    // PASS and any unknown / unresolved verdict contribute only to the
                    // total count — they carry no failure/error/skipped child element.
                    break;
            }

            totalMs += scenario.DurationMs ?? 0;
        }

        output.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");

        // The root <testsuites> mirrors the aggregate attributes so a consumer that reads
        // only the root sees the same shape as the single <testsuite> beneath it.
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "<testsuites tests=\"{0}\" failures=\"{1}\" errors=\"{2}\" skipped=\"{3}\" time=\"{4}\">",
            tests,
            failures,
            errors,
            skipped,
            FormatSeconds(totalMs)));

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  <testsuite name=\"{0}\" tests=\"{1}\" failures=\"{2}\" errors=\"{3}\" skipped=\"{4}\" time=\"{5}\">",
            XmlEscape(SuiteName),
            tests,
            failures,
            errors,
            skipped,
            FormatSeconds(totalMs)));

        foreach (var scenario in model.Scenarios)
        {
            WriteTestcase(output, scenario);
        }

        output.WriteLine("  </testsuite>");
        output.WriteLine("</testsuites>");
    }

    private static void WriteTestcase(TextWriter output, ScenarioModel scenario)
    {
        // The original verdict token (PASS/FAIL/ENV_ERROR/INCONCLUSIVE) — defaulted to a
        // neutral "(unknown)" for a partial buffer with no scenario-completed.  This is
        // the value preserved verbatim on the vouchfx.verdict property so the exact
        // taxonomy survives the lossy JUnit primitive mapping.
        var verdictToken = scenario.Verdict ?? "(unknown)";

        // classname carries the source file (or the suite name as a fallback) so CI can
        // group scenarios by file.
        var classname = string.IsNullOrEmpty(scenario.File) ? SuiteName : scenario.File;

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "    <testcase name=\"{0}\" classname=\"{1}\" time=\"{2}\">",
            XmlEscape(scenario.ScenarioId),
            XmlEscape(classname),
            FormatSeconds(scenario.DurationMs ?? 0)));

        // ALWAYS carry the original vouchfx verdict so the exact taxonomy is recoverable
        // (the JUnit failure/error/skipped primitive is intentionally lossy).
        output.WriteLine("      <properties>");
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "        <property name=\"vouchfx.verdict\" value=\"{0}\"/>",
            XmlEscape(verdictToken)));
        output.WriteLine("      </properties>");

        // The four-verdict mapping — kept strictly separate (§12.1).  The message is a
        // secret-safe summary: verdict token + scenario id + the per-verdict step counts
        // (shape only, never a value).
        var message = BuildSummaryMessage(scenario, verdictToken);

        switch (scenario.Verdict)
        {
            case "FAIL":
                // A genuine assertion failure.
                WriteOutcomeElement(output, "failure", message);
                break;

            case "ENV_ERROR":
                // JUnit's DISTINCT "error" primitive: infrastructure breakage, NOT an
                // assertion failure.  Keeping this separate from <failure> is the whole
                // point — conflating an env error with a defect destroys trust (§12.1).
                WriteOutcomeElement(output, "error", message);
                break;

            case "INCONCLUSIVE":
                // The faithful JUnit primitive for "could not determine correctness".
                // NOT a failure.
                WriteOutcomeElement(output, "skipped", message);
                break;

            default:
                // PASS (and any unknown / unresolved verdict) — a clean testcase with no
                // failure/error/skipped child element.
                break;
        }

        output.WriteLine("    </testcase>");
    }

    /// <summary>
    /// Writes a <c>&lt;failure&gt;</c>, <c>&lt;error&gt;</c>, or <c>&lt;skipped&gt;</c>
    /// element with a secret-safe <c>message</c> attribute and matching escaped body
    /// text.  Both the attribute and the body are XML-escaped.
    /// </summary>
    private static void WriteOutcomeElement(TextWriter output, string elementName, string message)
    {
        var escaped = XmlEscape(message);
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "      <{0} message=\"{1}\">{1}</{0}>",
            elementName,
            escaped));
    }

    /// <summary>
    /// Builds the secret-safe summary message for a scenario's outcome element: the
    /// verdict token, the scenario id, and the per-verdict step counts (shape only).
    /// No observation, captured value, or secret value is ever included (§17).
    /// </summary>
    private static string BuildSummaryMessage(ScenarioModel scenario, string verdictToken)
    {
        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "Scenario '{0}' {1} (pass={2} fail={3} envError={4} inconclusive={5})",
            scenario.ScenarioId,
            verdictToken,
            scenario.Counts.Pass,
            scenario.Counts.Fail,
            scenario.Counts.EnvError,
            scenario.Counts.Inconclusive);

        // #372: the engine's own cause, APPENDED rather than substituted. The shape summary is
        // what a publisher UI groups and diffs on, so replacing it would break triage that works
        // today; the cause is what tells a maintainer WHY, which is the whole gap. A stream with
        // no message renders byte-identically to before.
        //
        // This does not weaken the §17 rule stated in this file's header. That rule bars
        // OBSERVATIONS and captured or resolved VALUES — a response body, a row, a secret. This
        // is the engine's own refusal text, already scrubbed through the run's secret ledger by
        // the producer before it was ever stamped onto the record.
        return string.IsNullOrEmpty(scenario.Message)
            ? summary
            : summary + ": " + scenario.Message;
    }

    /// <summary>
    /// Formats a millisecond duration as a JUnit <c>time</c> attribute value: seconds
    /// with three decimal places, in the invariant culture (so a comma-decimal locale
    /// cannot corrupt the attribute).
    /// </summary>
    private static string FormatSeconds(long milliseconds)
        => (milliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------
    // XML escaping — the load-bearing safety helper.  Every dynamic string the
    // renderer emits passes through here, both to keep the document well-formed and
    // so no value can sneak through the markup (§17).
    // -------------------------------------------------------------------------

    /// <summary>
    /// XML-escapes the five markup-significant characters in <paramref name="value"/>:
    /// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, and <c>'</c>.  The ampersand is
    /// replaced FIRST so that the entity replacements introduced for the other
    /// characters are not themselves re-escaped.
    /// </summary>
    /// <remarks>
    /// XML 1.0 forbids every C0 control character below U+0020 EXCEPT TAB (U+0009),
    /// LF (U+000A) and CR (U+000D) — and the forbidden ones cannot even be represented
    /// as numeric character references.  The XML 1.0 <c>Char</c> production additionally
    /// forbids the two BMP non-characters U+FFFE and U+FFFF (both fall outside the
    /// permitted <c>[#xE000-#xFFFD]</c> range), and these are reachable here because
    /// System.Text.Json deserialises them into ordinary .NET string characters that flow
    /// through a scenario id or file path.  A single such character anywhere in a dynamic
    /// string (a scenario id, a failure message, …) would make the emitted document
    /// non-parseable and break CI ingestion.  We therefore DROP the forbidden C0 controls
    /// AND the two BMP non-characters U+FFFE/U+FFFF outright (dropping leaves the result
    /// unambiguously parseable, whereas a substitution would invent text the author never
    /// wrote), while preserving the three permitted whitespace controls and every other
    /// character — including legal astral-plane text carried by valid surrogate pairs.
    /// <para>
    /// A LONE / unpaired surrogate is a separate concern that never reaches this method.
    /// For an Extra-borne string field it is NOT rejected at parse — it is accepted as a
    /// <c>JsonElement</c> of kind String and throws <see cref="InvalidOperationException"/>
    /// only when read via <c>GetString()</c> during model-building; the renderer's
    /// per-line guard tolerates that read failure (the line is skipped, §14), so a lone
    /// surrogate never arrives here.  Only the (typed) envelope fields are validated at
    /// deserialisation; an Extra string field is not.
    /// </para>
    /// </remarks>
    /// <param name="value">The dynamic string to escape; <see langword="null"/> yields the empty string.</param>
    /// <returns>The XML-safe form of <paramref name="value"/>.</returns>
    private static string XmlEscape(string? value)
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
                    builder.Append("&apos;");
                    break;
                default:
                    // Drop the XML-1.0-forbidden characters reachable in a single BMP
                    // char — the C0 controls below U+0020 (except the three permitted
                    // whitespace controls TAB/LF/CR) and the two BMP non-characters
                    // U+FFFE/U+FFFF; emit every other character verbatim.
                    if (IsForbiddenXmlCharacter(ch))
                    {
                        break;
                    }

                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ch"/> is a character that the
    /// XML 1.0 <c>Char</c> production forbids and that is reachable here in a single BMP
    /// char: a C0 control below U+0020 that is not one of the three permitted whitespace
    /// controls TAB (U+0009), LF (U+000A) or CR (U+000D), or one of the two BMP
    /// non-characters U+FFFE / U+FFFF (which fall outside the permitted
    /// <c>[#xE000-#xFFFD]</c> range).  Valid surrogate PAIRS (astral-plane text) are legal
    /// XML and pass through untouched.  A LONE / unpaired surrogate is never seen here:
    /// for an Extra-borne string field it is NOT rejected at parse (only typed envelope
    /// fields are validated at deserialisation) — it throws
    /// <see cref="InvalidOperationException"/> when read via <c>GetString()</c>, which the
    /// renderer's per-line guard tolerates by skipping the line (§14), so it never flows
    /// into <see cref="XmlEscape(string?)"/>.
    /// </summary>
    private static bool IsForbiddenXmlCharacter(char ch)
        => (ch < ' ' && ch is not ('\t' or '\n' or '\r'))
           || ch is (char)0xFFFE or (char)0xFFFF;

    // -------------------------------------------------------------------------
    // Extra-field accessors — all defensive; never throw.  Ported from the sibling
    // renderers so all three read the flat wire shape identically.
    // -------------------------------------------------------------------------

    private static string? GetStr(EventEnvelope envelope, string key)
    {
        if (envelope.Extra is not null
            && envelope.Extra.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
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

    // -------------------------------------------------------------------------
    // In-memory report model — a light fold of the stream: scenarios in first-seen
    // order, each becoming one <testcase>.
    // -------------------------------------------------------------------------

    private sealed class ReportModel
    {
        private readonly Dictionary<(string RunId, string ScenarioId), ScenarioModel> _scenarioIndex = new();

        public List<ScenarioModel> Scenarios { get; } = new();

        public ScenarioModel GetOrAddScenario(string runId, string scenarioId)
        {
            // Key by (runId, scenarioId) — not scenarioId alone — so an aggregated
            // multi-run stream that reuses a scenario id keeps the two runs' scenarios
            // distinct rather than merging them into one testcase.
            var key = (runId, scenarioId);
            if (!_scenarioIndex.TryGetValue(key, out var scenario))
            {
                scenario = new ScenarioModel(scenarioId);
                _scenarioIndex[key] = scenario;
                Scenarios.Add(scenario);
            }

            return scenario;
        }
    }

    private sealed class ScenarioModel
    {
        public ScenarioModel(string scenarioId) => ScenarioId = scenarioId;

        public string ScenarioId { get; }

        public string? File { get; set; }

        public string? Verdict { get; set; }

        public long? DurationMs { get; set; }

        public (int Pass, int Fail, int EnvError, int Inconclusive) Counts { get; set; }

        /// <summary>The scenario-level cause, when the stream carried one (#372).</summary>
        public string? Message { get; set; }
    }
}
