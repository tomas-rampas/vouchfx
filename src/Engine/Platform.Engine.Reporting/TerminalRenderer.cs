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

        // Step-id → kind map, populated from step-started events as the stream is read.
        // A step-completed event does not carry its own kind, so we thread it forward.
        var stepKinds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in jsonLines)
        {
            // Skip blank / whitespace-only lines before attempting deserialisation.
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            EventEnvelope envelope;
            try
            {
                envelope = EventStreamJson.FromLine(line);
            }
            catch (JsonException)
            {
                // Malformed JSON — skip this line and continue with the rest of the
                // stream.  A diagnostic comment is intentionally omitted here to keep
                // the stub output clean; a future production renderer may write one.
                continue;
            }

            // Record step kinds so step-completed events can resolve their kind.
            if (envelope.Type == EventTypes.StepStarted)
            {
                var startedStepId = GetStr(envelope, "stepId");
                var startedKind = GetStr(envelope, "kind");
                if (startedStepId is not null && startedKind is not null)
                {
                    stepKinds[startedStepId] = startedKind;
                }
            }

            RenderEnvelope(envelope, output, stepKinds, diffLookup);
        }
    }

    // -------------------------------------------------------------------------
    // Private rendering logic
    // -------------------------------------------------------------------------

    private static void RenderEnvelope(
        EventEnvelope envelope,
        TextWriter output,
        IReadOnlyDictionary<string, string> stepKinds,
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
                    var stepId = GetStr(envelope, "stepId") ?? "(unknown)";
                    var attempt = GetInt(envelope, "attempt");
                    var outcome = GetStr(envelope, "outcome") ?? "(pending)";
                    var tMs = GetLong(envelope, "tMs");
                    var attemptSuffix = tMs.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, " ({0} ms)", tMs.Value)
                        : string.Empty;
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "    attempt {0} -> {1}{2}  [{3}]",
                            attempt,
                            outcome,
                            attemptSuffix,
                            stepId));
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
        IReadOnlyDictionary<string, string> stepKinds,
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

        // Resolve the step kind threaded forward from the step-started event.  Without
        // a kind we cannot select a provider's diff renderer, so we skip the diff.
        if (!stepKinds.TryGetValue(stepId, out var kind))
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
}
