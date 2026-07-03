// Platform.Steps.MqPublish.AzureServiceBus — mq-publish.azureservicebus step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-publish.azureservicebus step kind.
//
// Publishes a single UTF-8 message to an Azure Service Bus queue or topic.
// The Azure Service Bus emulator (two-container topology: SQL Server sidecar + ASB emulator)
// is the test-time broker.  Production scenarios use a real Azure Service Bus namespace.
//
// Substitution + secret model (canonical M2 pattern — mirrors mq-publish.nats):
//   The entity name (queue/topic) and payload are emitted as RAW template strings and
//   resolved inside the helper's guarded try via Secret_Helpers.ResolveTemplate (§17).
//   A missing secret throws SecretResolutionException → caught → Verdict.EnvironmentError.
//
// Entity provisioning: entities (queues/topics) must be declared in the authoring YAML under
//   environment.dependencies.<name>.queues / .topics.  EnvironmentMapper writes them into
//   Config.json and bind-mounts it into the emulator container at topology-setup time.
//   Runtime auto-create via ServiceBusAdministrationClient is deliberately omitted:
//   it requires the emulator's HTTP management port (5300), not the AMQP port (5672) used
//   by ServiceBusClient, and varies by emulator version.  A missing entity surfaces as a
//   ServiceBusException → Verdict.EnvironmentError (§12.1), which correctly signals a
//   misconfigured environment.
//
// Memory model (§5) — the leak-critical concern:
//   ServiceBusClient and ServiceBusSender are IAsyncDisposable.  Both are disposed via
//   await .DisposeAsync().ConfigureAwait(false) in finally blocks.  'using var' is
//   prohibited in CSX bodies (§13.3.1); disposal is always explicit.
//
// Credential redaction (§17):
//   The connection string carries SharedAccessKey=<value>.  Any caught exception whose
//   message echoes the key has its value stripped via RedactAsbConnStr before the
//   observation is written to Vars.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqPublish.AzureServiceBus;

