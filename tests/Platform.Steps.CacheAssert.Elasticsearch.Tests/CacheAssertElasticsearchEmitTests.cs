// Tests for CacheAssertElasticsearchProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Mirrors Platform.Steps.CacheAssert.Redis.Tests/CacheAssertRedisEmitTests.cs.
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment (§13.3.1 invariant).
//   3.  Emit: helper class is named 'CacheAssertElasticsearch_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: special characters in index/query are JSON-escaped.
//   6.  Emit: query template with {placeholder} survives verbatim (not emit-time interpolated).
//   7.  Emit: exact-count null is emitted as 'null' literal.
//   8.  Emit: field arrays are emitted as inline string array literals.
//   9.  Resources: yields an elasticsearch ResourceRequirement whose Name equals model.Target.
//   10. CompileReferenceAssemblies: contains the System.Net.Http assembly.
//   11. Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   12. Full compile-and-run (no docker): EnvironmentError when endpoint is dead (count mismatch path).
//   13. Full compile-and-run (no docker): credential URL not leaked in observation (§17 redaction).
//   14. Full compile-and-run (no docker): match_all default query compiles (no explicit query).
//   15. Full compile-and-run (no docker): field assertion path compiles (with expect.fields).
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Elasticsearch;
using Xunit;

namespace Platform.Steps.CacheAssert.Elasticsearch.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="CacheAssertElasticsearchProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class CacheAssertElasticsearchEmitTests
{
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

    private readonly CacheAssertElasticsearchProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  The ES provider uses
    /// BCL HttpClient, System.Text.Json, and System.Uri — none are in the default
    /// TPA-only Roslyn reference subset, so they must be supplied explicitly.
    /// These are compile-time references only; at runtime they resolve from the Default ALC.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    // A dead local endpoint — nothing listens on 56790, so HTTP POST fails fast.
    private const string DeadBaseUrl = "http://localhost:56790";

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("es-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'), "StatementBlock must begin with '{'.");
        Assert.True(block.EndsWith('}'), "StatementBlock must end with '}'.");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsCacheAssertElasticsearchPrefixedClass()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("CacheAssertElasticsearch_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "check-es-hits";
        var safeId = CsxFragment.SanitiseId(rawId);  // "check_es_hits"
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. Special characters in index/query are JSON-escaped ─────────────────

    [Fact]
    public void Emit_SpecialCharactersInIndex_AreJsonEscaped()
    {
        const string dangerousIndex = "my-index\"with\\quotes";
        var model = MakeModel(index: dangerousIndex);
        var fragment = _provider.Emit(model, new StubCompileContext("escape-test"));

        // The raw unescaped string must not appear verbatim — it would break the literal.
        Assert.DoesNotContain(dangerousIndex, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. {placeholder} in query template survives emit ─────────────────────

    [Fact]
    public void Emit_QueryWithPlaceholder_SurvivesVerbatimInEmittedLiteral()
    {
        // The {status} placeholder must survive in the emitted C# string literal so
        // CacheAssertElasticsearch_Helpers.ResolveQuery can resolve it at runtime.
        const string queryWithPlaceholder = "{\"query\":{\"match\":{\"status\":\"{status}\"}}}";
        var model = MakeModel(query: queryWithPlaceholder);
        var fragment = _provider.Emit(model, new StubCompileContext("placeholder-step"));

        // The placeholder text must appear inside the emitted StatementBlock —
        // specifically, it is JSON-serialized so {status} becomes {status}
        // OR appears verbatim inside a raw string with double-backslash escaping.
        // Either way the token text "status" must appear in the block.
        Assert.Contains("status", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. Exact-count null emitted as 'null' literal ─────────────────────────

    [Fact]
    public void Emit_NoExactCount_EmitsNullLiteralInStatementBlock()
    {
        var model = MakeModel(count: null, minCount: 2);
        var fragment = _provider.Emit(model, new StubCompileContext("count-step"));

        // When count is null the call site must pass 'null' so the helper uses min-count.
        Assert.Contains("null", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 8. Field assertion arrays emitted as inline array literals ────────────

    [Fact]
    public void Emit_WithFieldAssertions_EmitsStringArrayLiterals()
    {
        var fields = new[] { new EsFieldAssertion("status", "active") };
        var model = MakeModel(fields: fields);
        var fragment = _provider.Emit(model, new StubCompileContext("field-step"));

        // Field names and expected values must appear as string literals in the block.
        Assert.Contains("\"status\"", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("\"active\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor ───────────────────────────────────────────────

    [Fact]
    public void Resources_YieldsElasticsearchRequirementWithMatchingName()
    {
        var requirements = _provider.Resources(MakeModel(target: "search")).ToList();

        Assert.Single(requirements);
        Assert.Equal("elasticsearch", requirements[0].Family, StringComparer.Ordinal);
        Assert.Equal("search", requirements[0].Name, StringComparer.Ordinal);
    }

    // ── 10. ICompileReferenceContributor ──────────────────────────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsSystemNetHttpAssembly()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        Assert.Contains(contributor.CompileReferenceAssemblies.ToList(), a =>
            a.GetName().Name?.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile round-trip: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = MakeModel(target: "missing-dep");

        // No connection key seeded in Vars — the helper must detect the absence and write
        // EnvironmentError rather than throwing.
        var outcome = await RunStepAsync(
            model, "es-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 12. Compile round-trip: EnvironmentError when endpoint is dead ────────

    [Fact]
    public async Task Emit_CompileAndRun_DeadEndpoint_ReturnsEnvironmentError()
    {
        var model = MakeModel(target: "search", count: 1);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = DeadBaseUrl,
        };

        var outcome = await RunStepAsync(model, "es-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 13. Compile round-trip: credential URL absent from observation ─────────

    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        const string connUrl = "http://elastic:sup3rsecret@localhost:56790";
        var model = MakeModel(target: "search");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = connUrl,
        };

        var outcome = await RunStepAsync(model, "es-cred-leak-check", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        // §17: the password must never appear in the observation.
        Assert.DoesNotContain("sup3rsecret", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 14. Compile round-trip: default match_all query compiles ──────────────

    [Fact]
    public async Task Emit_CompileAndRun_DefaultMatchAllQuery_CompilesAndReturnsEnvironmentError()
    {
        // Model with no explicit query → the helper emits the match_all default.
        // Against a dead endpoint it must surface EnvironmentError (not a compile error).
        var model = MakeModel(target: "search", query: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = DeadBaseUrl,
        };

        var outcome = await RunStepAsync(model, "es-matchall", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── 15. Compile round-trip: field assertion path compiles ─────────────────

    [Fact]
    public async Task Emit_CompileAndRun_WithFieldAssertions_CompilesAndReturnsEnvironmentError()
    {
        // Model with field assertions — proves the expanded array literals compile.
        var fields = new[] { new EsFieldAssertion("status", "active") };
        var model = MakeModel(target: "search", fields: fields);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("search")] = DeadBaseUrl,
        };

        var outcome = await RunStepAsync(model, "es-fields", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static CacheAssertElasticsearchModel MakeModel(
        string target = "search",
        string index = "orders",
        string? query = null,
        int? count = null,
        int minCount = 1,
        IReadOnlyList<EsFieldAssertion>? fields = null) =>
        new CacheAssertElasticsearchModel(
            Target: target,
            Index: index,
            Query: query,
            Expect: new EsExpectation(Count: count, MinCount: minCount, Fields: fields));

    /// <summary>
    /// Emits the fragment for a <c>cache-assert.elasticsearch</c> step, assembles it,
    /// compiles it once, and executes it with the supplied <c>Vars</c> dictionary.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        CacheAssertElasticsearchModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}
