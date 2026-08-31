// Vouchfx.Engine.Planning.Ingest — SuiteSetLoader (M3 Planner, REQ-001, REQ-008, EDGE-003,
// EDGE-009, flag F-6, flag F-19).
//
// A behaviour-parity clone of Vouchfx.Cli's internal ScenarioDiscovery: the public library
// API (REQ-001) cannot call ScenarioDiscovery (it is internal to Vouchfx.Cli, exposed only
// to Vouchfx.Cli.Tests, and lives above the engine libraries), so the Planner owns its own
// loader that replicates its behaviour precisely — *.e2e.yaml glob, SearchOption.AllDirectories,
// Path.GetFullPath + ordinal sort, single-file root accepted with an OrdinalIgnoreCase
// suffix check, the 1 MiB MaxDocumentSizeBytes cap, and parse failures captured (never
// thrown). tests/Vouchfx.Cli.Tests/PlanDiscoveryParityTests.cs is the CI-guarded parity gate
// (flag F-6) against ScenarioDiscovery's ordered path list, AND (flag F-19) against
// RunCommand.ScenarioName's ScenarioId derivation.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Planning.Ingest;

/// <summary>
/// Discovers and parses <c>.e2e.yaml</c> suite files under a root directory, or a single
/// explicitly named <c>.e2e.yaml</c> file, mirroring <c>Vouchfx.Cli.ScenarioDiscovery</c>.
/// </summary>
internal static class SuiteSetLoader
{
    /// <summary>The glob the discovery walk matches — identical to <c>ScenarioDiscovery.ScenarioGlob</c>.</summary>
    internal const string ScenarioGlob = "*.e2e.yaml";

    /// <summary>
    /// The maximum permitted size, in bytes, of a single <c>.e2e.yaml</c> document —
    /// identical to <c>ScenarioDiscovery.MaxDocumentSizeBytes</c> (1 MiB).
    /// </summary>
    internal const long MaxDocumentSizeBytes = 1024 * 1024;

    /// <summary>
    /// The maximum permitted length, in UTF-16 characters, of a captured parse/AST-build
    /// error message surfaced in <see cref="PlanUnanalysableSuite.Error"/> (MINOR fix-round).
    /// Unlike the adjacent I/O-fault catch in <see cref="ParseFile"/> (which deliberately
    /// names only the exception TYPE, never <c>Message</c>, to avoid leaking an absolute
    /// path), a parse/AST-build message IS embedded here because it is the author's own
    /// actionable diagnostic (e.g. "unknown step type 'x'"). But <c>AstBuilder</c>'s error
    /// messages echo raw content straight from the offending YAML verbatim (a bad step's
    /// entire <c>type</c> scalar, however long) — an unbounded message would let a suite
    /// file's own content reach a shared report with no size limit at all. This bound caps
    /// that blast radius without losing the diagnostic's actionable point; a real parser
    /// message is normally well under 200 characters, so 500 is generous headroom, not a
    /// tight fit.
    /// </summary>
    internal const int MaxParseErrorMessageChars = 500;

    /// <summary>The outcome of a suite-set load: the analysed root plus every suite, split by whether it parsed.</summary>
    /// <param name="AnalysedRoot">
    /// The absolute directory used to compute every suite's <see cref="PlanSuite.RelativePath"/>
    /// — the discovery root itself for a directory, or the containing directory for a
    /// single-file root.
    /// </param>
    /// <param name="Suites">Every suite that parsed and built successfully.</param>
    /// <param name="UnanalysableSuites">Every discovered suite that failed to parse (EDGE-003).</param>
    internal sealed record LoadResult(
        string AnalysedRoot,
        IReadOnlyList<PlanSuite> Suites,
        IReadOnlyList<PlanUnanalysableSuite> UnanalysableSuites);

