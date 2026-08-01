// Vouchfx.Steps.MqExpect.AzureServiceBus — mq-expect.azureservicebus step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-expect.azureservicebus step kind.
//
// NON-DESTRUCTIVE PEEK DISCIPLINE:
//   The helper uses ServiceBusReceiver.PeekMessagesAsync which reads messages without
//   acquiring a lock or advancing the receive pointer.  Messages remain available for
//   subsequent RETRY attempts — this is the correct poll discipline for RETRY (§7):
//     • Fail   = no matching message found in the current peek batch (engine retries).
//     • Pass   = a matching message was found.
//     • Inconclusive is written by the engine's RetryRunner on timeout, never by the helper.
//
// Entity provisioning: entities (queues/topics) must be declared in the authoring YAML under
//   environment.dependencies.<name>.queues / .topics.  EnvironmentMapper writes them into
//   Config.json and bind-mounts it into the emulator container at topology-setup time.
//   Runtime auto-create via ServiceBusAdministrationClient is deliberately omitted: it requires
//   the emulator's HTTP management port (5300), not the AMQP port (5672) used by
//   ServiceBusClient, and varies by emulator version.  A missing entity surfaces as a
//   ServiceBusException → Verdict.EnvironmentError (§12.1).
//
// Memory model (§5):
//   ServiceBusClient and ServiceBusReceiver are IAsyncDisposable.  Both are disposed via
//   await .DisposeAsync().ConfigureAwait(false) in finally blocks.  'using var' is
//   prohibited in CSX bodies (§13.3.1); disposal is always explicit.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.AzureServiceBus;

