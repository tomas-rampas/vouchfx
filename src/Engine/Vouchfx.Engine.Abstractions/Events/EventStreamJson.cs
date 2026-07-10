// Shared JSON serialisation helpers for the vouchfx structured event stream.
//
// Design constraints:
//   • System.Text.Json only — no third-party serialisers (§5.7, library
//     invariants).  This project carries zero PackageReferences; STJ ships
//     in-box with net8.0.
//   • WriteIndented = false: JSON Lines requires exactly one compact object per
//     line; embedded newlines would break every line-oriented consumer.
//   • DefaultIgnoreCondition = WhenWritingNull: null optional fields (e.g.
//     CorrelationIds) are omitted from the wire, matching the §14.4 examples.
//   • No naming policy is applied.  The EventEnvelope properties that need
//     non-default wire names carry explicit [JsonPropertyName] attributes.
//     Applying a CamelCase policy would re-rename those properties a second time
//     (e.g. RunId → "runId" before the attribute, then the attribute wins, but
//     the Extra bag would also be affected), so the safest and most explicit
//     approach is no policy at all.

using System.Text.Json;
using System.Text.Json.Serialization;

// VerdictJsonConverter lives in the parent namespace; bring it in without
// polluting the public API surface of this file.
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Abstractions.Events;

/// <summary>
/// Static helper that owns the single shared <see cref="JsonSerializerOptions"/>
/// instance and provides <see cref="ToLine"/> / <see cref="FromLine"/> for the
/// structured JSON Lines event stream (§14.4).
/// </summary>
/// <remarks>
/// All event emission and all renderer ingestion MUST go through these helpers
/// so that the wire format is controlled from one place.  Creating ad-hoc
/// <see cref="JsonSerializerOptions"/> instances in caller code is prohibited —
/// it risks inconsistent serialisation (e.g. accidentally writing indented JSON
/// that violates the JSON Lines contract).
/// </remarks>
public static class EventStreamJson
{
    /// <summary>
    /// The single shared <see cref="JsonSerializerOptions"/> instance used for
    /// all event-stream serialisation and deserialisation.
    /// </summary>
    /// <remarks>
    /// Options selected:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WriteIndented = false</c> — JSON Lines mandate: one compact object
    ///     per line; embedded newlines would corrupt line-oriented consumers.
    ///   </description></item>
    ///   <item><description>
    ///     <c>DefaultIgnoreCondition = WhenWritingNull</c> — null optional fields
    ///     are omitted from the wire, consistent with the §14.4 examples.
    ///   </description></item>
    ///   <item><description>
    ///     No <c>PropertyNamingPolicy</c> — wire names are controlled exclusively
    ///     via <c>[JsonPropertyName]</c> attributes on the record properties, which
    ///     is safer than relying on a naming transformation that could interact
    ///     unexpectedly with extension-data keys.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The instance is frozen via <see cref="JsonSerializerOptions.MakeReadOnly()"/>
    /// at construction time so that callers cannot mutate the shared options after
    /// first use, which would silently corrupt serialisation across the process.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new VerdictJsonConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Serialises <paramref name="envelope"/> to a compact single-line JSON
    /// string suitable for appending to a JSON Lines stream.
    /// </summary>
    /// <param name="envelope">The event envelope to serialise.</param>
    /// <returns>
    /// A compact JSON object string with no embedded newline characters.
    /// </returns>
    public static string ToLine(EventEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// Deserialises a single JSON Lines record into an <see cref="EventEnvelope"/>.
    /// Unknown fields are captured in <see cref="EventEnvelope.Extra"/> and are
    /// preserved on re-serialisation (§14 forward-compatibility guarantee).
    /// </summary>
    /// <param name="line">
    /// A single JSON object string, as produced by <see cref="ToLine"/> or
    /// emitted by any compatible engine version.
    /// </param>
    /// <returns>The deserialised envelope.</returns>
    /// <exception cref="JsonException">
    /// Thrown if <paramref name="line"/> is not valid JSON or does not represent
    /// an object.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the required fields (<c>type</c>, <c>runId</c>) are absent.
    /// </exception>
    public static EventEnvelope FromLine(string line) =>
        JsonSerializer.Deserialize<EventEnvelope>(line, Options)
            ?? throw new InvalidOperationException(
                "Deserialisation of event-stream line produced a null result; " +
                "the input was not a JSON object.");

    /// <summary>
    /// Serialises <paramref name="payload"/> to a compact single-line JSON
    /// string suitable for appending to a JSON Lines stream.
    /// </summary>
    /// <typeparam name="T">
    /// The typed event-payload record (e.g. <see cref="StepCompletedEvent"/>).
    /// </typeparam>
    /// <param name="payload">The payload record to serialise.</param>
    /// <returns>
    /// A compact JSON object string with no embedded newline characters.
    /// </returns>
    public static string ToLine<T>(T payload) =>
        JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// Deserialises a single JSON Lines record into the specified typed
    /// event-payload record.
    /// </summary>
    /// <typeparam name="T">
    /// The typed event-payload record (e.g. <see cref="StepCompletedEvent"/>).
    /// </typeparam>
    /// <param name="line">
    /// A single JSON object string, as produced by <see cref="ToLine{T}"/> or
    /// emitted by any compatible engine version.
    /// </param>
    /// <returns>The deserialised payload record.</returns>
    /// <exception cref="JsonException">
    /// Thrown if <paramref name="line"/> is not valid JSON or cannot be
    /// deserialised as <typeparamref name="T"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if deserialisation produces a <see langword="null"/> result.
    /// </exception>
    public static T FromLine<T>(string line) =>
        JsonSerializer.Deserialize<T>(line, Options)
            ?? throw new InvalidOperationException(
                $"Deserialisation of event-stream line as {typeof(T).Name} produced a null result; " +
                "the input was not a JSON object.");
}
