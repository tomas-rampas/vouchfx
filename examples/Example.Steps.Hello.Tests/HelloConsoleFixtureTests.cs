// Integration-test fixture for the WORKED-EXAMPLE provider (S08 T5a / S08-F-04).
//
// This is the acceptance proof that the frozen v1 Vouchfx.Sdk contract is usable
// to build a NON-Core provider end to end, WITHOUT Docker:
//
//   1. The reflective StepKindRegistry discovers the example provider purely from
//      its [StepProvider] attribute (no hand-registration) — proving an external
//      contributor only has to (a) add a project, (b) implement the v1 interfaces,
//      (c) mark [StepProvider].
//   2. The provider's JsonSchemaFragment composes into the unified language schema,
//      so a .e2e.yaml using `hello.console` passes schema validation.
//   3. The full front end (parse → AST) accepts the step.
//   4. The provider's Bind → Validate → Emit produces a CsxFragment that the shared
//      CsxAssembler splices and Roslyn compiles ONCE (§5 memory model: compile-once,
//      isolate, unload — never CSharpScript.EvaluateAsync).
//   5. RunIsolatedAsync executes the emitted step against a ScriptGlobalVariables,
//      and the step writes a StepOutcome the runner reads back from Vars.
//
// All tests are non-docker: `hello.console` is a dependency-free step (no service,
// no managed dependency), so the fixture drives the compile-and-run pipeline directly
// without standing up any Aspire topology — mirroring the existing non-docker provider
// round-trip tests (e.g. Vouchfx.Steps.Script.Csharp.Tests).
//
// BDD note: this fixture was written FIRST (red — the provider did not yet exist),
// then the provider was implemented to make it pass (green).

using System.Linq;
using Example.Steps.Hello;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Xunit;

namespace Example.Steps.Hello.Tests;

// ── Engine-supplied context stubs ───────────────────────────────────────────────
//
// IBindingContext / IProjectContext / ICompileContext are engine-supplied and
// provider-consumed.  In production the engine's ProviderPipeline constructs them;
// here the fixture supplies minimal stand-ins so the example provider can be driven
// in isolation.  These same three tiny stubs are what a contributor would copy to
// unit-test their own provider.

/// <summary>Minimal <see cref="IBindingContext"/> for the example fixture.</summary>
file sealed class FixtureBindingContext : IBindingContext { }

/// <summary>Minimal <see cref="IProjectContext"/> for the example fixture.</summary>
file sealed class FixtureProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Minimal <see cref="ICompileContext"/> for the example fixture.</summary>
file sealed class FixtureCompileContext : ICompileContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal FixtureCompileContext(string stepId) => StepId = stepId;

    public string StepId { get; }

    public string SuiteNamespace => "VouchfxGenerated";

    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}

/// <summary>
/// End-to-end, non-docker integration fixture for the worked-example
/// <see cref="HelloConsoleProvider"/> (<c>hello.console</c>).
/// </summary>
public sealed class HelloConsoleFixtureTests
{
    // The step kind id the worked example registers under.
    private const string StepKind = "hello.console";

    // The example provider assembly — the ONLY input the reflective registry needs.
    private static readonly System.Reflection.Assembly s_exampleAssembly =
        typeof(HelloConsoleProvider).Assembly;

    private static StepKindRegistry BuildRegistry() =>
        StepKindRegistry.BuildAndFreeze(new[] { s_exampleAssembly });

    // ── Acceptance: the reflective registry discovers the example provider ────────

    /// <summary>
    /// The example provider must be discovered by <see cref="StepKindRegistry"/>
    /// purely from its <c>[StepProvider]</c> attribute — no hand-registration.
    /// This is the "discovered by the reflective registry" half of the acceptance.
    /// </summary>
    [Fact]
    public void ExampleProvider_IsDiscoveredByReflectiveRegistry()
    {
        var registry = BuildRegistry();

        var found = registry.TryGet(StepKind, out var registered);

        Assert.True(found, $"Expected the reflective registry to discover '{StepKind}'.");
        Assert.NotNull(registered);
        Assert.Equal("hello", registered!.Kind.Family);
        Assert.Equal("console", registered.Kind.Provider);
        Assert.IsType<HelloConsoleProvider>(registered.Instance);
        Assert.NotNull(registered.SchemaFragment);
    }

    /// <summary>
    /// §5.6 teaching point: the worked example lives in a NON-reserved namespace
    /// (<c>Example.Steps.*</c>), NOT the reserved <c>Vouchfx.Steps.*</c> /
    /// <c>Vouchfx.Engine.*</c> namespaces that customer DLLs are refused for.  This
    /// test documents and guards that the template a contributor copies is itself
    /// compliant with the assembly-graph-hygiene rule.
    /// </summary>
    [Fact]
    public void ExampleProvider_UsesNonReservedNamespace()
    {
        var ns = typeof(HelloConsoleProvider).Namespace;

        Assert.Equal("Example.Steps.Hello", ns);
        Assert.False(
            ns!.StartsWith("Vouchfx.Steps", StringComparison.Ordinal),
            "The worked example must NOT use the reserved 'Vouchfx.Steps.*' namespace (§5.6).");
        Assert.False(
            ns.StartsWith("Vouchfx.Engine", StringComparison.Ordinal),
            "The worked example must NOT use the reserved 'Vouchfx.Engine.*' namespace (§5.6).");
    }

