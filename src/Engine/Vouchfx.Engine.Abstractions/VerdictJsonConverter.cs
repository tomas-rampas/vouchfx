// BCL-only JsonConverter for Verdict (§12.1).
//
// The explicit converter pins the four canonical wire tokens precisely and is
// independently unit-testable.  [JsonStringEnumConverter] is intentionally NOT
// used because it would produce the enum member names ("EnvironmentError")
// rather than the specified tokens ("ENV_ERROR"), and it cannot be constrained
// to exactly four values without additional reflection overhead.
//
// Wire tokens (case-sensitive):
//   Pass             → "PASS"
//   Fail             → "FAIL"
//   EnvironmentError → "ENV_ERROR"
//   Inconclusive     → "INCONCLUSIVE"

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// <see cref="JsonConverter{T}"/> for <see cref="Verdict"/> that maps enum
/// members to their canonical wire tokens and rejects anything else with a
/// <see cref="JsonException"/>.
/// </summary>
/// <remarks>
/// This converter is registered on <see cref="Events.EventStreamJson.Options"/>
/// so all event-stream serialisation uses the correct tokens automatically.
/// Register it on any additional <see cref="JsonSerializerOptions"/> instance
/// that needs to handle <see cref="Verdict"/> values.
/// </remarks>
public sealed class VerdictJsonConverter : JsonConverter<Verdict>
{
    private const string TokenPass = "PASS";
    private const string TokenFail = "FAIL";
    private const string TokenEnvError = "ENV_ERROR";
    private const string TokenInconclusive = "INCONCLUSIVE";

    /// <inheritdoc />
    public override Verdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a JSON string for {nameof(Verdict)} but got {reader.TokenType}.");
        }

        var token = reader.GetString();

        return token switch
        {
            TokenPass => Verdict.Pass,
            TokenFail => Verdict.Fail,
            TokenEnvError => Verdict.EnvironmentError,
            TokenInconclusive => Verdict.Inconclusive,
            _ => throw new JsonException(
                $"Unknown {nameof(Verdict)} token \"{token}\". " +
                $"Accepted tokens (case-sensitive): " +
                $"\"{TokenPass}\", \"{TokenFail}\", \"{TokenEnvError}\", \"{TokenInconclusive}\"."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Verdict value, JsonSerializerOptions options)
    {
        var token = value switch
        {
            Verdict.Pass => TokenPass,
            Verdict.Fail => TokenFail,
            Verdict.EnvironmentError => TokenEnvError,
            Verdict.Inconclusive => TokenInconclusive,
            _ => throw new JsonException(
                $"Unrecognised {nameof(Verdict)} value {value}. " +
                "This is an engine bug; all enum members must be covered."),
        };

        writer.WriteStringValue(token);
    }
}
