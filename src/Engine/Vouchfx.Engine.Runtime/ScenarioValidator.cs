// Vouchfx.Engine.Runtime — ScenarioValidator (#260).
//
// A public, topology-free "compile-validate" entry point: runs the SAME pre-topology
// gate ScenarioRunner.RunScenarioOwningTopologyAsync runs before it ever calls
// SuiteTopology.StartAsync (schema validation, parse/AST build, the provider
// pipeline's bind/validate/emit, and the topology-free secret-reference syntax
// check), then goes one step further and calls RoslynScriptCompiler.CompileOnce —
// proving the assembled CSX actually compiles — WITHOUT starting any Aspire
// topology and therefore WITHOUT touching Docker.
//
// This is the engine-side seam behind `vouchfx validate` (Vouchfx.Cli.ValidateCommand)
// and is deliberately public so it can be consumed directly by other hosts (e.g. a
// future vouchfx-mcp validate worker) without depending on any Vouchfx.Cli-internal
// type such as ScenarioDiscovery.
//
// Deliberate scope limit: this stops exactly where the run path would start the
// topology (SuiteTopology.StartAsync). It therefore does NOT catch an environment-
// mapping error (a malformed `environment.services`/`dependencies` reference, an
// unknown `${conn:...}` target, …) — those are only detected once EnvironmentMapper.Map
// runs, which requires starting the topology-build sequence. Catching those without any
// container would require duplicating EnvironmentMapper's own validation here, which is
// a larger, separate piece of work than this compile-level gate.
//
// Reuse, not duplication (CLAUDE.md "How to work here"): every step below calls the
// SAME engine entry points the run path calls (DocumentValidator.Validate,
// AstBuilder.Build, ProviderPipeline.Compile, ScenarioRunner.TryValidateSecretReferences,
// ScenarioRunner.BclReferencePaths, RoslynScriptCompiler.CompileOnce) — the three
// ScenarioRunner members were promoted from private to internal for exactly this reuse
// (see their own remarks).
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// The compile-validation stage a <see cref="ValidationDiagnostic"/> belongs to.
/// </summary>
/// <remarks>
/// Mirrors the four topology-free stages <see cref="ScenarioValidator.ValidateScenario"/>
/// runs, in order: <see cref="Schema"/> → <see cref="Parse"/> → <see cref="Pipeline"/> →
/// <see cref="Roslyn"/>. Validation stops at the first failing stage, so a single
/// scenario's <see cref="ScenarioValidationResult.Diagnostics"/> never mixes stages
/// except for <see cref="Schema"/>, which can report every schema error at once.
/// </remarks>
public enum ValidationStage
{
    /// <summary>
    /// The document failed composed-JSON-Schema validation (§8) — it never reached the
    /// parser. Corresponds to <see cref="DocumentValidator.Validate"/>.
    /// </summary>
    Schema,

    /// <summary>
    /// The document parsed as YAML but failed to build into a <see cref="ScenarioAst"/>
    /// (e.g. an internal AST-builder invariant). This is a distinct, narrower stage than
    /// <see cref="Schema"/>: a schema-valid document can still fail here in principle,
    /// though in practice a schema-valid document almost always builds cleanly.
    /// </summary>
    Parse,

    /// <summary>
    /// The scenario parsed and built, but the provider pipeline's bind/validate/emit pass
    /// (<see cref="ProviderPipeline.Compile"/>) — including the topology-free
    /// secret-reference syntax check — reported a failure (e.g. a step's model failed its
    /// provider's own <c>IStepValidator&lt;T&gt;.Validate</c>, or a
    /// <c>${secret:source/path}</c> reference names an unknown source).
    /// </summary>
    Pipeline,

    /// <summary>
    /// The assembled CSX script failed to compile via
    /// <see cref="RoslynScriptCompiler.CompileOnce"/>. Reaching this stage proves every
    /// earlier stage passed; a Roslyn-stage failure is necessarily a provider-emitted C#
    /// defect (or, for <c>script.csharp</c>, invalid author-supplied C#) rather than an
    /// authoring/YAML problem.
    /// </summary>
    Roslyn,
}

/// <summary>
/// A single compile-validation finding: the stage it was raised at and a human-readable
/// message.
/// </summary>
/// <param name="Stage">The validation stage that raised this diagnostic.</param>
/// <param name="Message">
/// A human-readable description of the problem. For <see cref="ValidationStage.Schema"/>
/// this is carried verbatim from <see cref="Vouchfx.Engine.Compilation.Schema.SchemaValidationError.Message"/>
/// — including its best-effort <c>(line N)</c> prefix when
/// <see cref="DocumentValidator"/> resolved one — and is never re-parsed here.
/// </param>
public sealed record ValidationDiagnostic(ValidationStage Stage, string Message);

