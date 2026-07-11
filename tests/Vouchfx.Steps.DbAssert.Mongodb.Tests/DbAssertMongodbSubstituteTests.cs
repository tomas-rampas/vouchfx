// DbAssertMongodbProvider substitution emit tests (non-docker).
//
// Verifies that DbAssertMongodbProvider.Emit:
//   • passes the raw filter template as a literal to ExecuteAsync (H1 blast-radius
//     containment — ResolveFilter is called INSIDE DbAssertMongodb_Helpers, not at
//     the StatementBlock call site);
//   • wraps each document-field expected value in Substitute_Helpers.Resolve(Vars, …)
//     so {placeholder} tokens in expect.document values resolve at runtime;
//   • includes Substitute_Helpers in RequiredHelpers;
//   • emits no 'using var';
//   • produces a valid BSON-safe filter when a placeholder value contains a BSON
//     operator string (JSON-escape prevents operator injection).
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Mongodb;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Mongodb.Tests;

/// <summary>
/// Emit-lint tests for <see cref="DbAssertMongodbProvider"/> substitution wrapping
/// and BSON injection safety.
/// </summary>
public sealed class DbAssertMongodbSubstituteTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    // ── Filter template: raw literal in StatementBlock; ResolveFilter in helper ──────

    /// <summary>
    /// H1 blast-radius containment: the StatementBlock must pass the raw filter
    /// template (JSON-escaped) directly to <c>ExecuteAsync</c> — NOT via a
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c> call expression.
    /// <c>ResolveFilter</c> must appear instead in
    /// <see cref="CsxFragment.RequiredHelpers"/> (inside
    /// <c>DbAssertMongodb_Helpers</c>) where it JSON-escapes injected values,
    /// limiting a bad-value failure to a step-scoped
    /// <c>Verdict.EnvironmentError</c> rather than a scenario-level abort.
    /// </summary>
    [Fact]
    public void Emit_FilterTemplate_IsRawLiteralInStatementBlock_NotResolveExpression()
    {
        var provider = new DbAssertMongodbProvider();
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{\"orderId\": \"{capturedId}\"}",
            Expect: new MongoExpectation(Count: 1, Document: null));
        var ctx = new StubCompileContext("mongo-step");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // (a) The StatementBlock must NOT resolve the filter via Substitute_Helpers.Resolve.
        //     Filter resolution (with JSON-escape) happens entirely inside DbAssertMongodb_Helpers.
        Assert.DoesNotContain("Substitute_Helpers.Resolve(Vars,", block, StringComparison.Ordinal);

        // (b) The raw {capturedId} placeholder must survive as literal text inside the
        //     JSON-escaped string argument passed to ExecuteAsync.
        Assert.Contains("{capturedId}", block, StringComparison.Ordinal);

        // (c) ResolveFilter must appear in RequiredHelpers.
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);
        Assert.Contains("ResolveFilter", allHelpers, StringComparison.Ordinal);
    }

    // ── Document field expected values wrapped in Resolve ─────────────────────────

    /// <summary>
    /// Expected-field values in <c>expect.document</c> must be wrapped in
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c> in the StatementBlock so that
    /// <c>{placeholder}</c> tokens resolve at runtime from <c>Vars</c>.
    /// </summary>
    [Fact]
    public void Emit_DocumentFieldExpectValues_AreWrappedInResolveCall()
    {
        var provider = new DbAssertMongodbProvider();
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{\"orderId\": 1}",
            Expect: new MongoExpectation(
                Count: null,
                Document: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "{expectedStatus}",
                }));
        var ctx = new StubCompileContext("mongo-step-2");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // The expected value must be wrapped in Resolve.
        Assert.Contains("Substitute_Helpers.Resolve(Vars,", block, StringComparison.Ordinal);

        // The {expectedStatus} token must survive as literal text.
        Assert.Contains("{expectedStatus}", block, StringComparison.Ordinal);
    }

    // ── Substitute_Helpers in RequiredHelpers ─────────────────────────────────────

    /// <summary>
    /// <c>Substitute_Helpers</c> must appear in
    /// <see cref="CsxFragment.RequiredHelpers"/>.
    /// </summary>
    [Fact]
    public void Emit_SubstituteHelpersInRequiredHelpers()
    {
        var provider = new DbAssertMongodbProvider();
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));
        var ctx = new StubCompileContext("s");

        var fragment = provider.Emit(model, ctx);
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Substitute_Helpers", allHelpers, StringComparison.Ordinal);
    }

    // ── No 'using var' ───────────────────────────────────────────────────────────

    /// <summary>
    /// Neither the StatementBlock nor RequiredHelpers must contain <c>using var</c>.
    /// </summary>
    [Fact]
    public void Emit_NoUsingVar_AfterSubstituteIntegration()
    {
        var provider = new DbAssertMongodbProvider();
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{\"status\": \"{capturedStatus}\"}",
            Expect: new MongoExpectation(
                Count: null,
                Document: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "{expectedStatus}",
                }));
        var ctx = new StubCompileContext("sub-test");

        var fragment = provider.Emit(model, ctx);
        var full = fragment.StatementBlock
                   + "\n"
                   + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }

    // ── BSON injection safety: ResolveFilter JSON-escapes placeholder values ───────

    /// <summary>
    /// <c>DbAssertMongodb_Helpers.ResolveFilter</c> must JSON-escape placeholder
    /// values so that a value containing a BSON operator string such as
    /// <c>{"$gt":""}</c> becomes a JSON-escaped string literal inside the filter
    /// document — not a nested BSON object that would change query semantics.
    /// This test drives ResolveFilter directly via a compile-and-invoke round-trip
    /// through Roslyn.
    /// </summary>
    [Fact]
    public async Task ResolveFilter_BsonOperatorValue_IsJsonEscapedNotInjected()
    {
        // Inline the DbAssertMongodb_Helpers source (from the provider) and call
        // ResolveFilter directly — no topology, no docker.
        var provider = new DbAssertMongodbProvider();
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{\"orderId\": \"{capturedId}\"}",
            Expect: new MongoExpectation(Count: 1, Document: null));
        var ctx = new StubCompileContext("bson-inject-test");
        var fragment = provider.Emit(model, ctx);

        // Build a minimal CSX that calls ResolveFilter with an operator-injection value.
        // The helper class uses extension methods from MongoDB.Driver (e.g. .Find),
        // so the using directive must be present even though all types are fully qualified.
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);
        var csx =
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using System.Text.RegularExpressions;\n" +
            "using System.Text.Json;\n" +
            "using MongoDB.Driver;\n" +
            "using MongoDB.Bson;\n" +
            allHelpers + "\n" +
            "var fakeVars = new Dictionary<string, object?>(StringComparer.Ordinal)\n" +
            "{\n" +
            "    [\"capturedId\"] = \"{\\\"$gt\\\":\\\"\\\"}\",\n" +
            "};\n" +
            "Vars[\"result\"] = DbAssertMongodb_Helpers.ResolveFilter(fakeVars, \"{\\\"orderId\\\": \\\"{capturedId}\\\"}\");\n";

        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = Vouchfx.Engine.Compilation.RoslynScriptCompiler.CompileOnce(
            csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);
        await Vouchfx.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var resolved = Assert.IsType<string>(vars["result"]);

        // The resolved filter must be valid JSON with the operator as a string value,
        // not as a nested object: {\"orderId\": \"...$gt...\"}, not {\"orderId\": {\"$gt\":\"\"}}
        // The operator characters must be present but escaped — they cannot form a nested object.
        Assert.Contains("$gt", resolved, StringComparison.Ordinal);

        // The resolved value must be parseable as JSON — no structure-breaking injection.
        var doc = MongoDB.Bson.BsonDocument.Parse(resolved); // throws if injection broke JSON structure
        Assert.NotNull(doc);

        // orderId must be a string value (JSON-escaped), not an object (injected).
        var orderId = doc["orderId"];
        Assert.Equal(MongoDB.Bson.BsonType.String, orderId.BsonType);
    }

    // ── Resolve: arbitrary values substitute without throwing ─────────────────────

    /// <summary>
    /// <c>Substitute_Helpers.Resolve</c> must continue to substitute arbitrary values
    /// (including values that contain braces or special chars) without throwing —
    /// regression guard for expect.document field value paths.
    /// </summary>
    [Fact]
    public async Task Resolve_ArbitraryValue_SubstitutesWithoutThrowing()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{val}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["val"] = "{\"$gt\":\"\"}",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Vouchfx.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Vouchfx.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("{\"$gt\":\"\"}", vars["result"]);
    }
}
