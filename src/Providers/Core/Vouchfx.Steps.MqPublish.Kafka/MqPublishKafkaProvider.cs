// Vouchfx.Steps.MqPublish.Kafka — mq-publish.kafka step provider (DSL §5, §13).
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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Kafka;

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
          "description": "Publishes one UTF-8 message to a Kafka topic, either as a plain string value or as an Avro-encoded value via a schema registry. A Pass verdict confirms hand-off to the broker; verify delivery with a following mq-expect.kafka step.",
          "type": "object",
          "required": ["target", "topic", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of a declared kafka dependency to publish to (environment.dependencies), or a declared service (environment.services) — a customer-supplied broker under its own entrypoint/config. A dependency target of any other type is rejected. A service target validates but currently fails closed at run time as an environment error: provider-side connection staging for service targets arrives with a later slice.",
              "type": "string",
              "minLength": 1
            },
            "topic": {
              "description": "The Kafka topic to publish the message to.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string",
              "minLength": 1
            },
            "key": {
              "description": "Optional message key.  May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is sent as text either way.",
              "type": ["string", "integer", "number", "boolean"]
            },
            "payload": {
              "description": "The message payload sent as the Kafka message value.  A UTF-8 string (literal or inline JSON).  May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is sent as text either way.",
              "$comment": "minLength constrains the string branch of the type union only — a no-op against a number/boolean instance (JSON Schema draft 2020-12 §6.3.1); it still catches an empty-string payload, the meaningful case, regardless of the widening.",
              "type": ["string", "integer", "number", "boolean"],
              "minLength": 1
            },
            "headers": {
              "description": "Optional map of message header names to their values, sent as text — a bare numeric or boolean scalar is read as its literal text.",
              "type": "object",
              "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
            },
            "avro": {
              "description": "Optional Avro / schema-registry encoding.  When present, the message value is built as an Avro GenericRecord from 'schema' + 'record' and produced via the Confluent Schema Registry Avro serializer; the plain 'payload' is ignored.",
              "type": "object",
              "required": ["schemaRegistry", "subject", "schema", "record"],
              "properties": {
                "schemaRegistry": {
                  "description": "Logical name of the kafka dependency whose schema registry to publish through (its schemaRegistry-enabled registry URL is staged under svc::<name>-sr).",
                  "type": "string",
                  "minLength": 1
                },
                "subject": {
                  "description": "The schema-registry subject under which the inline schema is registered.",
                  "type": "string",
                  "minLength": 1
                },
                "schema": {
                  "description": "The inline Avro schema (avsc JSON) for the message value.",
                  "type": "string",
                  "minLength": 1
                },
                "record": {
                  "description": "Map of Avro field names to their values.  Each value may contain {placeholder} and ${secret:source/path} tokens; field names are used verbatim.",
                  "type": "object",
                  "minProperties": 1,
                  "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
                }
              },
              "additionalProperties": false
            }
          }
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
                Headers: null,
                Avro: null);
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

        // 'avro' block (additive, §13): present → bind schemaRegistry/subject/schema/record.
        var avro = BindAvro(mapping);

        return new MqPublishKafkaModel(
            Target: target,
            Topic: topic,
            Key: key,
            Payload: payload,
            Headers: headers,
            Avro: avro);
    }

    /// <summary>
    /// Binds the optional nested <c>avro</c> mapping into a <see cref="KafkaAvro"/>.
    /// Returns <see langword="null"/> when the <c>avro</c> field is absent or not a
    /// mapping (selecting the PLAIN-payload path).
    /// </summary>
    private static KafkaAvro? BindAvro(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("avro"), out var avroNode)
            || avroNode is not YamlMappingNode avroMap)
        {
            return null;
        }

        var schemaRegistry = GetScalar(avroMap, "schemaRegistry");
        var subject = GetScalar(avroMap, "subject");
        var schema = GetScalar(avroMap, "schema");

        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        if (avroMap.Children.TryGetValue(new YamlScalarNode("record"), out var recordNode)
            && recordNode is YamlMappingNode recordMap)
        {
            foreach (var (k, v) in recordMap.Children)
            {
                if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                    record[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
            }
        }

        return new KafkaAvro(
            SchemaRegistryTarget: schemaRegistry,
            Subject: subject,
            Schema: schema,
            Record: record);
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

        // (d) target reconciliation: names a declared kafka dependency, OR (REQ-011,
        //     services-generalisation spec) a declared SERVICE — a customer-supplied broker
        //     under environment.services, since the customer's own mTLS broker runs its own
        //     entrypoint/config and is authored as a service, never the engine-provisioned
        //     kafka dependency type. Acceptance is name-membership only for the service case;
        //     protocol correctness is a later slice's concern (the probe fails closed at
        //     runtime).
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                if (!string.Equals(depType, "kafka", StringComparison.Ordinal))
                {
                    errors.Add(
                        $"mq-publish.kafka: 'target' '{model.Target}' is declared as a " +
                        $"'{depType}' dependency, not the required kafka dependency.");
                }
            }
            else if (!ctx.DeclaredServices.ContainsKey(model.Target))
            {
                errors.Add(
                    $"mq-publish.kafka: 'target' '{model.Target}' is not a kafka dependency " +
                    "declared in environment.dependencies, nor a declared service in " +
                    "environment.services. " +
                    ProjectContextDescriptions.DescribeDeclaredSurfaces(ctx));
            }
        }

        // (e) avro block (when present): schemaRegistry must name a declared kafka
        //     dependency (same reconciliation as 'target' — the registry is provisioned
        //     from that dep's schemaRegistry Extra); subject/schema/record must be present.
        if (model.Avro is { } avro)
        {
            if (string.IsNullOrWhiteSpace(avro.SchemaRegistryTarget))
            {
                errors.Add("mq-publish.kafka: 'avro.schemaRegistry' must not be empty.");
            }
            else if (!ctx.DeclaredDependencies.TryGetValue(avro.SchemaRegistryTarget, out var srType))
            {
                errors.Add(
                    $"mq-publish.kafka: 'avro.schemaRegistry' '{avro.SchemaRegistryTarget}' is not a " +
                    "kafka dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(srType, "kafka", StringComparison.Ordinal))
            {
                errors.Add(
                    $"mq-publish.kafka: 'avro.schemaRegistry' '{avro.SchemaRegistryTarget}' is declared as a " +
                    $"'{srType}' dependency, not the required kafka dependency.");
            }

            if (string.IsNullOrWhiteSpace(avro.Subject))
                errors.Add("mq-publish.kafka: 'avro.subject' must not be empty.");

            if (string.IsNullOrWhiteSpace(avro.Schema))
                errors.Add("mq-publish.kafka: 'avro.schema' must not be empty.");

            if (avro.Record.Count == 0)
                errors.Add("mq-publish.kafka: 'avro.record' must declare at least one field.");
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
            "Vouchfx.Engine.Abstractions",
        };

    // NOTE: the Avro branch in the helper fully-qualifies every Confluent.SchemaRegistry,
    // Confluent.Kafka.SyncOverAsync, and Avro type (e.g. Confluent.SchemaRegistry.
    // CachedSchemaRegistryClient, Avro.Generic.GenericRecord), so no extra using is needed
    // here — the helper compiles independently of using ordering (§13.3.1).

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
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string outcomeKey,\n" +
        "        string bootstrapKey,\n" +
        "        string targetName,\n" +
        "        string topicTemplate,\n" +
        "        string? keyTemplate,\n" +
        "        string payloadTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        string? avroRegistrySvcKey,\n" +
        "        string? avroSubject,\n" +
        "        string? avroSchemaJson,\n" +
        "        string[] avroFieldNames,\n" +
        "        string[] avroFieldValueTemplates,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        // AVRO path is selected when the avro args are present (avroSubject non-null).\n" +
        "        // Otherwise the PLAIN string path runs, byte-identical to the committed slice.\n" +
        "        if (avroSubject is not null)\n" +
        "        {\n" +
        "            await PublishAvroAsync(vars, secrets, security, outcomeKey, bootstrapKey, targetName,\n" +
        "                topicTemplate,\n" +
        "                keyTemplate, headerNames, headerValueTemplates, avroRegistrySvcKey,\n" +
        "                avroSubject, avroSchemaJson, avroFieldNames, avroFieldValueTemplates,\n" +
        "                ct, budgetGoverned)\n" +
        "                .ConfigureAwait(false);\n" +
        "            return;\n" +
        "        }\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        // Read the bootstrap-servers string staged by the orchestrator under the key\n" +
        "        // this step's Emit chose at COMPILE time — VarKeys.Connection for a dependency\n" +
        "        // target, VarKeys.Service for a service one (REQ-011), never guessed here and\n" +
        "        // never resolved by trying one and falling back to the other.  A null or empty\n" +
        "        // string means the target was not discovered → EnvironmentError (§12.1).\n" +
        "        var bootstrap = vars.TryGetValue(bootstrapKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(bootstrap))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"kafka bootstrap not found for key '\" + bootstrapKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
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
        "            // REQ-015: set transport security for THIS step's target from the declared\n" +
        "            // security block, before the producer is built. Inside the guarded try,\n" +
        "            // deliberately — a declared-but-unloadable path throws SecurityMaterialException,\n" +
        "            // which the catches below map to a step-scoped EnvironmentError (§12.1) naming the\n" +
        "            // declared path, rather than an opaque librdkafka transport failure. A target with\n" +
        "            // no security block leaves the config exactly as built above.\n" +
        "            KafkaSecurity_Helpers.ConfigureClient(security, targetName, config);\n" +
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
        "            var dr = await producer.ProduceAsync(topic, msg, ct).ConfigureAwait(false);\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"topic\\\":\" + System.Text.Json.JsonSerializer.Serialize(dr.Topic) +\n" +
        "                \",\\\"partition\\\":\" + dr.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +\n" +
        "                \",\\\"offset\\\":\" + dr.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing / unknown secret = EnvironmentError (§12.1): a configuration\n" +
        "            // problem in the run environment, NOT a product defect and NOT a\n" +
        "            // scenario-level abort.  REFERENCE-ONLY observation (§17): a fixed\n" +
        "            // message plus the discrete source/path coordinates — never the value.\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this method's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the produce-failure branch below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (Confluent.Kafka.ProduceException<string, string> ex)\n" +
        "        {\n" +
        "            // Produce failure: broker unreachable, topic authorization, etc. = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection / configuration failure = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
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
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// AVRO publish path: builds an Avro GenericRecord from the inline schema and the\n" +
        "    /// resolved record field map and produces it via the Confluent Schema Registry\n" +
        "    /// Avro serializer (which auto-registers the schema under the subject — the\n" +
        "    /// 'registry-validated' acceptance).  Bootstrap-missing OR registry-URL-missing OR\n" +
        "    /// any serialize/produce failure = EnvironmentError (§12.1); a missing secret in a\n" +
        "    /// record value = EnvironmentError.  Success = Pass (topic/partition/offset).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): the producer owns a native librdkafka handle and the registry\n" +
        "    /// client is IDisposable; BOTH are Flush()ed/Dispose()d (producer) and Dispose()d\n" +
        "    /// (registry) in the finally so nothing survives the collectible ALC unload.\n" +
        "    /// using-var is illegal in CSX (§13.3.1) — disposal is explicit.\n" +
        "    /// The registry-URL check precedes any client construction, so the missing-URL path\n" +
        "    /// is reachable WITHOUT a live registry or broker.\n" +
        "    /// </remarks>\n" +
        "    private static async System.Threading.Tasks.Task PublishAvroAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string outcomeKey,\n" +
        "        string bootstrapKey,\n" +
        "        string targetName,\n" +
        "        string topicTemplate,\n" +
        "        string? keyTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        string? avroRegistrySvcKey,\n" +
        "        string avroSubject,\n" +
        "        string? avroSchemaJson,\n" +
        "        string[] avroFieldNames,\n" +
        "        string[] avroFieldValueTemplates,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        // No hard-coded transport timeout to lift here — the step token plus\n" +
        "        // the assembler's late supersession are the bound (#232).\n" +
        "        _ = budgetGoverned;\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        // Bootstrap (broker) must be present, exactly as the plain path requires.\n" +
        "        var bootstrap = vars.TryGetValue(bootstrapKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(bootstrap))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"kafka bootstrap not found for key '\" + bootstrapKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        // Schema-registry URL is staged under svc::<sr>-sr (VarKeys.Service pattern).\n" +
        "        // This check runs BEFORE any registry/producer client is constructed, so the\n" +
        "        // missing-URL path needs no live registry or broker.\n" +
        "        var registryUrl = (avroRegistrySvcKey is not null\n" +
        "                && vars.TryGetValue(avroRegistrySvcKey, out var rv) && rv is string rs) ? rs : null;\n" +
        "        if (string.IsNullOrEmpty(registryUrl))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"schema registry URL not found for key '\" + (avroRegistrySvcKey ?? string.Empty) + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        Confluent.SchemaRegistry.CachedSchemaRegistryClient? registry = null;\n" +
        "        Confluent.Kafka.IProducer<string, Avro.Generic.GenericRecord>? producer = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve topic / key INSIDE the guarded region (§17). Record field NAMES are\n" +
        "            // verbatim; only VALUES are resolved (single pass: {placeholder}+${secret}).\n" +
        "            var topic = Secret_Helpers.ResolveTemplate(secrets, vars, topicTemplate);\n" +
        "            var key = keyTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, keyTemplate);\n" +
        "            var schema = (Avro.RecordSchema)Avro.Schema.Parse(avroSchemaJson);\n" +
        "            var record = new Avro.Generic.GenericRecord(schema);\n" +
        "            for (int fi = 0; fi < avroFieldNames.Length; fi++)\n" +
        "            {\n" +
        "                var fieldName = avroFieldNames[fi];\n" +
        "                var resolvedValue = Secret_Helpers.ResolveTemplate(secrets, vars, avroFieldValueTemplates[fi]);\n" +
        "                Avro.Field field;\n" +
        "                if (!schema.TryGetField(fieldName, out field))\n" +
        "                    throw new System.InvalidOperationException(\n" +
        "                        \"avro record field '\" + fieldName + \"' is not present in the schema\");\n" +
        "                record.Add(fieldName, CoerceField(fieldName, field.Schema, resolvedValue));\n" +
        "            }\n" +
        "            registry = new Confluent.SchemaRegistry.CachedSchemaRegistryClient(\n" +
        "                new Confluent.SchemaRegistry.SchemaRegistryConfig { Url = registryUrl });\n" +
        "            // AvroSerializer<T> has no single-arg ctor in 2.14.x: pass a default config.\n" +
        "            // AsSyncOverAsync() (Confluent.Kafka.SyncOverAsync) adapts the async serializer\n" +
        "            // to the ISerializer<T> a ProducerBuilder.SetValueSerializer expects.\n" +
        "            var serializer = new Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>(\n" +
        "                registry, new Confluent.SchemaRegistry.Serdes.AvroSerializerConfig());\n" +
        "            var config = new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrap };\n" +
        "            // REQ-015: set transport security for THIS step's target from the declared\n" +
        "            // security block, before the producer is built. Inside the guarded try,\n" +
        "            // deliberately — a declared-but-unloadable path throws SecurityMaterialException,\n" +
        "            // which the catches below map to a step-scoped EnvironmentError (§12.1) naming the\n" +
        "            // declared path, rather than an opaque librdkafka transport failure. A target with\n" +
        "            // no security block leaves the config exactly as built above.\n" +
        "            KafkaSecurity_Helpers.ConfigureClient(security, targetName, config);\n" +
        "            producer = new Confluent.Kafka.ProducerBuilder<string, Avro.Generic.GenericRecord>(config)\n" +
        "                .SetValueSerializer(Confluent.Kafka.SyncOverAsync.SyncOverAsyncSerializerExtensionMethods.AsSyncOverAsync(serializer))\n" +
        "                .Build();\n" +
        "            var msg = new Confluent.Kafka.Message<string, Avro.Generic.GenericRecord>\n" +
        "            {\n" +
        "                Key = key ?? string.Empty,\n" +
        "                Value = record,\n" +
        "            };\n" +
        "            if (headerNames.Length > 0)\n" +
        "            {\n" +
        "                var msgHeaders = new Confluent.Kafka.Headers();\n" +
        "                for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "                {\n" +
        "                    var headerValue = Secret_Helpers.ResolveTemplate(\n" +
        "                        secrets, vars, headerValueTemplates[hi]);\n" +
        "                    msgHeaders.Add(headerNames[hi], System.Text.Encoding.UTF8.GetBytes(headerValue));\n" +
        "                }\n" +
        "                msg.Headers = msgHeaders;\n" +
        "            }\n" +
        "            var dr = await producer.ProduceAsync(topic, msg, ct).ConfigureAwait(false);\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"topic\\\":\" + System.Text.Json.JsonSerializer.Serialize(dr.Topic) +\n" +
        "                \",\\\"partition\\\":\" + dr.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +\n" +
        "                \",\\\"offset\\\":\" + dr.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing secret in a record value / topic / key = EnvironmentError (§17).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this method's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the catch-all below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // SchemaRegistry / Avro parse-or-coerce / Produce / any other failure =\n" +
        "            // EnvironmentError (§12.1): an infrastructure/configuration problem.\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): release the native producer handle AND the registry client\n" +
        "            // within this step, before the collectible ALC unloads.\n" +
        "            if (producer is not null)\n" +
        "            {\n" +
        "                try { producer.Flush(System.TimeSpan.FromSeconds(10)); } catch { }\n" +
        "                producer.Dispose();\n" +
        "            }\n" +
        "            if (registry is not null)\n" +
        "                registry.Dispose();\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Coerces a resolved STRING value to the Avro field's primitive type.  Supports\n" +
        "    /// string, int, long, float, double, boolean, and null (where the schema permits).\n" +
        "    /// A nullable union [null, T] is unwrapped to its non-null branch; the literal\n" +
        "    /// 'null' (or an empty string against a nullable union) yields a null value.\n" +
        "    /// An unsupported type or an unparseable value throws InvalidOperationException,\n" +
        "    /// which the caller maps to EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// SECRET SAFETY (§17): the resolved 'value' may be a revealed ${secret:…} value, so\n" +
        "    /// no coercion-failure message EVER embeds 'value' — only the (author-declared,\n" +
        "    /// non-secret) field NAME and the EXPECTED Avro type are reported, so a secret that\n" +
        "    /// fails coercion can never reach the observation / event stream.\n" +
        "    /// </remarks>\n" +
        "    private static object? CoerceField(string fieldName, Avro.Schema fieldSchema, string value)\n" +
        "    {\n" +
        "        var schema = fieldSchema;\n" +
        "        var nullable = false;\n" +
        "        // Unwrap a union to its single non-null branch (the common nullable pattern).\n" +
        "        if (schema.Tag == Avro.Schema.Type.Union && schema is Avro.UnionSchema union)\n" +
        "        {\n" +
        "            Avro.Schema? nonNull = null;\n" +
        "            for (int i = 0; i < union.Schemas.Count; i++)\n" +
        "            {\n" +
        "                if (union.Schemas[i].Tag == Avro.Schema.Type.Null)\n" +
        "                    nullable = true;\n" +
        "                else if (nonNull is null)\n" +
        "                    nonNull = union.Schemas[i];\n" +
        "                else\n" +
        "                    throw new System.InvalidOperationException(\n" +
        "                        \"avro union with more than one non-null branch is not supported\");\n" +
        "            }\n" +
        "            if (nonNull is null)\n" +
        "                return null;  // union of only null.\n" +
        "            schema = nonNull;\n" +
        "        }\n" +
        "        if (nullable && (value is null || string.Equals(value, \"null\", System.StringComparison.Ordinal)))\n" +
        "            return null;\n" +
        "        switch (schema.Tag)\n" +
        "        {\n" +
        "            case Avro.Schema.Type.Null:\n" +
        "                return null;\n" +
        "            case Avro.Schema.Type.String:\n" +
        "                return value;\n" +
        "            case Avro.Schema.Type.Boolean:\n" +
        "                if (bool.TryParse(value, out var b)) return b;\n" +
        "                throw new System.InvalidOperationException(\"avro field '\" + fieldName + \"' could not be coerced to boolean\");\n" +
        "            case Avro.Schema.Type.Int:\n" +
        "                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iv)) return iv;\n" +
        "                throw new System.InvalidOperationException(\"avro field '\" + fieldName + \"' could not be coerced to int\");\n" +
        "            case Avro.Schema.Type.Long:\n" +
        "                if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lv)) return lv;\n" +
        "                throw new System.InvalidOperationException(\"avro field '\" + fieldName + \"' could not be coerced to long\");\n" +
        "            case Avro.Schema.Type.Float:\n" +
        "                if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv)) return fv;\n" +
        "                throw new System.InvalidOperationException(\"avro field '\" + fieldName + \"' could not be coerced to float\");\n" +
        "            case Avro.Schema.Type.Double:\n" +
        "                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv)) return dv;\n" +
        "                throw new System.InvalidOperationException(\"avro field '\" + fieldName + \"' could not be coerced to double\");\n" +
        "            default:\n" +
        "                throw new System.InvalidOperationException(\n" +
        "                    \"unsupported avro field type '\" + schema.Tag.ToString() + \"' for field '\" + fieldName + \"' (string, int, long, float, double, boolean, null only)\");\n" +
        "        }\n" +
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

        // REQ-011 + REQ-023 (amended): `target` may name a kafka DEPENDENCY, staged at
        // conn::<name>, or a declared SERVICE — the customer-supplied broker shape — staged at
        // svc::<name>. Which one is a compile-time fact, read from the same DeclaredServices map
        // Validate reconciled the target against, so the emitted lookup is the key the engine
        // actually stages. This provider does NOT transform the staged value afterwards: the
        // engine stages a Kafka-addressed target as the bare bootstrap authority librdkafka
        // expects, and a provider rewriting that value would mean the engine staged the wrong
        // form (REQ-023's own rule).
        var bootstrapKey = ctx.DeclaredServices.ContainsKey(model.Target)
            ? VarKeys.Service(model.Target)
            : VarKeys.Connection(model.Target);

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

        // Avro args: emitted as the bare 'null' literals when no avro block is declared
        // (selecting the PLAIN path in the helper), or as the resolved svc-key / subject /
        // schema literals plus parallel field-name / value-template arrays when present.
        // Record field VALUES are emitted as RAW templates (resolved at runtime, §17);
        // field NAMES and the inline schema are emitted verbatim (JSON-escaped literals).
        string avroRegistrySvcKeyLiteral = "null";
        string avroSubjectLiteral = "null";
        string avroSchemaLiteral = "null";
        string avroFieldNamesLiteral = "new string[] { }";
        string avroFieldValueTemplatesLiteral = "new string[] { }";
        if (model.Avro is { } avro)
        {
            // The schema-registry URL is staged under svc::<sr>-sr; read it via
            // VarKeys.Service(<sr>-sr) at runtime (the helper looks up vars[svcKey]).
            avroRegistrySvcKeyLiteral =
                JsonSerializer.Serialize(VarKeys.Service(avro.SchemaRegistryTarget + "-sr"));
            avroSubjectLiteral = JsonSerializer.Serialize(avro.Subject);
            avroSchemaLiteral = JsonSerializer.Serialize(avro.Schema);
            avroFieldNamesLiteral = BuildStringArrayLiteral(avro.Record.Keys.ToArray());
            avroFieldValueTemplatesLiteral = BuildStringArrayLiteral(avro.Record.Values.ToArray());
        }

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
                    Security,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(bootstrapKey)}},
                    {{JsonSerializer.Serialize(model.Target)}},
                    {{topicTemplateLiteral}},
                    {{keyTemplateLiteral}},
                    {{payloadTemplateLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    {{avroRegistrySvcKeyLiteral}},
                    {{avroSubjectLiteral}},
                    {{avroSchemaLiteral}},
                    {{avroFieldNamesLiteral}},
                    {{avroFieldValueTemplatesLiteral}},
                    __stepCt_{{safeId}},
                    __stepBudgetGoverned_{{safeId}});
            }
            """;

        // Build the helpers list: MqPublishKafka_Helpers + Substitute_Helpers +
        // Secret_Helpers.  Both shared helper sources are byte-identical across
        // providers — deduplication is handled by CsxAssembler.
        var helpers = new List<string>(s_helpers)
        {
            SubstituteHelper.Source,
            SecretHelper.Source,

            // REQ-015. Spliced unconditionally rather than only when the target declares
            // `security`: the emitted helper's own call site is unconditional (it must be — the
            // declaration is resolved at step-execution time, never at compile time, §17), and a
            // provider cannot know at Emit time whether a target declares a security block at all.
            // `ICompileContext` exposes StepId/SuiteNamespace/SuiteDirectory/Captures/CaptureExprs
            // and nothing about `environment` — verified against the frozen v1 contract, which is
            // additive-only and so cannot gain such a member here. CsxAssembler dedupes this source
            // to one copy across every Kafka step in the suite.
            KafkaSecurityHelper.Source,
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
    /// and related types, PLUS the <c>Confluent.SchemaRegistry</c>,
    /// <c>Confluent.SchemaRegistry.Serdes.Avro</c>, and <c>Apache.Avro</c> assemblies so
    /// the emitted CSX <em>always</em> compiles whether or not the Avro branch is taken
    /// (the helper class references all three unconditionally).  Every assembly is already
    /// loaded in the Default ALC (the provider project references them directly) and must
    /// never be loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Confluent.Kafka.ProducerConfig).Assembly;
            // Confluent.SchemaRegistry — CachedSchemaRegistryClient / SchemaRegistryConfig.
            yield return typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly;
            // Confluent.SchemaRegistry.Serdes.Avro — AvroSerializer<T>.
            yield return typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly;
            // Apache.Avro — Avro.Schema / Avro.Generic.GenericRecord.
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
