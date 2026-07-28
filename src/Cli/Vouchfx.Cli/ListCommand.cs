// Vouchfx.Cli — ListCommand (#260; enriched catalogue Spec A).
//
// The `vouchfx list [--step-types] [--json]` subcommand: lists catalogue information
// about this CLI build. `--step-types` is the only mode today AND the default when
// omitted (a future mode — e.g. listing discovered scenarios by tag — can be added
// later as an additional, explicit flag without a breaking change). Docker-free: it
// only reflects over the frozen Core StepKindRegistry (ProviderRegistryFactory), the
// SAME registry `run` and `validate` freeze at startup.
//
// The --json document is the public shape-level catalogue (EngineExport.BuildCatalogue):
// every registered type with required/optional fields, captureSupported, and familyIntent.
// Incomplete metadata fails the entire export (non-zero exit, no partial document).

using System.CommandLine;
using Vouchfx.Engine.Compilation.Schema;
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
            + "key (family.provider) with required/optional fields, capture support, and "
            + "family intent.");

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
            return Task.FromResult(Execute(json, Console.Out, Console.Error));
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
    /// The <c>--json</c> flag: emit a single schema-versioned JSON catalogue document to
    /// stdout instead of the human-readable table.
    /// </summary>
    internal static Option<bool> BuildJsonOption() => new("--json")
    {
        Description = "Emit a single schema-versioned JSON catalogue document to stdout "
            + "instead of the human-readable table (required/optional fields, capture "
            + "support, family intent per step type).",
    };

    /// <summary>
    /// The Docker-free orchestration of a <c>list</c> invocation: freezes the Core
    /// registry, builds the shape-level catalogue via
    /// <see cref="EngineExport.BuildCatalogue"/>, and renders either the human table or
    /// the <c>--json</c> document.
    /// </summary>
    /// <param name="json">When <see langword="true"/>, emit the JSON document instead of the human table.</param>
    /// <param name="output">The writer that receives the table / JSON document.</param>
    /// <param name="errorOutput">
    /// The writer that receives catalogue-export failure diagnostics (stderr) so stdout
    /// stays pure JSON or empty on failure.
    /// </param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/> on success;
    /// <see cref="ExitCodes.EnvironmentError"/> when catalogue metadata is incomplete.
    /// </returns>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker CLI tests can assert the
    /// rendered catalogue matches the sealed registry without going through
    /// <see cref="System.CommandLine"/> parsing.
    /// </remarks>
    internal static int Execute(bool json, TextWriter output, TextWriter? errorOutput = null)
    {
        errorOutput ??= TextWriter.Null;

        var registry = ProviderRegistryFactory.BuildCoreRegistry();

        StepCatalogueDocument catalogue;
        try
        {
            catalogue = EngineExport.BuildCatalogue(registry, CliJsonContract.EngineVersion);
        }
        catch (CatalogueExportException ex)
        {
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }

        if (json)
        {
            // Same options / camelCase / property order as the library API so CLI and
            // EngineExport.SerializeCatalogue cannot drift on wire shape.
            CliJsonContract.Write(catalogue, output);
        }
        else
        {
            WriteHumanTable(registry, catalogue.StepTypes, output);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Renders the sorted step-type catalogue as a simple table (TYPE / FAMILY / PROVIDER /
    /// VERSION columns — VERSION is a human-only convenience column, absent from the frozen
    /// JSON shape), then a summary count line.
    /// </summary>
    private static void WriteHumanTable(
        StepKindRegistry registry,
        IReadOnlyList<StepCatalogueEntry> stepTypes,
        TextWriter output)
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
