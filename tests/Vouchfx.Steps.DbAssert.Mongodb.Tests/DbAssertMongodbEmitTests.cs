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
//  11. Full compile-and-run (no docker): credential absent from observation on credentialed failure.
//  12. Direct: RedactCredentials strips secret from a crafted message containing the secret.
//  13. Direct: RedactCredentials handles mongodb+srv:// URIs.
//  14. Full compile-and-run (no docker): runtime denylist catches {placeholder} resolving to $where.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Mongodb;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Mongodb.Tests;

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
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

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

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

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
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

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

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

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

    // ── 11. Compile round-trip: credential absent from observation on failure ──────

    /// <summary>
    /// When the connection string contains credentials and the connection attempt fails,
    /// the observation must NOT expose the password (§17 — no secrets in observations).
    /// <c>RedactCredentials</c> inside <c>DbAssertMongodb_Helpers</c> sanitises the
    /// full connection string and the MongoDB userinfo (user:pwd@) segment.
    /// The URI includes <c>serverSelectionTimeoutMS=500</c> to ensure the test
    /// completes in under a second rather than waiting for the 30s default timeout.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        const string stepId = "mongo-cred-leak-check";
        const string connStr =
            "mongodb://user:sup3rsecret@bad-host:1/db" +
            "?serverSelectionTimeoutMS=500&connectTimeoutMS=500&socketTimeoutMS=500";
        var model = MakeModel("dep", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("dep")] = connStr,
        };
        var globals = new ScriptGlobalVariables(vars);

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
        Assert.DoesNotContain("sup3rsecret", outcome.Observation, StringComparison.Ordinal);
    }

    // ── 12. Direct test of RedactCredentials with a crafted dangerous message ────

    /// <summary>
    /// Directly tests the emitted <c>DbAssertMongodb_Helpers.RedactCredentials</c> method
    /// by invoking it — via a compiled test CSX body — with a crafted message that
    /// intentionally contains the password.  This verifies that the redaction logic
    /// actually strips the secret rather than relying on the driver never emitting
    /// credentials in its error message (which is driver-version-dependent).
    /// Both the literal connection-string replacement path and the URI-pattern
    /// (<c>mongodb://user:pwd@</c>) path are exercised.
    /// </summary>
    [Fact]
    public async Task Emit_RedactCredentials_WithCraftedMessageContainingSecret_SecretIsStripped()
    {
        const string stepId = "mongo-redact-direct";
        const string connStr = "mongodb://user:sup3rsecret@bad-host:1/db";
        // Craft a message that DOES contain the secret, simulating a hypothetical driver
        // that leaks credentials in its error output.  Without RedactCredentials this
        // would expose the password; after redaction it must not.
        const string craftedMessage =
            "connection failed to mongodb://user:sup3rsecret@bad-host:1/db (timed out)";

        var model = MakeModel("dep", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        // CSX body: read connStr + message from Vars (avoids string-literal escaping hazards),
        // call the internal RedactCredentials method directly, write result back to Vars.
        const string scriptBody =
            "Vars[\"__redact_result__\"] = DbAssertMongodb_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

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
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_result__"),
            "Expected '__redact_result__' in Vars after calling RedactCredentials.");
        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("sup3rsecret", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    // ── 13. Direct test: RedactCredentials handles mongodb+srv:// URIs ────────────

    /// <summary>
    /// Verifies that <c>RedactCredentials</c> redacts Atlas SRV URIs
    /// (<c>mongodb+srv://user:pwd@cluster</c>) as well as standard URIs.
    /// This tests the Minor-1 fix: the regex must match both <c>mongodb://</c>
    /// and <c>mongodb+srv://</c> userinfo segments.
    /// </summary>
    [Fact]
    public async Task Emit_RedactCredentials_SrvUri_SecretIsStripped()
    {
        const string stepId = "mongo-redact-srv";
        const string connStr = "mongodb+srv://user:sup3rsecret@cluster.mongodb.net/db";
        const string craftedMessage =
            "TLS handshake failed to mongodb+srv://user:sup3rsecret@cluster.mongodb.net/db";

        var model = MakeModel("dep", "orders", "{}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_srv_result__\"] = DbAssertMongodb_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

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
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_srv_result__"),
            "Expected '__redact_srv_result__' in Vars.");
        var result = Assert.IsType<string>(vars["__redact_srv_result__"]);
        Assert.DoesNotContain("sup3rsecret", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    // ── 14. Runtime denylist: placeholder resolving to $where → Fail ─────────────

    /// <summary>
    /// When a placeholder in key position resolves to a denied operator at runtime
    /// (e.g. <c>Vars["op"] = "$where"</c>), the runtime denylist re-check inside
    /// <c>ContainsDeniedOperatorRuntime</c> must catch it after <c>ResolveFilter</c>
    /// but BEFORE any network call, producing <see cref="Verdict.Fail"/> (not
    /// <see cref="Verdict.EnvironmentError"/>).
    ///
    /// The connection string deliberately points at a non-existent host
    /// (<c>bad-host:1</c>).  The test completes without a network timeout because:
    /// <list type="bullet">
    ///   <item><c>new MongoClient()</c>, <c>GetDatabase()</c>, and <c>GetCollection()</c>
    ///         are lazy — no connection is established until the first I/O call.</item>
    ///   <item>The runtime denylist fires on the parsed <see cref="MongoDB.Bson.BsonDocument"/>
    ///         (an in-process operation) and returns before <c>CountDocumentsAsync</c>
    ///         (the first network call).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_RuntimeDenylistTriggeredByPlaceholder_ReturnsFail()
    {
        const string stepId = "mongo-runtime-deny";
        // Filter template: the key position contains a {placeholder}.
        // At runtime, Vars["op"] = "$where" makes ResolveFilter produce {"$where": 1},
        // which the runtime re-check must detect before CountDocumentsAsync is called.
        var model = MakeModel("dep", "orders", "{\"{op}\": 1}", count: 1L);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

        var additionalRefs = new[]
        {
            typeof(MongoDB.Driver.MongoClient).Assembly.Location,
            typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Present so the helper proceeds past the "absent conn key" guard.
            // The host is unreachable; the denylist fires before CountDocumentsAsync.
            [VarKeys.Connection("dep")] =
                "mongodb://bad-host:1/db" +
                "?serverSelectionTimeoutMS=500&connectTimeoutMS=500&socketTimeoutMS=500",
            // This value resolves {op} → $where after ResolveFilter.
            ["op"] = "$where",
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

        // Must be Fail, not EnvironmentError — an injection attempt is a test defect, not infra.
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);

        // The observation must report the denylist message, confirming the runtime guard fired.
        Assert.Contains("after placeholder substitution", outcome.Observation, StringComparison.Ordinal);
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
