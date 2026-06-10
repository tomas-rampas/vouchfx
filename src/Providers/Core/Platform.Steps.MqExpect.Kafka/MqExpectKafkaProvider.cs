// Platform.Steps.MqExpect.Kafka — mq-expect.kafka step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-expect.kafka step kind.
//
// PLAIN-payload slice: the assertion criteria are evaluated against the UTF-8 message
// value (a literal string or inline JSON via JSONPath).  Avro / schema-registry
// decoding is a SEPARATE later task and is not present here.
//
// RETRY model (§7): this provider is the primary verifyMode: RETRY consumer.  The
// engine wraps a RETRY step in a polling loop, so the emitted helper performs an
// IDEMPOTENT SINGLE poll: it consumes whatever is available this attempt and writes a
// non-Pass StepOutcome (Fail) when no matching message has been seen yet.  It contains
// NO retry logic — the RetryRunner re-invokes the delegate and converts a sustained
// Fail to Inconclusive on timeout (§12.1).  The helper NEVER writes Inconclusive.
//
// Substitution + secret model (canonical M2 pattern — mirrors http.rest / mq-publish):
//   • The expected key, payloadContains, each header VALUE, and each json expected
//     VALUE are emitted as RAW template strings and passed to the helper.  Inside the
//     helper's guarded try — BEFORE any broker contact — each is resolved in a SINGLE
//     pass via Secret_Helpers.ResolveTemplate(secrets, vars, …), which handles BOTH
//     {placeholder} substitution AND ${secret:source/path} resolution over the original
//     template text.  A missing secret throws SecretResolutionException → caught →
//     Verdict.EnvironmentError for THIS step only, with NO consumer ever built (§17).
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, topic, match).
//     The type const discriminator is injected by the SchemaComposer from Kind — never
//     from the fragment text.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition; StatementBlock is
//     a C# 11 $$"""…""" block; 'using var' is illegal.
//
// Memory model (§5) — the leak-critical concern for this provider:
//   • A Confluent.Kafka consumer owns a NATIVE librdkafka handle.  The emitted helper
//     creates exactly one consumer per step and Close()es then Dispose()s it in a
//     finally so no native handle survives the collectible AssemblyLoadContext.Unload().
//     'using var' is prohibited in CSX, so disposal is explicit in a finally.
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqExpect.Kafka;

