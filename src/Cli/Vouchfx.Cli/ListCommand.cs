// Vouchfx.Cli — ListCommand (#260).
//
// The `vouchfx list [--step-types] [--json]` subcommand: lists catalogue information
// about this CLI build. `--step-types` is the only mode today AND the default when
// omitted (a future mode — e.g. listing discovered scenarios by tag — can be added
// later as an additional, explicit flag without a breaking change). Docker-free: it
// only reflects over the frozen Core StepKindRegistry (ProviderRegistryFactory), the
// SAME registry `run` and `validate` freeze at startup.

using System.CommandLine;
using System.Text.Json.Serialization;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>list</c> subcommand.
/// </summary>
internal static class ListCommand
{
    /// <summary>
    /// Builds the <c>list</c> <see cref="Command"/>, wiring its async action to
    /// <see cref="Execute"/>.
    /// </summary>
    /// <returns>The configured <c>list</c> command, ready to add to the root.</returns>
    public static Command Build()
    {
        var command = new Command(
            "list",
            "List catalogue information about this CLI build. --step-types (the only mode "
            + "today, and the default when omitted) lists every registered dotted step-type "
            + "key (family.provider).");

        var stepTypesOption = BuildStepTypesOption();
        command.Add(stepTypesOption);

        var jsonOption = BuildJsonOption();
        command.Add(jsonOption);

        command.SetAction((parseResult, _) =>
        {
            // --step-types is the only mode and the default when omitted: its presence
            // does not currently change behaviour, but the explicit flag exists so a
            // future second mode can be added as a sibling flag without breaking this one.
            parseResult.GetValue(stepTypesOption);
            var json = parseResult.GetValue(jsonOption);
            return Task.FromResult(Execute(json, Console.Out));
        });

        return command;
    }

    /// <summary>
    /// The <c>--step-types</c> flag: list every registered dotted step-type key. The only
    /// mode today, and the default when omitted.
    /// </summary>
    internal static Option<bool> BuildStepTypesOption() => new("--step-types")
    {
        Description = "List every registered dotted step-type key (family.provider). The "
            + "only mode today, and the default when omitted.",
    };

    /// <summary>
    /// The <c>--json</c> flag: emit a single schema-versioned JSON document to stdout
    /// instead of the human-readable table.
    /// </summary>
    internal static Option<bool> BuildJsonOption() => new("--json")
    {
        Description = "Emit a single schema-versioned JSON document to stdout instead of "
            + "the human-readable table.",
    };

    /// <summary>
    /// The Docker-free orchestration of a <c>list</c> invocation: freezes the Core
    /// registry, projects it into the sorted step-type catalogue, and renders either the
    /// human table or the <c>--json</c> document.
    /// </summary>
    /// <param name="json">When <see langword="true"/>, emit the JSON document instead of the human table.</param>
    /// <param name="output">The writer that receives the table / JSON document.</param>
    /// <returns><see cref="ExitCodes.Success"/> — listing the frozen registry cannot fail.</returns>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker CLI tests can assert the
    /// rendered catalogue matches the sealed registry (count + spot keys) without going
    /// through <see cref="System.CommandLine"/> parsing.
    /// <para>
    /// Unlike <see cref="ValidateCommand.Execute"/>, <c>list</c> has no bad-input / usage-
    /// error branch to keep off stdout in <c>--json</c> mode: there is no <c>&lt;path&gt;</c>
    /// argument, no discovery step, and <see cref="ProviderRegistryFactory.BuildCoreRegistry"/>
    /// reflects over the CLI's own compiled-in provider assemblies (never user input), so it
    /// cannot fail on anything a caller supplies. <paramref name="output"/> therefore always
    /// carries either the JSON document or the human table — never a stray diagnostic line.
    /// </para>
    /// </remarks>
    internal static int Execute(bool json, TextWriter output)
    {
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        var stepTypes = BuildStepTypeCatalogue(registry);

        if (json)
        {
            CliJsonContract.Write(
                new ListJsonDocument(
                    CliJsonContract.SchemaVersion, CliJsonContract.EngineVersion, stepTypes),
                output);
        }
        else
        {
            WriteHumanTable(registry, stepTypes, output);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Projects every <see cref="RegisteredProvider"/> in <paramref name="registry"/> into
    /// the <c>list --step-types</c> catalogue entry, sorted by the dotted <c>type</c> key
    /// (ordinal) so the output is deterministic regardless of registry iteration order.
    /// </summary>
    private static List<ListJsonStepType> BuildStepTypeCatalogue(StepKindRegistry registry) =>
        registry.All
            .Select(rp => new ListJsonStepType(
                $"{rp.Kind.Family}.{rp.Kind.Provider}", rp.Kind.Family, rp.Kind.Provider))
            .OrderBy(st => st.Type, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Renders the sorted step-type catalogue as a simple table (TYPE / FAMILY / PROVIDER /
    /// VERSION columns — VERSION is a human-only convenience column, absent from the frozen
    /// JSON shape), then a summary count line.
    /// </summary>
    private static void WriteHumanTable(
        StepKindRegistry registry, IReadOnlyList<ListJsonStepType> stepTypes, TextWriter output)
    {
        output.WriteLine($"{"TYPE",-32} {"FAMILY",-16} {"PROVIDER",-16} VERSION");

        foreach (var stepType in stepTypes)
        {
            var version = registry.TryGet(stepType.Type, out var rp) && rp is not null
                ? rp.Metadata.Version
                : "?";
            output.WriteLine(
                $"{stepType.Type,-32} {stepType.Family,-16} {stepType.Provider,-16} {version}");
        }

        output.WriteLine($"{stepTypes.Count} step type(s).");
    }
}

// ── `list --json` wire shape (frozen contract, pinned by ListJsonGoldenTests) ──
//
// {
//   "schemaVersion": 1,
//   "engineVersion": "...",
//   "stepTypes": [
//     { "type": "family.provider", "family": "family", "provider": "provider" }
//   ]
// }
//
// stepTypes is sorted by "type" (ordinal). Property order is declaration order — keep the
// declared order below in lock-step with the comment above and the golden file.

/// <summary>The top-level <c>list --json</c> document.</summary>
internal sealed record ListJsonDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string EngineVersion,
    [property: JsonPropertyOrder(2)] IReadOnlyList<ListJsonStepType> StepTypes);

/// <summary>A single step-type entry in the <c>list --json</c> document.</summary>
internal sealed record ListJsonStepType(
    [property: JsonPropertyOrder(0)] string Type,
    [property: JsonPropertyOrder(1)] string Family,
    [property: JsonPropertyOrder(2)] string Provider);
