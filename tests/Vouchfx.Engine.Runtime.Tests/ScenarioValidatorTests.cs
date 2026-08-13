// Vouchfx.Engine.Runtime.Tests — ScenarioValidator (#260). Non-docker.
//
// Exercises the four topology-free compile-validation stages
// (Schema → Parse → Pipeline → Roslyn) against REAL Core providers (http.rest,
// script.csharp) — no stub providers, no topology, no container. Each test drives
// exactly one stage's failure mode plus one full happy path that reaches Roslyn
// success.
//
//   • Schema failure   — http.rest missing its required 'method' field.
//   • Pipeline failure — db-assert.postgres 'target' naming no environment.dependencies
//     entry in the document (passes schema — a step's own SchemaFragment has no
//     visibility into sibling document sections, so this cross-reference can never be
//     a JSON Schema check — but fails DbAssertPostgresProvider.Validate's dependency
//     reconciliation). UPDATE (T3d, feat/fragment-completeness): this test used to use
//     http.rest's 'path' without a leading '/' — that check has since been LIFTED into
//     http.rest's own JsonSchemaFragment (an SSRF guard pattern), so the identical
//     document now fails at the Schema stage instead (pinned directly against
//     DocumentValidator in SchemaValidateConstraintsTests, Vouchfx.Engine.Compilation.Tests)
//     and no longer demonstrates a Pipeline-stage failure at all. Dependency
//     reconciliation is NOT liftable the same way — it is a genuine, permanent
//     Pipeline-only check — so it replaces the retired example here.
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

