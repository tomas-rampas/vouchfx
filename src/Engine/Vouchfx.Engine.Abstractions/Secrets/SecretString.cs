// Vouchfx.Engine.Abstractions — SecretString (S05-B-02, §17).
//
// The typed carrier of a resolved secret value. Redaction is enforced AT THE
// SOURCE (§17): the value is never exposed by ToString(), never formatted via
// IFormattable, never implicitly converted to string, and never serialised by
// System.Text.Json. The single deliberate escape hatch is Reveal() — greppable,
// auditable, and invoked only at an injection sink (e.g. an HTTP header value)
// inside the collectible script context.
//
// Memory-model note (§5): this is a pure managed object with no static state.
// When the collectible AssemblyLoadContext unloads, an instance survives only as
// an ordinary object held by whatever sink consumed it; nothing here roots the
// context back into the Default context.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Abstractions.Secrets;

/// <summary>
/// A resolved secret value with redaction enforced at the source (§17).
/// </summary>
/// <remarks>
/// <para>
/// The wrapped value is reachable through exactly one member —
/// <see cref="Reveal"/> — which is intentionally <see langword="public"/> so the
/// emitted script (running in a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>)
/// can feed the value into an injection sink at execution time. Every other path
/// out of the type is redacted:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ToString"/> returns <see cref="RedactedMarker"/>, never the value.
///   </description></item>
///   <item><description>
///     The type does <strong>not</strong> implement <see cref="IFormattable"/> and
///     defines <strong>no</strong> implicit <see langword="string"/> conversion, so
///     it can never be coerced to its value by string interpolation or formatting.
///   </description></item>
///   <item><description>
///     The bundled <see cref="JsonConverter{T}"/> writes <see cref="RedactedMarker"/>,
///     so <c>System.Text.Json.JsonSerializer.Serialize(secret)</c> can never emit
///     the value.
///   </description></item>
/// </list>
/// <para>
/// The constructor is <see langword="internal"/>: only the secrets subsystem (and,
/// via <c>InternalsVisibleTo</c>, its tests) may mint a <see cref="SecretString"/>.
/// </para>
/// <para>
/// <strong>This is intentionally a <see langword="sealed"/> <see langword="class"/>,
/// NOT a <c>record</c> — refactoring it to a record is forbidden.</strong> A record's
/// compiler-generated structural <c>Equals</c>/<c>GetHashCode</c> compare the wrapped
/// <c>_value</c>, which would create a value-comparison oracle: an attacker (or a
/// careless caller) could confirm a guessed secret by equality or hash-collision
/// probing, defeating the redaction-at-source guarantee (§17). The reference-equality
/// semantics of a plain class are deliberate.
/// </para>
/// </remarks>
[JsonConverter(typeof(SecretStringJsonConverter))]
public sealed class SecretString
{
    /// <summary>
    /// The marker text emitted in place of any secret value — by
    /// <see cref="ToString"/> and by the JSON converter.
    /// </summary>
    public const string RedactedMarker = "***REDACTED***";

    private readonly string _value;

    /// <summary>
    /// Initialises a new instance wrapping <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The resolved secret value; must not be <see langword="null"/>.</param>
    /// <remarks>
    /// Internal by design: only the secrets subsystem may construct a
    /// <see cref="SecretString"/>, ensuring every value entering the type has
    /// passed through a resolver (§17).
    /// </remarks>
    internal SecretString(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets the length of the wrapped value. <see langword="internal"/> by design:
    /// even the exact length is an oracle channel (it narrows brute-force/guessing
    /// of the secret), and nothing in production needs it publicly. Reachable only
    /// from the secrets subsystem and, via <c>InternalsVisibleTo</c>, its tests (§17).
    /// </summary>
    internal int Length => _value.Length;

    /// <summary>
    /// The one deliberate, auditable escape hatch: returns the raw secret value.
    /// </summary>
    /// <returns>The wrapped value verbatim.</returns>
    /// <remarks>
    /// Greppable by design. Call this only at the moment a value is handed to an
    /// injection sink (e.g. an HTTP <c>Authorization</c> header); never write the
    /// result back into <c>Vars</c> or any logged/serialised structure (§17).
    /// </remarks>
    public string Reveal() => _value;

    /// <summary>
    /// Returns <see cref="RedactedMarker"/> — never the wrapped value.
    /// </summary>
    /// <returns>The redaction marker.</returns>
    public override string ToString() => RedactedMarker;
}

/// <summary>
/// A <see cref="JsonConverter{T}"/> that guarantees a <see cref="SecretString"/>
/// never serialises its value: it writes <see cref="SecretString.RedactedMarker"/>
/// and refuses to deserialise (a secret is minted only by a resolver, never read
/// back from JSON, §17).
/// </summary>
public sealed class SecretStringJsonConverter : JsonConverter<SecretString>
{
    /// <summary>
    /// Always throws: a <see cref="SecretString"/> is never reconstituted from JSON.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override SecretString Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => throw new NotSupportedException(
            "A SecretString cannot be deserialised from JSON; secret values are " +
            "minted only by a resolver at run time (§17).");

    /// <summary>
    /// Writes <see cref="SecretString.RedactedMarker"/> instead of the value.
    /// </summary>
    public override void Write(
        Utf8JsonWriter writer,
        SecretString value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(SecretString.RedactedMarker);
}
