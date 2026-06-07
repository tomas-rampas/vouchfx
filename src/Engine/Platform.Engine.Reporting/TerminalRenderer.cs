// Minimal terminal renderer stub for the vouchfx structured JSON Lines event
// stream (§14, S02-G-02).
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

using System.Globalization;
using System.Text.Json;
using Platform.Engine.Abstractions.Events;

namespace Platform.Engine.Reporting;

/// <summary>
/// Consumes the schema-versioned JSON Lines event stream and prints
/// per-step verdicts to a <see cref="TextWriter"/>.
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
/// This is a <em>stub</em> renderer.  Its purpose is to give the compiler
/// workstream a concrete reporting surface to exercise continuously, not to
/// provide the full terminal UX (colours, progress bars, diffing) that a
/// production release will ship.
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
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        ArgumentNullException.ThrowIfNull(output);

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

            RenderEnvelope(envelope, output);
        }
    }

    // -------------------------------------------------------------------------
    // Private rendering logic
    // -------------------------------------------------------------------------

    private static void RenderEnvelope(EventEnvelope envelope, TextWriter output)
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
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "    attempt {0} -> {1}  [{2}]",
                            attempt,
                            outcome,
                            stepId));
                    break;
                }

            case EventTypes.StepCompleted:
                {
                    var stepId = GetStr(envelope, "stepId") ?? "(unknown)";
                    var verdict = GetStr(envelope, "verdict") ?? "(unknown)";
                    output.WriteLine(
                        string.Format(CultureInfo.InvariantCulture, "  step '{0}': {1}", stepId, verdict));
                    break;
                }

            case EventTypes.ScenarioCompleted:
                {
                    var scenarioId = GetStr(envelope, "scenarioId") ?? "(unknown)";
                    var verdict = GetStr(envelope, "verdict") ?? "(unknown)";
                    var counts = ReadCounts(envelope);
                    output.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Scenario '{0}': {1}  (pass={2} fail={3} envError={4} inconclusive={5})",
                            scenarioId,
                            verdict,
                            counts.Pass,
                            counts.Fail,
                            counts.EnvError,
                            counts.Inconclusive));
                    break;
                }

            // Unknown / unrendered event types (SuiteStarted, SuiteCompleted, and
            // any type introduced by a future engine release) are silently ignored.
            // This is the core §14 forward-compatibility guarantee.
            default:
                break;
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
