// Terminal renderer v0 for the vouchfx structured JSON Lines event stream
// (§14, S02-G-02, S03-G-01).
//
// Design:
//   • Reads from IEnumerable<string> (JSON Lines) and writes to a TextWriter so
//     that callers can substitute StringWriter for testing without touching the
//     console.
//   • Tolerates unknown event types (silently skipped), unknown fields on known
//     events (they live in Extra and are never accessed), malformed JSON lines
//     (JsonException caught per-line; rendering continues), and blank lines
//     (checked before deserialisation).
//   • All per-event-type payload fields arrive in EventEnvelope.Extra because
//     the flat wire format places them at the root alongside the envelope fields,
//     and only the envelope-declared properties are mapped to typed fields.
//   • Uses CultureInfo.InvariantCulture for all numeric formatting so that the
//     output is locale-neutral (CA1305).
//   • S03-G-01: step-completed lines include the duration in milliseconds
//     (e.g. "  step 'ping': PASS (42 ms)").  When durationMs is absent the
//     suffix is omitted rather than throwing.  step-attempt lines include tMs
//     when present.  scenario-completed appends durationMs when present.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Platform.Engine.Abstractions.Events;

namespace Platform.Engine.Reporting;

/// <summary>
/// Consumes the schema-versioned JSON Lines event stream and prints
/// per-step verdicts with durations to a <see cref="TextWriter"/> (v0).
/// </summary>
/// <remarks>
/// <para>
/// The renderer is deliberately tolerant: unknown event types are silently
/// skipped, unknown fields on known events are ignored (they ride in
/// <see cref="EventEnvelope.Extra"/> and are never accessed), malformed JSON
/// lines are skipped rather than aborting the render, and blank lines are
/// filtered before deserialisation.  This satisfies the §14 guarantee that
/// older renderers remain useful against newer engine output.
/// </para>
/// <para>
/// This is the v0 renderer (S03-G-01).  It renders a single legible line per
/// step that includes the step id, verdict token, and wall-clock duration in
/// milliseconds, giving the compiler workstream a concrete feedback surface
/// for Phase 2.  Colours, progress bars, and diffing are deferred to a later
/// production release.
/// </para>
/// </remarks>
public sealed class TerminalRenderer
{
    /// <summary>
    /// Renders the supplied JSON Lines event stream to <paramref name="output"/>.
    /// </summary>
    /// <param name="jsonLines">
    /// The sequence of JSON Lines strings to render.  Blank, whitespace-only, and
    /// malformed lines are skipped silently.  The sequence is enumerated exactly
    /// once; it is safe to pass a streaming source.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered text.  Typical
    /// call sites pass <see cref="Console.Out"/> (production) or a
    /// <see cref="System.IO.StringWriter"/> (tests).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="jsonLines"/> or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static void Render(IEnumerable<string> jsonLines, TextWriter output)
        => Render(jsonLines, output, diffLookup: null);

    /// <summary>
    /// Renders the supplied JSON Lines event stream to <paramref name="output"/>,
    /// optionally drawing a provider-specific expected-vs-observed diff under each
    /// failed step (S07-G-01).
    /// </summary>
    /// <param name="jsonLines">
    /// The sequence of JSON Lines strings to render.  Blank, whitespace-only, and
    /// malformed lines are skipped silently.  The sequence is enumerated exactly
    /// once; it is safe to pass a streaming source.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered text.
    /// </param>
    /// <param name="diffLookup">
    /// An optional delegate that, given a step <c>kind</c> (e.g.
    /// <c>"db-assert.postgres"</c>) and the step's structured observation, returns the
    /// rendered diff text for that observation, or <see langword="null"/> when no diff
    /// is applicable.  Invoked only for a <c>step-completed</c> event whose verdict is
    /// <see cref="Verdict.Fail"/> and which carries an <c>observation</c>.  When
    /// <see langword="null"/> the renderer behaves exactly like the two-argument
    /// overload (no diff is drawn).
    /// </param>
    /// <remarks>
    /// <para>
    /// The diff is computed by <paramref name="diffLookup"/> at render time — the event
    /// stream itself carries only the structured observation, never rendered diff text
    /// (§14: one schema-versioned stream feeds every renderer).
    /// </para>
    /// <para>
    /// The delegate is intentionally a plain
    /// <see cref="Func{T1, T2, TResult}"/> over <see cref="JsonElement"/> so this
    /// assembly stays decoupled from <c>Platform.Sdk</c> and the
    /// <c>IStepDiffRenderer</c> type: the runner builds the closure over the frozen
    /// registry and passes it in.
    /// </para>
    /// <para>
    /// Because a <c>step-completed</c> event does not itself carry the step
    /// <c>kind</c>, the kind is threaded forward from the matching
    /// <c>step-started</c> event: the renderer builds a step-id → kind map from
    /// <c>step-started</c> events as it streams, then looks the kind up when a
    /// <c>step-completed</c> event arrives.
    /// </para>
    /// </remarks>
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