/// <summary>
/// Core provider for the <c>mq-expect.kafka</c> step kind (DSL §5).
/// Consumes from a declared Kafka dependency and asserts that a message matching
/// the declared criteria (key / headers / payload substring / JSONPath) is present.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.
/// The engine's <c>SchemaComposer</c> assembles the unified schema by injecting
/// a <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> — the fragment text never repeats that discriminator (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose emitted
/// CSX builds a <c>Confluent.Kafka</c> consumer keyed by
/// <c>VarKeys.Connection(model.Target)</c>, performs one bounded poll, evaluates each
/// consumed message against the resolved criteria, and writes a typed
/// <see cref="StepOutcome"/> into <c>Vars</c> for the runner to read after execution
/// (§13.3.1).  The expected key, payload substring, header values, and JSONPath
/// expected values are emitted as RAW template literals and resolved at runtime inside
/// the helper's guarded region via <c>Secret_Helpers.ResolveTemplate</c> (handling both
/// <c>{placeholder}</c> substitution and <c>${secret:source/path}</c> resolution, §17).
/// </para>
/// <para>
/// PLAIN-payload slice: the criteria are evaluated against the UTF-8 message value;
/// Avro / schema-registry decoding is a separate later task and is not implemented here.
/// </para>
/// <para>
/// This is the primary <c>verifyMode: RETRY</c> provider (§7): the emitted poll is
/// IDEMPOTENT — a non-match yields <see cref="Verdict.Fail"/>, and the engine-owned
/// RetryRunner re-invokes the delegate and converts a sustained Fail to
/// <see cref="Verdict.Inconclusive"/> on timeout.  The helper never writes Inconclusive.
/// </para>
/// </remarks>
[StepProvider]
public sealed class MqExpectKafkaProvider
    : IStepProvider,
      IStepBinder<MqExpectKafkaModel>,
      IStepValidator<MqExpectKafkaModel>,
      IStepCompiler<MqExpectKafkaModel>,
      IResourceContributor<MqExpectKafkaModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-expect", "kafka");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqExpectKafkaModel> ───────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>mq-expect.kafka</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it
    /// as an <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "topic", "match"],
          "properties": {
            "target": {
              "description": "Logical name of the kafka dependency to consume from, as declared under environment.dependencies.",
              "type": "string"
            },
            "topic": {
              "description": "The Kafka topic to consume the message from.",
              "type": "string"
            },
            "match": {
              "description": "The criteria a consumed message must satisfy.  At least one criterion (key, headers, payloadContains, or json) must be declared.",
              "type": "object",
              "properties": {
                "key": {
                  "description": "Optional expected message key (ordinal equality).  May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "headers": {
                  "description": "Optional map of expected header names to their expected string values.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                },
                "payloadContains": {
                  "description": "Optional substring the UTF-8 message value must contain (ordinal).  May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "json": {
                  "description": "Optional map of JSONPath expressions to their expected string values, evaluated over the message value parsed as JSON.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                }
              },
              "additionalProperties": true
            },
            "avro": {
              "description": "Optional Avro / schema-registry decoding.  When present, the consumed message is Avro-decoded to a GenericRecord, converted to a JSON string, and the existing match criteria run against that JSON.",
              "type": "object",
              "required": ["schemaRegistry"],
              "properties": {
                "schemaRegistry": {
                  "description": "Logical name of the kafka dependency whose schema registry to decode through (its schemaRegistry-enabled registry URL is staged under svc::<name>-sr).",
                  "type": "string"
                },
                "subject": {
                  "description": "Optional schema-registry subject (informational; the writer schema is fetched by the message's embedded schema id).",
                  "type": "string"
                },
                "schema": {
                  "description": "Optional inline reader schema (avsc JSON); informational for the expect side.",
                  "type": "string"
                }
              },
              "additionalProperties": true
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public MqExpectKafkaModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqExpectKafkaModel(
                Target: string.Empty,
                Topic: string.Empty,
                Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: null, Json: null),
                Avro: null);
        }

        var target = GetScalar(mapping, "target");
        var topic = GetScalar(mapping, "topic");

        var match = BindMatch(mapping);
        var avro = BindAvro(mapping);

        return new MqExpectKafkaModel(
            Target: target,
            Topic: topic,
            Match: match,
            Avro: avro);
    }

    /// <summary>
    /// Binds the optional nested <c>avro</c> mapping into a <see cref="KafkaAvro"/>.
    /// Returns <see langword="null"/> when the <c>avro</c> field is absent or not a
    /// mapping (selecting the PLAIN-payload path).  For the expect side <c>subject</c>
    /// and <c>schema</c> are optional and bound as <see langword="null"/> when absent.
    /// </summary>
    private static KafkaAvro? BindAvro(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("avro"), out var avroNode)
            || avroNode is not YamlMappingNode avroMap)
        {
            return null;
        }

        var schemaRegistry = GetScalar(avroMap, "schemaRegistry");

        string? subject = null;
        if (avroMap.Children.TryGetValue(new YamlScalarNode("subject"), out var subjectNode)
            && subjectNode is YamlScalarNode subjectScalar)
        {
            subject = subjectScalar.Value ?? string.Empty;
        }

        string? schema = null;
        if (avroMap.Children.TryGetValue(new YamlScalarNode("schema"), out var schemaNode)
            && schemaNode is YamlScalarNode schemaScalar)
        {
            schema = schemaScalar.Value ?? string.Empty;
        }

        return new KafkaAvro(
            SchemaRegistryTarget: schemaRegistry,
            Subject: subject,
            Schema: schema);
    }

    /// <summary>
    /// Parses the nested <c>match</c> mapping into a <see cref="KafkaMatch"/>.
    /// Tolerant of an absent or non-mapping <c>match</c> node — both yield an
    /// all-<see langword="null"/> match (which validation then rejects).
    /// </summary>
    private static KafkaMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new KafkaMatch(Key: null, Headers: null, PayloadContains: null, Json: null);
        }

        // 'key' is optional: present as a scalar → its value; absent → null.
        string? key = null;
        if (matchMap.Children.TryGetValue(new YamlScalarNode("key"), out var keyNode)
            && keyNode is YamlScalarNode keyScalar)
        {
            key = keyScalar.Value ?? string.Empty;
        }

        // 'payloadContains' is optional: present as a scalar → its value; absent → null.
        string? payloadContains = null;
        if (matchMap.Children.TryGetValue(new YamlScalarNode("payloadContains"), out var pcNode)
            && pcNode is YamlScalarNode pcScalar)
        {
            payloadContains = pcScalar.Value ?? string.Empty;
        }

        var headers = BindStringMap(matchMap, "headers");
        var json = BindStringMap(matchMap, "json");

        return new KafkaMatch(
            Key: key,
            Headers: headers,
            PayloadContains: payloadContains,
            Json: json);
    }

    /// <summary>
    /// Reads an optional string→string mapping field from <paramref name="parent"/>.
    /// Returns <see langword="null"/> when the field is absent or not a mapping.
    /// </summary>
    private static Dictionary<string, string>? BindStringMap(
        YamlMappingNode parent, string field)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(field), out var node)
            || node is not YamlMappingNode map)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                dict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
        }
        return dict;
    }

    // ── IStepValidator<MqExpectKafkaModel> ────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqExpectKafkaModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-expect.kafka: 'target' must not be empty.");

        // (b) topic must not be empty.
        if (string.IsNullOrWhiteSpace(model.Topic))
            errors.Add("mq-expect.kafka: 'topic' must not be empty.");

        // (c) match must declare at least one criterion.
        if (!HasAnyCriterion(model.Match))
        {
            errors.Add(
                "mq-expect.kafka: 'match' must declare at least one criterion " +
                "(key, headers, payloadContains, or json).");
        }

        // (d) dependency reconciliation: target must name a declared kafka dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-expect.kafka: 'target' '{model.Target}' is not a " +
                    "kafka dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "kafka", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-expect.kafka: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required kafka dependency.");
            }
        }

        // (e) avro block (when present): schemaRegistry must name a declared kafka
        //     dependency (same reconciliation as 'target').  subject/schema are optional
        //     for the expect side — the writer schema is fetched by the embedded id.
        if (model.Avro is { } avro)
        {
            if (string.IsNullOrWhiteSpace(avro.SchemaRegistryTarget))
            {
                errors.Add("mq-expect.kafka: 'avro.schemaRegistry' must not be empty.");
            }
            else if (!ctx.DeclaredDependencies.TryGetValue(avro.SchemaRegistryTarget, out var srType))
            {
                errors.Add(
                    $"mq-expect.kafka: 'avro.schemaRegistry' '{avro.SchemaRegistryTarget}' is not a " +
                    "kafka dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(srType, "kafka", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-expect.kafka: 'avro.schemaRegistry' '{avro.SchemaRegistryTarget}' is declared as a " +
                    $"'{srType}' dependency, not the required kafka dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="match"/> declares at least
    /// one effective criterion: a non-null key, a non-empty headers map, a non-null
    /// payloadContains, or a non-empty json map.
    /// </summary>
    private static bool HasAnyCriterion(KafkaMatch match)
        => match.Key is not null
           || match.PayloadContains is not null
           || (match.Headers is { Count: > 0 })
           || (match.Json is { Count: > 0 });

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "System.Threading",
            "System.Threading.Tasks",
            "Confluent.Kafka",
            "Platform.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MqExpectKafka_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// All types are fully-qualified so the helper compiles independently of the
    /// spliced <c>using</c> ordering.  <c>using var</c> is absent — explicit
    /// <c>.Dispose()</c> in a <c>finally</c> is used throughout.
    /// </para>
    /// <para>
    /// LEAK GATE (§5): the <c>Confluent.Kafka</c> consumer owns a native librdkafka
    /// handle.  Exactly one consumer is created per step and is <c>Close()</c>ed then
    /// <c>Dispose()</c>d in a <c>finally</c> so no native handle survives the
    /// collectible-ALC unload.
    /// </para>
    /// <para>
    /// IDEMPOTENT single poll (§7): the helper consumes whatever is available within a
    /// bounded per-attempt budget and writes <c>Pass</c> on a match or <c>Fail</c> on a
    /// non-match.  It NEVER writes <c>Inconclusive</c> — the engine-owned RetryRunner
    /// re-invokes the delegate and performs the Fail→Inconclusive-on-timeout conversion.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqExpectKafka_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Performs ONE idempotent poll over a Kafka topic via a Confluent.Kafka\n" +
        "    /// consumer and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing bootstrap = EnvironmentError (§12.1).\n" +
        "    /// A matching message = Pass; no match this attempt = Fail (the RETRY runner\n" +
        "    /// retries on non-Pass and converts a sustained Fail to Inconclusive on\n" +
        "    /// timeout — this helper NEVER writes Inconclusive, §7/§12.1).\n" +
        "    /// A missing secret or a consume/connection failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): the consumer owns a native librdkafka handle and is\n" +
        "    /// Close()ed then Dispose()d in the finally so no handle survives the\n" +
        "    /// collectible AssemblyLoadContext.Unload().  CSX disallows using-var\n" +
        "    /// declarations (§13.3.1), so disposal is explicit.\n" +
        "    /// Expected key / payloadContains / header VALUES / json expected VALUES are\n" +
        "    /// resolved INSIDE the guarded region BEFORE the consumer is built via\n" +
        "    /// Secret_Helpers.ResolveTemplate (single pass over the original template:\n" +
        "    /// both {placeholder} substitution AND ${secret:source/path} resolution, §17).\n" +
        "    /// A missing secret throws SecretResolutionException → caught → EnvironmentError\n" +
        "    /// for THIS step only, with NO broker contacted.\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task ExpectAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string topic,\n" +
        "        string? expectKeyTemplate,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValueTemplates,\n" +
        "        bool avro,\n" +
        "        string? avroRegistrySvcKey)\n" +
        "    {\n" +
        "        // AVRO path is selected when 'avro' is true.  Otherwise the PLAIN string\n" +
        "        // consumer path runs, byte-identical to the committed slice.\n" +
        "        if (avro)\n" +
        "        {\n" +
        "            await ExpectAvroAsync(vars, secrets, outcomeKey, connKey, topic,\n" +
        "                expectKeyTemplate, payloadContainsTemplate, headerNames,\n" +
        "                headerValueTemplates, jsonPaths, jsonValueTemplates, avroRegistrySvcKey)\n" +
        "                .ConfigureAwait(false);\n" +
        "            return;\n" +
        "        }\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        // Read the bootstrap-servers string staged by the orchestrator\n" +
        "        // (VarKeys.Connection pattern).  A null or empty string means the\n" +
        "        // dependency was not discovered → EnvironmentError (§12.1).\n" +
        "        var bootstrap = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(bootstrap))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"kafka bootstrap not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        Confluent.Kafka.IConsumer<string, string>? consumer = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every expected author-text field INSIDE the guarded region and\n" +
        "            // BEFORE creating the consumer (§17).  Each is resolved in a SINGLE pass via\n" +
        "            // ResolveTemplate (both {placeholder} substitution and ${secret:source/path}\n" +
        "            // resolution over the original template).  This ordering means a missing\n" +
        "            // secret throws SecretResolutionException BEFORE any broker contact, so the\n" +
        "            // missing-secret path is reachable with no broker present.\n" +
        "            var expectKey = expectKeyTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, expectKeyTemplate);\n" +
        "            var payloadContains = payloadContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var headerValues = new string[headerValueTemplates.Length];\n" +
        "            for (int hi = 0; hi < headerValueTemplates.Length; hi++)\n" +
        "            {\n" +
        "                // Header NAMES are used verbatim; only VALUES are resolved (§17).\n" +
        "                headerValues[hi] = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);\n" +
        "            }\n" +
        "            var jsonValues = new string[jsonValueTemplates.Length];\n" +
        "            for (int ji = 0; ji < jsonValueTemplates.Length; ji++)\n" +
        "            {\n" +
        "                // JSONPath expressions are used verbatim; only expected VALUES are resolved (§17).\n" +
        "                jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);\n" +
        "            }\n" +
        "            // A fresh GroupId per attempt + AutoOffsetReset.Earliest makes the poll\n" +
        "            // idempotent: each attempt scans from the start of the available log.\n" +
        "            // EnablePartitionEof lets us break as soon as the available log is drained.\n" +
        "            var config = new Confluent.Kafka.ConsumerConfig\n" +
        "            {\n" +
        "                BootstrapServers = bootstrap,\n" +
        "                GroupId = \"vouchfx-expect-\" + System.Guid.NewGuid().ToString(\"n\"),\n" +
        "                AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,\n" +
        "                EnableAutoCommit = false,\n" +
        "                EnablePartitionEof = true,\n" +
        "            };\n" +
        "            consumer = new Confluent.Kafka.ConsumerBuilder<string, string>(config).Build();\n" +
        "            consumer.Subscribe(topic);\n" +
        "            int scanned = 0;\n" +
        "            bool matched = false;\n" +
        "            // Bounded per-attempt poll budget.  The RETRY runner owns the overall\n" +
        "            // timeout/backoff; this is just one attempt's drain window.\n" +
        "            var deadline = System.DateTime.UtcNow.AddSeconds(1);\n" +
        "            while (System.DateTime.UtcNow < deadline && !matched)\n" +
        "            {\n" +
        "                var cr = consumer.Consume(System.TimeSpan.FromMilliseconds(200));\n" +
        "                if (cr is null)\n" +
        "                    continue;\n" +
        "                if (cr.IsPartitionEOF)\n" +
        "                    break;  // drained what is available this attempt.\n" +
        "                scanned++;\n" +
        "                if (MatchesMessage(cr, expectKey, payloadContains, headerNames, headerValues, jsonPaths, jsonValues))\n" +
        "                    matched = true;\n" +
        "            }\n" +
        "            if (matched)\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "                observation = \"{\\\"matched\\\":true,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                // No match THIS attempt = Fail.  The RETRY runner retries on non-Pass\n" +
        "                // and converts a sustained Fail to Inconclusive on timeout — never here.\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"matched\\\":false,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Platform.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing / unknown secret = EnvironmentError (§12.1): a configuration\n" +
        "            // problem in the run environment, NOT a product defect and NOT a\n" +
        "            // scenario-level abort.  REFERENCE-ONLY observation (§17): a fixed\n" +
        "            // message plus the discrete source/path coordinates — never the value.\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (Confluent.Kafka.ConsumeException ex)\n" +
        "        {\n" +
        "            // Consume failure: broker unreachable, topic authorization, etc. = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection / configuration failure = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): close then release the native librdkafka handle within\n" +
        "            // this step, before the collectible ALC unloads.  Close() commits final\n" +
        "            // offsets and leaves the group cleanly; a Close() failure must not mask\n" +
        "            // the outcome, so it is swallowed before the unconditional Dispose().\n" +
        "            if (consumer is not null)\n" +
        "            {\n" +
        "                try { consumer.Close(); } catch { }\n" +
        "                consumer.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// PLAIN-path adapter: extracts the payload / key / headers from a string-valued\n" +
        "    /// ConsumeResult and delegates to the shared MatchesDecoded core.  Unchanged\n" +
        "    /// behaviour from the committed slice.\n" +
        "    /// </summary>\n" +
        "    private static bool MatchesMessage(\n" +
        "        Confluent.Kafka.ConsumeResult<string, string> cr,\n" +
        "        string? expectKey,\n" +
        "        string? payloadContains,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValues,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValues)\n" +
        "    {\n" +
        "        return MatchesDecoded(cr.Message.Value ?? string.Empty, cr.Message.Key,\n" +
        "            cr.Message.Headers, expectKey, payloadContains, headerNames, headerValues,\n" +
        "            jsonPaths, jsonValues);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Evaluates one decoded message against the (already-resolved) criteria.\n" +
        "    /// All criteria are conjunctive (logical AND); an absent criterion imposes\n" +
        "    /// no constraint.  A non-JSON payload or a path that selects nothing fails the\n" +
        "    /// JSON criteria WITHOUT raising — JSON parse / JSONPath failures are guarded\n" +
        "    /// so a malformed payload simply yields no match.  Shared by the PLAIN string\n" +
        "    /// path (payload = message value) and the AVRO path (payload = GenericRecord\n" +
        "    /// converted to JSON); key and headers come from the ConsumeResult either way.\n" +
        "    /// </summary>\n" +
        "    private static bool MatchesDecoded(\n" +
        "        string payload,\n" +
        "        string? messageKey,\n" +
        "        Confluent.Kafka.Headers? messageHeaders,\n" +
        "        string? expectKey,\n" +
        "        string? payloadContains,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValues,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValues)\n" +
        "    {\n" +
        "        // (1) key equality (ordinal) when an expected key is set.\n" +
        "        if (expectKey is not null && !string.Equals(messageKey ?? string.Empty, expectKey, System.StringComparison.Ordinal))\n" +
        "            return false;\n" +
        "        // (2) payload substring (ordinal) when set.\n" +
        "        if (payloadContains is not null && !payload.Contains(payloadContains, System.StringComparison.Ordinal))\n" +
        "            return false;\n" +
        "        // (3) each expected header must be present with a matching UTF-8 value.\n" +
        "        for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "        {\n" +
        "            var headerName = headerNames[hi];\n" +
        "            var expectedValue = headerValues[hi];\n" +
        "            string? actualValue = null;\n" +
        "            if (messageHeaders is not null)\n" +
        "            {\n" +
        "                // TryGetLastBytes returns the latest value when a header is repeated.\n" +
        "                if (messageHeaders.TryGetLastBytes(headerName, out var bytes) && bytes is not null)\n" +
        "                    actualValue = System.Text.Encoding.UTF8.GetString(bytes);\n" +
        "            }\n" +
        "            if (actualValue is null || !string.Equals(actualValue, expectedValue, System.StringComparison.Ordinal))\n" +
        "                return false;\n" +
        "        }\n" +
        "        // (4) each JSONPath must select a node whose stringified value equals the\n" +
        "        // expected value.  A non-JSON payload or a parse/evaluation failure yields\n" +
        "        // no match (guarded — never throws out of MatchesMessage).\n" +
        "        if (jsonPaths.Length > 0)\n" +
        "        {\n" +
        "            System.Text.Json.Nodes.JsonNode? node;\n" +
        "            try\n" +
        "            {\n" +
        "                node = System.Text.Json.Nodes.JsonNode.Parse(payload);\n" +
        "            }\n" +
        "            catch (System.Exception)\n" +
        "            {\n" +
        "                node = null;\n" +
        "            }\n" +
        "            if (node is null)\n" +
        "                return false;\n" +
        "            for (int ji = 0; ji < jsonPaths.Length; ji++)\n" +
        "            {\n" +
        "                var jsonPath = jsonPaths[ji];\n" +
        "                var expectedValue = jsonValues[ji];\n" +
        "                string? actualValue = null;\n" +
        "                try\n" +
        "                {\n" +
        "                    var pathResult = Json.Path.JsonPath.Parse(jsonPath).Evaluate(node);\n" +
        "                    var matches = pathResult.Matches;\n" +
        "                    if (matches != null && matches.Count > 0 && matches[0].Value is not null)\n" +
        "                    {\n" +
        "                        var firstMatch = matches[0].Value;\n" +
        "                        if (firstMatch is System.Text.Json.Nodes.JsonValue jv)\n" +
        "                        {\n" +
        "                            // Scalar value: compare the raw string/number/bool without surrounding quotes.\n" +
        "                            var rawElem = jv.GetValue<System.Text.Json.JsonElement>();\n" +
        "                            actualValue = rawElem.ValueKind == System.Text.Json.JsonValueKind.String\n" +
        "                                ? rawElem.GetString() ?? string.Empty\n" +
        "                                : rawElem.GetRawText();\n" +
        "                        }\n" +
        "                        else\n" +
        "                        {\n" +
        "                            // Object or array: compact JSON.\n" +
        "                            actualValue = firstMatch.ToJsonString();\n" +
        "                        }\n" +
        "                    }\n" +
        "                }\n" +
        "                catch (System.Exception)\n" +
        "                {\n" +
        "                    actualValue = null;\n" +
        "                }\n" +
        "                if (actualValue is null || !string.Equals(actualValue, expectedValue, System.StringComparison.Ordinal))\n" +
        "                    return false;\n" +
        "            }\n" +
        "        }\n" +
        "        return true;\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// AVRO expect path: performs ONE idempotent poll over the topic using an Avro\n" +
        "    /// GenericRecord deserializer (writer schema fetched from the registry by the\n" +
        "    /// message's embedded schema id), converts each decoded record to a JSON string,\n" +
        "    /// and runs the EXISTING match criteria (key/headers/payloadContains/json) against\n" +
        "    /// that JSON.  matched = Pass; no match this attempt = Fail (RetryRunner keeps\n" +
        "    /// polling); broker/registry/deserialize failure = EnvironmentError.  Never Inconclusive.\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): the consumer (native librdkafka handle) is Close()ed + Dispose()d\n" +
        "    /// AND the registry client Dispose()d in the finally.  using-var is illegal in CSX.\n" +
        "    /// Expected values are resolved BEFORE the consumer/registry are built, so a missing\n" +
        "    /// secret OR a missing registry URL is reachable with no broker contacted.\n" +
        "    /// </remarks>\n" +
        "    private static async System.Threading.Tasks.Task ExpectAvroAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string topic,\n" +
        "        string? expectKeyTemplate,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValueTemplates,\n" +
        "        string? avroRegistrySvcKey)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var bootstrap = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(bootstrap))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"kafka bootstrap not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        // Schema-registry URL staged under svc::<sr>-sr; checked BEFORE any client build\n" +
        "        // so the missing-URL path needs no live registry or broker.\n" +
        "        var registryUrl = (avroRegistrySvcKey is not null\n" +
        "                && vars.TryGetValue(avroRegistrySvcKey, out var rv) && rv is string rs) ? rs : null;\n" +
        "        if (string.IsNullOrEmpty(registryUrl))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"schema registry URL not found for key '\" + (avroRegistrySvcKey ?? string.Empty) + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        Confluent.SchemaRegistry.CachedSchemaRegistryClient? registry = null;\n" +
        "        Confluent.Kafka.IConsumer<string, Avro.Generic.GenericRecord>? consumer = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every expected value BEFORE building the consumer/registry (§17).\n" +
        "            var expectKey = expectKeyTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, expectKeyTemplate);\n" +
        "            var payloadContains = payloadContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var headerValues = new string[headerValueTemplates.Length];\n" +
        "            for (int hi = 0; hi < headerValueTemplates.Length; hi++)\n" +
        "                headerValues[hi] = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);\n" +
        "            var jsonValues = new string[jsonValueTemplates.Length];\n" +
        "            for (int ji = 0; ji < jsonValueTemplates.Length; ji++)\n" +
        "                jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);\n" +
        "            registry = new Confluent.SchemaRegistry.CachedSchemaRegistryClient(\n" +
        "                new Confluent.SchemaRegistry.SchemaRegistryConfig { Url = registryUrl });\n" +
        "            // AvroDeserializer<T> has a single-arg ctor; AsSyncOverAsync() adapts the\n" +
        "            // async deserializer to the IDeserializer<T> ConsumerBuilder expects.\n" +
        "            var deserializer = new Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>(registry);\n" +
        "            var config = new Confluent.Kafka.ConsumerConfig\n" +
        "            {\n" +
        "                BootstrapServers = bootstrap,\n" +
        "                GroupId = \"vouchfx-expect-\" + System.Guid.NewGuid().ToString(\"n\"),\n" +
        "                AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,\n" +
        "                EnableAutoCommit = false,\n" +
        "                EnablePartitionEof = true,\n" +
        "            };\n" +
        "            consumer = new Confluent.Kafka.ConsumerBuilder<string, Avro.Generic.GenericRecord>(config)\n" +
        "                .SetValueDeserializer(Confluent.Kafka.SyncOverAsync.SyncOverAsyncDeserializerExtensionMethods.AsSyncOverAsync(deserializer))\n" +
        "                .Build();\n" +
        "            consumer.Subscribe(topic);\n" +
        "            int scanned = 0;\n" +
        "            bool matched = false;\n" +
        "            var deadline = System.DateTime.UtcNow.AddSeconds(1);\n" +
        "            while (System.DateTime.UtcNow < deadline && !matched)\n" +
        "            {\n" +
        "                var cr = consumer.Consume(System.TimeSpan.FromMilliseconds(200));\n" +
        "                if (cr is null)\n" +
        "                    continue;\n" +
        "                if (cr.IsPartitionEOF)\n" +
        "                    break;\n" +
        "                scanned++;\n" +
        "                var json = RecordToJson(cr.Message.Value);\n" +
        "                if (MatchesDecoded(json, cr.Message.Key, cr.Message.Headers, expectKey,\n" +
        "                        payloadContains, headerNames, headerValues, jsonPaths, jsonValues))\n" +
        "                    matched = true;\n" +
        "            }\n" +
        "            if (matched)\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "                observation = \"{\\\"matched\\\":true,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                verdict = Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"matched\\\":false,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Platform.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Broker / registry / deserialize / any other failure = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            if (consumer is not null)\n" +
        "            {\n" +
        "                try { consumer.Close(); } catch { }\n" +
        "                consumer.Dispose();\n" +
        "            }\n" +
        "            if (registry is not null)\n" +
        "                registry.Dispose();\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Converts a decoded Avro GenericRecord to a JSON string by iterating its schema\n" +
        "    /// fields into a JsonObject (primitives only for MVP: string/int/long/float/double/\n" +
        "    /// boolean/null).  Non-primitive field values are stringified via ToString().\n" +
        "    /// A null record yields the empty JSON object.\n" +
        "    /// </summary>\n" +
        "    private static string RecordToJson(Avro.Generic.GenericRecord record)\n" +
        "    {\n" +
        "        var obj = new System.Text.Json.Nodes.JsonObject();\n" +
        "        if (record is null)\n" +
        "            return obj.ToJsonString();\n" +
        "        var fields = record.Schema.Fields;\n" +
        "        for (int i = 0; i < fields.Count; i++)\n" +
        "        {\n" +
        "            var name = fields[i].Name;\n" +
        "            object? val;\n" +
        "            if (!record.TryGetValue(name, out val))\n" +
        "                val = null;\n" +
        "            obj[name] = ToJsonNode(val);\n" +
        "        }\n" +
        "        return obj.ToJsonString();\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Maps a decoded Avro primitive value to a JsonNode.  Numbers and booleans are\n" +
        "    /// emitted as JSON primitives so JSONPath value comparisons match the raw form;\n" +
        "    /// anything else is stringified.\n" +
        "    /// </summary>\n" +
        "    private static System.Text.Json.Nodes.JsonNode? ToJsonNode(object? val)\n" +
        "    {\n" +
        "        switch (val)\n" +
        "        {\n" +
        "            case null:\n" +
        "                return null;\n" +
        "            case string sv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(sv);\n" +
        "            case bool bv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(bv);\n" +
        "            case int iv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(iv);\n" +
        "            case long lv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(lv);\n" +
        "            case float fv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(fv);\n" +
        "            case double dv:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(dv);\n" +
        "            default:\n" +
        "                return System.Text.Json.Nodes.JsonValue.Create(\n" +
        "                    System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);\n" +
        "        }\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqExpectKafkaModel> ─────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution builds a <c>Confluent.Kafka</c> consumer keyed
    /// by <c>VarKeys.Connection(model.Target)</c>, performs one idempotent bounded poll
    /// over <c>model.Topic</c>, evaluates each consumed message against the resolved
    /// criteria, and writes a typed <see cref="StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read after the
    /// script returns.
    /// </para>
    /// <para>
    /// Substitution + secret model (canonical M2 pattern): the expected key, payload
    /// substring, each header VALUE, and each json expected VALUE are emitted as RAW
    /// template literals (JSON-escaped C# string literals).  They are NOT pre-resolved
    /// at the call site — the helper resolves each in a single pass via
    /// <c>Secret_Helpers.ResolveTemplate</c> (both <c>{placeholder}</c> substitution and
    /// <c>${secret:source/path}</c> resolution) inside its guarded region, BEFORE the
    /// consumer is built, so a missing secret maps to a step-scoped
    /// <see cref="Verdict.EnvironmentError"/> with no broker contact and no secret value
    /// is ever baked into the emitted IL (§17).
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full
    ///   <c>static class MqExpectKafka_Helpers</c> definition; byte-identical across
    ///   instances; <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> appended.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block;
    ///   no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped C#
    ///   string literals.  An absent key / payloadContains is emitted as the <c>null</c>
    ///   literal.</item>
    ///   <item>The step id is sanitised via <c>CsxFragment.SanitiseId</c> before splicing.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(MqExpectKafkaModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

        // Expand the header criteria into parallel name/value-template arrays.  Values
        // are emitted as RAW templates; the helper substitutes then secret-resolves each
        // at runtime, inside the guarded region.  No secret value is ever baked into the
        // emitted IL — only the reference token text is.
        string[] headerNames;
        string[] headerValueTemplates;
        if (match.Headers is { Count: > 0 } headers)
        {
            headerNames = headers.Keys.ToArray();
            headerValueTemplates = headers.Values.ToArray();
        }
        else
        {
            headerNames = Array.Empty<string>();
            headerValueTemplates = Array.Empty<string>();
        }

        // Expand the json criteria into parallel path/value-template arrays.
        string[] jsonPaths;
        string[] jsonValueTemplates;
        if (match.Json is { Count: > 0 } json)
        {
            jsonPaths = json.Keys.ToArray();
            jsonValueTemplates = json.Values.ToArray();
        }
        else
        {
            jsonPaths = Array.Empty<string>();
            jsonValueTemplates = Array.Empty<string>();
        }

        var headerNamesLiteral = BuildStringArrayLiteral(headerNames);
        var headerValueTemplatesLiteral = BuildStringArrayLiteral(headerValueTemplates);
        var jsonPathsLiteral = BuildStringArrayLiteral(jsonPaths);
        var jsonValueTemplatesLiteral = BuildStringArrayLiteral(jsonValueTemplates);

        // Topic is emitted as a JSON-escaped C# string literal (a plain field — no
        // {placeholder}/${secret} resolution applies to the topic in this slice).
        var topicLiteral = JsonSerializer.Serialize(model.Topic);

        // The expected key / payloadContains are optional: emit the JSON-escaped template
        // literal when present, or the bare 'null' literal when absent.  Any
        // {placeholder} or ${secret:…} token inside survives as LITERAL TEXT here (not an
        // emit-time interpolation hole) and is processed at runtime.  CRITICAL: inside a
        // $$"""…""" block, {{expr}} is the interpolation hole; a lone {placeholder} or
        // ${secret:…} passes through verbatim.
        var keyTemplateLiteral = match.Key is null
            ? "null"
            : JsonSerializer.Serialize(match.Key);
        var payloadContainsLiteral = match.PayloadContains is null
            ? "null"
            : JsonSerializer.Serialize(match.PayloadContains);

        // Avro args: 'false' + bare 'null' literals when no avro block is declared
        // (selecting the PLAIN path), or 'true' + the resolved svc-key literal when present.
        // The registry URL is staged under svc::<sr>-sr; read via VarKeys.Service at runtime.
        var avroFlagLiteral = model.Avro is null ? "false" : "true";
        var avroRegistrySvcKeyLiteral = model.Avro is { } avro
            ? JsonSerializer.Serialize(VarKeys.Service(avro.SchemaRegistryTarget + "-sr"))
            : "null";

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        // 'Secrets' is the ScriptGlobalVariables.Secrets instance property.
        var block = $$"""
            {
                await MqExpectKafka_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{topicLiteral}},
                    {{keyTemplateLiteral}},
                    {{payloadContainsLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    {{jsonPathsLiteral}},
                    {{jsonValueTemplatesLiteral}},
                    {{avroFlagLiteral}},
                    {{avroRegistrySvcKeyLiteral}});
            }
            """;

        // Build the helpers list: MqExpectKafka_Helpers + Substitute_Helpers +
        // Secret_Helpers.  Both shared helper sources are byte-identical across
        // providers — deduplication is handled by CsxAssembler.
        var helpers = new List<string>(s_helpers)
        {
            SubstituteHelper.Source,
            SecretHelper.Source,
        };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<MqExpectKafkaModel> ──────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="ResourceRequirement"/> with <c>Family="kafka"</c>
    /// and <c>Name=model.Target</c>.  The engine maps the <c>Name</c> to the Kafka
    /// broker resource so the bootstrap-servers connection string is resolved from
    /// the discovered Aspire resource (§4).
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(MqExpectKafkaModel model)
    {
        yield return new ResourceRequirement(
            Family: "kafka",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>Confluent.Kafka</c> assembly (so the Roslyn compiler can resolve
    /// <c>ConsumerConfig</c>, <c>ConsumerBuilder&lt;,&gt;</c>, <c>ConsumeResult&lt;,&gt;</c>,
    /// and related types), the <c>JsonPath.Net</c> assembly (<c>Json.Path.JsonPath</c>),
    /// and the <c>Confluent.SchemaRegistry</c>, <c>Confluent.SchemaRegistry.Serdes.Avro</c>,
    /// and <c>Apache.Avro</c> assemblies so the emitted CSX <em>always</em> compiles whether
    /// or not the Avro branch is taken (the helper references all of them unconditionally).
    /// Every assembly is already loaded in the Default ALC (the provider project references
    /// them directly) and must never be loaded into the collectible ALC (§5 memory-model).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Confluent.Kafka.ConsumerConfig).Assembly;
            // JsonPath.Net: Json.Path.JsonPath is in the Json.Path namespace.
            yield return typeof(Json.Path.JsonPath).Assembly;
            // Confluent.SchemaRegistry — CachedSchemaRegistryClient / SchemaRegistryConfig.
            yield return typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly;
            // Confluent.SchemaRegistry.Serdes.Avro — AvroDeserializer<T>.
            yield return typeof(Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>).Assembly;
            // Apache.Avro — Avro.Generic.GenericRecord.
            yield return typeof(Avro.Schema).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a C# array-initialiser literal from a string array, with each element
    /// individually JSON-serialised to escape embedded quotes, backslashes, and
    /// control characters before splicing into the CSX StatementBlock.
    /// </summary>
    /// <remarks>
    /// Example: <c>["a", "b\"c"]</c> → <c>new string[] { "a", "b\"c" }</c> where the
    /// inner quotes are escaped by <see cref="JsonSerializer.Serialize"/>.
    /// </remarks>
    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
        {
            return "new string[] { }";
        }

        var sb = new System.Text.StringBuilder("new string[] { ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(JsonSerializer.Serialize(values[i]));
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