/// <summary>
/// Core provider for the <c>mq-publish.azureservicebus</c> step kind (DSL §5).
/// Publishes a single UTF-8 message (with optional application properties) to an Azure
/// Service Bus queue or topic, writing a <see cref="StepOutcome"/> with the verdict.
/// </summary>
[StepProvider]
public sealed class MqPublishAzureServiceBusProvider
    : IStepProvider,
      IStepBinder<MqPublishAzureServiceBusModel>,
      IStepValidator<MqPublishAzureServiceBusModel>,
      IStepCompiler<MqPublishAzureServiceBusModel>,
      IResourceContributor<MqPublishAzureServiceBusModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-publish", "azureservicebus");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqPublishAzureServiceBusModel> ────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Publishes one UTF-8 message to an Azure Service Bus queue or topic.  A Pass verdict confirms the send was accepted by the broker.  Verify with a following mq-expect.azureservicebus step.",
          "type": "object",
          "required": ["target", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of the azureservicebus dependency to publish to, as declared under environment.dependencies.",
              "type": "string"
            },
            "queue": {
              "description": "The target queue name.  Exactly one of 'queue' or 'topic' must be set.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "topic": {
              "description": "The target topic name.  Exactly one of 'queue' or 'topic' must be set.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "payload": {
              "description": "The message body sent as UTF-8 bytes.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "properties": {
              "description": "Optional application properties to attach to the message (string key=value pairs).  Values may contain {placeholder} and ${secret:source/path} tokens.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public MqPublishAzureServiceBusModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqPublishAzureServiceBusModel(
                Target: string.Empty,
                Queue: null,
                Topic: null,
                Payload: string.Empty,
                Properties: null);
        }

        var target = GetScalar(mapping, "target");
        var queue = GetScalarOrNull(mapping, "queue");
        var topic = GetScalarOrNull(mapping, "topic");
        var payload = GetScalar(mapping, "payload");

        Dictionary<string, string>? properties = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("properties"), out var propsNode)
            && propsNode is YamlMappingNode propsMapping)
        {
            properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in propsMapping.Children)
            {
                if (k is YamlScalarNode kScalar && kScalar.Value is not null
                    && v is YamlScalarNode vScalar && vScalar.Value is not null)
                {
                    properties[kScalar.Value] = vScalar.Value;
                }
            }
        }

        return new MqPublishAzureServiceBusModel(
            Target: target,
            Queue: queue,
            Topic: topic,
            Payload: payload,
            Properties: properties);
    }

    // ── IStepValidator<MqPublishAzureServiceBusModel> ─────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqPublishAzureServiceBusModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-publish.azureservicebus: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Payload))
            errors.Add("mq-publish.azureservicebus: 'payload' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Queue) && string.IsNullOrWhiteSpace(model.Topic))
            errors.Add("mq-publish.azureservicebus: exactly one of 'queue' or 'topic' must be set.");

        if (!string.IsNullOrWhiteSpace(model.Queue) && !string.IsNullOrWhiteSpace(model.Topic))
            errors.Add("mq-publish.azureservicebus: 'queue' and 'topic' are mutually exclusive — set exactly one.");

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-publish.azureservicebus: 'target' '{model.Target}' is not an " +
                    "azureservicebus dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "azureservicebus", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-publish.azureservicebus: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required azureservicebus dependency.");
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
            "System.Threading",
            "System.Threading.Tasks",
            "Azure.Messaging.ServiceBus",
            "Platform.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MqPublishAzureServiceBus_</c> to prevent collisions
    /// when multiple providers contribute helpers to the same Roslyn submission.
    /// All types NOT in the s_usings namespaces are fully-qualified.
    /// <c>using var</c> is absent — explicit <c>await .DisposeAsync()</c> in <c>finally</c>
    /// blocks is used instead (§5 / §13.3.1).
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqPublishAzureServiceBus_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Publishes one UTF-8 message to an Azure Service Bus queue or topic and\n" +
        "    /// writes a typed StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// Successful send = Pass.\n" +
        "    /// A missing secret, a ServiceBusException, or any other failure = EnvironmentError.\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): ServiceBusClient and ServiceBusSender are IAsyncDisposable.\n" +
        "    /// Both are disposed via await .DisposeAsync().ConfigureAwait(false) in finally\n" +
        "    /// blocks.  'using var' / 'await using var' are prohibited in CSX bodies\n" +
        "    /// (§13.3.1); disposal is always explicit in emitted helpers.\n" +
        "    /// Entity provisioning: entities must be declared in EnvironmentMapper Config.json\n" +
        "    /// (via extra.queues / extra.topics in the authoring YAML).  A missing entity\n" +
        "    /// surfaces as ServiceBusException → EnvironmentError (§12.1).\n" +
        "    /// Credential redaction (§17): SharedAccessKey=<value> is scrubbed before\n" +
        "    /// the observation is written to Vars.\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task PublishAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string? queueTemplate,\n" +
        "        string? topicTemplate,\n" +
        "        string payloadTemplate,\n" +
        "        string[] propKeys,\n" +
        "        string[] propValueTemplates)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"Azure Service Bus connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        Azure.Messaging.ServiceBus.ServiceBusClient? sbClient = null;\n" +
        "        Azure.Messaging.ServiceBus.ServiceBusSender? sender = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve author-text fields INSIDE the guarded region (§17) via\n" +
        "            // ResolveTemplate (single pass: {placeholder} substitution + ${secret} resolution).\n" +
        "            var queue = queueTemplate is null ? null : Secret_Helpers.ResolveTemplate(secrets, vars, queueTemplate);\n" +
        "            var topic = topicTemplate is null ? null : Secret_Helpers.ResolveTemplate(secrets, vars, topicTemplate);\n" +
        "            var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);\n" +
        "            var propValues = new string[propValueTemplates.Length];\n" +
        "            for (int __pi = 0; __pi < propValueTemplates.Length; __pi++)\n" +
        "                propValues[__pi] = Secret_Helpers.ResolveTemplate(secrets, vars, propValueTemplates[__pi]);\n" +
        "            var entityPath = queue ?? topic ?? throw new System.InvalidOperationException(\"Either queue or topic must be set.\");\n" +
        "            // Entities must be declared in EnvironmentMapper Config.json (via extra.queues/\n" +
        "            // extra.topics in the authoring YAML).  A missing entity surfaces as\n" +
        "            // ServiceBusException → EnvironmentError (§12.1).\n" +
        "            sbClient = new Azure.Messaging.ServiceBus.ServiceBusClient(connStr);\n" +
        "            sender = sbClient.CreateSender(entityPath);\n" +
        "            var msg = new Azure.Messaging.ServiceBus.ServiceBusMessage(System.Text.Encoding.UTF8.GetBytes(payload));\n" +
        "            for (int __pi = 0; __pi < propKeys.Length; __pi++)\n" +
        "                msg.ApplicationProperties[propKeys[__pi]] = propValues[__pi];\n" +
        "            await sender.SendMessageAsync(msg, System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"sent\\\":true,\\\"entity\\\":\" + System.Text.Json.JsonSerializer.Serialize(entityPath) + \"}\";\n" +
        "        }\n" +
        "        catch (Platform.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (Azure.Messaging.ServiceBus.ServiceBusException sbEx)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactAsbConnStr(connStr ?? string.Empty, sbEx.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactAsbConnStr(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): both are IAsyncDisposable.  Dispose within this step,\n" +
        "            // before the collectible ALC unloads.  Swallow disposal failures.\n" +
        "            if (sender is not null)\n" +
        "            {\n" +
        "                try { await sender.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            if (sbClient is not null)\n" +
        "            {\n" +
        "                try { await sbClient.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Strips Azure Service Bus credentials from an error message before it reaches\n" +
        "    /// the observation / event stream (§17).\n" +
        "    /// Two layers: (a) literal full connection-string replacement;\n" +
        "    /// (b) regex to scrub SharedAccessKey=<value> up to the next semicolon.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactAsbConnStr(string connStr, string message)\n" +
        "    {\n" +
        "        var redacted = message ?? string.Empty;\n" +
        "        // (a) Literal full connection string replacement.\n" +
        "        if (!string.IsNullOrEmpty(connStr))\n" +
        "            redacted = redacted.Replace(connStr, \"***\", System.StringComparison.Ordinal);\n" +
        "        // (b) Regex: scrub SharedAccessKey=<value> up to the next semicolon.\n" +
        "        redacted = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            redacted,\n" +
        "            \"SharedAccessKey=[^;]*\",\n" +
        "            \"SharedAccessKey=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return redacted;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqPublishAzureServiceBusModel> ──────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqPublishAzureServiceBusModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Queue and topic templates are emitted as RAW template literals so any
        // {placeholder} or ${secret:…} token survives as literal text and is resolved
        // at runtime by Secret_Helpers.ResolveTemplate (§17).
        var queueLiteral = model.Queue is null
            ? "null"
            : JsonSerializer.Serialize(model.Queue);

        var topicLiteral = model.Topic is null
            ? "null"
            : JsonSerializer.Serialize(model.Topic);

        var payloadLiteral = JsonSerializer.Serialize(model.Payload);

        // Emit property keys and value-template literals as parallel arrays.
        var propEntries = model.Properties?.ToArray() ?? System.Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>();
        var propKeysLiteral = "new string[] { "
            + string.Join(", ", propEntries.Select(e => JsonSerializer.Serialize(e.Key)))
            + " }";
        var propValuesLiteral = "new string[] { "
            + string.Join(", ", propEntries.Select(e => JsonSerializer.Serialize(e.Value)))
            + " }";

        var block = $$"""
            {
                await MqPublishAzureServiceBus_Helpers.PublishAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{queueLiteral}},
                    {{topicLiteral}},
                    {{payloadLiteral}},
                    {{propKeysLiteral}},
                    {{propValuesLiteral}});
            }
            """;

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

    // ── IResourceContributor<MqPublishAzureServiceBusModel> ───────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqPublishAzureServiceBusModel model)
    {
        yield return new ResourceRequirement(
            Family: "azureservicebus",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            // Azure.Messaging.ServiceBus — ServiceBusClient, ServiceBusSender, ServiceBusMessage,
            // ServiceBusException, ServiceBusReceivedMessage.  All types referenced by the emitted
            // CSX helpers are in this single assembly.  Azure.Core is a transitive dep of ServiceBus
            // but is NOT referenced by the emitted CSX (no Azure.Response<T> in the data-plane path),
            // so it does not need to be contributed explicitly — it is resolved from Azure.Messaging.
            // ServiceBus.dll's metadata at compile time without being loaded into the collectible ALC.
            yield return typeof(Azure.Messaging.ServiceBus.ServiceBusClient).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    private static string? GetScalarOrNull(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
}