        // (runId, stepId) → kind map, populated from step-started events as the stream
        // is read.  A step-completed event does not carry its own kind, so we thread it
        // forward.  Keying by (runId, stepId) — not stepId alone — disambiguates an
        // aggregated multi-RUN stream where two runs reuse the same step id: without the
        // runId, a later run's step-started would overwrite the earlier run's kind and
        // its step-completed would resolve the wrong diff.  FULL interleaving correctness
        // for multiple SCENARIOS within ONE run still needs scenarioId on the step events
        // (they don't carry it today) and is tracked for the S8 parallelism work.
        var stepKinds = new Dictionary<(string RunId, string StepId), string>();

        foreach (var line in jsonLines)
        {
            // Skip blank / whitespace-only lines before attempting deserialisation.
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // The parse AND the per-line field extraction (step-kind recording plus the
            // RenderEnvelope read of every Extra field) share one try/catch so a single
            // unreadable line is skipped without aborting the whole render.  A lone /
            // unpaired UTF-16 surrogate in a string VALUE (e.g. the JSON escape "\uD800"
            // with no low-surrogate partner) PARSES fine — JsonDocument.Parse succeeds and
            // the value lands in Extra as a JsonElement of kind String — but throws
            // InvalidOperationException ("Cannot read incomplete UTF-16…") LATER, when
            // GetString() reads it during rendering.  That read happens below, so the guard
            // must span the extraction too; catching only JsonException (which fires at
            // parse time) would let the InvalidOperationException escape and abort the
            // entire render.  Both are tolerated per-line per §14 — the offending line is
            // skipped and the rest of the stream still renders.
            try
            {
                var envelope = EventStreamJson.FromLine(line);

                // Record step kinds so step-completed events can resolve their kind.
                if (envelope.Type == EventTypes.StepStarted)
                {
                    // runId is a typed envelope field (mapped from the wire "runId"); it
                    // does not ride in Extra, so it is read directly rather than via GetStr.
                    var startedRunId = envelope.RunId;
                    var startedStepId = GetStr(envelope, "stepId");
                    var startedKind = GetStr(envelope, "kind");
                    if (!string.IsNullOrEmpty(startedRunId) && startedStepId is not null && startedKind is not null)
                    {
                        stepKinds[(startedRunId, startedStepId)] = startedKind;
                    }
                }

                RenderEnvelope(envelope, output, stepKinds, diffLookup);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Malformed JSON (JsonException at parse) OR an unreadable string value such
                // as a lone surrogate (InvalidOperationException at GetString) — skip this
                // line and continue with the rest of the stream.  This ALSO tolerates a line
                // whose EventStreamJson.FromLine itself throws InvalidOperationException — a
                // null / non-object / missing-required-field line — which is skipped here
                // just like malformed JSON.  A diagnostic comment is intentionally omitted
                // here to keep the stub output clean; a future production renderer may write
                // one.
                continue;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Private rendering logic
    // -------------------------------------------------------------------------

    private static void RenderEnvelope(
        EventEnvelope envelope,
        TextWriter output,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        switch (envelope.Type)
        {
            case EventTypes.ScenarioStarted:
                {
                    var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                    output.WriteLine(
                        string.Format(CultureInfo.InvariantCulture, "Scenario '{0}' started", scenarioId));
                    break;
                }

            case EventTypes.StepStarted:
                {
                    var stepId = GetStr(envelope, "stepId") ?? "(unknown)";
                    output.WriteLine(
                        string.Format(CultureInfo.InvariantCulture, "  step '{0}' started", stepId));
                    break;
                }

            case EventTypes.StepAttempt:
                {
                    RenderAttemptTimelineLine(envelope, output);
                    break;
                }

            case EventTypes.StepCompleted:
                {
                    var stepId = GetStr(envelope, "stepId") ?? "(unknown)";
                    var verdict = GetStr(envelope, "verdict") ?? "(unknown)";
                    var durationMs = GetLong(envelope, "durationMs");
                    var durationSuffix = durationMs.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, " ({0} ms)", durationMs.Value)
                        : string.Empty;
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "  step '{0}': {1}{2}",
                            stepId,
                            verdict,
                            durationSuffix));

                    // S07-G-01: render a provider-specific expected-vs-observed diff
                    // under a FAILED step when a diff lookup is supplied and the step
                    // carries a structured observation.  The diff is computed here, at
                    // render time — the stream itself only ever carries the structured
                    // observation, never rendered text (§14).
                    RenderStepDiff(envelope, output, stepId, verdict, stepKinds, diffLookup);

                    // S08-G-02 (T9): render the captured-variable provenance thread for
                    // this step — every value it captured (name ← this step, JSONPath)
                    // and every {placeholder}/${secret:…} substituted INTO it (with the
                    // originating step id).  Rendered ONLY from the existing Captured /
                    // Substitutions fields; secret-derived substitutions render a
                    // redaction marker and never a value (§17).
                    RenderProvenanceThread(envelope, output, stepId);
                    break;
                }

            case EventTypes.ScenarioCompleted:
                {
                    var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                    var verdict = GetStr(envelope, "verdict") ?? "(unknown)";
                    var counts = ReadCounts(envelope);
                    var totalMs = GetLong(envelope, "durationMs");
                    var totalSuffix = totalMs.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, " total={0} ms", totalMs.Value)
                        : string.Empty;
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Scenario '{0}': {1}  (pass={2} fail={3} envError={4} inconclusive={5}{6})",
                            scenarioId,
                            verdict,
                            counts.Pass,
                            counts.Fail,
                            counts.EnvError,
                            counts.Inconclusive,
                            totalSuffix));
                    break;
                }

            case EventTypes.EnvironmentError:
                {
                    var resourceName = GetStr(envelope, "resourceName") ?? "(unknown)";
                    var errorKind = GetStr(envelope, "errorKind") ?? "(unknown)";
                    var registryHost = GetStr(envelope, "registryHost") ?? "(none)";
                    var detail = GetStr(envelope, "detail") ?? "(no detail)";
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Environment error on '{0}' [{1}] registry={2}: {3}",
                            resourceName,
                            errorKind,
                            registryHost,
                            detail));
                    break;
                }

            // Unknown / unrendered event types (SuiteStarted, SuiteCompleted, and
            // any type introduced by a future engine release) are silently ignored.
            // This is the core §14 forward-compatibility guarantee.
            default:
                break;
        }
    }

    /// <summary>
    /// Renders the provider-specific expected-vs-observed diff under a step line when
    /// the step failed, a <paramref name="diffLookup"/> is supplied, and the step
    /// carries a structured <c>observation</c> whose kind resolves to a provider that
    /// can render it (S07-G-01).  A no-op otherwise.
    /// </summary>
    private static void RenderStepDiff(
        EventEnvelope envelope,
        TextWriter output,
        string stepId,
        string verdict,
        IReadOnlyDictionary<(string RunId, string StepId), string> stepKinds,
        Func<string, JsonElement, string?>? diffLookup)
    {
        // Only failed steps get a diff, and only when a lookup is wired in.
        if (diffLookup is null
            || !string.Equals(verdict, "FAIL", StringComparison.Ordinal))
        {
            return;
        }

        // The observation rides on the step-completed event as extension data.
        if (envelope.Extra is null
            || !envelope.Extra.TryGetValue("observation", out var observation)
            || observation.ValueKind == JsonValueKind.Null
            || observation.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        // Resolve the step kind threaded forward from the step-started event, keyed by
        // (runId, stepId) so an aggregated multi-run stream does not cross-resolve a
        // shared step id.  runId is a typed envelope field (read directly, not via Extra).
        // Without a runId or a recorded kind we cannot select a provider's diff renderer,
        // so we skip the diff.
        var runId = envelope.RunId;
        if (string.IsNullOrEmpty(runId) || !stepKinds.TryGetValue((runId, stepId), out var kind))
        {
            return;
        }

        var diff = diffLookup(kind, observation);
        if (string.IsNullOrEmpty(diff))
        {
            return;
        }

        // Indent each line of the rendered diff under the step line.
        foreach (var diffLine in diff.Split('\n'))
        {
            // Skip a trailing empty segment from a terminal newline so we do not emit a
            // stray blank indented line.
            if (diffLine.Length == 0)
            {
                continue;
            }

            output.WriteLine("    " + diffLine);
        }
    }

    /// <summary>
    /// Renders the captured-variable provenance thread for a <c>step-completed</c>
    /// event (S08-G-02, T9, docs/01 §14): the values this step <em>captured</em> and
    /// the <c>{placeholder}</c> / <c>${secret:…}</c> references substituted <em>into</em>
    /// it, so an author can trace a value end-to-end across the scenario.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered exclusively from the existing <c>captured</c> and <c>substitutions</c>
    /// arrays on the step-completed event (frozen by T3); it adds no field to the event
    /// contract.  The section is drawn only when the step has at least one capture or
    /// substitution, so a plain step run produces no provenance noise.
    /// </para>
    /// <para>
    /// <strong>§17 secret-safety.</strong>  A substitution whose <c>secretDerived</c>
    /// flag is <see langword="true"/> renders as a redaction marker — its
    /// <c>placeholder</c> field carries the non-sensitive secret <em>reference</em>
    /// label (e.g. <c>env/API_TOKEN</c>), never a value, and the value is never present
    /// in the stream to begin with.  The rendering makes the secret-derived provenance
    /// visible WITHOUT the value.  Captured variable names and JSONPaths are metadata
    /// (safe to show); captured values are not in the stream and are never invented.
    /// </para>
    /// <para>
    /// The walk is a stateless function of THIS event's own fields, so an aggregated /
    /// parallel multi-scenario stream (each scenario a distinct <c>runId</c>, T1) cannot
    /// cross-contaminate: a step's captures and substitutions are read from the step's
    /// own event, and the origin step id printed for a substitution is the one the
    /// compiler recorded on that same event — no shared per-var state is keyed across
    /// runs.
    /// </para>
    /// </remarks>
    private static void RenderProvenanceThread(EventEnvelope envelope, TextWriter output, string stepId)
    {
        var captured = ReadArray(envelope, "captured");
        var substitutions = ReadArray(envelope, "substitutions");

        if (captured.Length == 0 && substitutions.Length == 0)
        {
            // No provenance to show for this step — emit nothing (no empty section).
            return;
        }

        output.WriteLine("    provenance:");

        // Captures: each value this step extracted, named and traced to its JSONPath.
        // "captured 'orderId' <- step 'create-order' ($.id)".
        foreach (var capture in captured)
        {
            if (capture.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetStrFromObject(capture, "name") ?? "(unknown)";
            var path = GetStrFromObject(capture, "path") ?? "(unknown)";
            var matched = GetBoolFromObject(capture, "matched");

            // A capture whose JSONPath matched nothing is called out explicitly so the
            // reader can see why a downstream substitution had no value to thread.
            var matchSuffix = matched ? string.Empty : "   (no match)";

            output.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "      captured '{0}' <- step '{1}' ({2}){3}",
                    name,
                    stepId,
                    path,
                    matchSuffix));
        }

        // Substitutions: each {placeholder} / ${secret:…} threaded INTO this step.
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
                // label (e.g. env/API_TOKEN); render it with a redaction marker and never
                // a value.  The value is not in the stream — the marker makes the
                // secret-derived provenance visible WITHOUT it.
                output.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "      substituted ${{secret:{0}}} (redacted) -> step '{1}'",
                        placeholder,
                        stepId));
            }
            else
            {
                // A plain placeholder: trace it back to the step that first captured it
                // when that origin is known, else note it as untraced (e.g. it came from
                // the variables block rather than a prior capture).
                var origin = string.IsNullOrEmpty(originStepId)
                    ? "(variables/untraced)"
                    : string.Format(CultureInfo.InvariantCulture, "step '{0}'", originStepId);

                output.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "      substituted '{{{0}}}' (from {1}) -> step '{2}'",
                        placeholder,
                        origin,
                        stepId));
            }
        }
    }

    /// <summary>
    /// Renders a single attempt of a step as one legible line of the polling
    /// timeline (S08-G-01, docs/01 §14.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line is indented beneath the step it belongs to and carries the
    /// elapsed time relative to step start (rendered in seconds), the
    /// one-based attempt counter, and the per-attempt outcome — turning a RETRY
    /// step's asynchrony back into the story the author wrote rather than a
    /// final pass-or-fail at the end of a timeout window.  Example:
    /// </para>
    /// <code>
    ///       t=  0.5s   attempt 1   INCONCLUSIVE
    ///       t=  1.5s   attempt 2   FAIL
    /// </code>
    /// <para>
    /// Renders exclusively from the existing <c>StepAttemptEvent</c> fields
    /// (<c>attempt</c>, <c>tMs</c>, <c>outcome</c>, <c>observation</c>); it adds
    /// no field to the event contract.  The optional observation summary is the
    /// provider's structured observation, which is metadata-only by construction
    /// (§17) — no captured or secret value can appear here.  An attempt whose
    /// outcome the engine has not yet resolved (a mid-RETRY poll with a null
    /// <c>outcome</c>) renders a <c>(pending)</c> marker in place of a verdict.
    /// </para>
    /// </remarks>
    private static void RenderAttemptTimelineLine(EventEnvelope envelope, TextWriter output)
    {
        var attempt = GetInt(envelope, "attempt");
        var outcome = GetStr(envelope, "outcome") ?? "(pending)";
        var tMs = GetLong(envelope, "tMs");

        // Elapsed time relative to step start, in seconds with one decimal place,
        // right-padded so the "attempt" columns line up across attempts (the §14.5
        // example aligns on the elapsed column).  Absent timing renders as "?".
        var elapsed = tMs.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0,5:0.0}s", tMs.Value / 1000.0)
            : "    ?s";

        var observationSummary = SummariseObservation(envelope);

        output.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "      t={0}   attempt {1}   {2}{3}",
                elapsed,
                attempt,
                outcome,
                observationSummary));
    }

    /// <summary>
    /// Produces a compact, secret-safe one-line summary of an attempt's structured
    /// <c>observation</c> for the polling timeline, or the empty string when no
    /// renderable observation is present.
    /// </summary>
    /// <remarks>
    /// The observation is provider-supplied structured metadata (e.g.
    /// <c>{"matched":0}</c> or a diff) and is metadata-only by construction (§17),
    /// so it carries no captured or secret value.  Even so, the secret-safety
    /// invariant here is kept <em>total</em>: nothing from the observation is ever
    /// rendered verbatim.  An object/array is summarised by its shape (its keys or
    /// length), and a SCALAR observation is summarised by its JSON value-kind
    /// (<c>&lt;string&gt;</c> / <c>&lt;number&gt;</c> / <c>&lt;bool&gt;</c>) rather
    /// than its literal value.  No Core provider emits a bare-scalar observation
    /// today, but <c>StepAttemptEvent.Observation</c> advertises "raw response" as
    /// legitimate, so a future provider could place a sensitive scalar there; a
    /// shape hint ensures the polling timeline never prints that value.  The full
    /// expected-vs-observed diff is left to the <c>step-completed</c> diff renderer.
    /// A leading separator is included so the caller can concatenate the result
    /// directly.
    /// </remarks>
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
            // Scalars are rendered as a SHAPE HINT (their JSON value-kind), never
            // verbatim: the keys-only secret-safety invariant is total, so a future
            // provider that puts a sensitive scalar in the observation cannot leak it
            // onto the timeline.  The full diff (if any) renders under step-completed.
            JsonValueKind.String => "<string>",
            JsonValueKind.Number => "<number>",
            JsonValueKind.True or JsonValueKind.False => "<bool>",

            // Objects and arrays get a brief shape hint rather than their full
            // contents; the full diff is rendered under the step-completed line.
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

    /// <summary>
    /// Describes a JSON object observation as a brief, comma-separated list of its
    /// property names, e.g. <c>{matched, diff}</c>.  Property <em>values</em> are
    /// deliberately omitted to keep the timeline terse and to avoid surfacing any
    /// value that could be sensitive; the keys alone tell the reader what the
    /// provider observed on this attempt.
    /// </summary>
    private static string DescribeObject(JsonElement observation)
    {
        var keys = observation
            .EnumerateObject()
            .Select(static p => p.Name);

        return "{" + string.Join(", ", keys) + "}";
    }

    // -------------------------------------------------------------------------
    // Extra-field accessors — all defensive; never throw.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the string value of <paramref name="key"/> from
    /// <see cref="EventEnvelope.Extra"/>, or <see langword="null"/> when the key
    /// is absent or not a JSON string.
    /// </summary>
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

    /// <summary>
    /// Returns the integer value of <paramref name="key"/> from
    /// <see cref="EventEnvelope.Extra"/>, or <c>0</c> when the key is absent or
    /// not a JSON number.
    /// </summary>
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

    /// <summary>
    /// Returns the <see cref="long"/> value of <paramref name="key"/> from
    /// <see cref="EventEnvelope.Extra"/>, or <see langword="null"/> when the key
    /// is absent or not a JSON number.  Returning <see langword="null"/> rather
    /// than a sentinel allows callers to distinguish a genuine zero from an
    /// absent field and to omit the duration suffix gracefully.
    /// </summary>
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

    /// <summary>
    /// Reads the nested <c>counts</c> object from <see cref="EventEnvelope.Extra"/>
    /// and returns the four verdict counts.  Any missing sub-field defaults to
    /// <c>0</c>; a missing <c>counts</c> object returns all zeros.
    /// </summary>
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

    /// <summary>
    /// Reads an integer sub-property from a <see cref="JsonElement"/> of kind
    /// <see cref="JsonValueKind.Object"/>.  Returns <c>0</c> if the property is
    /// absent or not a number.
    /// </summary>
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

    /// <summary>
    /// Returns the elements of the JSON array stored under <paramref name="key"/> in
    /// <see cref="EventEnvelope.Extra"/>, or an empty list when the key is absent, not
    /// an array, or <see langword="null"/>.  Defensive by design — never throws.
    /// </summary>
    /// <remarks>
    /// Used to read the <c>captured</c> / <c>substitutions</c> provenance arrays that
    /// ride on a <c>step-completed</c> event as extension data (S08-G-02).  Each
    /// element is a <see cref="JsonElement"/> of kind <see cref="JsonValueKind.Object"/>
    /// in practice; callers re-check the kind before reading sub-properties.
    /// </remarks>
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

    /// <summary>
    /// Reads a string sub-property from a <see cref="JsonElement"/> of kind
    /// <see cref="JsonValueKind.Object"/>.  Returns <see langword="null"/> if the
    /// property is absent or not a JSON string.
    /// </summary>
    private static string? GetStrFromObject(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    /// <summary>
    /// Reads a boolean sub-property from a <see cref="JsonElement"/> of kind
    /// <see cref="JsonValueKind.Object"/>.  Returns <see langword="false"/> if the
    /// property is absent or not a JSON boolean (so an omitted <c>secretDerived</c> /
    /// <c>matched</c> defaults to <see langword="false"/>).
    /// </summary>
    private static bool GetBoolFromObject(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return prop.GetBoolean();
        }

        return false;
    }
}