/// <summary>
/// Core provider for the <c>mq-expect.azureservicebus</c> step kind (DSL §5).
/// Non-destructively peeks an Azure Service Bus queue or topic subscription and verifies
/// that at least one message matches the declared expectations.
/// </summary>
[StepProvider]
public sealed class MqExpectAzureServiceBusProvider
    : IStepProvider,
      IStepBinder<MqExpectAzureServiceBusModel>,
      IStepValidator<MqExpectAzureServiceBusModel>,
      IStepCompiler<MqExpectAzureServiceBusModel>,
      IResourceContributor<MqExpectAzureServiceBusModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-expect", "azureservicebus");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqExpectAzureServiceBusModel> ─────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Non-destructively peeks an Azure Service Bus queue or topic subscription and verifies at least one message matches the declared expectations.  Designed for verifyMode: RETRY — the engine retries on Fail until a matching message is found or the timeout is reached.  Note: each attempt scans at most 100 messages (PeekMessagesAsync window); a match beyond the first 100 retained messages in the entity will not be found.",
          "type": "object",
          "required": ["target"],
          "properties": {
            "target": {
              "description": "Logical name of the azureservicebus dependency to peek, as declared under environment.dependencies.",
              "type": "string"
            },
            "queue": {
              "description": "The source queue to peek.  Set 'queue' for queue-based messaging, or 'topic'+'subscription' for topic-based messaging.",
              "type": "string"
            },
            "topic": {
              "description": "The source topic (requires 'subscription' to also be set).  May contain {placeholder} substitution tokens.",
              "type": "string"
            },
            "subscription": {
              "description": "The subscription on the topic to peek.  Required when 'topic' is set.  May contain {placeholder} substitution tokens.",
              "type": "string"
            },
            "expectPayloadContains": {
              "description": "Optional substring the message body must contain for a match.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "expectProperties": {
              "description": "Optional application-property key=value pairs all of which must be present on the matched message.  Values may contain {placeholder} and ${secret:source/path} tokens.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            }
          }
        }
        """);

    /// <inheritdoc />
    public MqExpectAzureServiceBusModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqExpectAzureServiceBusModel(
                Target: string.Empty,
                Queue: null,
                Topic: null,
                Subscription: null,
                ExpectPayloadContains: null,
                ExpectProperties: null);
        }

        var target = GetScalar(mapping, "target");
        var queue = GetScalarOrNull(mapping, "queue");
        var topic = GetScalarOrNull(mapping, "topic");
        var subscription = GetScalarOrNull(mapping, "subscription");
        var expectPayloadContains = GetScalarOrNull(mapping, "expectPayloadContains");

        Dictionary<string, string>? expectProperties = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("expectProperties"), out var propsNode)
            && propsNode is YamlMappingNode propsMapping)
        {
            expectProperties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in propsMapping.Children)
            {
                if (k is YamlScalarNode kScalar && kScalar.Value is not null
                    && v is YamlScalarNode vScalar && vScalar.Value is not null)
                {
                    expectProperties[kScalar.Value] = vScalar.Value;
                }
            }
        }

        return new MqExpectAzureServiceBusModel(
            Target: target,
            Queue: queue,
            Topic: topic,
            Subscription: subscription,
            ExpectPayloadContains: expectPayloadContains,
            ExpectProperties: expectProperties);
    }

    // ── IStepValidator<MqExpectAzureServiceBusModel> ──────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqExpectAzureServiceBusModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-expect.azureservicebus: 'target' must not be empty.");

        var hasQueue = !string.IsNullOrWhiteSpace(model.Queue);
        var hasTopic = !string.IsNullOrWhiteSpace(model.Topic);
        var hasSubscription = !string.IsNullOrWhiteSpace(model.Subscription);

        if (!hasQueue && !hasTopic)
            errors.Add("mq-expect.azureservicebus: exactly one of 'queue' or 'topic'+'subscription' must be set.");

        if (hasQueue && hasTopic)
            errors.Add("mq-expect.azureservicebus: 'queue' and 'topic' are mutually exclusive.");

        if (hasTopic && !hasSubscription)
            errors.Add("mq-expect.azureservicebus: 'subscription' is required when 'topic' is set.");

        if (hasSubscription && !hasTopic)
            errors.Add("mq-expect.azureservicebus: 'subscription' requires 'topic' to also be set.");

        // At least one matching criterion must be declared.
        var hasPayloadContains = !string.IsNullOrWhiteSpace(model.ExpectPayloadContains);
        var hasProperties = model.ExpectProperties is { Count: > 0 };
        if (!hasPayloadContains && !hasProperties)
        {
            errors.Add(
                "mq-expect.azureservicebus: at least one of 'expectPayloadContains' or " +
                "'expectProperties' must be set.");
        }

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-expect.azureservicebus: 'target' '{model.Target}' is not an " +
                    "azureservicebus dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "azureservicebus", StringComparison.Ordinal))
            {
                errors.Add(
                    $"mq-expect.azureservicebus: 'target' '{model.Target}' is declared as a " +
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
            "System.Globalization",
            "System.Threading",
            "System.Threading.Tasks",
            "Azure.Messaging.ServiceBus",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// RETRY discipline: the helper writes <see cref="Verdict.Fail"/> when no matching
    /// message is found so the engine's <c>RetryRunner</c> re-invokes; it writes
    /// <see cref="Verdict.Inconclusive"/> NEVER — the engine writes that on timeout.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqExpectAzureServiceBus_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Peeks messages from an Azure Service Bus queue or topic subscription and\n" +
        "    /// writes a typed StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// Matching message found = Pass.\n" +
        "    /// No matching message (may not have arrived yet) = Fail (engine retries under RETRY).\n" +
        "    /// A missing secret, ServiceBusException, or other failure = EnvironmentError.\n" +
        "    /// Inconclusive is written by the RetryRunner on timeout, never by this helper.\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// NON-DESTRUCTIVE PEEK: PeekMessagesAsync reads without acquiring a lock or\n" +
        "    /// advancing the receive pointer.  Messages remain available for subsequent RETRY\n" +
        "    /// attempts — idempotent poll discipline (§7).\n" +
        "    /// LEAK GATE (§5): ServiceBusClient and ServiceBusReceiver are IAsyncDisposable.\n" +
        "    /// Both are disposed via await .DisposeAsync().ConfigureAwait(false) in finally\n" +
        "    /// blocks.  Roslyn-script bodies prohibit scoped-variable 'using' declarations\n" +
        "    /// (§13.3.1); disposal is always explicit in emitted helpers.\n" +
        "    /// Entity provisioning: entities must be declared in EnvironmentMapper Config.json\n" +
        "    /// (via extra.queues / extra.topics in the authoring YAML).  A missing entity\n" +
        "    /// surfaces as ServiceBusException → EnvironmentError (§12.1).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task ExpectAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string? queueTemplate,\n" +
        "        string? topicTemplate,\n" +
        "        string? subscriptionTemplate,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] expectPropKeys,\n" +
        "        string[] expectPropValueTemplates,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        // No hard-coded transport timeout to lift here — the step token plus\n" +
        "        // the assembler's late supersession are the bound (#232).\n" +
        "        _ = budgetGoverned;\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"Azure Service Bus connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        Azure.Messaging.ServiceBus.ServiceBusClient? sbClient = null;\n" +
        "        Azure.Messaging.ServiceBus.ServiceBusReceiver? receiver = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Entity names: {placeholder} substitution only — secrets must NOT appear\n" +
        "            // in broker entity names (§17).  A ${secret:…} token left unresolved\n" +
        "            // becomes a literal broker path → ServiceBusException → EnvironmentError.\n" +
        "            var queue = queueTemplate is null ? null : Substitute_Helpers.Resolve(vars, queueTemplate);\n" +
        "            var topic = topicTemplate is null ? null : Substitute_Helpers.Resolve(vars, topicTemplate);\n" +
        "            var subscription = subscriptionTemplate is null ? null : Substitute_Helpers.Resolve(vars, subscriptionTemplate);\n" +
        "            // Peek criteria: full secret + placeholder resolution (§17).\n" +
        "            var payloadContains = payloadContainsTemplate is null ? null : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var expectPropValues = new string[expectPropValueTemplates.Length];\n" +
        "            for (int __pi = 0; __pi < expectPropValueTemplates.Length; __pi++)\n" +
        "                expectPropValues[__pi] = Secret_Helpers.ResolveTemplate(secrets, vars, expectPropValueTemplates[__pi]);\n" +
        "            // Entities must be declared in EnvironmentMapper Config.json (via extra.queues/\n" +
        "            // extra.topics in the authoring YAML).  A missing entity surfaces as\n" +
        "            // ServiceBusException → EnvironmentError (§12.1).\n" +
        "            sbClient = new Azure.Messaging.ServiceBus.ServiceBusClient(connStr);\n" +
        "            if (queue is not null)\n" +
        "                receiver = sbClient.CreateReceiver(queue);\n" +
        "            else if (topic is not null && subscription is not null)\n" +
        "                receiver = sbClient.CreateReceiver(topic, subscription);\n" +
        "            else\n" +
        "                throw new System.InvalidOperationException(\"Either queue, or topic + subscription must be set.\");\n" +
        "            // NON-DESTRUCTIVE PEEK: read up to 100 messages without locking or consuming.\n" +
        "            // Messages remain available for subsequent RETRY attempts.\n" +
        "            // NOTE: in Azure.Messaging.ServiceBus 7.18.x the first param is 'maxMessages'\n" +
        "            // (not 'maxMessageCount'); positional arg avoids version-skew compile errors.\n" +
        "            var messages = await receiver.PeekMessagesAsync(\n" +
        "                100,\n" +
        "                cancellationToken: ct).ConfigureAwait(false);\n" +
        "            int scanned = 0;\n" +
        "            bool matched = false;\n" +
        "            foreach (var msg in messages)\n" +
        "            {\n" +
        "                scanned++;\n" +
        "                var body = msg.Body.ToString();\n" +
        "                bool match = true;\n" +
        "                if (payloadContains is not null && !body.Contains(payloadContains, System.StringComparison.Ordinal))\n" +
        "                    match = false;\n" +
        "                if (match && expectPropKeys.Length > 0)\n" +
        "                {\n" +
        "                    for (int __pi = 0; __pi < expectPropKeys.Length; __pi++)\n" +
        "                    {\n" +
        "                        if (!msg.ApplicationProperties.TryGetValue(expectPropKeys[__pi], out var propVal) ||\n" +
        "                            propVal?.ToString() != expectPropValues[__pi])\n" +
        "                        {\n" +
        "                            match = false;\n" +
        "                            break;\n" +
        "                        }\n" +
        "                    }\n" +
        "                }\n" +
        "                if (match)\n" +
        "                {\n" +
        "                    matched = true;\n" +
        "                    break;\n" +
        "                }\n" +
        "            }\n" +
        "            // RETRY discipline (§7): write Fail (not Inconclusive) so the engine retries.\n" +
        "            // Inconclusive is written by RetryRunner on timeout, never by the helper.\n" +
        "            verdict = matched ? Vouchfx.Engine.Abstractions.Verdict.Pass : Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "            observation = \"{\\\"matched\\\":\" + (matched ? \"true\" : \"false\") +\n" +
        "                \",\\\"scanned\\\":\" + scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (Azure.Messaging.ServiceBus.ServiceBusException sbEx)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactAsbConnStr(connStr ?? string.Empty, sbEx.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this provider's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the generic-EnvironmentError branch below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(RedactAsbConnStr(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): IAsyncDisposable — dispose before the collectible ALC unloads.\n" +
        "            if (receiver is not null)\n" +
        "            {\n" +
        "                try { await receiver.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            if (sbClient is not null)\n" +
        "            {\n" +
        "                try { await sbClient.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Strips Azure Service Bus credentials from an error message before it reaches\n" +
        "    /// the observation / event stream (§17).\n" +
        "    /// </summary>\n" +
        "    internal static string RedactAsbConnStr(string connStr, string message)\n" +
        "    {\n" +
        "        var redacted = message ?? string.Empty;\n" +
        "        if (!string.IsNullOrEmpty(connStr))\n" +
        "            redacted = redacted.Replace(connStr, \"***\", System.StringComparison.Ordinal);\n" +
        "        redacted = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            redacted,\n" +
        "            \"SharedAccessKey=[^;]*\",\n" +
        "            \"SharedAccessKey=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return redacted;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqExpectAzureServiceBusModel> ───────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqExpectAzureServiceBusModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        var queueLiteral = model.Queue is null
            ? "null"
            : JsonSerializer.Serialize(model.Queue);

        var topicLiteral = model.Topic is null
            ? "null"
            : JsonSerializer.Serialize(model.Topic);

        var subscriptionLiteral = model.Subscription is null
            ? "null"
            : JsonSerializer.Serialize(model.Subscription);

        var payloadContainsLiteral = model.ExpectPayloadContains is null
            ? "null"
            : JsonSerializer.Serialize(model.ExpectPayloadContains);

        var propEntries = model.ExpectProperties?.ToArray() ?? System.Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>();
        var expectPropKeysLiteral = "new string[] { "
            + string.Join(", ", propEntries.Select(e => JsonSerializer.Serialize(e.Key)))
            + " }";
        var expectPropValuesLiteral = "new string[] { "
            + string.Join(", ", propEntries.Select(e => JsonSerializer.Serialize(e.Value)))
            + " }";

        var block = $$"""
            {
                await MqExpectAzureServiceBus_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{queueLiteral}},
                    {{topicLiteral}},
                    {{subscriptionLiteral}},
                    {{payloadContainsLiteral}},
                    {{expectPropKeysLiteral}},
                    {{expectPropValuesLiteral}},
                    __stepCt_{{safeId}},
                    __stepBudgetGoverned_{{safeId}});
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

    // ── IResourceContributor<MqExpectAzureServiceBusModel> ────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqExpectAzureServiceBusModel model)
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
            // Azure.Messaging.ServiceBus — ServiceBusClient, ServiceBusReceiver,
            // ServiceBusReceivedMessage, ServiceBusException.
            yield return typeof(Azure.Messaging.ServiceBus.ServiceBusClient).Assembly;
            // Azure.Core — required as a direct compile-time reference: Azure.Core types
            // (e.g. Azure.Response, Azure.RequestContext) appear in Azure.Messaging.ServiceBus
            // method signatures, so Roslyn needs the Azure.Core assembly to resolve those
            // type references in the emitted CSX at compile time.
            yield return typeof(Azure.Response).Assembly;
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