    /// <summary>
    /// Discovers every <c>*.e2e.yaml</c> file under <paramref name="suitePath"/> (or the
    /// single file it names) and parses each into a <see cref="PlanSuite"/>.
    /// </summary>
    /// <param name="suitePath">A directory to search recursively, or a single <c>*.e2e.yaml</c> file.</param>
    /// <param name="registry">The frozen provider registry used to build each suite's AST.</param>
    /// <returns>The load result: analysed root, parsed suites, and captured parse failures.</returns>
    /// <exception cref="PlanInputException">
    /// Thrown when <paramref name="suitePath"/> does not exist, names a file that does not
    /// end in <c>.e2e.yaml</c>, or discovers zero <c>*.e2e.yaml</c> files at all (EDGE-009).
    /// </exception>
    internal static LoadResult Load(string suitePath, StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(suitePath);
        ArgumentNullException.ThrowIfNull(registry);

        var fullRoot = Path.GetFullPath(suitePath);

        List<string> files;
        string analysedRoot;

        if (File.Exists(fullRoot))
        {
            // OrdinalIgnoreCase deliberately: an explicitly-named existing file
            // (Login.E2E.YAML) must not be rejected on a case technicality — mirrors
            // ScenarioDiscovery.Discover exactly.
            if (!fullRoot.EndsWith(".e2e.yaml", StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanInputException(
                    $"Suite path '{suitePath}' is a file but not a {ScenarioGlob} scenario "
                    + $"(resolved to '{fullRoot}'). Scenario files must end in '.e2e.yaml'.");
            }

            files = new List<string> { fullRoot };
            analysedRoot = Path.GetDirectoryName(fullRoot) ?? fullRoot;
        }
        else if (Directory.Exists(fullRoot))
        {
            try
            {
                files = Directory
                    .EnumerateFiles(fullRoot, ScenarioGlob, SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // MINOR fix-round (reviewed question, resolved: harden rather than accept
                // parity with ScenarioDiscovery.Discover, which has this identical gap — a
                // raw BCL exception escaping this PUBLIC library API's entry point is worse
                // than parity with an internal CLI helper whose caller already wraps every
                // call site). SearchOption.AllDirectories walks lazily, so a permission
                // denial or delete race on any subdirectory beneath an otherwise-accessible
                // root surfaces here, not at the top-level Directory.Exists check above.
                // Wrapped as PlanInputException (never the raw type) so BuildPlan's contract
                // — only PlanInputException/CatalogueExportException ever leave it — holds
                // for this path too; never silently treated as "zero suites discovered"
                // (EDGE-009's message), which would misleadingly suggest an empty folder
                // rather than a folder this process could not fully walk.
                // MINOR fix-round (reviewed question, resolved — keep exit 2, but the message
                // must not read like a bad-argument complaint): '{suitePath}' itself is a
                // confirmed-valid, existing directory (this catch only fires once
                // EnumerateFiles has begun walking it), so this is an ENVIRONMENT/
                // infrastructure fault encountered mid-scan, not a usage mistake about the
                // path argument. The wording says so explicitly, even though the exit code is
                // still UsageError (2) — the Planner has no partial/best-effort discovery mode
                // to fall back to, so it must still fail the invocation, but a reader (or an
                // on-call human paging through logs) must not conclude "the caller typed a bad
                // path" when the true remedy is fixing a permission/lock/concurrent-delete
                // condition on the machine, not the command line. See docs/planner.md's exit
                // code table for the same caveat spelled out for suite authors.
                throw new PlanInputException(
                    $"Suite path '{suitePath}' (resolved to '{fullRoot}') is a valid, existing "
                    + "directory, but could not be FULLY enumerated because of an "
                    + $"access/infrastructure fault encountered while scanning it ({ex.GetType().Name}) "
                    + "- this is an environment problem, NOT a malformed argument: a subdirectory "
                    + "became locked, was deleted mid-scan, or is access-denied. Exit code 2 still "
                    + "applies (the Planner has no partial-enumeration fallback), but the remedy is "
                    + "fixing the environment, not the command line.");
            }

            analysedRoot = fullRoot;
        }
        else
        {
            throw new PlanInputException(
                $"Suite path '{suitePath}' does not exist (resolved to '{fullRoot}'). Pass a "
                + $"directory to search recursively for {ScenarioGlob} suites, or a single "
                + "*.e2e.yaml file.");
        }

        if (files.Count == 0)
        {
            throw new PlanInputException(
                $"Suite path '{suitePath}' (resolved to '{fullRoot}') discovers zero "
                + $"{ScenarioGlob} suites. An empty suite folder means there is no declared "
                + "universe to analyse (EDGE-009) - this is a configuration error, not a finding.");
        }

        var suites = new List<PlanSuite>(files.Count);
        var unanalysable = new List<PlanUnanalysableSuite>();

        foreach (var absolutePath in files)
        {
            var relativePath = ToRelativePath(analysedRoot, absolutePath);
            ParseFile(absolutePath, relativePath, registry, suites, unanalysable);
        }

        return new LoadResult(analysedRoot, suites, unanalysable);
    }

    /// <summary>
    /// Computes a suite's <see cref="PlanSuite.ScenarioId"/> exactly as
    /// <c>Vouchfx.Cli.RunCommand.ScenarioName</c> does: <c>metadata.name</c> when present
    /// (and not empty/whitespace-only), else the filename stem, stripping a
    /// <c>.e2e.yaml</c> suffix case-insensitively.
    /// </summary>
    /// <param name="metadataName">The suite's raw <c>metadata.name</c>, or <see langword="null"/>.</param>
    /// <param name="absolutePath">The suite's absolute file path.</param>
    internal static string ComputeScenarioId(string? metadataName, string absolutePath)
    {
        if (!string.IsNullOrWhiteSpace(metadataName))
        {
            return metadataName;
        }

        var fileName = Path.GetFileName(absolutePath);
        const string suffix = ".e2e.yaml";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : fileName;
    }

    private static void ParseFile(
        string absolutePath,
        string relativePath,
        StepKindRegistry registry,
        List<PlanSuite> suites,
        List<PlanUnanalysableSuite> unanalysable)
    {
        string yamlText;
        try
        {
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > MaxDocumentSizeBytes)
            {
                unanalysable.Add(new PlanUnanalysableSuite(
                    relativePath,
                    $"File size {fileInfo.Length} bytes exceeds the {MaxDocumentSizeBytes}-byte "
                    + $"(1 MiB) limit for a single {ScenarioGlob} document; split the suite into "
                    + "smaller files."));
                return;
            }

            yamlText = File.ReadAllText(absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Security minor (fix-round): BCL IOException/UnauthorizedAccessException
            // messages typically embed the file's ABSOLUTE path. Every other field in this
            // report is analysed-root-relative (a shareable artefact must not leak local
            // machine/user path structure), so the exception TYPE is named instead of its
            // raw Message — never the absolute path.
            unanalysable.Add(new PlanUnanalysableSuite(
                relativePath,
                $"Could not read file ({ex.GetType().Name}): the file may be locked, deleted, or access-denied."));
            return;
        }

        try
        {
            var doc = YamlDocumentParser.Parse(yamlText);
            var ast = AstBuilder.Build(doc, registry);
            var scenarioId = ComputeScenarioId(ast.Metadata?.Name, absolutePath);
            suites.Add(new PlanSuite(absolutePath, relativePath, scenarioId, ast.Metadata?.Name, ast));
        }
        catch (Exception ex)
        {
            // Mirrors ScenarioDiscovery.ParseFile: a parse / AST-build failure must not
            // abort the rest of the suite set (EDGE-003), so every exception is captured
            // here rather than propagated. MINOR fix-round: ex.Message is bounded to
            // MaxParseErrorMessageChars before it is embedded — see that constant's own
            // remarks for why (a bad step's raw YAML content can otherwise reach the report
            // with no length limit at all).
            var message = TruncateParseErrorMessage(ex.Message);
            unanalysable.Add(new PlanUnanalysableSuite(relativePath, $"Parse / AST error: {message}"));
        }
    }

    private static string ToRelativePath(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    /// <summary>
    /// Bounds <paramref name="message"/> to <see cref="MaxParseErrorMessageChars"/> UTF-16
    /// characters, never splitting a surrogate pair (MINOR fix-round). A naive
    /// <c>message[..MaxParseErrorMessageChars]</c> slice can land exactly between a high
    /// surrogate and its low-surrogate partner when the offending YAML's raw content (echoed
    /// verbatim by <c>AstBuilder</c>'s error messages) contains an astral-plane character —
    /// e.g. an emoji in a bad step's <c>type</c> scalar — leaving a lone, unpaired high
    /// surrogate in the truncated string. A lone surrogate is invalid UTF-16 and can throw
    /// when this report is later serialised or transcoded, which would defeat the very point
    /// of bounding this field (turning a length-limit safeguard into a NEW crash vector).
    /// </summary>
    /// <param name="message">The raw, unbounded exception message.</param>
    /// <returns>
    /// <paramref name="message"/> unchanged when at or under the limit; otherwise a prefix no
    /// longer than <see cref="MaxParseErrorMessageChars"/> characters (shorter still when the
    /// limit would otherwise split a surrogate pair) with <c>"... (truncated)"</c> appended.
    /// </returns>
    /// <remarks>
    /// <see langword="internal"/> (not <see langword="private"/>), matching this file's
    /// existing testability convention (<see cref="ComputeScenarioId"/>) — exposed so
    /// SuiteSetLoaderTests can pin the surrogate-pair edge case directly and deterministically
    /// rather than relying on an end-to-end fixture that would need a real UTF-16 astral
    /// character positioned at an exact byte offset to exercise it.
    /// </remarks>
    internal static string TruncateParseErrorMessage(string message)
    {
        if (message.Length <= MaxParseErrorMessageChars)
        {
            return message;
        }

        var cut = MaxParseErrorMessageChars;
        while (cut > 0 && char.IsHighSurrogate(message[cut - 1]))
        {
            cut--;
        }

        return message[..cut] + "... (truncated)";
    }
}
