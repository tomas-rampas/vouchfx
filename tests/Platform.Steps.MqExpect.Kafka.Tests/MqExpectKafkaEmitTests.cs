// Tests for MqExpectKafkaProvider — CSX emitter + resource / compile-reference contributors.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' anywhere in the emitted fragment (and no literal 'using var'
//      token even inside comments — CSX parse-error guard, §13.3.1).
//   3. Emit: helper class is named 'MqExpectKafka_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised to the outcome key '__outcome::expect_evt'.
//   5. Emit: RequiredUsings contains the Confluent.Kafka namespace.
//   6. Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   7. Emit: payloadContains / topic with special characters are JSON-escaped (literal safety).
//   8. Emit: an absent key and an absent payloadContains are emitted as bare 'null' literals.
//   9. Resources: yields exactly one kafka ResourceRequirement whose Name == model.Target.
//  10. CompileReferenceAssemblies: contains the Confluent.Kafka AND JsonPath.Net assemblies.
//  11. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  12. Full compile-and-run (no docker): EnvironmentError via SECRET resolution (a missing
//      ${secret:env/…} payloadContains reference) WITHOUT a broker — the helper resolves
//      every expected value BEFORE building the consumer, so the missing secret throws
//      first; the observation is reference-only (source/path, never the value, §17).
using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqExpect.Kafka;
using Xunit;

