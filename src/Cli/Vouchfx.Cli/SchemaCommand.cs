// Vouchfx.Cli — SchemaCommand (Spec A / engine-schema-and-catalogue-export).
//
// The `vouchfx schema [--schema-version v1|v2] [--output <path>]` subcommand: emits a composed JSON Schema
// (root language grammar + every registered Core provider fragment) for the current
// process registration. Default output is stdout (pipe-friendly); optional --output
// writes the same document to a file.
//
// Exit codes:
//   0 (ExitCodes.Success)           — schema written successfully.
//   2 (ExitCodes.UsageError)        — bad --output path (missing parent directory, etc.).
//   3 (ExitCodes.EnvironmentError)  — composition / incomplete-metadata failure (a
//                                     registered provider cannot supply a schema fragment).
//
// Docker-free: only freezes the Core StepKindRegistry and composes schema text.

using System.CommandLine;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration.Environments;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>schema</c> subcommand.
/// </summary>
internal static class SchemaCommand
{
    /// <summary>
    /// Builds the <c>schema</c> <see cref="Command"/>, wiring its action to
    /// <see cref="Execute"/>.
    /// </summary>
    public static Command Build()
    {
        var command = new Command(
            "schema",
            "Emit the composed JSON Schema (v1 by default; --schema-version v2 opts in) with every "
            + "registered provider fragment) for this CLI build. Default output is stdout; "
            + "use --output <path> to write a file instead.");

        var versionOption = new Option<string>("--schema-version")
        {
            Description = "Language schema to export: v1 (default) or v2.",
            DefaultValueFactory = _ => "v1",
        };
        command.Add(versionOption);
        var outputOption = BuildOutputOption();
        command.Add(outputOption);
        var environmentDriverOption = new Option<string?>("--environment-driver")
        {
            Description = "Export an environment driver's separate configuration schema. Supported: compose.",
        };
        command.Add(environmentDriverOption);

        command.SetAction((parseResult, _) =>
        {
            var outputPath = parseResult.GetValue(outputOption);
            return Task.FromResult(Execute(outputPath, Console.Out, Console.Error, parseResult.GetValue(environmentDriverOption), parseResult.GetValue(versionOption) ?? "v1"));
        });

        return command;
    }

    /// <summary>
    /// The optional <c>--output &lt;path&gt;</c> flag: write the composed schema to a file
    /// instead of stdout.
    /// </summary>
    internal static Option<string?> BuildOutputOption() => new("--output")
    {
        Description = "Write the composed schema JSON to this file path instead of stdout. "
            + "The parent directory must already exist.",
    };

    /// <summary>
    /// Freezes the Core registry, composes the schema, and writes it to stdout or
    /// <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="outputPath">
    /// When non-null/non-empty, the schema is written to this path and nothing is written
    /// to <paramref name="output"/>. When null/empty, the schema is written to
    /// <paramref name="output"/> (stdout).
    /// </param>
    /// <param name="output">Stdout writer (schema text when not using <c>--output</c>).</param>
    /// <param name="errorOutput">Stderr writer for diagnostics on failure.</param>
    /// <returns>The process exit code (see class header).</returns>
    internal static int Execute(string? outputPath, TextWriter output, TextWriter errorOutput, string? environmentDriver = null, string schemaVersion = "v1")
    {
        if (schemaVersion is not ("v1" or "v2") || (environmentDriver is not null && schemaVersion != "v1"))
        {
            errorOutput.WriteLine("Supported language schemas: v1, v2. Driver schemas cannot select a language version.");
            return ExitCodes.UsageError;
        }
        if (environmentDriver is not null && environmentDriver != "compose")
        {
            errorOutput.WriteLine("Unknown environment schema driver. Supported: compose.");
            return ExitCodes.UsageError;
        }
        string schemaJson;
        try
        {
            schemaJson = environmentDriver == "compose" ? ComposeEnvironmentDriver.ExportConfigurationSchema()
                : EngineExport.ComposeVersionedSchemaJson(ProviderRegistryFactory.BuildCoreRegistry(), schemaVersion);
        }
        catch (CatalogueExportException ex)
        {
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }
        catch (InvalidOperationException ex)
        {
            errorOutput.WriteLine($"Schema composition failed: {ex.Message}");
            return ExitCodes.EnvironmentError;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            output.WriteLine(schemaJson);
            return ExitCodes.Success;
        }

        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                errorOutput.WriteLine(
                    $"Cannot write schema: parent directory does not exist: '{parent}'.");
                return ExitCodes.UsageError;
            }

            File.WriteAllText(fullPath, schemaJson);
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or PathTooLongException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            errorOutput.WriteLine($"Cannot write schema to '{outputPath}': {ex.Message}");
            return ExitCodes.UsageError;
        }
    }
}
