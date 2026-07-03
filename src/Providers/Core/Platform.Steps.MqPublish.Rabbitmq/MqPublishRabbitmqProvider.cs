// Platform.Steps.MqPublish.Rabbitmq — mq-publish.rabbitmq step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor.
//
// Memory model (§5):
//   RabbitMQ.Client 7.x IConnection and IChannel are IAsyncDisposable only (not
//   IDisposable). The emitted helper disposes both explicitly in a finally block via
//   await DisposeAsync().ConfigureAwait(false). 'using var' and 'await using var' are
//   prohibited in CSX (§13.3.1); disposal is explicit.
//
// Credential redaction: the AMQP URI may embed user:pass. Any caught exception whose
// message echoes the URI has its credentials stripped (amqp://user:pass@ → amqp://***@)
// before the observation is written to Vars — so no credentials reach the event stream.
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqPublish.Rabbitmq;

/// <summary>
/// Core provider for the <c>mq-publish.rabbitmq</c> step kind (DSL §5).
/// Publishes a single message to a declared RabbitMQ dependency via AMQP.
/// </summary>
[StepProvider]
public sealed class MqPublishRabbitmqProvider
    : IStepProvider,
      IStepBinder<MqPublishRabbitmqModel>,
      IStepValidator<MqPublishRabbitmqModel>,
      IStepCompiler<MqPublishRabbitmqModel>,
      IResourceContributor<MqPublishRabbitmqModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-publish", "rabbitmq");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqPublishRabbitmqModel> ──────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "A Pass verdict confirms hand-off to the broker client; delivery is NOT confirmed (publisher confirms are a post-v1 feature). Verify delivery with a following mq-expect.rabbitmq step.",
          "type": "object",
          "required": ["target", "routingKey", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of the rabbitmq dependency to publish to, as declared under environment.dependencies.",
              "type": "string"
            },
            "exchange": {
              "description": "Optional AMQP exchange name. Empty or absent routes to the default exchange. May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "routingKey": {
              "description": "The AMQP routing key. For the default exchange this is the queue name. May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "payload": {
              "description": "The message payload sent as the AMQP message body (UTF-8). May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "headers": {
              "description": "Optional map of AMQP message header names to their string values.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public MqPublishRabbitmqModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqPublishRabbitmqModel(
                Target: string.Empty,
                Exchange: null,
                RoutingKey: string.Empty,
                Payload: string.Empty,
                Headers: null);
        }

        var target = GetScalar(mapping, "target");
        var routingKey = GetScalar(mapping, "routingKey");
        var payload = GetScalar(mapping, "payload");

        string? exchange = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("exchange"), out var exNode)
            && exNode is YamlScalarNode exScalar)
        {
            exchange = exScalar.Value ?? string.Empty;
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

        return new MqPublishRabbitmqModel(
            Target: target,
            Exchange: exchange,
            RoutingKey: routingKey,
            Payload: payload,
            Headers: headers);
    }

    // ── IStepValidator<MqPublishRabbitmqModel> ────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqPublishRabbitmqModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-publish.rabbitmq: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.RoutingKey))
            errors.Add("mq-publish.rabbitmq: 'routingKey' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Payload))
            errors.Add("mq-publish.rabbitmq: 'payload' must not be empty.");

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-publish.rabbitmq: 'target' '{model.Target}' is not a " +
                    "rabbitmq dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "rabbitmq", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-publish.rabbitmq: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required rabbitmq dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "System.Threading",
            "System.Threading.Tasks",
            "RabbitMQ.Client",
            "Platform.Engine.Abstractions",
        };

    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqPublishRabbitmq_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Publishes one message to a RabbitMQ exchange via the AMQP 0-9-1 protocol\n" +
        "    /// (RabbitMQ.Client 7.x) and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing AMQP URI = EnvironmentError (§12.1).\n" +
        "    /// Successful publish = Pass (observation carries exchange/routingKey).\n" +
        "    /// A broker failure or a missing secret = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): IConnection and IChannel are IAsyncDisposable only in 7.x.\n" +
        "    /// Both are disposed via await DisposeAsync().ConfigureAwait(false) in a\n" +
        "    /// finally block. Scoped disposal declarations are prohibited in CSX\n" +
        "    /// bodies (§13.3.1); disposal is always explicit in emitted helpers.\n" +
        "    /// Credential redaction: the AMQP URI may embed user:pass; caught exception\n" +
        "    /// messages are sanitised (amqp://user:pass@ → amqp://***@) before writing\n" +
        "    /// to the observation so no credentials reach the event stream.\n" +
        "    /// Topic / routingKey / payload / header VALUES are resolved INSIDE the\n" +
        "    /// guarded region via Secret_Helpers.ResolveTemplate (§17).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task PublishAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string? exchangeTemplate,\n" +
        "        string routingKeyTemplate,\n" +
        "        string payloadTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var amqpUri = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(amqpUri))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"rabbitmq connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        RabbitMQ.Client.IConnection? conn = null;\n" +
        "        RabbitMQ.Client.IChannel? channel = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every author-text field INSIDE the guarded region (§17) via\n" +
        "            // ResolveTemplate (single pass: {placeholder} substitution + ${secret} resolution).\n" +
        "            // A missing secret throws SecretResolutionException → caught → EnvironmentError.\n" +
        "            var exchange = exchangeTemplate is null\n" +
        "                ? string.Empty\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, exchangeTemplate);\n" +
        "            var routingKey = Secret_Helpers.ResolveTemplate(secrets, vars, routingKeyTemplate);\n" +
        "            var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);\n" +
        "            var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new System.Uri(amqpUri) };\n" +
        "            conn = await factory.CreateConnectionAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            channel = await conn.CreateChannelAsync(cancellationToken: System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            var props = new RabbitMQ.Client.BasicProperties();\n" +
        "            if (headerNames.Length > 0)\n" +
        "            {\n" +
        "                props.Headers = new System.Collections.Generic.Dictionary<string, object?>();\n" +
        "                for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "                {\n" +
        "                    var hv = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);\n" +
        "                    props.Headers[headerNames[hi]] = System.Text.Encoding.UTF8.GetBytes(hv);\n" +
        "                }\n" +
        "            }\n" +
        "            var body = System.Text.Encoding.UTF8.GetBytes(payload).AsMemory();\n" +
        "            await channel.BasicPublishAsync(\n" +
        "                exchange: exchange,\n" +
        "                routingKey: routingKey,\n" +
        "                mandatory: false,\n" +
        "                basicProperties: props,\n" +
        "                body: body,\n" +
        "                cancellationToken: System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"exchange\\\":\" + System.Text.Json.JsonSerializer.Serialize(exchange) +\n" +
        "                \",\\\"routingKey\\\":\" + System.Text.Json.JsonSerializer.Serialize(routingKey) + \"}\";\n" +
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
        "            // Broker / connection / channel / publish failure = EnvironmentError (§12.1).\n" +
        "            // Redact AMQP credentials from any reflected URI in the error message.\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactAmqpUri(amqpUri ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): IChannel and IConnection are IAsyncDisposable in 7.x.\n" +
        "            // Dispose channel before connection; swallow disposal failures so they\n" +
        "            // do not mask the step outcome already captured above.\n" +
        "            if (channel is not null)\n" +
        "            {\n" +
        "                try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            if (conn is not null)\n" +
        "            {\n" +
        "                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Strips AMQP credentials from an error message before it reaches the\n" +
        "    /// observation / event stream (§17).  Three-layer approach:\n" +
        "    ///   (a) Literal full-URI replacement (catches URIs echoed verbatim).\n" +
        "    ///   (b) Parsed-userinfo replacement (catches SetUri-style messages that\n" +
        "    ///       echo only the userinfo, e.g. 'Bad user info in AMQP URI: user:pass').\n" +
        "    ///       Also replaces just the password portion, handling passwords that\n" +
        "    ///       contain colons (user:p:ass → 'p:ass' is extracted and replaced).\n" +
        "    ///   (c) Regex fallback with greedy [^/\\s]* that matches past interior '@'\n" +
        "    ///       characters to the LAST '@' before the host, so a password\n" +
        "    ///       containing '@' (user:p@ss) is fully redacted.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactAmqpUri(string amqpUri, string message)\n" +
        "    {\n" +
        "        var redacted = message ?? string.Empty;\n" +
        "        // (a) Literal full URI replacement.\n" +
        "        if (!string.IsNullOrEmpty(amqpUri))\n" +
        "            redacted = redacted.Replace(amqpUri, \"***\", System.StringComparison.Ordinal);\n" +
        "        // (b) Parsed userinfo replacement.\n" +
        "        try\n" +
        "        {\n" +
        "            var __uri = new System.Uri(amqpUri);\n" +
        "            var __userInfo = __uri.UserInfo;\n" +
        "            if (!string.IsNullOrEmpty(__userInfo))\n" +
        "            {\n" +
        "                redacted = redacted.Replace(__userInfo, \"***\", System.StringComparison.Ordinal);\n" +
        "                var __colonIdx = __userInfo.IndexOf(':');\n" +
        "                if (__colonIdx >= 0)\n" +
        "                {\n" +
        "                    var __password = __userInfo.Substring(__colonIdx + 1);\n" +
        "                    if (!string.IsNullOrEmpty(__password))\n" +
        "                        redacted = redacted.Replace(__password, \"***\", System.StringComparison.Ordinal);\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        catch { }\n" +
        "        // (c) Regex fallback — greedy [^/\\s]* matches past interior '@' to the LAST one.\n" +
        "        redacted = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            redacted,\n" +
        "            \"amqps?://[^/\\\\s]*@\",\n" +
        "            \"amqp://***@\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return redacted;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqPublishRabbitmqModel> ─────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqPublishRabbitmqModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

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

        // Exchange is optional: emit the JSON-escaped template literal when present,
        // or the bare 'null' literal when absent (the helper defaults to empty string).
        var exchangeLiteral = model.Exchange is null
            ? "null"
            : JsonSerializer.Serialize(model.Exchange);

        var routingKeyLiteral = JsonSerializer.Serialize(model.RoutingKey);
        var payloadLiteral = JsonSerializer.Serialize(model.Payload);

        var block = $$"""
            {
                await MqPublishRabbitmq_Helpers.PublishAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{exchangeLiteral}},
                    {{routingKeyLiteral}},
                    {{payloadLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}});
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

    // ── IResourceContributor<MqPublishRabbitmqModel> ──────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqPublishRabbitmqModel model)
    {
        yield return new ResourceRequirement(
            Family: "rabbitmq",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(RabbitMQ.Client.ConnectionFactory).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
            return "new string[] { }";

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
