// Vouchfx.Cli — CliJsonContract (#260).
//
// Shared serialisation contract for the CLI's tooling-facing `--json` document outputs
// (`validate --json`, `list --json`). Centralised here for the same reason
// Vouchfx.Engine.Abstractions.Events.EventStreamJson centralises the event-stream
// JsonSerializerOptions: every `--json` document on this CLI must share ONE
// JsonSerializerOptions instance, ONE schemaVersion constant, and ONE engineVersion
// resolver, so the wire shape can never drift between commands.
//
// Unlike EventStreamJson (JSON LINES — one compact object per line), these are each a
// SINGLE document written once to stdout, so WriteIndented=true is used for readability.
// Both documents are pinned byte-for-byte by golden tests (ValidateJsonGoldenTests,
// ListJsonGoldenTests) — this IS the frozen contract a downstream tool (e.g. the
// vouchfx-mcp validate worker) can parse without depending on any Cli-internal type.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Cli;

/// <summary>
/// Shared serialisation options and metadata for the CLI's <c>--json</c> document
/// outputs (<c>validate --json</c>, <c>list --json</c>).
/// </summary>
internal static class CliJsonContract
{
    /// <summary>The wire schema version stamped on every CLI <c>--json</c> document.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>
    /// The single shared <see cref="JsonSerializerOptions"/> for every CLI <c>--json</c>
    /// document: camelCase property names, camelCase enum values, and indented output
    /// (each document is written once, not as JSON Lines).
    /// </summary>
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// The informational version of the running <c>vouchfx</c> executable (e.g.
    /// <c>"1.0.0-alpha.9"</c>), or <c>"unknown"</c> when the attribute is absent — a
    /// <c>--json</c> document must never throw over a missing version stamp.
    /// </summary>
    internal static string EngineVersion { get; } =
        typeof(CliJsonContract).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    /// <summary>
    /// Serialises <paramref name="document"/> using <see cref="Options"/> and writes it to
    /// <paramref name="output"/> as a single line-terminated block.
    /// </summary>
    internal static void Write<T>(T document, TextWriter output) =>
        output.WriteLine(JsonSerializer.Serialize(document, Options));
}
