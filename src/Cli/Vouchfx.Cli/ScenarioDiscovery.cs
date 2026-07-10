// Vouchfx.Cli — ScenarioDiscovery (S07-C-01).
//
// Walks a directory tree for *.e2e.yaml files and parses each into a ScenarioAst.
// Parsing lives HERE (not deferred to the runner) so that a later task can select
// scenarios by metadata (tag/owner/name) without re-reading or re-parsing the files.
//
// A parse / AST-build failure is captured as a ParseError on the DiscoveredScenario
// rather than thrown: a malformed file must not crash discovery of the rest of the
// suite. The run command surfaces such a file as an Inconclusive scenario (§12.1 — an
// authoring error, the scenario never ran), never as a Fail.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// A single <c>.e2e.yaml</c> file found under the discovery root, with its raw text and
/// (when it parses) its built <see cref="ScenarioAst"/>.
/// </summary>
/// <param name="AbsolutePath">
/// The absolute path to the file.  Captured (rather than a relative path) because later
/// selection / reporting tasks key off a stable, unambiguous identity.
/// </param>
/// <param name="YamlText">The verbatim file contents (needed for schema validation and compile).</param>
/// <param name="Ast">
/// The built scenario AST, or <see langword="null"/> when parsing failed (see
/// <paramref name="ParseError"/>).
/// </param>
/// <param name="ParseError">
/// A human-readable parse / AST-build error message, or <see langword="null"/> when the
/// file parsed cleanly.
/// </param>
internal sealed record DiscoveredScenario(
    string AbsolutePath,
    string YamlText,
    ScenarioAst? Ast,
    string? ParseError)
{
    /// <summary>
    /// <see langword="true"/> when the file failed to parse (carries a
    /// <see cref="ParseError"/> and a <see langword="null"/> <see cref="Ast"/>).
    /// </summary>
    public bool Failed => Ast is null;
}

/// <summary>
/// Discovers and parses <c>.e2e.yaml</c> scenario files under a root directory.
/// </summary>
internal static class ScenarioDiscovery
{
    /// <summary>The glob the discovery walk matches.</summary>
    public const string ScenarioGlob = "*.e2e.yaml";

    /// <summary>
    /// Recursively finds every <c>*.e2e.yaml</c> file under <paramref name="root"/> and
    /// parses each into a <see cref="DiscoveredScenario"/>.
    /// </summary>
    /// <param name="root">
    /// The directory to search.  A relative path is resolved against the current working
    /// directory; <c>"."</c> means the working directory.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry used to build the AST (the AST builder needs it to
    /// resolve step types).
    /// </param>
    /// <returns>
    /// The discovered scenarios in a stable, ordinal-sorted path order (so a run is
    /// deterministic regardless of filesystem enumeration order).  A file that fails to
    /// parse is included with a non-null <see cref="DiscoveredScenario.ParseError"/>.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <paramref name="root"/> does not exist — a usage error the caller maps
    /// to exit code 2.
    /// </exception>
    public static IReadOnlyList<DiscoveredScenario> Discover(string root, StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Discovery root '{root}' does not exist (resolved to '{fullRoot}').");
        }

        var files = Directory
            .EnumerateFiles(fullRoot, ScenarioGlob, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var results = new List<DiscoveredScenario>(files.Count);
        foreach (var path in files)
        {
            results.Add(ParseFile(path, registry));
        }

        return results;
    }

    /// <summary>
    /// Reads and parses a single scenario file, capturing any parse / AST-build failure
    /// as a <see cref="DiscoveredScenario.ParseError"/> rather than throwing.
    /// </summary>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker tests can exercise the
    /// parse-failure-capture path against a single temp file.
    /// </remarks>
    internal static DiscoveredScenario ParseFile(string absolutePath, StepKindRegistry registry)
    {
        string yamlText;
        try
        {
            yamlText = File.ReadAllText(absolutePath);
        }
        catch (IOException ex)
        {
            // An unreadable file is captured, not thrown: discovery of the rest of the
            // suite must proceed.  The empty YAML keeps the record well-formed.
            return new DiscoveredScenario(absolutePath, string.Empty, Ast: null,
                ParseError: $"Could not read file: {ex.Message}");
        }

        try
        {
            var doc = YamlDocumentParser.Parse(yamlText);
            var ast = AstBuilder.Build(doc, registry);
            return new DiscoveredScenario(absolutePath, yamlText, ast, ParseError: null);
        }
        catch (Exception ex)
        {
            return new DiscoveredScenario(absolutePath, yamlText, Ast: null,
                ParseError: $"Parse / AST error: {ex.Message}");
        }
    }
}
