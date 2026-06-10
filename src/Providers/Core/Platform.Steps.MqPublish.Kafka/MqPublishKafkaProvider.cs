// Platform.Steps.MqPublish.Kafka — mq-publish.kafka step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-publish.kafka step kind.
//
// PLAIN-payload slice: the payload is a UTF-8 string (literal or inline JSON).
// Avro / schema-registry encoding is a SEPARATE later task and is not present here.
//
// Substitution + secret model (canonical M2 pattern — mirrors http.rest, S05-B-02):
//   • The topic, key, payload, and each header VALUE are emitted as RAW template
//     strings and passed to the helper.  Inside the helper's guarded try, each is
//     resolved in a SINGLE pass via Secret_Helpers.ResolveTemplate(secrets, vars, …),
//     which handles BOTH {placeholder} substitution AND ${secret:source/path}
//     resolution over the original template text.  A missing secret throws
//     SecretResolutionException → caught → Verdict.EnvironmentError for THIS step only
//     (step-scoped blast radius, never baked into IL — §17).
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, topic, key,
//     payload, headers).  The type const discriminator is injected by the
//     SchemaComposer from Kind — never from the fragment text.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition; StatementBlock
//     is a C# 11 $$"""…""" block; 'using var' is illegal.
//
// Memory model (§5) — the leak-critical concern for this provider:
//   • A Confluent.Kafka producer owns a NATIVE librdkafka handle.  The emitted helper
//     creates exactly one producer per step and Flushes + Disposes it in a finally so
//     no native handle survives the collectible AssemblyLoadContext.Unload().
//     'using var' is prohibited in CSX, so disposal is explicit in a finally.
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqPublish.Kafka;

