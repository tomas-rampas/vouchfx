// Vouchfx.Engine.Runtime.Tests — ScenarioValidator (#260). Non-docker.
//
// Exercises the four topology-free compile-validation stages
// (Schema → Parse → Pipeline → Roslyn) against REAL Core providers (http.rest,
// script.csharp) — no stub providers, no topology, no container. Each test drives
// exactly one stage's failure mode plus one full happy path that reaches Roslyn
// success.
//
//   • Schema failure   — http.rest missing its required 'method' field.
//   • Pipeline failure — http.rest 'path' that does not start with '/' (passes
//     schema — 'path' is just a string there — but fails HttpRestProvider.Validate's
//     SSRF guard).
//   • Roslyn failure   — script.csharp with a deliberately invalid C# body. This is
//     the one Core-provider path that can reach the Roslyn stage at all:
//     script.csharp splices `code` VERBATIM into the CSX submission (by design, §13 —
//     it is Turing-complete C#, never a substitutable template), so invalid author
//     C# passes schema (any string) and pipeline validate (Validate only checks
//     exactly-one-of code/file, never parses the C#) and fails only at
//     RoslynScriptCompiler.CompileOnce. No other Core provider's Validate is loose
//     enough to let a body through that would fail to compile — their emitted CSX
//     is provider-authored, not author-supplied — so this is the ONLY realistic,
//     real-provider route to a ValidationStage.Roslyn failure; it is exercised below
//     rather than a synthetic stub, per the task brief.
//   • Roslyn success   — a minimal valid http.rest scenario; proves a scenario can
//     reach IsValid=true (schema, parse, pipeline, AND a real Roslyn compile) with
//     zero containers.

