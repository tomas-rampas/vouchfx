// Platform.Steps.MqExpect.Redis — mq-expect.redis step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-expect.redis step kind.
//
// Semantics: Redis Streams.  Match criteria are evaluated against the UTF-8 message
// payload carried under the canonical "payload" stream field, via optional substring
// check and/or JSONPath evaluation.  No key / headers in v1.
//
// RETRY model (§7): this provider is a verifyMode: RETRY consumer, exactly like
// mq-expect.nats.  The engine wraps a RETRY step in a polling loop, so the emitted
// helper performs an IDEMPOTENT SINGLE poll: it scans the ENTIRE stream from the start
// via XRANGE <stream> - + on every attempt (mirrors mq-expect.nats's DeliverPolicy.All
// ordered-consumer rationale: re-runnable under engine-owned RETRY) and writes Pass or
// Fail.  It contains NO retry logic — the RetryRunner re-invokes the delegate and
// converts a sustained Fail to Inconclusive on timeout (§12.1).  The helper NEVER
// writes Inconclusive.
//
// Shared-stream caution (mirrors mq-expect.nats's FIX M3 constraint):
//   mq-expect.redis scans the WHOLE retained stream on every attempt (XRANGE - +,
//   unbounded by default — no MAXLEN trimming is applied by this provider).  Do NOT
//   share a single redis dependency across scenarios that assert on the same stream
//   key: entries from prior runs will produce a false Pass.  Use separate dependency
//   declarations per scenario, or distinct stream names.
//
// Substitution + secret model (canonical M2 pattern):
//   The payloadContains template and json expected VALUES are emitted as RAW template
//   strings and resolved BEFORE any broker contact via Secret_Helpers.ResolveTemplate
//   (§17).  A missing secret throws SecretResolutionException → caught → EnvironmentError
//   with no connection ever opened.
//
// Memory model (§5):
//   ConnectionMultiplexer is IDisposable.  The emitted helper disposes the connection via
//   Dispose() in a finally.  'using var' is prohibited in CSX bodies (§13.3.1); disposal
//   is always explicit.
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqExpect.Redis;

