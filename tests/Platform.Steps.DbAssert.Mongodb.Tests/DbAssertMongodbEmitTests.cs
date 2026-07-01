// Tests for DbAssertMongodbProvider — CSX emitter + resource contributor.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment.
//   3. Emit: helper class is named 'DbAssertMongodb_Helpers' (§13.3.1 prefix rule).
//   4. Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5. Emit: filter template and field values containing special characters are JSON-escaped.
//   6. Emit: RequiredUsings contains the MongoDB.Driver namespace.
//   7. Resources: yields a mongodb ResourceRequirement whose Name equals model.Target.
//   8. CompileReferenceAssemblies: contains the MongoDB.Driver assembly.
//   9. Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//  10. Full compile-and-run (no docker): EnvironmentError when conn string is malformed.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mongodb;
using Xunit;

namespace Platform.Steps.DbAssert.Mongodb.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="DbAssertMongodbProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>),
/// resource contributor (<see cref="IResourceContributor{TModel}"/>), and
/// compile-reference contributor (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class DbAssertMongodbEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        /// <inheritdoc />
        public string StepId { get; }

        /// <inheritdoc />
        public string SuiteNamespace => "Generated";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly DbAssertMongodbProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{'
    /// and end with '}', satisfying the §13.3.1 brace rule.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("testmongo", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext("check-step");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual start: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual end: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' ────────────────────────────────────────────────────────

    /// <summary>
    /// Neither the <see cref="CsxFragment.StatementBlock"/> nor any entry in
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain 'using var'
    /// (Roslyn script parse error, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = MakeModel("testmongo", "orders", "{\"status\":\"SHIPPED\"}", count: 1L,
            document: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "SHIPPED",
            });
        var ctx = new StubCompileContext("my-step");

        var fragment = _provider.Emit(model, ctx);
        var fullSource = fragment.StatementBlock
            + "\n"
            + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain an entry whose class name
    /// begins with <c>DbAssertMongodb_</c> (§13.3.1 provider-prefix rule).
    /// The fragment also contains <c>Substitute_Helpers</c>; the test therefore asserts
    /// the provider-prefixed class is present rather than requiring exactly one helper.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsDbAssertMongodbPrefixedClass()
    {
        var model = MakeModel("testmongo", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("DbAssertMongodb_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A step id containing hyphens must appear in the StatementBlock only after
    /// sanitisation (hyphens replaced with underscores), never as the raw hyphenated
    /// form (which would be an invalid C# identifier, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "check-order-status";
        var safeId = CsxFragment.SanitiseId(rawId); // "check_order_status"
        var model = MakeModel("testmongo", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        // The sanitised outcome key must appear in the block.
        var expectedKey = VarKeys.Outcome(safeId);
        Assert.Contains(expectedKey, fragment.StatementBlock, StringComparison.Ordinal);

        // The raw hyphenated id must NOT appear in the block (it would be an invalid identifier).
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. JSON-escaped filter and field values ───────────────────────────────────

    /// <summary>
    /// Filter template text and document field values containing double-quotes,
    /// backslashes, or other special characters must be emitted as JSON-escaped
    /// string literals so they cannot break the CSX statement block.
    /// </summary>
    [Fact]
    public void Emit_SpecialCharactersInFilterAndDocument_AreJsonEscaped()
    {
        const string dangerousFilter = "{\"field\": \"val\\with\\backslash\"}";
        const string dangerousFieldValue = "val\\with\"quotes";
        var model = MakeModel(
            target: "testmongo",
            collection: "orders",
            filter: dangerousFilter,
            count: 1L,
            document: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = dangerousFieldValue,
            });
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        // The raw unescaped filter must not appear verbatim in the StatementBlock.
        Assert.DoesNotContain(dangerousFilter, fragment.StatementBlock, StringComparison.Ordinal);

        // The raw unescaped field value must not appear verbatim in the StatementBlock.
        Assert.DoesNotContain(dangerousFieldValue, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. RequiredUsings contains MongoDB.Driver namespace ───────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must include the
    /// <c>MongoDB.Driver</c> namespace, which the emitted helper class requires.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsMongoDbDriverNamespace()
    {
        var model = MakeModel("testmongo", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("MongoDB.Driver", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 7. IResourceContributor yields mongodb ResourceRequirement ────────────────

    /// <summary>
    /// <see cref="IResourceContributor{TModel}.Resources"/> must yield exactly one
    /// <see cref="ResourceRequirement"/> with <c>Family="mongodb"</c> and
    /// <c>Name</c> equal to <see cref="DbAssertMongodbModel.Target"/>.
    /// </summary>
    [Fact]
    public void Resources_YieldsMongodbRequirementWithMatchingName()
    {
        var model = MakeModel("testmongo", "orders", "{}", count: 1L);

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("mongodb", req.Family, StringComparer.Ordinal);
        Assert.Equal("testmongo", req.Name, StringComparer.Ordinal);
    }

    // ── 8. ICompileReferenceContributor returns MongoDB assemblies ────────────────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// contain the <c>MongoDB.Driver</c> assembly so the Roslyn compiler can resolve
    /// <c>MongoClient</c> in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsMongoDbDriverAssembly()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("MongoDB.Driver", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 9. Compile round-trip: EnvironmentError when conn key absent ──────────────

    /// <summary>
    /// When the connection key is absent from <c>Vars</c>, the emitted helper must
    /// write <see cref="Verdict.EnvironmentError"/> to the outcome key rather than
    /// throwing an unhandled exception.  This test also verifies that the emitted
    /// CSX compiles without errors with the MongoDB.Driver reference assembly.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "mongo-step";
        var model = MakeModel("missing-dep", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        // Assemble exactly as CsxAssembler.Assemble would.
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        // Supply MongoDB.Driver + MongoDB.Bson + System.Text.Json as compile-time references.
        // Neither is ever loaded into the collectible ALC (§5 memory-model invariant).
        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // No connection key seeded in Vars — the helper must detect the absence and
        // write EnvironmentError rather than throwing.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 10. Compile round-trip: EnvironmentError when conn string is malformed ─────

    /// <summary>
    /// When the connection key is present but the connection string is malformed
    /// (causing <c>MongoClient</c> constructor to throw), the emitted helper must
    /// catch the exception and write <see cref="Verdict.EnvironmentError"/> rather
    /// than propagating the throw.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MalformedConnStr_ReturnsEnvironmentError()
    {
        const string stepId = "mongo-step-bad-conn";
        var model = MakeModel("my-dep", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Present but non-parseable as a MongoDB URI — MongoClient constructor will throw.
            [VarKeys.Connection("my-dep")] = "@@@not-a-mongo-uri@@@",
        };
        var globals = new ScriptGlobalVariables(vars);

        // Must NOT propagate the exception — the helper catches it and writes EnvironmentError.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="DbAssertMongodbModel"/> for use in emit tests.
    /// </summary>
    private static DbAssertMongodbModel MakeModel(
        string target,
        string collection,
        string filter,
        long? count = null,
        IReadOnlyDictionary<string, string>? document = null)
    {
        return new DbAssertMongodbModel(
            Target: target,
            Collection: collection,
            Filter: filter,
            Expect: new MongoExpectation(Count: count, Document: document));
    }
}