using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class ScenarioValidatorTests
{
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    // ── Happy path: valid scenario reaches Roslyn success, zero containers ───────

    [Fact]
    public void ValidateScenario_ValidHttpRestScenario_IsValidWithNoDiagnostics()
    {
        const string yaml = """
            metadata:
              name: valid-scenario
            steps:
              - id: check-health
                type: http.rest
                target: svc
                method: GET
                path: /health
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "valid.e2e.yaml", s_registry);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("valid.e2e.yaml", result.Path);
    }

    // ── Schema stage failure ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateScenario_MissingRequiredField_FailsAtSchemaStage()
    {
        // http.rest's JSON Schema fragment requires target/method/path (§8); 'method' is
        // omitted here, so DocumentValidator.Validate must reject this BEFORE any parse.
        const string yaml = """
            steps:
              - id: bad-step
                type: http.rest
                target: svc
                path: /health
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "schema-invalid.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => Assert.Equal(ValidationStage.Schema, d.Stage));
    }

    // ── Pipeline stage failure (real provider-model validation, not a stub) ───────

    [Fact]
    public void ValidateScenario_HttpRestPathWithoutLeadingSlash_FailsAtPipelineStage()
    {
        // 'path: users/123' (no leading '/') is a perfectly valid JSON Schema string, so
        // schema validation passes; HttpRestProvider.Validate's SSRF guard rejects it
        // ("must be a rooted relative path") — a genuine ProviderPipeline.Compile failure.
        const string yaml = """
            steps:
              - id: bad-path
                type: http.rest
                target: svc
                method: GET
                path: users/123
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "pipeline-invalid.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        Assert.Contains("rooted relative path", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateScenario_UnknownSecretSource_FailsAtPipelineStage()
    {
        // The secret-reference syntax check (§17, reused from ScenarioRunner) runs as
        // part of the Pipeline stage: an unknown source ("bogus") must be rejected here,
        // topology-free, exactly as the run path rejects it before starting Aspire.
        const string yaml = """
            steps:
              - id: bad-secret
                type: http.rest
                target: svc
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:bogus/token}"
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "bad-secret.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
    }

    // ── Roslyn stage failure (script.csharp — the one real-provider route, see file header) ──

    [Fact]
    public void ValidateScenario_ScriptCsharpWithInvalidCSharp_FailsAtRoslynStage()
    {
        // 'var x = ;' is a syntax error (missing expression after '='). script.csharp
        // splices 'code' verbatim (never validated as C#), so this passes schema and
        // pipeline validate, and fails ONLY at RoslynScriptCompiler.CompileOnce.
        const string yaml = """
            steps:
              - id: broken-script
                type: script.csharp
                code: "var x = ;"
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "roslyn-invalid.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Roslyn, diagnostic.Stage);
        Assert.NotEmpty(diagnostic.Message);
    }

    // ── Parse stage: exercised via a document that fails to build an AST ──────────

    [Fact]
    public void ValidateScenario_UnknownStepType_FailsBeforeRoslyn()
    {
        // A step 'type' naming a kind absent from the registry cannot bind — AstBuilder
        // rejects it. This scenario's registry only carries http.rest + script.csharp, so
        // 'noop.missing' is unknown; the failure must be reported (Schema or Parse — the
        // schema's step 'type' pattern alone doesn't know the registry's kinds, so this is
        // rejected at whichever of the two stages catches an unregistered kind) and must
        // never reach Pipeline or Roslyn.
        const string yaml = """
            steps:
              - id: unknown-step
                type: noop.missing
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "unknown-type.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(
            result.Diagnostics,
            d => Assert.True(
                d.Stage is ValidationStage.Schema or ValidationStage.Parse,
                $"Expected Schema or Parse, got {d.Stage}."));
    }

    // ── Multi-scenario aggregation ─────────────────────────────────────────────────

    [Fact]
    public void Validate_MixOfValidAndInvalidScenarios_AggregatesCorrectly()
    {
        const string validYaml = """
            steps:
              - id: ok
                type: http.rest
                target: svc
                method: GET
                path: /ok
            """;

        const string invalidYaml = """
            steps:
              - id: bad
                type: http.rest
                target: svc
                path: /bad
            """;

        var sources = new[]
        {
            new ScenarioSource("a.e2e.yaml", validYaml),
            new ScenarioSource("b.e2e.yaml", invalidYaml),
        };

        var report = ScenarioValidator.Validate(sources, s_registry);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Scenarios.Count);
        Assert.Equal("a.e2e.yaml", report.Scenarios[0].Path);
        Assert.True(report.Scenarios[0].IsValid);
        Assert.Equal("b.e2e.yaml", report.Scenarios[1].Path);
        Assert.False(report.Scenarios[1].IsValid);
    }

    [Fact]
    public void Validate_EmptyScenarioList_IsVacuouslyValid()
    {
        var report = ScenarioValidator.Validate(Array.Empty<ScenarioSource>(), s_registry);

        Assert.True(report.IsValid);
        Assert.Empty(report.Scenarios);
    }

    // ── Per-scenario base directory (issue #268) ──────────────────────────────────
    //
    // ScenarioValidator.Validate is the Docker-free layer ScenarioRunner.RunSuiteAsync's
    // per-scenario compile loop mirrors (both ultimately call ProviderPipeline.Compile with
    // a per-scenario base directory), so proving per-scenario resolution HERE gives run-path
    // coverage without Docker. Each ScenarioSource below carries a DIFFERENT
    // SeedBaseDirectory — its OWN directory — exactly as RunCommand.ExecuteAsync's
    // scenarioBaseDirectories and ValidateCommand.Execute's per-source directory now do.

    /// <summary>
    /// Two scenarios in SEPARATE directories, each referencing a <c>script.csharp</c>
    /// <c>file:</c> helper that lives BESIDE it (and only there — never beside the other
    /// scenario). Passing each <see cref="ScenarioSource"/> its OWN directory must resolve
    /// BOTH references correctly (issue #268): neither scenario's compile leaks into, or
    /// depends on, the other's directory.
    /// </summary>
    [Fact]
    public void Validate_TwoScenariosInSeparateDirectories_EachResolvesFileReferenceAgainstItsOwnDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "vouchfx-scenario-validator-" + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        try
        {
            // Deliberately a BARE filename (no path segments) so resolution is entirely
            // determined by which directory is passed as the base — exactly the ambiguity
            // per-scenario resolution must get right.
            const string scriptWithFileReference = """
                steps:
                  - id: run-helper
                    type: script.csharp
                    file: helper.csx
                """;
            const string helperCsxContents = "// no-op\n";

            File.WriteAllText(Path.Combine(dirA, "helper.csx"), helperCsxContents);
            File.WriteAllText(Path.Combine(dirB, "helper.csx"), helperCsxContents);

            var sources = new[]
            {
                new ScenarioSource(
                    Path.Combine(dirA, "scenario-a.e2e.yaml"), scriptWithFileReference, dirA),
                new ScenarioSource(
                    Path.Combine(dirB, "scenario-b.e2e.yaml"), scriptWithFileReference, dirB),
            };

            var report = ScenarioValidator.Validate(sources, s_registry);

            Assert.Equal(2, report.Scenarios.Count);
            Assert.True(
                report.Scenarios[0].IsValid,
                "scenario-a must resolve 'file: helper.csx' against ITS OWN directory ('a'). "
                + "Diagnostics: "
                + string.Join("; ", report.Scenarios[0].Diagnostics.Select(d => $"[{d.Stage}] {d.Message}")));
            Assert.True(
                report.Scenarios[1].IsValid,
                "scenario-b must resolve 'file: helper.csx' against ITS OWN directory ('b'). "
                + "Diagnostics: "
                + string.Join("; ", report.Scenarios[1].Diagnostics.Select(d => $"[{d.Stage}] {d.Message}")));
            Assert.True(report.IsValid);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The failure counterpart: when a scenario's helper lives ONLY beside a DIFFERENT
    /// scenario (never its own directory), per-scenario resolution must correctly report it
    /// invalid — proving the fix genuinely resolves each source against its OWN directory
    /// rather than, say, silently falling back to some other shared root.
    /// </summary>
    [Fact]
    public void Validate_ScenarioFileReferenceOnlyBesideAnotherScenario_FailsAtPipelineStage()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "vouchfx-scenario-validator-" + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        try
        {
            const string scriptWithFileReference = """
                steps:
                  - id: run-helper
                    type: script.csharp
                    file: helper.csx
                """;
            const string helperCsxContents = "// no-op\n";

            // helper.csx exists ONLY in 'a' — scenario-b (rooted at 'b') must NOT find it.
            File.WriteAllText(Path.Combine(dirA, "helper.csx"), helperCsxContents);

            var sources = new[]
            {
                new ScenarioSource(
                    Path.Combine(dirB, "scenario-b.e2e.yaml"), scriptWithFileReference, dirB),
            };

            var report = ScenarioValidator.Validate(sources, s_registry);

            var entry = Assert.Single(report.Scenarios);
            Assert.False(entry.IsValid);
            var diagnostic = Assert.Single(entry.Diagnostics);
            Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
            Assert.Contains("helper.csx", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("not found", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
