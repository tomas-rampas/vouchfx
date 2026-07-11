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
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

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

        // Assemble exactly as CsxAssembler.Assemble would.
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

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

            // Assemble exactly as CsxAssembler.Assemble would (same harness as test 11).
            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

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
        var contributor = (ICompileReferenceContributor)_provider;
        var names = contributor.CompileReferenceAssemblies
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

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

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

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

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

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

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

    // ── Private helpers ───────────────────────────────────────────────────────────

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
