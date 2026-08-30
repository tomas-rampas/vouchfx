// Tests for MqPublishKafkaProvider — CSX emitter + resource / compile-reference contributors.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment (CSX parse-error guard, §13.3.1).
//   3. Emit: helper class is named 'MqPublishKafka_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised to the outcome key '__outcome::pub_evt'.
//   5. Emit: RequiredUsings contains the Confluent.Kafka namespace.
//   6. Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   7. Emit: payload / topic with special characters are JSON-escaped (literal safety).
//   8. Emit: an absent key is emitted as the bare 'null' literal.
//   9. Resources: yields exactly one kafka ResourceRequirement whose Name == model.Target.
//  10. CompileReferenceAssemblies: contains the Confluent.Kafka assembly.
//  11. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  12. Full compile-and-run (no docker): EnvironmentError via SECRET resolution (a
//      missing ${secret:env/…} payload reference) WITHOUT a broker — the helper
//      resolves secrets before building the producer, so ProduceAsync is never
//      reached; the observation is reference-only (source/path, never the value, §17).
//  13. Emit (avro): RequiredHelpers contain the avro publish + CoerceField paths.
//  14. CompileReferenceAssemblies includes the Avro serdes assemblies.
//  15. Avro compile round-trip: EnvironmentError when the registry URL is absent.
//  16. Avro compile round-trip: EnvironmentError when the bootstrap is absent.
//  17. Avro compile round-trip: a coercion failure is value-free (§17).
//  18. Emit (#367): the teardown flush is bounded by a CTS linked to the step token on
//      BOTH produce paths, and no unconditional Flush(TimeSpan.FromSeconds(10)) remains.
//  19. Emit (#367): the ten-second cap survives for a step declaring no timeout.
//  20. Emit (#367): the flush cut is swallowed, the producer is disposed FIRST, and the
//      linked CTS is explicitly disposed after it.
//  21. Emit (#367): no client delivery timeout is derived from the step budget — the
//      rejected alternative fix, pinned so it is not reintroduced.
//  22. Compile-and-RUN (#367, no docker): a governed step against a refused peer
//      (127.0.0.1:9) concludes at its budget with the wrapper's step-timeout outcome
//      intact — the one #367 assertion that EXECUTES the new teardown rather than
//      text-matching it. Fails at roughly budget + 10s against the pre-fix shape.
//
// Entries 13-17 were present in the file but missing from this index before #367; they
// are enumerated here rather than left as a silent gap the next author renumbers into.
using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqPublishKafkaProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqPublishKafkaEmitTests
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

    private readonly MqPublishKafkaProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{' and
    /// end with '}', satisfying the §13.3.1 brace rule.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("events-bus", "orders.created", "hello");
        var ctx = new StubCompileContext("publish-step");

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
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain 'using var' (Roslyn
    /// script parse error, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = MakeModel("bus", "t", "hello", key: "k",
            headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" });
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
    /// begins with <c>MqPublishKafka_</c> (§13.3.1 provider-prefix rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsMqPublishKafkaPrefixedClass()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqPublishKafka_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A hyphenated step id must appear in the StatementBlock only after sanitisation;
    /// the id <c>pub-evt</c> must yield the outcome key <c>__outcome::pub_evt</c>, and
    /// the raw hyphenated form must NOT appear (it would be an invalid C# identifier).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "pub-evt";
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("__outcome::pub_evt", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredUsings contains Confluent.Kafka namespace ─────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must include the <c>Confluent.Kafka</c>
    /// namespace, which the emitted helper requires (§13.3.1 bare namespace rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsConfluentKafkaNamespace()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("Confluent.Kafka", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 6. RequiredHelpers includes shared substitution + secret helpers ─────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must include the byte-identical
    /// <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources so the emitted CSX
    /// can resolve <c>{placeholder}</c> and <c>${secret:source/path}</c> at runtime.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 7. Special characters JSON-escaped ───────────────────────────────────────

    /// <summary>
    /// Payload and topic text containing double-quotes or backslashes must be emitted
    /// as JSON-escaped string literals so they cannot break the CSX statement block.
    /// </summary>
    [Fact]
    public void Emit_SpecialCharactersInPayloadAndTopic_AreJsonEscaped()
    {
        const string dangerousPayload = "{\"raw\":\"a\\b\\\"c\"}";
        const string dangerousTopic = "topic\"with\\specials";
        var model = MakeModel("bus", dangerousTopic, dangerousPayload);
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        // The raw unescaped strings must not appear verbatim — they would break the literal.
        Assert.DoesNotContain(dangerousPayload, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousTopic, fragment.StatementBlock, StringComparison.Ordinal);

        // The block must compile cleanly — verified by the compile round-trip test.
    }

    // ── 8. Absent key → bare 'null' literal ──────────────────────────────────────

    /// <summary>
    /// When the model has no key, the StatementBlock must pass the bare <c>null</c>
    /// literal for the key argument (not a quoted empty string).
    /// </summary>
    [Fact]
    public void Emit_AbsentKey_EmitsNullLiteral()
    {
        var model = MakeModel("bus", "t", "hello", key: null);
        var ctx = new StubCompileContext("k");

        var fragment = _provider.Emit(model, ctx);

        // The key argument sits between the topic literal and the payload literal in the
        // PublishAsync call; with no key it must be the bare 'null' keyword.
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor yields kafka ResourceRequirement ─────────────────

    /// <summary>
    /// <see cref="IResourceContributor{TModel}.Resources"/> must yield exactly one
    /// <see cref="ResourceRequirement"/> with <c>Family="kafka"</c>, <c>Name</c> equal
    /// to <see cref="MqPublishKafkaModel.Target"/>, and a <c>null</c> Image.
    /// </summary>
    [Fact]
    public void Resources_YieldsSingleKafkaRequirementWithMatchingName()
    {
        var model = MakeModel("events-bus", "t", "hello");

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("kafka", req.Family, StringComparer.Ordinal);
        Assert.Equal("events-bus", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 10. ICompileReferenceContributor returns Confluent.Kafka assembly ────────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// contain the <c>Confluent.Kafka</c> assembly so the Roslyn compiler can resolve
    /// the producer types in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsConfluentKafkaAssembly()
    {
        var assemblies = ((ICompileReferenceContributor)_provider).CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile round-trip: EnvironmentError when conn key absent ────────────

    /// <summary>
    /// When the connection (bootstrap) key is absent from <c>Vars</c>, the emitted
    /// helper must write <see cref="Verdict.EnvironmentError"/> to the outcome key
    /// rather than throwing or attempting to connect.  This proves the emitted CSX
    /// compiles against the real Confluent.Kafka metadata AND that the
    /// bootstrap-missing path is reached WITHOUT any broker (no Docker required):
    /// because no <c>conn::&lt;target&gt;</c> key is staged, the helper short-circuits
    /// to EnvironmentError before ever building a producer or calling ProduceAsync.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "pub-step";
        var model = MakeModel("missing-bus", "orders.created", "hello",
            key: "k",
            headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" });
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        // Assemble via the real CsxAssembler (not a manual join) — it declares the
        // per-step __stepCt_<safeId> / __stepBudgetGoverned_<safeId> locals the emitted
        // call site now references (§4 common step fields, issue #232).
        var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(
            new[] { (stepId, fragment) }).CsxSource;

        // The emitted helper references Confluent.Kafka, System.Text.Json, System.Text
        // (Encoding), System.Globalization — AND (because the helper class is now
        // unconditionally Avro-aware) the Avro serdes assemblies, even though THIS step is
        // a plain-payload step.  Supply each as compile-time metadata.  None is ever loaded
        // into the collectible ALC (§5 memory-model invariant).
        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // No connection key seeded in Vars — the helper must detect the absence and
        // write EnvironmentError rather than throwing or connecting to a broker.
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

    // ── 12. Compile round-trip: EnvironmentError via SECRET resolution (no broker) ─

    /// <summary>
    /// When the payload carries a <c>${secret:env/…}</c> reference whose environment
    /// variable is unset, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> via secret resolution — NOT via a broker
    /// connection.  A bootstrap value is staged in <c>Vars</c> so the helper passes the
    /// bootstrap check and proceeds INTO its guarded region; there,
    /// <c>Secret_Helpers.ResolveTemplate</c> resolves the topic/key/payload BEFORE
    /// <c>new ProducerBuilder(...).Build()</c>, so the missing secret throws
    /// <c>SecretResolutionException</c> before any producer exists and before
    /// <c>ProduceAsync</c> is ever reached.  Hence no Kafka broker is contacted and the
    /// test cannot hang on a connection attempt.  The observation is REFERENCE-ONLY
    /// (§17): it carries the <c>secretError</c> marker plus the <c>env</c> source and the
    /// variable-name path, and never the (non-existent) secret value.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayload_ReturnsEnvironmentError_ReferenceOnly()
    {
        // Unique env name so the variable is guaranteed absent and cannot be raced by a
        // sibling test (matches the SecretResolution* tests' convention).
        var envName = "VOUCHFX_MQPUBLISH_MISSING_" + Guid.NewGuid().ToString("N");

        // Defensive: ensure the variable is absent (the GUID name should not exist).
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "pub-secret-step";
            const string target = "events-bus";

            // The payload carries a missing secret reference; the topic is a plain string.
            var model = MakeModel(
                target,
                "orders.created",
                $"{{\"token\":\"${{secret:env/{envName}}}\"}}");
            var ctx = new StubCompileContext(stepId);
            var fragment = _provider.Emit(model, ctx);

            // Assemble via the real CsxAssembler (same harness as test 11).
            var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(
                new[] { (stepId, fragment) }).CsxSource;

            // Same reference set as test 11 (Confluent.Kafka + Avro serdes + BCL facades),
            // plus System.Text.RegularExpressions which Secret_Helpers / Substitute_Helpers
            // use for token scanning.
            var additionalRefs = new[]
            {
                typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
                typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
                typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
                typeof(Avro.Schema).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Text.Encoding).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            };
            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

            // Stage a bootstrap VALUE so the helper passes the bootstrap check and
            // proceeds to secret resolution.  No broker is ever contacted: resolution
            // throws BEFORE the producer is built (helper order, §17).
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "localhost:9092",
            };

            // A REAL env-backed accessor — the same resolver the runner builds — over the
            // env source.  Because envName is unset, env/<NAME> genuinely cannot resolve,
            // so ResolveTemplate throws SecretResolutionException inside the guarded region.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — the exception is contained inside the step's guarded region.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId(stepId);
            var outcomeKey = VarKeys.Outcome(safeId);

            Assert.True(vars.ContainsKey(outcomeKey),
                $"Expected Vars to contain outcome key '{outcomeKey}'. " +
                $"Actual keys: [{string.Join(", ", vars.Keys)}]");

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            // The verdict is EnvironmentError reached via SECRET resolution, not a broker.
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
            Assert.NotNull(outcome.Observation);

            // REFERENCE-ONLY contract (§17): the observation names the secret-error marker,
            // the 'env' source, and the variable-name path — the discrete reference
            // coordinates — and NEVER a secret value (none exists when resolution fails).
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 13. Emit (avro): RequiredHelpers contain the avro publish + CoerceField paths ─

    /// <summary>
    /// When the model carries an avro spec, the emitted helper text contains the Avro
    /// publish path (CachedSchemaRegistryClient / AvroSerializer / GenericRecord) and the
    /// <c>CoerceField</c> coercion method.  The fragment also still contains no
    /// <c>using var</c> (CSX parse-error guard, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_AvroModel_HelperContainsAvroPathAndCoerceField()
    {
        var model = MakeAvroModel();
        var ctx = new StubCompileContext("pub-avro");

        var fragment = _provider.Emit(model, ctx);
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("CachedSchemaRegistryClient", fullSource, StringComparison.Ordinal);
        Assert.Contains("AvroSerializer", fullSource, StringComparison.Ordinal);
        Assert.Contains("GenericRecord", fullSource, StringComparison.Ordinal);
        Assert.Contains("CoerceField", fullSource, StringComparison.Ordinal);
        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);

        // The svc::<sr>-sr registry key is spliced into the call (VarKeys.Service pattern).
        Assert.Contains("svc::events-bus-sr", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 14. CompileReferenceAssemblies includes the Avro serdes assemblies ───────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must include
    /// the Confluent.SchemaRegistry, Confluent.SchemaRegistry.Serdes.Avro, and Apache.Avro
    /// (Avro) assemblies so the emitted Avro CSX compiles.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsAvroSerdesAssemblies()
    {
        var names = ((ICompileReferenceContributor)_provider).CompileReferenceAssemblies
            .Select(a => a.GetName().Name)
            .ToList();

        Assert.Contains("Confluent.SchemaRegistry", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Confluent.SchemaRegistry.Serdes.Avro", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Avro", names, StringComparer.OrdinalIgnoreCase);
    }

    // ── 15. Avro compile round-trip: EnvironmentError when registry URL absent ───

    /// <summary>
    /// An avro publish step emits CSX that COMPILES against the real Avro serdes metadata,
    /// and — with a kafka bootstrap staged but NO <c>svc::&lt;sr&gt;-sr</c> registry URL
    /// staged — writes <see cref="Verdict.EnvironmentError"/> ("schema registry URL not
    /// found").  This proves the Avro CSX compiles AND that the missing-registry path is
    /// reachable WITHOUT a live registry or broker: the registry-URL check precedes any
    /// CachedSchemaRegistryClient / producer construction, so neither a registry nor a
    /// broker is ever contacted and the test cannot hang.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_Avro_AbsentRegistryUrl_ReturnsEnvironmentError()
    {
        const string stepId = "pub-avro-step";
        const string target = "events-bus";
        var model = MakeAvroModel();
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(
            new[] { (stepId, fragment) }).CsxSource;

        // Supply the serdes + supporting assemblies as compile-time metadata.  The emitted
        // Avro path references Confluent.SchemaRegistry(.Serdes.Avro), Avro, Confluent.Kafka,
        // System.Text.Json, System.Text (Encoding), System.Globalization, and (via shared
        // helpers) System.Text.RegularExpressions.
        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // Stage a bootstrap value so the helper passes the bootstrap check and reaches the
        // registry-URL check — but DO NOT stage the svc::<sr>-sr key.  The registry-URL
        // check runs BEFORE any client is built, so no broker/registry is contacted.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = "localhost:9092",
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected outcome key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("schema registry URL not found", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 16. Avro compile round-trip: EnvironmentError when bootstrap absent ──────

    /// <summary>
    /// An avro publish step with NO kafka bootstrap staged writes
    /// <see cref="Verdict.EnvironmentError"/> ("kafka bootstrap not found") — the bootstrap
    /// check precedes the registry-URL check, so this path is also broker-free.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_Avro_AbsentBootstrap_ReturnsEnvironmentError()
    {
        const string stepId = "pub-avro-nb";
        var model = MakeAvroModel();
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(
            new[] { (stepId, fragment) }).CsxSource;

        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // No bootstrap, no registry URL — the bootstrap check fires first.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Contains("kafka bootstrap not found", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 17. Avro compile round-trip: coercion failure is value-free (§17) ─────────

    /// <summary>
    /// SECRET-LEAK GUARD (§17): when an avro record value fails type coercion (here a
    /// non-numeric literal against an <c>int</c> field), the emitted helper's
    /// <c>CoerceField</c> throws an <see cref="InvalidOperationException"/> that the
    /// catch-all maps to <see cref="Verdict.EnvironmentError"/> — and the resulting
    /// observation must name only the (author-declared, non-secret) FIELD NAME and the
    /// EXPECTED Avro type, NEVER the offending value.  Because a <c>${secret:…}</c> field
    /// is secret-resolved BEFORE coercion runs, echoing the value would place a revealed
    /// secret onto the event stream; this test pins that it does not (a plain non-numeric
    /// value exercises the identical throw path and proves the message is value-free).
    /// <para>
    /// BROKER-FREE: BOTH a kafka bootstrap AND the <c>svc::&lt;sr&gt;-sr</c> registry URL
    /// are staged so the helper passes the bootstrap and registry-URL checks and reaches
    /// record-building.  <c>CoerceField</c> throws during <c>GenericRecord</c> construction
    /// — BEFORE the <c>CachedSchemaRegistryClient</c> is constructed and BEFORE any
    /// <c>ProduceAsync</c>/registry HTTP call — so neither a live registry nor a broker is
    /// ever contacted and the test cannot hang.  (CachedSchemaRegistryClient is lazy in any
    /// case: it connects only on first serialize/register, which is never reached here.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_Avro_CoercionFailure_ObservationIsValueFree()
    {
        const string stepId = "pub-avro-coerce";
        const string target = "events-bus";

        // A recognisable sentinel value that fails int coercion.  If the (now-fixed) bug
        // regressed, this literal would appear verbatim in the observation; the assertions
        // below prove it does not.
        const string sentinel = "NOT_A_NUMBER_SENTINEL_a1b2c3d4";

        // Avro schema declaring a single 'amount' field of type int; the record supplies a
        // non-numeric value for it, so CoerceField's int branch throws during record build.
        var model = new MqPublishKafkaModel(
            Target: target,
            Topic: "orders.created",
            Key: null,
            Payload: "ignored-when-avro",
            Headers: null,
            Avro: new KafkaAvro(
                SchemaRegistryTarget: target,
                Subject: "orders.created-value",
                Schema: "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[" +
                        "{\"name\":\"amount\",\"type\":\"int\"}]}",
                Record: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["amount"] = sentinel,
                }));
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(
            new[] { (stepId, fragment) }).CsxSource;

        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // Stage BOTH the bootstrap AND the registry URL so the helper gets PAST both guards
        // and into record-building, where CoerceField throws BEFORE any client construction.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = "localhost:9092",
            [VarKeys.Service(target + "-sr")] = "http://localhost:8081",
        };
        var globals = new ScriptGlobalVariables(vars);

        // Must NOT throw or hang — CoerceField throws inside the step's guarded region,
        // before any registry/broker network call.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected outcome key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

        // The coercion failure is an EnvironmentError (§12.1), reached without a broker.
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);

        // The observation names the (non-secret) field and the expected type — but NEVER
        // the offending value.  This is the §17 secret-leak pin.
        Assert.Contains("amount", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("int", outcome.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 18-21. Teardown flush is bounded by the step token (#367) ────────────────

    /// <summary>
    /// The emitted helper must bound its teardown flush by a
    /// <c>CancellationTokenSource</c> linked to the step token — on BOTH the plain and
    /// the Avro produce paths — and must retain no unconditional
    /// <c>Flush(TimeSpan.FromSeconds(10))</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the #367 regression pin, and it guards a defect that lived entirely in the
    /// teardown rather than in the client.  MEASURED against an unreachable broker on
    /// Confluent.Kafka 2.14.2 with a 5000 ms token: <c>ProduceAsync(topic, msg, ct)</c>
    /// returned <c>OperationCanceledException</c> at 5025 ms — the step token IS observed —
    /// after which the fixed <c>Flush(10s)</c> burned its full 10001 ms because the
    /// undeliverable message was still queued.  Total 15045 ms on a 5000 ms budget, which
    /// is the same budget + 10 s constant behind both figures in the filing (30027 ms on a
    /// 20 s budget; 20099 ms on a 10 s budget).
    /// </para>
    /// <para>
    /// A future maintainer removing the linkage would restore an overrun that only a
    /// live unreachable broker can expose, which is why the shape is pinned here where
    /// no Docker is needed.
    /// </para>
    /// </remarks>
    [Fact]
    public void Emit_TeardownFlush_IsBoundedByTheStepTokenOnBothProducePaths()
    {
        var helperSource = EmittedHelperSource();

        // The fixed-duration flush is the defect: it must be gone from both paths.
        Assert.DoesNotContain(
            "producer.Flush(System.TimeSpan.FromSeconds(10))",
            helperSource,
            StringComparison.Ordinal);

        // Both produce paths (plain + Avro) link their flush CTS to the step token.
        Assert.Equal(
            2,
            CountOccurrences(
                helperSource,
                "System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct)"));
        Assert.Equal(2, CountOccurrences(helperSource, "producer.Flush(flushCts.Token)"));
    }

    /// <summary>
    /// The ten-second cap survives for a step that declares no timeout — <c>ct</c> is
    /// <c>CancellationToken.None</c> there, so <c>CancelAfter</c> is the only thing that
    /// can cut the flush.  The fix must not tighten the ungoverned case.
    /// </summary>
    /// <remarks>
    /// The cap is <strong>defensive, not the operative bound</strong> on the ungoverned
    /// single-message path, and the assertion is kept for that reason rather than because
    /// it is load-bearing today. MEASURED: with no declared timeout the flush leg costs
    /// 0 ms, because an ungoverned step blocks inside <c>ProduceAsync</c> instead — on
    /// librdkafka's own 300-second <c>message.timeout.ms</c> default — and never reaches
    /// the teardown with the message still queued. The cap therefore only bites if a
    /// future change lets a queued message reach this <c>finally</c> without a governing
    /// token, which is precisely the regression worth pinning.
    /// </remarks>
    [Fact]
    public void Emit_TeardownFlush_KeepsTheTenSecondCapForAnUngovernedStep()
    {
        var helperSource = EmittedHelperSource();

        Assert.Equal(
            2,
            CountOccurrences(helperSource, "flushCts.CancelAfter(System.TimeSpan.FromSeconds(10))"));
    }

    /// <summary>
    /// The flush cut must be swallowed and the linked CTS disposed: an
    /// <c>OperationCanceledException</c> escaping the <c>finally</c> would displace the
    /// produce's own outcome, and <c>using var</c> is illegal in a Roslyn script body
    /// (§13.3.1), so disposal is explicit.
    /// </summary>
    [Fact]
    public void Emit_TeardownFlush_SwallowsTheCutAndDisposesTheLinkedSource()
    {
        var helperSource = EmittedHelperSource();

        Assert.DoesNotContain("using var", helperSource, StringComparison.Ordinal);

        var collapsed = CollapseWhitespace(helperSource);

        // The Flush call sits inside a try whose catch swallows everything, so no teardown
        // failure — cancellation included — can reach the step's classification.  Compared
        // over whitespace-collapsed text so re-indenting the emitted helper does not
        // redden a test about its control flow.
        Assert.Equal(
            2,
            CountOccurrences(
                collapsed,
                CollapseWhitespace("producer.Flush(flushCts.Token); } catch { }")));

        // Both disposals live in the SAME finally and the ORDER is the assertion: the
        // producer goes first, so nothing — not the swallowing catch above ceasing to
        // swallow, not a throw from the CTS's own disposal — can precede releasing the
        // native librdkafka handle. §5 is the hard invariant; a leaked CTS timer is not.
        Assert.Equal(
            2,
            CountOccurrences(
                collapsed,
                CollapseWhitespace(
                    "finally { producer.Dispose(); if (flushCts is not null) flushCts.Dispose(); }")));

        // The linked source is constructed INSIDE the guarded region, so a throw from
        // CreateLinkedTokenSource itself cannot skip the producer's release.
        Assert.Equal(
            2,
            CountOccurrences(
                collapsed,
                CollapseWhitespace(
                    "try { flushCts = System.Threading.CancellationTokenSource" +
                    ".CreateLinkedTokenSource(ct);")));
    }

    /// <summary>
    /// The producer config must NOT derive <c>message.timeout.ms</c> (or any sibling
    /// delivery timeout) from the step budget.
    /// </summary>
    /// <remarks>
    /// The rejected alternative, pinned so it is not reintroduced as a "fix" for #367.
    /// librdkafka's own delivery timeout is not on the critical path — the step token
    /// already cuts <c>ProduceAsync</c> at the budget (measured, 5025 ms against 5000 ms) —
    /// and setting it to the budget would race that token.  Whichever won would decide the
    /// verdict: the token yields <c>Inconclusive</c>, which §12.1 makes the correct outcome
    /// for a timeout and which the filing explicitly confirms as already correct, while a
    /// delivery-timeout expiry surfaces as a <c>ProduceException</c> and would be
    /// classified <c>EnvironmentError</c>.  Deriving the client timeout would therefore
    /// trade a timing defect for a nondeterministic verdict.
    /// </remarks>
    [Fact]
    public void Emit_ProducerConfig_DoesNotDeriveAClientDeliveryTimeoutFromTheBudget()
    {
        var helperSource = EmittedHelperSource();

        Assert.DoesNotContain("MessageTimeoutMs", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("message.timeout.ms", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeliveryTimeoutMs", helperSource, StringComparison.Ordinal);
    }

    // ── 22. Compile-and-RUN: the bounded teardown actually executes (#367) ───────

    /// <summary>
    /// Executes the real emitted teardown against a refused connection under a governed
    /// step token, with no broker and no Docker: the step must conclude at its budget,
    /// carry the wrapper's <c>step-timeout</c> outcome undisplaced, and not re-block in
    /// <c>Dispose()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other #367 assertion in this file text-matches the emitted source. This one
    /// runs it, closing the gap that the new teardown was pinned but never executed
    /// outside a Docker job. Three things are proved together and only by executing:
    /// the <c>finally</c> does not throw (a teardown throw would surface as some other
    /// verdict), the swallowed flush cancellation does not displace the produce's own
    /// outcome, and <c>producer.Dispose()</c> does not re-block after the cut.
    /// </para>
    /// <para>
    /// <c>127.0.0.1:9</c> is the discard port: nothing listens, so the connection is
    /// refused immediately and repeatedly. That is deliberately NOT a fast produce
    /// failure — librdkafka keeps retrying and the message stays QUEUED (its own
    /// <c>message.timeout.ms</c> default is 300 s), which is exactly the stuck state the
    /// teardown has to handle. Measured against a refused peer, the produce is cut by the
    /// token, the linked flush is cut within one ~100 ms librdkafka poll slice, and
    /// <c>Dispose()</c> purges the queue in ~110 ms without re-blocking.
    /// </para>
    /// <para>
    /// Against the pre-fix teardown this test FAILS on the duration assertion, at roughly
    /// budget + 10 s — it is a genuine regression pin, not a smoke test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Emit_CompileAndRun_GovernedStepAgainstARefusedPeer_ConcludesAtItsBudget()
    {
        const string stepId = "pub-bounded";
        const long budgetMs = 2_000;

        // SHARED MEASUREMENT (see KafkaStepTimeoutBoundDockerTests.GraceMs, which sizes its
        // own grace from the same numbers): the post-fix tail is ~250 ms — a flush cut within
        // one ~100 ms librdkafka poll slice, plus a Dispose measured at 16-110 ms depending on
        // how the peer refuses. Generous against that, still far below the ~10 000 ms the
        // unbounded flush cost, so the pin cannot pass while the defect is present however
        // loaded the CI host is. Deliberately duplicated rather than hoisted into TestSupport:
        // the two live in different assemblies and each needs its own budget-relative headroom.
        const long graceMs = 1_500;

        var model = MakeModel("bus", "orders", "hello");
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        // TimeoutMs set, so CsxAssembler emits the IMMEDIATE wrapper: the per-step CTS,
        // the step stopwatch, and the filtered catch that classifies a token cut as
        // Inconclusive(step-timeout). Without it the emitted call site would receive
        // CancellationToken.None and nothing here would be governed.
        var csx = Vouchfx.Engine.Compilation.CsxAssembler.Assemble(new[]
        {
            new Vouchfx.Engine.Compilation.StepCompilePlan(
                stepId, fragment, Retry: false, TimeoutMs: budgetMs, PollIntervalMs: null),
        }).CsxSource;

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx, additionalReferencePaths: EmittedHelperReferencePaths);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // The discard port: refused, never listening, no container involved.
            [VarKeys.Connection("bus")] = "127.0.0.1:9",
        };

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(stepId))]);

        // The produce was cut by the step token and the wrapper classified it — proving the
        // teardown neither threw nor overwrote the outcome on its way out.
        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.Contains("step-timeout", outcome.Observation!, StringComparison.Ordinal);

        // The bound itself: this is the assertion the pre-fix teardown fails.
        Assert.True(
            outcome.DurationMs <= budgetMs + graceMs,
            $"step declared a {budgetMs} ms timeout but concluded in {outcome.DurationMs} ms — " +
            $"an overrun of {outcome.DurationMs - budgetMs} ms against an allowed grace of " +
            $"{graceMs} ms (#367). Observation: {outcome.Observation}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Compile-time metadata references the emitted helper needs. The helper class is
    /// unconditionally Avro-aware, so the serdes assemblies are required even for a
    /// plain-payload step. None is ever loaded into the collectible ALC (§5).
    /// </summary>
    private static readonly string[] EmittedHelperReferencePaths =
    {
        typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
        typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
        typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
        typeof(Avro.Schema).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Encoding).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    /// <summary>
    /// Returns the provider-owned <c>MqPublishKafka_Helpers</c> source from a fresh emit.
    /// The shared <c>Substitute_Helpers</c> / <c>Secret_Helpers</c> / <c>KafkaSecurity_Helpers</c>
    /// entries are excluded so these assertions cannot be satisfied — or broken — by SDK
    /// helper text this provider does not own.
    /// </summary>
    private string EmittedHelperSource()
    {
        var fragment = _provider.Emit(MakeModel("bus", "t", "hello"), new StubCompileContext("s"));

        return System.Linq.Enumerable.Single(
            fragment.RequiredHelpers,
            h => h.Contains("MqPublishKafka_Helpers", StringComparison.Ordinal));
    }

    /// <summary>
    /// Collapses every run of whitespace to a single space so a control-flow assertion
    /// survives re-indentation of the emitted helper.
    /// </summary>
    private static string CollapseWhitespace(string source)
        => System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ").Trim();

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static MqPublishKafkaModel MakeModel(
        string target,
        string topic,
        string payload,
        string? key = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(
            Target: target,
            Topic: topic,
            Key: key,
            Payload: payload,
            Headers: headers);

    private static MqPublishKafkaModel MakeAvroModel()
        => new(
            Target: "events-bus",
            Topic: "orders.created",
            Key: "order-42",
            Payload: "ignored-when-avro",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" },
            Avro: new KafkaAvro(
                SchemaRegistryTarget: "events-bus",
                Subject: "orders.created-value",
                Schema: "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[" +
                        "{\"name\":\"id\",\"type\":\"int\"}," +
                        "{\"name\":\"amount\",\"type\":\"double\"}," +
                        "{\"name\":\"name\",\"type\":\"string\"}," +
                        "{\"name\":\"active\",\"type\":\"boolean\"}]}",
                Record: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = "42",
                    ["amount"] = "19.99",
                    ["name"] = "widget",
                    ["active"] = "true",
                }));
}
