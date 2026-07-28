// Vouchfx.Cli — ScaffoldCommand (Spec B / mcp-generator-scaffold-and-run).
//
// The `vouchfx scaffold --intent <file|-> [--output <path>]` subcommand: accepts a
// structured JSON intent (step types, environment outline, step ids/labels) and emits
// a catalogue-grounded, schema-valid .e2e.yaml skeleton. Deterministic — no LLM.
//
// Exit codes:
//   0 (ExitCodes.Success)           — YAML written successfully.
//   2 (ExitCodes.UsageError)        — bad --intent / --output path, unreadable intent,
//                                     malformed JSON, missing parent directory.
//   3 (ExitCodes.EnvironmentError)  — scaffold validation failure (unknown step type,
//                                     unknown dependency kind, duplicate ids, empty steps).
//
// Docker-free: only freezes the Core StepKindRegistry and runs SuiteScaffolder.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Compilation.Scaffold;
using Vouchfx.Engine.Compilation.Schema;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>scaffold</c> subcommand.
/// </summary>
internal static class ScaffoldCommand
{
    private static readonly JsonSerializerOptions IntentJsonOptions = CreateIntentJsonOptions();

    private static JsonSerializerOptions CreateIntentJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        return options;
    }

    /// <summary>
    /// Builds the <c>scaffold</c> <see cref="Command"/>.
    /// </summary>
    public static Command Build()
    {
        var command = new Command(
            "scaffold",
            "Generate a machine-drafted, schema-valid .e2e.yaml skeleton from a structured "
            + "JSON intent (step types, environment outline, step ids). Grounded in the live "
            + "step catalogue — never free text. Default output is stdout; use --output to "
            + "write a file. Intent schema: "
            + "{ \"steps\":[{ \"id\",\"type\",\"label?\" }], "
            + "\"services\":[{ \"name\",\"image?\" }], "
            + "\"dependencies\":[{ \"name\",\"type\" }] }.");

        var intentOption = BuildIntentOption();
        command.Add(intentOption);

        var outputOption = BuildOutputOption();
        command.Add(outputOption);

        command.SetAction((parseResult, _) =>
        {
            var intentPath = parseResult.GetValue(intentOption);
            var outputPath = parseResult.GetValue(outputOption);
            return Task.FromResult(Execute(intentPath, outputPath, Console.Out, Console.Error));
        });

        return command;
    }

    /// <summary>
    /// Required <c>--intent &lt;file|-&gt;</c>: path to a JSON intent file, or <c>-</c> for stdin.
    /// </summary>
    internal static Option<string> BuildIntentOption() => new("--intent")
    {
        Description = "Path to a JSON scaffold intent file, or '-' to read the intent from stdin. "
            + "Shape: { steps:[{id,type,label?}], services:[{name,image?}], "
            + "dependencies:[{name,type}] }. Free text is not accepted.",
        Required = true,
    };

    /// <summary>
    /// Optional <c>--output &lt;path&gt;</c>: write YAML to a file instead of stdout.
    /// </summary>
    internal static Option<string?> BuildOutputOption() => new("--output")
    {
        Description = "Write the scaffolded .e2e.yaml to this file path instead of stdout. "
            + "The parent directory must already exist.",
    };

    /// <summary>
    /// Reads intent JSON, freezes the Core registry, scaffolds YAML, and writes it.
    /// </summary>
    /// <param name="intentPath">
    /// Intent file path, or <c>-</c> / null empty for stdin. When null/empty treated as usage error.
    /// </param>
    /// <param name="outputPath">Optional file path; when null/empty write to <paramref name="output"/>.</param>
    /// <param name="output">Stdout writer (YAML when not using <c>--output</c>).</param>
    /// <param name="errorOutput">Stderr writer for diagnostics.</param>
    /// <returns>Process exit code (see class header).</returns>
    internal static int Execute(
        string? intentPath,
        string? outputPath,
        TextWriter output,
        TextWriter errorOutput)
    {
        if (string.IsNullOrWhiteSpace(intentPath))
        {
            errorOutput.WriteLine(
                "Scaffold requires --intent <file|-> with a JSON intent document.");
            return ExitCodes.UsageError;
        }

        string intentJson;
        try
        {
            intentJson = ReadIntentText(intentPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            errorOutput.WriteLine($"Cannot read scaffold intent from '{intentPath}': {ex.Message}");
            return ExitCodes.UsageError;
        }

        if (string.IsNullOrWhiteSpace(intentJson))
        {
            errorOutput.WriteLine("Scaffold intent is empty.");
            return ExitCodes.UsageError;
        }

        ScaffoldIntentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScaffoldIntentDto>(intentJson, IntentJsonOptions);
        }
        catch (JsonException ex)
        {
            errorOutput.WriteLine($"Scaffold intent is not valid JSON: {ex.Message}");
            return ExitCodes.UsageError;
        }

        if (dto is null)
        {
            errorOutput.WriteLine("Scaffold intent JSON deserialised to null.");
            return ExitCodes.UsageError;
        }

        ScaffoldIntent intent;
        try
        {
            intent = MapIntent(dto);
        }
        catch (ScaffoldException ex)
        {
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }

        string yaml;
        try
        {
            var registry = ProviderRegistryFactory.BuildCoreRegistry();
            yaml = SuiteScaffolder.Generate(registry, intent, CliJsonContract.EngineVersion);
        }
        catch (ScaffoldException ex)
        {
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }
        catch (CatalogueExportException ex)
        {
            // Incomplete catalogue metadata fails closed (exit 3), same class as schema export.
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }
        catch (InvalidOperationException ex)
        {
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            // Write without an extra trailing newline beyond what Generate already ends with.
            output.Write(yaml);
            if (!yaml.EndsWith('\n'))
                output.WriteLine();
            return ExitCodes.Success;
        }

        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                errorOutput.WriteLine(
                    $"Cannot write scaffold: parent directory does not exist: '{parent}'.");
                return ExitCodes.UsageError;
            }

            File.WriteAllText(fullPath, yaml);
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
            errorOutput.WriteLine($"Cannot write scaffold to '{outputPath}': {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static string ReadIntentText(string intentPath)
    {
        if (intentPath.Trim() == "-")
            return Console.In.ReadToEnd();

        return File.ReadAllText(intentPath);
    }

    private static ScaffoldIntent MapIntent(ScaffoldIntentDto dto)
    {
        IEnumerable<ScaffoldStepDto?> stepSource =
            dto.Steps ?? Enumerable.Empty<ScaffoldStepDto?>();

        var steps = stepSource
            .Select(s =>
            {
                if (s is null)
                    throw new ScaffoldException("A step entry in the intent is null.");
                return new ScaffoldStepIntent(
                    Id: s.Id ?? string.Empty,
                    Type: s.Type ?? string.Empty,
                    Label: s.Label);
            })
            .ToList();

        IReadOnlyList<ScaffoldServiceIntent>? services = null;
        if (dto.Services is { Count: > 0 })
        {
            services = dto.Services
                .Select(s =>
                {
                    if (s is null)
                        throw new ScaffoldException("A service entry in the intent is null.");
                    return new ScaffoldServiceIntent(Name: s.Name ?? string.Empty, Image: s.Image);
                })
                .ToList();
        }

        IReadOnlyList<ScaffoldDependencyIntent>? dependencies = null;
        if (dto.Dependencies is { Count: > 0 })
        {
            dependencies = dto.Dependencies
                .Select(d =>
                {
                    if (d is null)
                        throw new ScaffoldException("A dependency entry in the intent is null.");
                    return new ScaffoldDependencyIntent(
                        Name: d.Name ?? string.Empty,
                        Type: d.Type ?? string.Empty);
                })
                .ToList();
        }

        return new ScaffoldIntent(steps, services, dependencies);
    }

    // ── DTO types for JSON intent ─────────────────────────────────────────────

    private sealed class ScaffoldIntentDto
    {
        public List<ScaffoldStepDto?>? Steps { get; set; }
        public List<ScaffoldServiceDto?>? Services { get; set; }
        public List<ScaffoldDependencyDto?>? Dependencies { get; set; }
    }

    private sealed class ScaffoldStepDto
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Label { get; set; }
    }

    private sealed class ScaffoldServiceDto
    {
        public string? Name { get; set; }
        public string? Image { get; set; }
    }

    private sealed class ScaffoldDependencyDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
    }
}
