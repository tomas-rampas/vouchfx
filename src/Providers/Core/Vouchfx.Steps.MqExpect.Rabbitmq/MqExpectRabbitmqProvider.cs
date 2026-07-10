// Vouchfx.Steps.MqExpect.Rabbitmq — mq-expect.rabbitmq step provider (DSL §5, §13).
//
// RETRY model (§7): this provider is a primary verifyMode: RETRY consumer. The emitted
// helper performs an IDEMPOTENT SINGLE poll over the queue using BasicGet (non-destructive:
// non-matching messages are re-queued via BasicNack with requeue=true). It writes Pass on
// a match or Fail when no matching message was found. It NEVER writes Inconclusive.
// The engine-owned RetryRunner re-invokes and converts a sustained Fail to Inconclusive.
//
// Memory model (§5): IConnection and IChannel (RabbitMQ.Client 7.x) are IAsyncDisposable
// only. Disposed explicitly via await DisposeAsync() in a finally block.
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Rabbitmq;

/// <summary>
/// Core provider for the <c>mq-expect.rabbitmq</c> step kind (DSL §5).
/// Consumes from a declared RabbitMQ queue and asserts that a message matching
/// the declared criteria (payloadContains / headers / JSONPath) is present.
/// </summary>
[StepProvider]
public sealed class MqExpectRabbitmqProvider
    : IStepProvider,
      IStepBinder<MqExpectRabbitmqModel>,
      IStepValidator<MqExpectRabbitmqModel>,
      IStepCompiler<MqExpectRabbitmqModel>,
      IResourceContributor<MqExpectRabbitmqModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-expect", "rabbitmq");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqExpectRabbitmqModel> ────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "queue", "match"],
          "properties": {
            "target": {
              "description": "Logical name of the rabbitmq dependency to consume from, as declared under environment.dependencies.",
              "type": "string"
            },
            "queue": {
              "description": "The AMQP queue to consume messages from.",
              "type": "string"
            },
            "match": {
              "description": "The criteria a consumed message must satisfy. At least one criterion (payloadContains, headers, or json) must be declared.",
              "type": "object",
              "properties": {
                "payloadContains": {
                  "description": "Optional substring the UTF-8 message body must contain (ordinal). May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "headers": {
                  "description": "Optional map of expected AMQP header names to their expected string values.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                },
                "json": {
                  "description": "Optional map of JSONPath expressions to their expected string values, evaluated over the message body parsed as JSON.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                }
              },
              "additionalProperties": true
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public MqExpectRabbitmqModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqExpectRabbitmqModel(
                Target: string.Empty,
                Queue: string.Empty,
                Match: new RabbitmqMatch(PayloadContains: null, Headers: null, Json: null));
        }

        var target = GetScalar(mapping, "target");
        var queue = GetScalar(mapping, "queue");
        var match = BindMatch(mapping);

        return new MqExpectRabbitmqModel(
            Target: target,
            Queue: queue,
            Match: match);
    }

    private static RabbitmqMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new RabbitmqMatch(PayloadContains: null, Headers: null, Json: null);
        }

        string? payloadContains = null;
        if (matchMap.Children.TryGetValue(new YamlScalarNode("payloadContains"), out var pcNode)
            && pcNode is YamlScalarNode pcScalar)
        {
            payloadContains = pcScalar.Value ?? string.Empty;
        }

        var headers = BindStringMap(matchMap, "headers");
        var json = BindStringMap(matchMap, "json");

        return new RabbitmqMatch(
            PayloadContains: payloadContains,
            Headers: headers,
            Json: json);
    }

    private static Dictionary<string, string>? BindStringMap(YamlMappingNode parent, string field)
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

    // ── IStepValidator<MqExpectRabbitmqModel> ─────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqExpectRabbitmqModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-expect.rabbitmq: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Queue))
            errors.Add("mq-expect.rabbitmq: 'queue' must not be empty.");

        if (!HasAnyCriterion(model.Match))
        {
            errors.Add(
                "mq-expect.rabbitmq: 'match' must declare at least one criterion " +
                "(payloadContains, headers, or json).");
        }

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-expect.rabbitmq: 'target' '{model.Target}' is not a " +
                    "rabbitmq dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "rabbitmq", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-expect.rabbitmq: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required rabbitmq dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    private static bool HasAnyCriterion(RabbitmqMatch match)
        => match.PayloadContains is not null
           || (match.Headers is { Count: > 0 })
           || (match.Json is { Count: > 0 });

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
            "Vouchfx.Engine.Abstractions",
        };

    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqExpectRabbitmq_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Performs ONE idempotent BasicGet drain over a RabbitMQ queue and writes\n" +
        "    /// a typed StepOutcome into Vars.\n" +
        "    /// Missing AMQP URI = EnvironmentError (§12.1).\n" +
        "    /// A matching message = Pass; no match this attempt = Fail (the RETRY runner\n" +
        "    /// retries on non-Pass and converts a sustained Fail to Inconclusive on\n" +
        "    /// timeout — this helper NEVER writes Inconclusive, §7/§12.1).\n" +
        "    /// A missing secret or a connection failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): IConnection and IChannel are IAsyncDisposable in RabbitMQ.Client 7.x.\n" +
        "    /// Both are disposed via await DisposeAsync().ConfigureAwait(false) in the\n" +
        "    /// finally block. Scoped disposal declarations are prohibited in CSX (§13.3.1).\n" +
        "    /// IDEMPOTENT drain: non-matching messages are re-queued via BasicNack(requeue=true)\n" +
        "    /// so each attempt starts from the head of the queue.\n" +
        "    /// Expected values are resolved BEFORE the connection is built, so a missing\n" +
        "    /// secret is reachable without a broker (§17).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task ExpectAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string queue,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValueTemplates)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var amqpUri = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(amqpUri))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"rabbitmq connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        RabbitMQ.Client.IConnection? conn = null;\n" +
        "        RabbitMQ.Client.IChannel? channel = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve expected values BEFORE the connection is built (§17).\n" +
        "            // A missing secret throws SecretResolutionException BEFORE any broker contact.\n" +
        "            var payloadContains = payloadContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var headerValues = new string[headerValueTemplates.Length];\n" +
        "            for (int hi = 0; hi < headerValueTemplates.Length; hi++)\n" +
        "                headerValues[hi] = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);\n" +
        "            var jsonValues = new string[jsonValueTemplates.Length];\n" +
        "            for (int ji = 0; ji < jsonValueTemplates.Length; ji++)\n" +
        "                jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);\n" +
        "            var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new System.Uri(amqpUri) };\n" +
        "            conn = await factory.CreateConnectionAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            channel = await conn.CreateChannelAsync(cancellationToken: System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            const int maxMessages = 200;\n" +
        "            int scanned = 0;\n" +
        "            bool matched = false;\n" +
        "            // Peek-and-hold: collect unmatched delivery tags and requeue ALL of them\n" +
        "            // AFTER the drain loop exits.  Immediate per-message nack+requeue INSIDE\n" +
        "            // the loop causes head-of-queue starvation: when the first ready message\n" +
        "            // does not match, RabbitMQ places the requeued message back at (or near)\n" +
        "            // the head, and the next BasicGet re-fetches the SAME message, so the loop\n" +
        "            // never advances past the non-matching head to reach the matching message\n" +
        "            // behind it.  Holding all unacked tags prevents redelivery until the drain\n" +
        "            // is complete; the broker automatically requeues any still-unacked messages\n" +
        "            // if the channel closes (exception paths are therefore safe).\n" +
        "            var pendingTags = new System.Collections.Generic.List<ulong>();\n" +
        "            while (scanned < maxMessages && !matched)\n" +
        "            {\n" +
        "                var result = await channel.BasicGetAsync(queue, autoAck: false, System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "                if (result is null)\n" +
        "                    break;  // all ready messages are held as outstanding, or queue is empty\n" +
        "                scanned++;\n" +
        "                var body = System.Text.Encoding.UTF8.GetString(result.Body.Span);\n" +
        "                if (MatchesMessage(body, result.BasicProperties.Headers, payloadContains,\n" +
        "                        headerNames, headerValues, jsonPaths, jsonValues))\n" +
        "                {\n" +
        "                    await channel.BasicAckAsync(result.DeliveryTag, multiple: false, System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "                    matched = true;\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    // Hold the tag — do NOT requeue inside the loop (see comment above).\n" +
        "                    pendingTags.Add(result.DeliveryTag);\n" +
        "                }\n" +
        "            }\n" +
        "            // Verdict is computed BEFORE the requeue loop so that a transient broker\n" +
        "            // error during bulk nack (e.g. channel drop) cannot downgrade a genuine\n" +
        "            // Pass to EnvironmentError. The matched message is already acked at this\n" +
        "            // point; only the requeue of non-matching messages is at risk.\n" +
        "            if (matched)\n" +
        "            {\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "                observation = \"{\\\"matched\\\":true,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                // No match THIS attempt = Fail. RetryRunner retries on non-Pass;\n" +
        "                // converts a sustained Fail to Inconclusive on timeout — never here.\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"matched\\\":false,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "            // Best-effort requeue of inspected-but-unmatched messages.  Swallow any\n" +
        "            // nack error: the broker auto-requeues unacked messages on channel close\n" +
        "            // (happens immediately in the finally block), so silence here is safe.\n" +
        "            foreach (var pendingTag in pendingTags)\n" +
        "            {\n" +
        "                try { await channel.BasicNackAsync(pendingTag, multiple: false, requeue: true, System.Threading.CancellationToken.None).ConfigureAwait(false); } catch { }\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactAmqpUri(amqpUri ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
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
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Evaluates one decoded message against the resolved criteria.\n" +
        "    /// All criteria are conjunctive (AND); an absent criterion imposes no constraint.\n" +
        "    /// </summary>\n" +
        "    private static bool MatchesMessage(\n" +
        "        string body,\n" +
        "        System.Collections.Generic.IDictionary<string, object?>? messageHeaders,\n" +
        "        string? payloadContains,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValues,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValues)\n" +
        "    {\n" +
        "        // (1) payload substring (ordinal) when set.\n" +
        "        if (payloadContains is not null && !body.Contains(payloadContains, System.StringComparison.Ordinal))\n" +
        "            return false;\n" +
        "        // (2) each expected header must be present with a matching UTF-8 decoded value.\n" +
        "        for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "        {\n" +
        "            string? actualValue = null;\n" +
        "            if (messageHeaders is not null && messageHeaders.TryGetValue(headerNames[hi], out var hv) && hv is byte[] hBytes)\n" +
        "                actualValue = System.Text.Encoding.UTF8.GetString(hBytes);\n" +
        "            if (actualValue is null || !string.Equals(actualValue, headerValues[hi], System.StringComparison.Ordinal))\n" +
        "                return false;\n" +
        "        }\n" +
        "        // (3) each JSONPath must select a node whose stringified value equals the expected value.\n" +
        "        if (jsonPaths.Length > 0)\n" +
        "        {\n" +
        "            System.Text.Json.Nodes.JsonNode? node;\n" +
        "            try { node = System.Text.Json.Nodes.JsonNode.Parse(body); }\n" +
        "            catch (System.Exception) { node = null; }\n" +
        "            if (node is null)\n" +
        "                return false;\n" +
        "            for (int ji = 0; ji < jsonPaths.Length; ji++)\n" +
        "            {\n" +
        "                string? actualValue = null;\n" +
        "                try\n" +
        "                {\n" +
        "                    var pathResult = Json.Path.JsonPath.Parse(jsonPaths[ji]).Evaluate(node);\n" +
        "                    var matches = pathResult.Matches;\n" +
        "                    if (matches != null && matches.Count > 0 && matches[0].Value is not null)\n" +
        "                    {\n" +
        "                        var firstMatch = matches[0].Value;\n" +
        "                        if (firstMatch is System.Text.Json.Nodes.JsonValue jv)\n" +
        "                        {\n" +
        "                            var rawElem = jv.GetValue<System.Text.Json.JsonElement>();\n" +
        "                            actualValue = rawElem.ValueKind == System.Text.Json.JsonValueKind.String\n" +
        "                                ? rawElem.GetString() ?? string.Empty\n" +
        "                                : rawElem.GetRawText();\n" +
        "                        }\n" +
        "                        else\n" +
        "                        {\n" +
        "                            actualValue = firstMatch.ToJsonString();\n" +
        "                        }\n" +
        "                    }\n" +
        "                }\n" +
        "                catch (System.Exception) { actualValue = null; }\n" +
        "                if (actualValue is null || !string.Equals(actualValue, jsonValues[ji], System.StringComparison.Ordinal))\n" +
        "                    return false;\n" +
        "            }\n" +
        "        }\n" +
        "        return true;\n" +
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

    // ── IStepCompiler<MqExpectRabbitmqModel> ──────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqExpectRabbitmqModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

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

        var queueLiteral = JsonSerializer.Serialize(model.Queue);

        var payloadContainsLiteral = match.PayloadContains is null
            ? "null"
            : JsonSerializer.Serialize(match.PayloadContains);

        var block = $$"""
            {
                await MqExpectRabbitmq_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{queueLiteral}},
                    {{payloadContainsLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    {{jsonPathsLiteral}},
                    {{jsonValueTemplatesLiteral}});
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

    // ── IResourceContributor<MqExpectRabbitmqModel> ───────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqExpectRabbitmqModel model)
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
            yield return typeof(Json.Path.JsonPath).Assembly;
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