using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
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
        typeof(DbAssertPostgresProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    // ── Happy path: valid scenario reaches Roslyn success, zero containers ───────

    [Fact]
    public void ValidateScenario_ValidHttpRestScenario_IsValidWithNoDiagnostics()
    {
        // 'svc' must be a DECLARED service (services-generalisation spec, REQ-012): this
        // fixture predates REQ-012's target-reconciliation check, when http.rest accepted
        // any target string. This test was never "the hole", though: it is a positive/
        // happy-path test (asserts IsValid), so it only ever exercised REQ-012's ACCEPT
        // branch; the REJECT branch (an undeclared target) is independently covered by
        // HttpRestExecutionTests.Validate_TargetNamesNeitherServiceNorDependency_IsInvalid_ListsDeclaredSurfaces
        // (a dedicated provider-level test with a stub IProjectContext that deliberately
        // declares no services at all).
        const string yaml = """
            metadata:
              name: valid-scenario
            environment:
              services:
                svc:
                  image: myorg/svc:1.0
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
    public void ValidateScenario_DbAssertPostgresUndeclaredDependency_FailsAtPipelineStage()
    {
        // 'target: undeclared-db' names no entry under environment.dependencies in THIS
        // document. A step's own JsonSchemaFragment has no visibility into sibling
        // document sections, so this cross-reference can never become a JSON Schema
        // check — it stays a genuine ProviderPipeline.Compile failure:
        // DbAssertPostgresProvider.Validate's ctx.DeclaredDependencies reconciliation.
        const string yaml = """
            steps:
              - id: bad-target
                type: db-assert.postgres
                target: undeclared-db
                query: SELECT 1
                expect:
                  rowCount: 1
            """;

        var result = ScenarioValidator.ValidateScenario(yaml, "pipeline-invalid.e2e.yaml", s_registry);

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        // Pinned to DbAssertPostgresProvider.Validate's own reconciliation message
        // (minor, gatekeeper review): a loose "is not a" substring would equally
        // pass for an unrelated message accidentally containing that fragment.
        Assert.Contains(
            "db-assert.postgres: 'target' 'undeclared-db' is not a postgres dependency declared in environment.dependencies.",
            diagnostic.Message,
            StringComparison.Ordinal);
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
        // 'svc' must be a DECLARED service (services-generalisation spec, REQ-012) for the
        // VALID document — see ValidateScenario_ValidHttpRestScenario_IsValidWithNoDiagnostics's
        // own remark. The invalid document is unaffected: it fails at the schema stage
        // (missing 'method') before target reconciliation is ever reached, so it is
        // deliberately left with no environment block.
        const string validYaml = """
            environment:
              services:
                svc:
                  image: myorg/svc:1.0
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
    /// Two scenarios in SEPARATE directories, each referencing a DIFFERENTLY-NAMED
    /// <c>script.csharp</c> <c>file:</c> helper that exists ONLY beside that scenario — never
    /// beside the other. Passing each <see cref="ScenarioSource"/> its OWN directory must
    /// resolve BOTH references correctly (issue #268).
    /// </summary>
    /// <remarks>
    /// The two helpers are DELIBERATELY differently named (<c>helperA.csx</c> /
    /// <c>helperB.csx</c>) rather than sharing one filename: a shared filename present in
    /// BOTH directories would let this test pass even under a broken (suite-wide,
    /// first-scenario-only) base-directory resolution — it would not actually discriminate
    /// per-scenario resolution from the pre-#268 bug. With a distinct helper beside each
    /// scenario and nowhere else, a resolver that mistakenly broadcasts ONE shared directory
    /// to every scenario finds at most one of the two helpers, so at least one scenario fails
    /// — only genuine per-scenario resolution makes BOTH valid.
    /// </remarks>
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
            // Each scenario references a DIFFERENTLY-NAMED helper — deliberately a bare
            // filename (no path segments) so resolution is entirely determined by which
            // directory is passed as the base for THAT scenario.
            const string scenarioAYaml = """
                steps:
                  - id: run-helper
                    type: script.csharp
                    file: helperA.csx
                """;
            const string scenarioBYaml = """
                steps:
                  - id: run-helper
                    type: script.csharp
                    file: helperB.csx
                """;
            const string helperCsxContents = "// no-op\n";

            // helperA.csx exists ONLY in 'a'; helperB.csx exists ONLY in 'b' — a
            // shared-base-directory resolution would miss one or the other, whichever
            // directory it broadcasts.
            File.WriteAllText(Path.Combine(dirA, "helperA.csx"), helperCsxContents);
            File.WriteAllText(Path.Combine(dirB, "helperB.csx"), helperCsxContents);

            var sources = new[]
            {
                new ScenarioSource(
                    Path.Combine(dirA, "scenario-a.e2e.yaml"), scenarioAYaml, dirA),
                new ScenarioSource(
                    Path.Combine(dirB, "scenario-b.e2e.yaml"), scenarioBYaml, dirB),
            };

            var report = ScenarioValidator.Validate(sources, s_registry);

            Assert.Equal(2, report.Scenarios.Count);
            Assert.True(
                report.Scenarios[0].IsValid,
                "scenario-a must resolve 'file: helperA.csx' against ITS OWN directory ('a'). "
                + "Diagnostics: "
                + string.Join("; ", report.Scenarios[0].Diagnostics.Select(d => $"[{d.Stage}] {d.Message}")));
            Assert.True(
                report.Scenarios[1].IsValid,
                "scenario-b must resolve 'file: helperB.csx' against ITS OWN directory ('b'). "
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

    // ── EDGE-003 (#387): environment-level `security.clientKeyPassword` is reference-validated ──
    //
    // The asymmetry #387 measured: the same `${secret:nosuchsource/X}` token was diagnosed
    // properly in a step and silently mistaken for a filename in a security field, because the
    // secret-reference pass (Stage 3b) walked `ast.Steps` alone. `clientKeyPassword` is the ONE
    // reference-VALUED security field, so it is the one the pass was extended to reach; its
    // path-valued siblings are refused outright by REQ-011 at Stage 3a instead.

    /// <summary>
    /// EDGE-003 through the seam <c>vouchfx validate</c> actually reaches: an unknown source in
    /// an environment-level <c>clientKeyPassword</c> fails at the <see cref="ValidationStage.Pipeline"/>
    /// stage, naming the unknown source and the known ones — with the ENVIRONMENT field path and
    /// NOT the <c>step '…'</c> prefix, which has no sensible environment-level form.
    /// </summary>
    [Fact]
    public void ValidateScenario_EnvironmentClientKeyPasswordUnknownSource_FailsAtPipelineStage()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-edge003-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            // The path-valued fields must be real files: EnvironmentSecurityValidator runs at
            // Stage 3a, BEFORE the secret-reference pass, and would otherwise win the report.
            File.WriteAllText(Path.Combine(root, "client.pem"), "placeholder");
            File.WriteAllText(Path.Combine(root, "client.key"), "placeholder");

            const string yaml = """
                environment:
                  services:
                    api:
                      image: myorg/api:1.0
                      security:
                        profile: mtls
                        endpoint: 8443
                        clientCert: ./client.pem
                        clientKey: ./client.key
                        clientKeyPassword: "${secret:nosuchsource/KEY_PASS}"
                steps:
                  - id: call
                    type: http.rest
                    target: api
                    method: GET
                    path: /health
                """;

            var sources = new[]
            {
                new ScenarioSource(Path.Combine(root, "edge003.e2e.yaml"), yaml, root),
            };

            var report = ScenarioValidator.Validate(sources, s_registry);

            var entry = Assert.Single(report.Scenarios);
            Assert.False(entry.IsValid);
            var diagnostic = Assert.Single(entry.Diagnostics);
            Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);

            // The step surface's own message text, verbatim — the symmetry is the point of #387.
            Assert.Contains("names an unknown source 'nosuchsource'", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("known sources are:", diagnostic.Message, StringComparison.Ordinal);

            // …carried on an ENVIRONMENT field path, in EnvironmentSecurityValidator's spelling.
            Assert.Contains(
                "environment.services.api.security.clientKeyPassword:",
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("step '", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// EDGE-003's regression guard at the same seam: a VALID environment-level reference still
    /// validates clean, all four stages through.
    /// </summary>
    [Fact]
    public void ValidateScenario_EnvironmentClientKeyPasswordKnownSource_IsValid()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-edge003-ok-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "client.pem"), "placeholder");
            File.WriteAllText(Path.Combine(root, "client.key"), "placeholder");

            const string yaml = """
                environment:
                  services:
                    api:
                      image: myorg/api:1.0
                      security:
                        profile: mtls
                        endpoint: 8443
                        clientCert: ./client.pem
                        clientKey: ./client.key
                        clientKeyPassword: "${secret:env/CLIENT_KEY_PASS}"
                steps:
                  - id: call
                    type: http.rest
                    target: api
                    method: GET
                    path: /health
                """;

            var sources = new[]
            {
                new ScenarioSource(Path.Combine(root, "edge003-ok.e2e.yaml"), yaml, root),
            };

            var report = ScenarioValidator.Validate(sources, s_registry);

            var entry = Assert.Single(report.Scenarios);
            Assert.True(
                entry.IsValid,
                "Expected a valid document; diagnostics: " + string.Join(" | ", entry.Diagnostics.Select(d => d.Message)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── EDGE-003 at the pass itself (Stage 3b's internal) ────────────────────────────
    //
    // A MALFORMED reference cannot reach ValidateScenario's Stage 3b at all: the schema's
    // `clientKeyPassword` pattern is the anchored form of SecretReference's own grammar, so a
    // malformed token is rejected at Stage 1 and the document returns early. It IS reachable
    // through direct engine embedding, which is why the pass carries the rule rather than
    // trusting the schema — the same reasoning EnvironmentSecurityValidator's own tests record
    // for the artefact-target rules. These cases therefore drive the internal directly.

    private static ScenarioAst AstWithSecurity(
        IReadOnlyDictionary<string, ServiceSpec>? services = null,
        IReadOnlyDictionary<string, DependencySpec>? dependencies = null) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(services, dependencies, null, null, null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

    private static Dictionary<string, ServiceSpec> OneSecuredService(string name, string? clientKeyPassword) =>
        new(StringComparer.Ordinal)
        {
            [name] = new ServiceSpec("myorg/api:1.0", null, null, null, null)
            {
                Security = new SecuritySpec("mtls", "8443", null, "./client.pem", "./client.key", null)
                {
                    ClientKeyPassword = clientKeyPassword,
                },
            },
        };

    private static Dictionary<string, DependencySpec> OneSecuredDependency(string name, string? clientKeyPassword) =>
        new(StringComparer.Ordinal)
        {
            [name] = new DependencySpec("kafka", null, null)
            {
                Security = new SecuritySpec("mtls", "9093", null, "./client.pem", "./client.key", null)
                {
                    ClientKeyPassword = clientKeyPassword,
                },
            },
        };

    /// <summary>
    /// A malformed value is refused WITHOUT being quoted: <c>SecuritySpec.ClientKeyPassword</c>'s
    /// own remarks state that a direct embedder can bind a literal here, "so no consumer may
    /// assume the text is non-secret", and this pass is a consumer.
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_MalformedEnvironmentClientKeyPassword_IsRejectedWithoutQuotingIt()
    {
        // '${secret:env}' — the sigil with no '/path' segment. The fixture stands in for a
        // passphrase bound by a direct embedder, and is distinctive so the DoesNotContain below
        // cannot pass by accident.
        const string declared = "${secret:env}CORRECT-HORSE-BATTERY-STAPLE";
        var ast = AstWithSecurity(services: OneSecuredService("api", declared));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.NotNull(error);
        Assert.True(fromSecurity);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", error!, StringComparison.Ordinal);
        Assert.Contains("not a single, whole secret reference", error, StringComparison.Ordinal);

        // The whole point: the declared text never appears.
        Assert.DoesNotContain("CORRECT-HORSE-BATTERY-STAPLE", error, StringComparison.Ordinal);
        Assert.DoesNotContain("step '", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The NESTED-SIGIL shape, and it is reachable from an ordinary schema-valid document rather
    /// than only from a direct embedder: the schema pattern's path class <c>[^}]+</c> swallows a
    /// second <c>${secret:</c>. Before the withholding guard, this landed the whole declared value
    /// in the terminal and in <c>validate --json</c> via <c>ValidateField</c>'s malformed branch.
    /// <para>
    /// The first assertion is the measurement that makes this test necessary:
    /// <c>SecretReference.TryParse</c> ACCEPTS this value (its whole-token match simply spans the
    /// inner sigil), so the whole-token rule alone does not catch it — the known-source rule's
    /// own malformed branch is what must withhold, which is why
    /// <c>ValidateSecretBearingField</c> applies both rather than leaving them to a caller.
    /// </para>
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_NestedSigilClientKeyPassword_IsRejectedWithoutQuotingIt()
    {
        const string declared = "${secret:env/PASS${secret:CORRECT-HORSE-BATTERY-STAPLE}";

        // Measured, not assumed. If this ever answers false, the whole-token rule alone would
        // suffice for this shape and this test would stop covering the branch it was written for.
        Assert.True(SecretReference.TryParse(declared, out _));

        var ast = AstWithSecurity(services: OneSecuredService("api", declared));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.NotNull(error);
        Assert.True(fromSecurity);
        Assert.Contains("not a single, whole secret reference", error!, StringComparison.Ordinal);
        Assert.DoesNotContain("CORRECT-HORSE-BATTERY-STAPLE", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ADVERSARIAL shape: an unknown source AND a nested sigil in one value. It is the case a
    /// call-site predicate cannot get right — asking "is the source unknown?" answers yes, but the
    /// branch that actually fires is <c>ValidateField</c>'s MALFORMED one (the sigil counts
    /// disagree), which interpolates the whole field. Measured leaking through the real CLI in the
    /// terminal, in <c>run</c>, and in <c>validate --json</c> before <c>fieldMayBeSecret</c>
    /// moved the decision into the method that owns the arithmetic.
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_UnknownSourceAndNestedSigil_IsRejectedWithoutQuotingIt()
    {
        const string declared = "${secret:nosuchsource/PASS${secret:LEAKED_PASSPHRASE}";

        // Both halves of the trap, measured rather than assumed: the value parses as one whole
        // token (so guard 1 passes) AND names an unknown source (so an "unknown source only"
        // predicate would have relayed the malformed message verbatim).
        Assert.True(SecretReference.TryParse(declared, out var parsed));
        Assert.Equal("nosuchsource", parsed!.Source);

        var ast = AstWithSecurity(services: OneSecuredService("api", declared));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.NotNull(error);
        Assert.True(fromSecurity);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", error!, StringComparison.Ordinal);
        Assert.Contains("not a single, whole secret reference", error, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAKED_PASSPHRASE", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateSecretReferences_UnknownSourceOnADependency_NamesTheDependenciesFieldPath()
    {
        var ast = AstWithSecurity(
            dependencies: OneSecuredDependency("broker", "${secret:nosuchsource/KEY_PASS}"));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.NotNull(error);
        Assert.True(fromSecurity);
        Assert.Contains(
            "environment.dependencies.broker.security.clientKeyPassword:", error!, StringComparison.Ordinal);

        // Byte-identical to the step surface's own text — closing #387's asymmetry is the point,
        // and quoting the whole-token reference here is safe: it is a pointer, never a secret.
        Assert.Contains(
            "the secret reference '${secret:nosuchsource/KEY_PASS}' names an unknown source "
            + "'nosuchsource'; known sources are:",
            error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-018's carve-out must stay NARROW: a STEP's bad secret reference is an ordinary
    /// authoring error, not a failure to confirm a declared security assertion, so the signal is
    /// <see langword="false"/> even though the pass reports a failure.
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_StepFailure_DoesNotReportASecurityDeclaration()
    {
        // Built through the real parser + AstBuilder: a StepNode carries its own YamlMappingNode,
        // and CollectSubstitutableTexts reads it, so a hand-built node would not exercise the scan.
        const string yaml = """
            steps:
              - id: bad-secret
                type: http.rest
                target: svc
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:nosuchsource/TOKEN}"
            """;
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.NotNull(error);
        Assert.False(fromSecurity);
        Assert.Contains("step 'bad-secret'", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateSecretReferences_ValidEnvironmentClientKeyPassword_IsAccepted()
    {
        var ast = AstWithSecurity(services: OneSecuredService("api", "${secret:vault/kv/key-pass}"));

        Assert.False(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.Null(error);
        Assert.False(fromSecurity);
    }

    /// <summary>
    /// Pins the CALL SITE, not the method: the security walk must route through
    /// <c>SecretReference.ValidateSecretBearingField</c>, never <c>ValidateField</c>. Nothing
    /// structurally prevents a future caller reaching the quoting overload for this field — the
    /// only other guard is an XML remark — so the routing is asserted behaviourally, through the
    /// one input on which the two methods disagree in a way no other rule reproduces.
    /// <para>
    /// A plain literal is ACCEPTED by <c>ValidateField</c> (a step's field may be ordinary text)
    /// and REFUSED by <c>ValidateSecretBearingField</c>. So a scan wired to the wrong overload
    /// would silently pass a plaintext passphrase — this test fails the moment it is rewired.
    /// </para>
    /// </summary>
    private static readonly string[] s_knownSourcesForRouting = { "env", "vault" };

    [Fact]
    public void TryValidateSecretReferences_SecurityWalk_RoutesThroughTheSecretBearingOverload()
    {
        const string plaintextPassphrase = "hunter2-ROUTING-MARKER";

        // The discriminating property, measured here so the test states its own premise.
        Assert.True(SecretReference.ValidateField(plaintextPassphrase, s_knownSourcesForRouting, out _));

        var ast = AstWithSecurity(services: OneSecuredService("api", plaintextPassphrase));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.True(fromSecurity);
        Assert.Contains("not a single, whole secret reference", error!, StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTING-MARKER", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both faults present: the pass reports BOTH messages, step first. Before this, the security
    /// message was computed and discarded whenever a step fault won, so the exit code went red
    /// with nothing on screen to explain it.
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_StepAndSecurityFaults_ReportsBothMessages()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./client.pem
                    clientKey: ./client.key
                    clientKeyPassword: "${secret:nosuchsource/PASS}"
            steps:
              - id: call
                type: http.rest
                target: api
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:nosuchsource/STEP_TOKEN}"
            """;
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.True(fromSecurity);
        Assert.NotNull(error);
        Assert.Contains("step 'call'", error!, StringComparison.Ordinal);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", error, StringComparison.Ordinal);

        // Step first — the pre-existing ordering, retained.
        Assert.True(
            error!.IndexOf("step 'call'", StringComparison.Ordinal)
            < error.IndexOf("environment.services.api", StringComparison.Ordinal));
    }

    /// <summary>
    /// The scan must stay scoped to <c>clientKeyPassword</c>. A security block declaring NO
    /// passphrase is untouched by it — the path-valued siblings are REQ-011's business, and
    /// running them through reference validation as well would double-report.
    /// </summary>
    [Fact]
    public void TryValidateSecretReferences_SecurityBlockWithoutAPassphrase_IsAccepted()
    {
        var ast = AstWithSecurity(services: OneSecuredService("api", clientKeyPassword: null));

        Assert.False(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out var fromSecurity));
        Assert.Null(error);
        Assert.False(fromSecurity);
    }
}