namespace Platform.Steps.MqExpect.Kafka.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqExpectKafkaProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqExpectKafkaEmitTests
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
    }

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly MqExpectKafkaProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{' and
    /// end with '}', satisfying the §13.3.1 brace rule.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("events-bus", "orders.created",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: "hello", Json: null));
        var ctx = new StubCompileContext("expect-step");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual start: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual end: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' (including in comments) ─────────────────────────────────

    /// <summary>
    /// Neither the <see cref="CsxFragment.StatementBlock"/> nor any entry in
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain the literal token
    /// 'using var' anywhere — not even inside a comment (Roslyn script parse-error
    /// guard, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" };
        var json = new Dictionary<string, string>(StringComparer.Ordinal) { ["$.id"] = "1" };
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: "k", Headers: headers, PayloadContains: "p", Json: json));
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
    /// begins with <c>MqExpectKafka_</c> (§13.3.1 provider-prefix rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsMqExpectKafkaPrefixedClass()
    {
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqExpectKafka_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A hyphenated step id must appear in the StatementBlock only after sanitisation;
    /// the id <c>expect-evt</c> must yield the outcome key <c>__outcome::expect_evt</c>,
    /// and the raw hyphenated form must NOT appear (it would be an invalid C# identifier).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "expect-evt";
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("__outcome::expect_evt", fragment.StatementBlock, StringComparison.Ordinal);
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
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));
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
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 7. Special characters JSON-escaped ───────────────────────────────────────

    /// <summary>
    /// payloadContains and topic text containing double-quotes or backslashes must be
    /// emitted as JSON-escaped string literals so they cannot break the CSX statement block.
    /// </summary>
    [Fact]
    public void Emit_SpecialCharactersInPayloadAndTopic_AreJsonEscaped()
    {
        const string dangerousPayload = "{\"raw\":\"a\\b\\\"c\"}";
        const string dangerousTopic = "topic\"with\\specials";
        var model = MakeModel("bus", dangerousTopic,
            new KafkaMatch(Key: null, Headers: null, PayloadContains: dangerousPayload, Json: null));
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        // The raw unescaped strings must not appear verbatim — they would break the literal.
        Assert.DoesNotContain(dangerousPayload, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousTopic, fragment.StatementBlock, StringComparison.Ordinal);

        // The block must compile cleanly — verified by the compile round-trip tests.
    }

    // ── 8. Absent key / payloadContains → bare 'null' literal ────────────────────

    /// <summary>
    /// When the model has no key and no payloadContains, the StatementBlock must pass the
    /// bare <c>null</c> literal for those arguments (not a quoted empty string).
    /// </summary>
    [Fact]
    public void Emit_AbsentKeyAndPayloadContains_EmitsNullLiterals()
    {
        var json = new Dictionary<string, string>(StringComparer.Ordinal) { ["$.id"] = "1" };
        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: null, Json: json));
        var ctx = new StubCompileContext("k");

        var fragment = _provider.Emit(model, ctx);

        // The key and payloadContains arguments are bare 'null' keywords (comma-terminated).
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor yields kafka ResourceRequirement ─────────────────

    /// <summary>
    /// <see cref="IResourceContributor{TModel}.Resources"/> must yield exactly one
    /// <see cref="ResourceRequirement"/> with <c>Family="kafka"</c>, <c>Name</c> equal
    /// to <see cref="MqExpectKafkaModel.Target"/>, and a <c>null</c> Image.
    /// </summary>
    [Fact]
    public void Resources_YieldsSingleKafkaRequirementWithMatchingName()
    {
        var model = MakeModel("events-bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("kafka", req.Family, StringComparer.Ordinal);
        Assert.Equal("events-bus", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 10. ICompileReferenceContributor returns Confluent.Kafka + JsonPath.Net ──

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// contain BOTH the <c>Confluent.Kafka</c> assembly (consumer types) and the
    /// <c>JsonPath.Net</c> assembly (Json.Path.JsonPath) so the Roslyn compiler can
    /// resolve every type in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsConfluentKafkaAndJsonPathAssemblies()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("JsonPath.Net", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile round-trip: EnvironmentError when conn key absent ────────────

    /// <summary>
    /// When the connection (bootstrap) key is absent from <c>Vars</c>, the emitted
    /// helper must write <see cref="Verdict.EnvironmentError"/> to the outcome key
    /// rather than throwing or attempting to connect.  This proves the emitted CSX
    /// compiles against the real Confluent.Kafka and JsonPath.Net metadata AND that the
    /// bootstrap-missing path is reached WITHOUT any broker (no Docker required):
    /// because no <c>conn::&lt;target&gt;</c> key is staged, the helper short-circuits to
    /// EnvironmentError before ever building a consumer or subscribing.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "expect-step";
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" };
        var json = new Dictionary<string, string>(StringComparer.Ordinal) { ["$.id"] = "1" };
        var model = MakeModel("missing-bus", "orders.created",
            new KafkaMatch(Key: "k", Headers: headers, PayloadContains: "p", Json: json));
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        // Assemble exactly as CsxAssembler.Assemble would.
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        // The emitted helper references Confluent.Kafka, JsonPath.Net, System.Text.Json,
        // System.Text (Encoding), System.Globalization, System.Text.RegularExpressions
        // (via the shared helpers), AND (because the helper class is now unconditionally
        // Avro-aware) the Avro serdes assemblies, even though THIS step is plain — supply
        // each as compile-time metadata.  None is ever loaded into the collectible ALC.
        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ConsumerConfig).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>).Assembly.Location,
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
    /// When the payloadContains criterion carries a <c>${secret:env/…}</c> reference whose
    /// environment variable is unset, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> via secret resolution — NOT via a broker
    /// connection.  A bootstrap value is staged in <c>Vars</c> so the helper passes the
    /// bootstrap check and proceeds INTO its guarded region; there,
    /// <c>Secret_Helpers.ResolveTemplate</c> resolves every expected value (including
    /// payloadContains) BEFORE <c>new ConsumerBuilder(...).Build()</c>, so the missing
    /// secret throws <c>SecretResolutionException</c> before any consumer exists and before
    /// <c>Subscribe</c>/<c>Consume</c> is ever reached.  Hence no Kafka broker is contacted
    /// and the test cannot hang on a connection attempt.  The observation is REFERENCE-ONLY
    /// (§17): it carries the <c>secretError</c> marker plus the <c>env</c> source and the
    /// variable-name path, and never the (non-existent) secret value.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayloadContains_ReturnsEnvironmentError_ReferenceOnly()
    {
        // Unique env name so the variable is guaranteed absent and cannot be raced by a
        // sibling test (matches the SecretResolution* tests' convention).
        var envName = "VOUCHFX_MQEXPECT_MISSING_" + Guid.NewGuid().ToString("N");

        // Defensive: ensure the variable is absent (the GUID name should not exist).
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "expect-secret-step";
            const string target = "events-bus";

            // The payloadContains criterion carries a missing secret reference.
            var model = MakeModel(
                target,
                "orders.created",
                new KafkaMatch(
                    Key: null,
                    Headers: null,
                    PayloadContains: $"${{secret:env/{envName}}}",
                    Json: null));
            var ctx = new StubCompileContext(stepId);
            var fragment = _provider.Emit(model, ctx);

            // Assemble exactly as CsxAssembler.Assemble would (same harness as test 11).
            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

            var additionalRefs = new[]
            {
                typeof(Confluent.Kafka.ConsumerConfig).Assembly.Location,
                typeof(Json.Path.JsonPath).Assembly.Location,
                typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
                typeof(Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>).Assembly.Location,
                typeof(Avro.Schema).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Text.Encoding).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            };
            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

            // Stage a bootstrap VALUE so the helper passes the bootstrap check and
            // proceeds to secret resolution.  No broker is ever contacted: resolution
            // throws BEFORE the consumer is built (helper order, §17).
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

    // ── 13. Emit (avro): helper contains the avro consumer + RecordToJson paths ──

    /// <summary>
    /// When the model carries an avro spec, the emitted helper text contains the Avro
    /// consumer path (CachedSchemaRegistryClient / AvroDeserializer / GenericRecord) and
    /// the <c>RecordToJson</c> conversion method.  The fragment also still contains no
    /// <c>using var</c> (CSX parse-error guard, §13.3.1), and the call passes <c>true</c>
    /// for the avro flag plus the svc::&lt;sr&gt;-sr registry key.
    /// </summary>
    [Fact]
    public void Emit_AvroModel_HelperContainsAvroPathAndRecordToJson()
    {
        var model = MakeAvroModel();
        var ctx = new StubCompileContext("exp-avro");

        var fragment = _provider.Emit(model, ctx);
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("CachedSchemaRegistryClient", fullSource, StringComparison.Ordinal);
        Assert.Contains("AvroDeserializer", fullSource, StringComparison.Ordinal);
        Assert.Contains("GenericRecord", fullSource, StringComparison.Ordinal);
        Assert.Contains("RecordToJson", fullSource, StringComparison.Ordinal);
        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);

        // The avro flag is 'true' and the svc::<sr>-sr registry key is spliced in.
        Assert.Contains("svc::events-bus-sr", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("true,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the model has no avro spec, the emitted call passes <c>false</c> for the avro
    /// flag and a bare <c>null</c> for the registry key (the PLAIN path).
    /// </summary>
    [Fact]
    public void Emit_PlainModel_PassesFalseAndNullForAvroArgs()
    {
        var model = MakeModel("events-bus", "orders.created",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: "hello", Json: null));
        var ctx = new StubCompileContext("exp-plain");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("false,", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("svc::", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 14. CompileReferenceAssemblies includes the Avro serdes assemblies ───────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must include
    /// the Confluent.SchemaRegistry, Confluent.SchemaRegistry.Serdes.Avro, and Apache.Avro
    /// (Avro) assemblies (in addition to Confluent.Kafka + JsonPath.Net) so the emitted
    /// Avro CSX compiles.
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
    /// An avro expect step emits CSX that COMPILES against the real Avro serdes metadata,
    /// and — with a kafka bootstrap staged but NO <c>svc::&lt;sr&gt;-sr</c> registry URL
    /// staged — writes <see cref="Verdict.EnvironmentError"/> ("schema registry URL not
    /// found").  This proves the Avro CSX compiles AND that the missing-registry path is
    /// reachable WITHOUT a live registry or broker: every expected value is resolved and
    /// the registry-URL check runs BEFORE any CachedSchemaRegistryClient / consumer is
    /// built, so neither a registry nor a broker is ever contacted and the test cannot hang.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_Avro_AbsentRegistryUrl_ReturnsEnvironmentError()
    {
        const string stepId = "exp-avro-step";
        const string target = "events-bus";
        var model = MakeAvroModel();
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ConsumerConfig).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // Stage a bootstrap value so the helper passes the bootstrap check and reaches the
        // registry-URL check — but DO NOT stage the svc::<sr>-sr key.  The registry-URL
        // check runs BEFORE the consumer/registry are built, so no broker/registry is hit.
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
    /// An avro expect step with NO kafka bootstrap staged writes
    /// <see cref="Verdict.EnvironmentError"/> ("kafka bootstrap not found") — the bootstrap
    /// check precedes the registry-URL check, so this path is also broker-free.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_Avro_AbsentBootstrap_ReturnsEnvironmentError()
    {
        const string stepId = "exp-avro-nb";
        var model = MakeAvroModel();
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(Confluent.Kafka.ConsumerConfig).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Contains("kafka bootstrap not found", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private static MqExpectKafkaModel MakeModel(
        string target,
        string topic,
        KafkaMatch match)
        => new(
            Target: target,
            Topic: topic,
            Match: match);

    private static MqExpectKafkaModel MakeAvroModel()
        => new(
            Target: "events-bus",
            Topic: "orders.created",
            Match: new KafkaMatch(
                Key: "order-42",
                Headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" },
                PayloadContains: "widget",
                Json: new Dictionary<string, string>(StringComparer.Ordinal) { ["$.id"] = "42" }),
            Avro: new KafkaAvro(
                SchemaRegistryTarget: "events-bus",
                Subject: "orders.created-value",
                Schema: null));
}