/// <summary>
/// Core provider for the <c>mq-publish.kafka</c> step kind (DSL §5).
/// Publishes a single message (string / JSON payload) to a declared Kafka
/// dependency and reports the resulting partition/offset.
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
/// CSX builds a <c>Confluent.Kafka</c> producer keyed by
/// <c>VarKeys.Connection(model.Target)</c>, publishes one message, and writes a
/// typed <see cref="StepOutcome"/> into <c>Vars</c> for the runner to read after
/// execution (§13.3.1).  The topic, key, payload, and header values are emitted as
/// RAW template literals and resolved at runtime inside the helper's guarded region
/// via <c>Secret_Helpers.ResolveTemplate</c> (handling both <c>{placeholder}</c>
/// substitution and <c>${secret:source/path}</c> resolution, §17).
/// </para>
/// <para>
/// PLAIN-payload slice: the payload is a UTF-8 string; Avro / schema-registry
/// encoding is a separate later task and is not implemented here.
/// </para>
/// </remarks>
[StepProvider]
public sealed class MqPublishKafkaProvider
    : IStepProvider,
      IStepBinder<MqPublishKafkaModel>,
      IStepValidator<MqPublishKafkaModel>,
      IStepCompiler<MqPublishKafkaModel>,
      IResourceContributor<MqPublishKafkaModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-publish", "kafka");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqPublishKafkaModel> ──────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>mq-publish.kafka</c>
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
          "required": ["target", "topic", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of the kafka dependency to publish to, as declared under environment.dependencies.",
              "type": "string"
            },
            "topic": {
              "description": "The Kafka topic to publish the message to.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "key": {
              "description": "Optional message key.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "payload": {
              "description": "The message payload sent as the Kafka message value.  A UTF-8 string (literal or inline JSON).  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "headers": {
              "description": "Optional map of message header names to their string values.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public MqPublishKafkaModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqPublishKafkaModel(
                Target: string.Empty,
                Topic: string.Empty,
                Key: null,
                Payload: string.Empty,
                Headers: null);
        }

        var target = GetScalar(mapping, "target");
        var topic = GetScalar(mapping, "topic");
        var payload = GetScalar(mapping, "payload");

        // 'key' is optional: present as a scalar → its value; absent → null.
        string? key = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("key"), out var keyNode)
            && keyNode is YamlScalarNode keyScalar)
        {
            key = keyScalar.Value ?? string.Empty;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("headers"), out var headersNode)
            && headersNode is YamlMappingNode headersMap)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in headersMap.Children)
            {
                if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                    dict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
            }
            headers = dict;
        }

        return new MqPublishKafkaModel(
            Target: target,
            Topic: topic,
            Key: key,
            Payload: payload,
            Headers: headers);
    }

    // ── IStepValidator<MqPublishKafkaModel> ───────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqPublishKafkaModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-publish.kafka: 'target' must not be empty.");

        // (b) topic must not be empty.
        if (string.IsNullOrWhiteSpace(model.Topic))
            errors.Add("mq-publish.kafka: 'topic' must not be empty.");

        // (c) payload must not be empty.
        if (string.IsNullOrWhiteSpace(model.Payload))
            errors.Add("mq-publish.kafka: 'payload' must not be empty.");

        // (d) dependency reconciliation: target must name a declared kafka dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-publish.kafka: 'target' '{model.Target}' is not a " +
                    "kafka dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "kafka", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-publish.kafka: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required kafka dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

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
            "System.Threading.Tasks",
            "Confluent.Kafka",
            "Platform.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MqPublishKafka_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// All types are fully-qualified so the helper compiles independently of the
    /// spliced <c>using</c> ordering.  <c>using var</c> is absent — explicit
    /// <c>.Dispose()</c> in a <c>finally</c> is used throughout.
    /// </para>
    /// <para>
    /// LEAK GATE (§5): the <c>Confluent.Kafka</c> producer owns a native librdkafka
    /// handle.  Exactly one producer is created per step and is
    /// <c>Flush(TimeSpan.FromSeconds(10))</c>ed then <c>Dispose()</c>d in a
    /// <c>finally</c> so no native handle survives the collectible-ALC unload.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqPublishKafka_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Publishes one message to a Kafka topic via a Confluent.Kafka producer\n" +
        "    /// and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing bootstrap = EnvironmentError (§12.1).\n" +
        "    /// Successful publish = Pass (observation carries topic/partition/offset).\n" +
        "    /// A produce failure or a missing secret = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): the producer owns a native librdkafka handle and is\n" +
        "    /// Flush()ed then Dispose()d in the finally so no handle survives the\n" +
        "    /// collectible AssemblyLoadContext.Unload().  CSX disallows using-var\n" +
        "    /// declarations (§13.3.1), so disposal is explicit.\n" +
        "    /// Topic / key / payload / header VALUES are resolved INSIDE the guarded\n" +
        "    /// region via Secret_Helpers.ResolveTemplate (single pass over the original\n" +
        "    /// template: both {placeholder} substitution AND ${secret:source/path}\n" +
        "    /// resolution, §17).  A missing secret throws SecretResolutionException →\n" +
        "    /// caught → EnvironmentError for THIS step only.\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task PublishAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string topicTemplate,\n" +
        "        string? keyTemplate,\n" +
        "        string payloadTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates)\n" +
        "    {\n" +
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
        "        Confluent.Kafka.IProducer<string, string>? producer = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every author-text field INSIDE the guarded region (§17), each in\n" +
        "            // a SINGLE pass via ResolveTemplate (both {placeholder} substitution and\n" +
        "            // ${secret:source/path} resolution over the original template).  A missing\n" +
        "            // secret throws SecretResolutionException → caught below → EnvironmentError.\n" +
        "            var topic = Secret_Helpers.ResolveTemplate(secrets, vars, topicTemplate);\n" +
        "            var key = keyTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, keyTemplate);\n" +
        "            var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);\n" +
        "            var config = new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrap };\n" +
        "            producer = new Confluent.Kafka.ProducerBuilder<string, string>(config).Build();\n" +
        "            var msg = new Confluent.Kafka.Message<string, string>\n" +
        "            {\n" +
        "                Key = key ?? string.Empty,\n" +
        "                Value = payload,\n" +
        "            };\n" +
        "            if (headerNames.Length > 0)\n" +
        "            {\n" +
        "                var msgHeaders = new Confluent.Kafka.Headers();\n" +
        "                for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "                {\n" +
        "                    // Header NAMES are used verbatim; only VALUES are resolved (§17).\n" +
        "                    var headerValue = Secret_Helpers.ResolveTemplate(\n" +
        "                        secrets, vars, headerValueTemplates[hi]);\n" +
        "                    msgHeaders.Add(headerNames[hi], System.Text.Encoding.UTF8.GetBytes(headerValue));\n" +
        "                }\n" +
        "                msg.Headers = msgHeaders;\n" +
        "            }\n" +
        "            var dr = await producer.ProduceAsync(topic, msg).ConfigureAwait(false);\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"topic\\\":\" + System.Text.Json.JsonSerializer.Serialize(dr.Topic) +\n" +
        "                \",\\\"partition\\\":\" + dr.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +\n" +
        "                \",\\\"offset\\\":\" + dr.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
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
        "        catch (Confluent.Kafka.ProduceException<string, string> ex)\n" +
        "        {\n" +
        "            // Produce failure: broker unreachable, topic authorization, etc. = EnvironmentError (§12.1).\n" +
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
        "            // LEAK GATE (§5): flush any buffered messages then release the native\n" +
        "            // librdkafka handle within this step, before the collectible ALC unloads.\n" +
        "            if (producer is not null)\n" +
        "            {\n" +
        "                try { producer.Flush(System.TimeSpan.FromSeconds(10)); } catch { }\n" +
        "                producer.Dispose();  // explicit Dispose() in finally (§13.3.1).\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqPublishKafkaModel> ────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution builds a <c>Confluent.Kafka</c> producer
    /// keyed by <c>VarKeys.Connection(model.Target)</c>, publishes one message to
    /// <c>model.Topic</c> (with the optional key and headers), and writes a typed
    /// <see cref="StepOutcome"/> into <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c>
    /// for the runner to read after the script returns.
    /// </para>
    /// <para>
    /// Substitution + secret model (canonical M2 pattern): the topic, key, payload,
    /// and each header VALUE are emitted as RAW template literals (JSON-escaped C#
    /// string literals).  They are NOT pre-resolved at the call site — the helper
    /// resolves each in a single pass via <c>Secret_Helpers.ResolveTemplate</c>
    /// (both <c>{placeholder}</c> substitution and <c>${secret:source/path}</c>
    /// resolution) inside its guarded region, so a missing secret maps to a
    /// step-scoped <see cref="Verdict.EnvironmentError"/> and no secret value is ever
    /// baked into the emitted IL (§17).
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full
    ///   <c>static class MqPublishKafka_Helpers</c> definition; byte-identical across
    ///   instances; <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> appended.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block;
    ///   no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped C#
    ///   string literals.  The key is emitted as the <c>null</c> literal when absent.</item>
    ///   <item>The step id is sanitised via <c>CsxFragment.SanitiseId</c> before splicing.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(MqPublishKafkaModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Expand the headers map into parallel name/value-template arrays.  Values are
        // emitted as RAW templates; the helper substitutes then secret-resolves each
        // at runtime, inside the guarded region.  No secret value is ever baked into
        // the emitted IL — only the reference token text is.
        string[] headerNames;
        string[] headerValueTemplates;
        if (model.Headers is { Count: > 0 } headers)
        {
            headerNames = headers.Keys.ToArray();
            headerValueTemplates = headers.Values.ToArray();
        }
        else
        {
            headerNames = Array.Empty<string>();
            headerValueTemplates = Array.Empty<string>();
        }

        var headerNamesLiteral = BuildStringArrayLiteral(headerNames);
        var headerValueTemplatesLiteral = BuildStringArrayLiteral(headerValueTemplates);

        // Topic / payload are emitted as RAW template literals (JSON-escaped C# string
        // literals).  Any {placeholder} or ${secret:…} token inside survives as LITERAL
        // TEXT here (not an emit-time interpolation hole) and is processed at runtime.
        // CRITICAL: inside a $$"""…""" block, {{expr}} is the interpolation hole; a lone
        // {placeholder} or ${secret:…} passes through verbatim.
        var topicTemplateLiteral = JsonSerializer.Serialize(model.Topic);
        var payloadTemplateLiteral = JsonSerializer.Serialize(model.Payload);

        // The key is optional: emit the JSON-escaped template literal when present, or
        // the bare 'null' literal when absent (the helper sends an empty key for null).
        var keyTemplateLiteral = model.Key is null
            ? "null"
            : JsonSerializer.Serialize(model.Key);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        // 'Secrets' is the ScriptGlobalVariables.Secrets instance property.
        var block = $$"""
            {
                await MqPublishKafka_Helpers.PublishAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{topicTemplateLiteral}},
                    {{keyTemplateLiteral}},
                    {{payloadTemplateLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}});
            }
            """;

        // Build the helpers list: MqPublishKafka_Helpers + Substitute_Helpers +
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

    // ── IResourceContributor<MqPublishKafkaModel> ─────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="ResourceRequirement"/> with <c>Family="kafka"</c>
    /// and <c>Name=model.Target</c>.  The engine maps the <c>Name</c> to the Kafka
    /// broker resource so the bootstrap-servers connection string is resolved from
    /// the discovered Aspire resource (§4).
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(MqPublishKafkaModel model)
    {
        yield return new ResourceRequirement(
            Family: "kafka",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>Confluent.Kafka</c> assembly so the Roslyn compiler can resolve
    /// <c>ProducerConfig</c>, <c>ProducerBuilder&lt;,&gt;</c>, <c>Message&lt;,&gt;</c>,
    /// and related types in the emitted helper class.  The assembly is already loaded
    /// in the Default ALC (the provider project references it directly) and must never
    /// be loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Confluent.Kafka.ProducerConfig).Assembly;
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