/// <summary>
/// The compile-validation outcome for a single scenario file.
/// </summary>
/// <param name="Path">
/// The scenario's identifying path, exactly as supplied by the caller to
/// <see cref="ScenarioValidator.ValidateScenario"/> (the CLI passes the discovered file's
/// absolute path).
/// </param>
/// <param name="IsValid">
/// <see langword="true"/> when every stage passed — schema, parse, provider pipeline
/// (including the secret-reference syntax check), and the Roslyn compile-once.
/// </param>
/// <param name="Diagnostics">
/// The findings that made <paramref name="IsValid"/> <see langword="false"/>, or an empty
/// list when valid. Every diagnostic in this list shares one <see cref="ValidationStage"/>
/// — the FIRST stage that failed — because validation stops there (later stages are never
/// reached); the sole exception is <see cref="ValidationStage.Schema"/>, which can report
/// every schema error the composed JSON Schema found in one pass.
/// </param>
public sealed record ScenarioValidationResult(
    string Path,
    bool IsValid,
    IReadOnlyList<ValidationDiagnostic> Diagnostics);

/// <summary>
/// The aggregate compile-validation outcome for a set of scenario files.
/// </summary>
/// <param name="Scenarios">
/// The per-scenario results, in the order supplied to
/// <see cref="ScenarioValidator.Validate"/>.
/// </param>
public sealed record ValidationReport(IReadOnlyList<ScenarioValidationResult> Scenarios)
{
    /// <summary>
    /// <see langword="true"/> when every scenario in <see cref="Scenarios"/> is valid
    /// (vacuously <see langword="true"/> for an empty set — "nothing to validate" is not a
    /// failure, mirroring the taxonomy's "nothing ran, nothing failed" convention, §12.1).
    /// </summary>
    public bool IsValid => Scenarios.All(s => s.IsValid);
}

/// <summary>
/// A single scenario file to validate: its identifying path, raw YAML text, and the base
/// directory relative file-path fields (e.g. <c>script.csharp</c>'s <c>file</c>) resolve
/// against.
/// </summary>
/// <param name="Path">
/// The scenario's identifying path (typically its absolute file path). Carried through
/// unchanged into the matching <see cref="ScenarioValidationResult.Path"/>.
/// </param>
/// <param name="YamlText">The scenario's raw <c>.e2e.yaml</c> text.</param>
/// <param name="SeedBaseDirectory">
/// The base directory for the scenario's own relative file-path fields. Pass
/// <see langword="null"/> to fall back to the process's current directory (mirrors
/// <see cref="ProviderPipeline.Compile"/>'s own <c>suiteDirectory</c> default). Each
/// scenario carries its OWN base directory — deliberately not a single suite-wide
/// directory — because, unlike a suite run, <see cref="ScenarioValidator.Validate"/>
/// makes no assumption that the scenarios it validates share one <c>environment</c> block
/// or even live in the same directory (a directory scan may discover files nested at
/// different depths).
/// </param>
public sealed record ScenarioSource(string Path, string YamlText, string? SeedBaseDirectory = null);