    // ── Acceptance: a .e2e.yaml using the example step validates + runs → Pass ────

    /// <summary>
    /// The end-to-end acceptance: a small <c>.e2e.yaml</c> using the <c>hello.console</c>
    /// step validates against the composed schema, compiles through the shared pipeline,
    /// runs in an isolated collectible context, and yields <see cref="Verdict.Pass"/>.
    /// No Docker — the step has no dependency.
    /// </summary>
    [Fact]
    public async Task HelloConsoleStep_MatchingExpectation_RunsEndToEnd_Pass()
    {
        const string yaml = """
            metadata:
              name: hello-world-example
              description: Worked-example provider — emits a greeting and asserts a constant.
            steps:
              - id: say-hello
                type: hello.console
                message: "hello, vouchfx"
                expect: "hello, vouchfx"
            """;

        var (verdict, observation) = await RunSingleStepScenarioAsync(yaml, "say-hello");

        Assert.Equal(Verdict.Pass, verdict);
        // On a Pass the provider records the asserted message as its observation.
        Assert.Contains("hello, vouchfx", observation ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fail path: when the emitted message does not match the expectation the
    /// step yields <see cref="Verdict.Fail"/> (NOT an exception, NOT EnvironmentError) —
    /// proving the example provider's assertion is real, so the Pass above is meaningful.
    /// </summary>
    [Fact]
    public async Task HelloConsoleStep_MismatchedExpectation_RunsEndToEnd_Fail()
    {
        const string yaml = """
            steps:
              - id: say-hello
                type: hello.console
                message: "hello, vouchfx"
                expect: "goodbye, vouchfx"
            """;

        var (verdict, _) = await RunSingleStepScenarioAsync(yaml, "say-hello");

        Assert.Equal(Verdict.Fail, verdict);
    }

    /// <summary>
    /// A <c>.e2e.yaml</c> whose <c>hello.console</c> step omits the required
    /// <c>message</c> field must fail schema validation against the composed schema —
    /// proving the provider's <see cref="IStepBinder{TModel}.SchemaFragment"/> is
    /// actually composed in and enforced.
    /// </summary>
    [Fact]
    public void HelloConsoleStep_MissingRequiredField_FailsSchemaValidation()
    {
        const string yaml = """
            steps:
              - id: say-hello
                type: hello.console
                expect: "hello, vouchfx"
            """;

        var registry = BuildRegistry();

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid,
            "A hello.console step missing the required 'message' field must fail schema validation.");
    }

    // ── The end-to-end driver (the copyable harness) ─────────────────────────────

    /// <summary>
    /// Drives a single-step <c>.e2e.yaml</c> through the full public engine pipeline —
    /// schema-validate → parse → AST → bind → validate → emit → assemble → compile-once →
    /// run-isolated — and returns the step's recorded verdict and observation.
    /// </summary>
    /// <remarks>
    /// This is exactly the path the engine's <c>ScenarioRunner</c> takes, minus the
    /// Aspire topology (unnecessary for a dependency-free step).  Every stage uses the
    /// PUBLIC engine surface, so it doubles as a worked example of how to exercise any
    /// custom provider end to end in a unit-test harness.
    /// </remarks>
    private static async Task<(Verdict Verdict, string? Observation)> RunSingleStepScenarioAsync(
        string yaml,
        string stepId)
    {
        var registry = BuildRegistry();

        // 1. Schema-validate the YAML against the composed language schema.
        var schemaResult = DocumentValidator.Validate(yaml, registry);
        Assert.True(schemaResult.IsValid,
            "YAML failed schema validation: " +
            string.Join("; ", schemaResult.Errors.Select(e => e.Message)));

        // 2. Parse → AST (front end).
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, registry);
        var node = Assert.Single(ast.Steps);
        Assert.Equal(stepId, node.Id);

        // 3. Resolve the provider FROM THE REGISTRY (not constructed by hand) and drive
        //    the v1 contract: Bind → Validate → Emit.  The fixture knows the concrete
        //    model type because it owns the example provider; a generic engine would
        //    reflect these, but the strongly-typed calls show the contract directly.
        Assert.True(registry.TryGet(node.CanonicalType, out var registered));
        var provider = Assert.IsType<HelloConsoleProvider>(registered!.Instance);

        var model = provider.Bind(node.RawNode, new FixtureBindingContext());
        var validation = provider.Validate(model, new FixtureProjectContext());
        Assert.True(validation.IsValid,
            "Model validation failed: " + string.Join("; ", validation.Errors));

        var fragment = provider.Emit(model, new FixtureCompileContext(node.Id));

        // 4. Assemble the fragment(s) and compile ONCE (§5: compile-once / isolate / unload).
        var assembled = CsxAssembler.Assemble(new[] { (node.Id, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);

        // 5. Run in an isolated collectible context; the step writes its StepOutcome to Vars.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: stepId);

        // 6. Read the outcome back from Vars under the engine-reserved outcome key.
        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected the step to write an outcome under '{outcomeKey}'. " +
            $"Keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.True(outcome.DurationMs >= 0);
        return (outcome.Verdict, outcome.Observation);
    }
}