/// <summary>
/// Core provider for the <c>mq-expect.redis</c> step kind (DSL §5).
/// Scans a declared Redis Streams dependency via <c>XRANGE</c> and asserts that a
/// message matching the declared criteria (payloadContains and/or JSONPath) is present.
/// </summary>
[StepProvider]
public sealed class MqExpectRedisProvider
    : IStepProvider,
      IStepBinder<MqExpectRedisModel>,
      IStepValidator<MqExpectRedisModel>,
      IStepCompiler<MqExpectRedisModel>,
      IResourceContributor<MqExpectRedisModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-expect", "redis");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqExpectRedisModel> ───────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Asserts that a message matching the declared criteria is present on a Redis Stream, scanned from the beginning via XRANGE <stream> - + on every attempt.  IMPORTANT: do NOT share a single redis dependency across scenarios that assert on the same stream — entries from prior runs produce a false Pass.  Use verifyMode: RETRY to poll until the message arrives.",
          "type": "object",
          "required": ["target", "stream", "match"],
          "properties": {
            "target": {
              "description": "Logical name of the redis dependency to consume from, as declared under environment.dependencies.",
              "type": "string"
            },
            "stream": {
              "description": "The Redis Stream key to scan via XRANGE.",
              "type": "string"
            },
            "match": {
              "description": "The criteria a fetched message must satisfy.  At least one criterion (payloadContains or json) must be declared.",
              "type": "object",
              "properties": {
                "payloadContains": {
                  "description": "Optional substring the UTF-8 message payload must contain (ordinal).  May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "json": {
                  "description": "Optional map of JSONPath expressions to their expected string values, evaluated over the message payload parsed as JSON.",
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
    public MqExpectRedisModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqExpectRedisModel(
                Target: string.Empty,
                Stream: string.Empty,
                Match: new RedisMatch(PayloadContains: null, Json: null));
        }

        var target = GetScalar(mapping, "target");
        var stream = GetScalar(mapping, "stream");
        var match = BindMatch(mapping);

        return new MqExpectRedisModel(
            Target: target,
            Stream: stream,
            Match: match);
    }

    /// <summary>
    /// Parses the nested <c>match</c> mapping into a <see cref="RedisMatch"/>.
    /// Tolerant of an absent or non-mapping <c>match</c> node — both yield an
    /// all-<see langword="null"/> match (which validation then rejects).
    /// </summary>
    private static RedisMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new RedisMatch(PayloadContains: null, Json: null);
        }

        string? payloadContains = null;
        if (matchMap.Children.TryGetValue(new YamlScalarNode("payloadContains"), out var pcNode)
            && pcNode is YamlScalarNode pcScalar)
        {
            payloadContains = pcScalar.Value ?? string.Empty;
        }

        IReadOnlyDictionary<string, string>? json = BindStringMap(matchMap, "json");

        return new RedisMatch(
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

    // ── IStepValidator<MqExpectRedisModel> ────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqExpectRedisModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-expect.redis: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Stream))
            errors.Add("mq-expect.redis: 'stream' must not be empty.");

        if (!HasAnyCriterion(model.Match))
        {
            errors.Add(
                "mq-expect.redis: 'match' must declare at least one criterion " +
                "(payloadContains or json).");
        }

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-expect.redis: 'target' '{model.Target}' is not a " +
                    "redis dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "redis", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-expect.redis: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required redis dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="match"/> declares at least
    /// one effective criterion: a non-null payloadContains or a non-empty json map.
    /// </summary>
    private static bool HasAnyCriterion(RedisMatch match)
        => match.PayloadContains is not null
           || (match.Json is { Count: > 0 });

    // ── CsxFragment components ────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "Platform.Engine.Abstractions",
        };

    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqExpectRedis_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Performs ONE idempotent scan over a Redis Stream via XRANGE &lt;stream&gt; - +\n" +
        "    /// and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// A matching message = Pass; no match this attempt = Fail (the RETRY runner\n" +
        "    /// retries on non-Pass and converts a sustained Fail to Inconclusive on\n" +
        "    /// timeout — this helper NEVER writes Inconclusive, §7/§12.1).\n" +
        "    /// A missing secret or a Redis/connection failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): ConnectionMultiplexer is IDisposable.  The connection is disposed\n" +
        "    /// via Dispose() in a finally block.  'using var' is prohibited in CSX bodies\n" +
        "    /// (§13.3.1).\n" +
        "    /// Expected payloadContains / json VALUES are resolved INSIDE the guarded region\n" +
        "    /// BEFORE the connection is created, via Secret_Helpers.ResolveTemplate.  A\n" +
        "    /// missing secret throws SecretResolutionException -> caught -> EnvironmentError\n" +
        "    /// for THIS step only, with NO broker contacted.\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task ExpectAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string streamTemplate,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValueTemplates)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"connection string not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        StackExchange.Redis.ConnectionMultiplexer? mux = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every expected author-text field INSIDE the guarded region and\n" +
        "            // BEFORE creating the connection (§17).  This ordering means a missing\n" +
        "            // secret throws SecretResolutionException BEFORE any broker contact.\n" +
        "            var stream = Secret_Helpers.ResolveTemplate(secrets, vars, streamTemplate);\n" +
        "            var payloadContains = payloadContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var jsonValues = new string[jsonValueTemplates.Length];\n" +
        "            for (int ji = 0; ji < jsonValueTemplates.Length; ji++)\n" +
        "            {\n" +
        "                jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);\n" +
        "            }\n" +
        "            mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);\n" +
        "            var db = mux.GetDatabase();\n" +
        "            // XRANGE <stream> - + — scan the ENTIRE retained stream on every attempt (the\n" +
        "            // RETRY runner gets a clean slate each time, mirroring mq-expect.nats's\n" +
        "            // DeliverPolicy.All rationale).  A stream that does not yet exist returns an\n" +
        "            // empty array (no exception) — the first attempt before any publish is a\n" +
        "            // clean Fail, not an EnvironmentError.\n" +
        "            var entries = await db.StreamRangeAsync(stream, \"-\", \"+\").ConfigureAwait(false);\n" +
        "            int scanned = entries.Length;\n" +
        "            bool matched = false;\n" +
        "            for (int ei = 0; ei < entries.Length; ei++)\n" +
        "            {\n" +
        "                string? msgPayload = null;\n" +
        "                var values = entries[ei].Values;\n" +
        "                for (int vi = 0; vi < values.Length; vi++)\n" +
        "                {\n" +
        "                    if (values[vi].Name == \"payload\")\n" +
        "                    {\n" +
        "                        msgPayload = values[vi].Value.ToString();\n" +
        "                        break;\n" +
        "                    }\n" +
        "                }\n" +
        "                if (msgPayload is null)\n" +
        "                    continue;\n" +
        "                if (MatchesPayload(msgPayload, payloadContains, jsonPaths, jsonValues))\n" +
        "                {\n" +
        "                    matched = true;\n" +
        "                    break;\n" +
        "                }\n" +
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
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (StackExchange.Redis.RedisConnectionException ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (StackExchange.Redis.RedisTimeoutException ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): dispose the Redis connection before the collectible ALC unloads.\n" +
        "            if (mux is not null)\n" +
        "            {\n" +
        "                try { mux.Dispose(); } catch { }\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Evaluates one decoded payload against the (already-resolved) criteria.\n" +
        "    /// Criteria are conjunctive (logical AND); an absent criterion imposes no constraint.\n" +
        "    /// A non-JSON payload or a path that selects nothing fails the JSON criteria\n" +
        "    /// WITHOUT raising — JSON parse / JSONPath failures yield no match.\n" +
        "    /// </summary>\n" +
        "    private static bool MatchesPayload(\n" +
        "        string payload,\n" +
        "        string? payloadContains,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValues)\n" +
        "    {\n" +
        "        // (1) payload substring (ordinal) when set.\n" +
        "        if (payloadContains is not null && !payload.Contains(payloadContains, System.StringComparison.Ordinal))\n" +
        "            return false;\n" +
        "        // (2) each JSONPath must select a node whose stringified value equals the\n" +
        "        // expected value.  A non-JSON payload or a parse/evaluation failure yields\n" +
        "        // no match (guarded — never throws out of MatchesPayload).\n" +
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
        "    /// Redacts credential material from an exception message before it reaches the\n" +
        "    /// observation / event stream (§17).  Mirrors\n" +
        "    /// MqPublishRedis_Helpers.RedactCredentials / CacheAssertRedis_Helpers.RedactCredentials.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactCredentials(string connStr, string message)\n" +
        "    {\n" +
        "        if (!string.IsNullOrEmpty(connStr))\n" +
        "            message = message.Replace(connStr, \"***\", System.StringComparison.Ordinal);\n" +
        "        message = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            message,\n" +
        "            \"(?:password|pwd)\\\\s*=\\\\s*[^,;]+\",\n" +
        "            \"password=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        message = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            message,\n" +
        "            \"user\\\\s*=\\\\s*[^,;]+\",\n" +
        "            \"user=***\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return message;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqExpectRedisModel> ─────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqExpectRedisModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

        // Stream template: emitted as a RAW literal; resolved at runtime.
        var streamTemplateLiteral = JsonSerializer.Serialize(model.Stream);

        // payloadContains: null literal when absent; JSON-escaped template literal when present.
        var payloadContainsLiteral = match.PayloadContains is null
            ? "null"
            : JsonSerializer.Serialize(match.PayloadContains);

        // Expand json criteria into parallel path/value-template arrays.
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

        var jsonPathsLiteral = BuildStringArrayLiteral(jsonPaths);
        var jsonValueTemplatesLiteral = BuildStringArrayLiteral(jsonValueTemplates);

        var block = $$"""
            {
                await MqExpectRedis_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{streamTemplateLiteral}},
                    {{payloadContainsLiteral}},
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

    // ── IResourceContributor<MqExpectRedisModel> ──────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqExpectRedisModel model)
    {
        yield return new ResourceRequirement(
            Family: "redis",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly;
            // JsonPath.Net — Json.Path.JsonPath used in MatchesPayload.
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