/// <summary>
/// Topology-free "compile-validate" for <c>.e2e.yaml</c> scenarios (#260).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateScenario"/> runs the full pre-topology gate the run path
/// (<see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>) runs before it ever calls
/// <c>SuiteTopology.StartAsync</c> — schema validation, parse/AST build, the provider
/// pipeline's bind/validate/emit, and the topology-free secret-reference syntax check —
/// and then goes one step further: it calls
/// <see cref="RoslynScriptCompiler.CompileOnce"/> so a scenario that reaches
/// <see cref="ValidationStage.Roslyn"/> is PROVEN to compile, not merely assumed to. No
/// Aspire topology is built and no container runtime is touched at any stage.
/// </para>
/// <para>
/// <strong>What this does NOT catch (deliberate scope, see file header):</strong> an
/// <c>environment</c>-mapping error (an unknown <c>${conn:...}</c> target, a malformed
/// dependency reference) is only detected once the topology-build sequence starts
/// (<c>EnvironmentMapper.Map</c>), which this validator never reaches by design.
/// </para>
/// </remarks>
public static class ScenarioValidator
{
    /// <summary>
    /// Runs the topology-free compile-validation pipeline over a single scenario.
    /// </summary>
    /// <param name="yamlText">The scenario's raw <c>.e2e.yaml</c> text.</param>
    /// <param name="path">
    /// The scenario's identifying path, carried through unchanged into the returned
    /// result's <see cref="ScenarioValidationResult.Path"/>.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry to validate against (schema composition and the
    /// provider pipeline both key off it).
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory this scenario's relative file-path fields (e.g.
    /// <c>script.csharp</c>'s <c>file</c>) resolve against. Pass <see langword="null"/> to
    /// fall back to the process's current directory.
    /// </param>
    /// <returns>
    /// A <see cref="ScenarioValidationResult"/> whose <see cref="ScenarioValidationResult.IsValid"/>
    /// is <see langword="true"/> only when schema validation, parse/AST build, the
    /// provider pipeline (bind/validate/emit + the secret-reference syntax check), and the
    /// Roslyn compile-once all succeed.
    /// </returns>
    public static ScenarioValidationResult ValidateScenario(
        string yamlText,
        string path,
        StepKindRegistry registry,
        string? seedBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(registry);

        // ── Stage 1: schema validation (§8) ───────────────────────────────────
        var schemaResult = DocumentValidator.Validate(yamlText, registry);
        if (!schemaResult.IsValid)
        {
            var diagnostics = schemaResult.Errors
                .Select(e => new ValidationDiagnostic(ValidationStage.Schema, e.Message))
                .ToList();
            return new ScenarioValidationResult(path, IsValid: false, diagnostics);
        }

        // ── Stage 2: parse YAML → E2eDocument → ScenarioAst ───────────────────
        ScenarioAst ast;
        try
        {
            var doc = YamlDocumentParser.Parse(yamlText);
            ast = AstBuilder.Build(doc, registry);
        }
        catch (Exception ex)
        {
            return SingleDiagnostic(path, ValidationStage.Parse, $"Parse / AST error: {ex.Message}");
        }

        // ── Stage 3a: provider pipeline — bind / validate / resources / emit ──
        var pipelineResult = ProviderPipeline.Compile(ast, registry, ScenarioRunner.SuiteNamespace, seedBaseDirectory);
        if (pipelineResult.Failure is not null)
        {
            return SingleDiagnostic(path, ValidationStage.Pipeline, pipelineResult.Failure.Message);
        }

        // ── Stage 3b: secret-reference syntax check (§17) ─────────────────────
        // Topology-free by construction (it scans AST text only; it never resolves a
        // secret), so it runs here as part of the SAME "Pipeline" stage the run path
        // treats it as — reusing ScenarioRunner's exact check, not a re-implementation.
        if (ScenarioRunner.TryValidateSecretReferences(ast, out var secretError))
        {
            return SingleDiagnostic(path, ValidationStage.Pipeline, secretError!);
        }

        // ── Stage 4: Roslyn compile-once — no run, no topology, no Docker ─────
        try
        {
            var referencePaths = ScenarioRunner.BclReferencePaths()
                .Concat(pipelineResult.CompileReferencePaths)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            RoslynScriptCompiler.CompileOnce(
                pipelineResult.Assembled!.CsxSource,
                additionalOptions: null,
                additionalReferencePaths: referencePaths);
        }
        catch (ScriptCompilationException sce)
        {
            return SingleDiagnostic(path, ValidationStage.Roslyn, sce.Message);
        }

        return new ScenarioValidationResult(path, IsValid: true, Array.Empty<ValidationDiagnostic>());
    }

    /// <summary>
    /// Runs <see cref="ValidateScenario"/> over every supplied scenario and aggregates the
    /// results into a <see cref="ValidationReport"/>.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios to validate, each carrying its own path, YAML text, and (optional)
    /// seed base directory — see <see cref="ScenarioSource"/> for why the base directory is
    /// per-scenario rather than shared.
    /// </param>
    /// <param name="registry">The frozen provider registry to validate against.</param>
    /// <returns>
    /// A <see cref="ValidationReport"/> whose <see cref="ScenarioValidationResult"/> entries
    /// are in the same order as <paramref name="scenarios"/>.
    /// </returns>
    public static ValidationReport Validate(
        IReadOnlyList<ScenarioSource> scenarios,
        StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(registry);

        var results = new List<ScenarioValidationResult>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            results.Add(ValidateScenario(
                scenario.YamlText, scenario.Path, registry, scenario.SeedBaseDirectory));
        }

        return new ValidationReport(results);
    }

    /// <summary>Builds a failed, single-diagnostic result — the common shape for stages 2–4.</summary>
    private static ScenarioValidationResult SingleDiagnostic(string path, ValidationStage stage, string message) =>
        new(path, IsValid: false, new[] { new ValidationDiagnostic(stage, message) });
}
