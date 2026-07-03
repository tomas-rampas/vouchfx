// Platform.Steps.MqExpect.Nats — mq-expect.nats step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-expect.nats step kind.
//
// PLAIN-payload slice: match criteria are evaluated against the UTF-8 message payload
// via optional substring check and/or JSONPath evaluation.  No key / headers / Avro in v1.
//
// RETRY model (§7): this provider is the primary verifyMode: RETRY consumer.  The engine
// wraps a RETRY step in a polling loop, so the emitted helper performs an IDEMPOTENT
// SINGLE poll: it creates an ephemeral consumer per attempt (DeliverPolicy.All, scanning
// from the beginning), fetches all available messages, and writes Pass or Fail.  It
// contains NO retry logic — the RetryRunner re-invokes the delegate and converts a
// sustained Fail to Inconclusive on timeout (§12.1).  The helper NEVER writes Inconclusive.
//
// Substitution + secret model (canonical M2 pattern):
//   The payloadContains template and json expected VALUES are emitted as RAW template
//   strings and resolved BEFORE any broker contact via Secret_Helpers.ResolveTemplate
//   (§17).  A missing secret throws SecretResolutionException → caught → EnvironmentError
//   with no consumer ever created.
//
// Memory model (§5):
//   NatsConnection is IAsyncDisposable.  The emitted helper disposes the connection via
//   await conn.DisposeAsync().ConfigureAwait(false) in a finally.  'using var' is
//   prohibited in CSX bodies (§13.3.1); disposal is always explicit.
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqExpect.Nats;

/// <summary>
/// Core provider for the <c>mq-expect.nats</c> step kind (DSL §5).
/// Consumes from a declared NATS JetStream dependency and asserts that a message
/// matching the declared criteria (payloadContains and/or JSONPath) is present.
/// </summary>
[StepProvider]
public sealed class MqExpectNatsProvider
    : IStepProvider,
      IStepBinder<MqExpectNatsModel>,
      IStepValidator<MqExpectNatsModel>,
      IStepCompiler<MqExpectNatsModel>,
      IResourceContributor<MqExpectNatsModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-expect", "nats");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqExpectNatsModel> ────────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "subject", "match"],
          "properties": {
            "target": {
              "description": "Logical name of the nats dependency to consume from, as declared under environment.dependencies.",
              "type": "string"
            },
            "subject": {
              "description": "The NATS JetStream subject to filter messages on.",
              "type": "string"
            },
            "stream": {
              "description": "Optional JetStream stream name.  When absent, derived from 'subject' (same rule as mq-publish.nats).",
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
    public MqExpectNatsModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqExpectNatsModel(
                Target: string.Empty,
                Subject: string.Empty,
                Stream: null,
                Match: new NatsMatch(PayloadContains: null, Json: null));
        }

        var target = GetScalar(mapping, "target");
        var subject = GetScalar(mapping, "subject");

        string? stream = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("stream"), out var streamNode)
            && streamNode is YamlScalarNode streamScalar)
        {
            stream = streamScalar.Value ?? string.Empty;
        }

        var match = BindMatch(mapping);

        return new MqExpectNatsModel(
            Target: target,
            Subject: subject,
            Stream: stream,
            Match: match);
    }

    /// <summary>
    /// Parses the nested <c>match</c> mapping into a <see cref="NatsMatch"/>.
    /// Tolerant of an absent or non-mapping <c>match</c> node — both yield an
    /// all-<see langword="null"/> match (which validation then rejects).
    /// </summary>
    private static NatsMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new NatsMatch(PayloadContains: null, Json: null);
        }

        string? payloadContains = null;
        if (matchMap.Children.TryGetValue(new YamlScalarNode("payloadContains"), out var pcNode)
            && pcNode is YamlScalarNode pcScalar)
        {
            payloadContains = pcScalar.Value ?? string.Empty;
        }

        IReadOnlyDictionary<string, string>? json = BindStringMap(matchMap, "json");

        return new NatsMatch(
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

    // ── IStepValidator<MqExpectNatsModel> ─────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqExpectNatsModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-expect.nats: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Subject))
            errors.Add("mq-expect.nats: 'subject' must not be empty.");

        if (!HasAnyCriterion(model.Match))
        {
            errors.Add(
                "mq-expect.nats: 'match' must declare at least one criterion " +
                "(payloadContains or json).");
        }

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-expect.nats: 'target' '{model.Target}' is not a " +
                    "nats dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "nats", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-expect.nats: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required nats dependency.");
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
    private static bool HasAnyCriterion(NatsMatch match)
        => match.PayloadContains is not null
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
            "NATS.Client.Core",
            "NATS.Client.JetStream",
            "Platform.Engine.Abstractions",
        };

    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqExpectNats_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Performs ONE idempotent fetch over a NATS JetStream subject via an ephemeral\n" +
        "    /// consumer (DeliverPolicy.All) and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing NATS URL = EnvironmentError (§12.1).\n" +
        "    /// A matching message = Pass; no match this attempt = Fail (the RETRY runner\n" +
        "    /// retries on non-Pass and converts a sustained Fail to Inconclusive on\n" +
        "    /// timeout — this helper NEVER writes Inconclusive, §7/§12.1).\n" +
        "    /// A missing secret or a NATS/connection failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): NatsConnection is IAsyncDisposable.  The connection is disposed\n" +
        "    /// via await conn.DisposeAsync().ConfigureAwait(false) in a finally block.\n" +
        "    /// 'using var' / 'await using var' are prohibited in CSX bodies (§13.3.1).\n" +
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
        "        string subjectTemplate,\n" +
        "        string streamName,\n" +
        "        string? payloadContainsTemplate,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonValueTemplates)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var natsUrl = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(natsUrl))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
        "                Platform.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"NATS connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        NATS.Client.Core.NatsConnection? conn = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every expected author-text field INSIDE the guarded region and\n" +
        "            // BEFORE creating the connection (§17).  This ordering means a missing\n" +
        "            // secret throws SecretResolutionException BEFORE any broker contact.\n" +
        "            var subject = Secret_Helpers.ResolveTemplate(secrets, vars, subjectTemplate);\n" +
        "            var payloadContains = payloadContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, payloadContainsTemplate);\n" +
        "            var jsonValues = new string[jsonValueTemplates.Length];\n" +
        "            for (int ji = 0; ji < jsonValueTemplates.Length; ji++)\n" +
        "            {\n" +
        "                jsonValues[ji] = Secret_Helpers.ResolveTemplate(secrets, vars, jsonValueTemplates[ji]);\n" +
        "            }\n" +
        "            conn = new NATS.Client.Core.NatsConnection(new NATS.Client.Core.NatsOpts { Url = natsUrl });\n" +
        "            // NatsJSContext constructor: NatsJSContext(NatsConnection) — NATS.Net 2.4.0 API.\n" +
        "            // CreateJetStreamContext() extension method does not exist in 2.4.x.\n" +
        "            var js = new NATS.Client.JetStream.NatsJSContext(conn);\n" +
        "            // Ensure the stream exists (idempotent — if the publish step ran first it\n" +
        "            // already exists; if this step runs first in a RETRY scenario, creating it\n" +
        "            // here gives publish a chance to land before the next attempt).\n" +
        "            // StreamConfig is in NATS.Client.JetStream.Models (NATS.Net 2.4.0).\n" +
        "            // CreateOrUpdateStreamAsync does not exist; swallow NatsJSApiException for\n" +
        "            // 'stream already exists' errors so the step is idempotent.\n" +
        "            try\n" +
        "            {\n" +
        "                await js.CreateStreamAsync(\n" +
        "                    new NATS.Client.JetStream.Models.StreamConfig(streamName, new string[] { subject }),\n" +
        "                    System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            }\n" +
        "            catch (NATS.Client.JetStream.NatsJSApiException) { }\n" +
        "            // Ephemeral consumer: DeliverPolicy.All scans from the beginning of the\n" +
        "            // retained log on every attempt, giving the RETRY runner a clean slate.\n" +
        "            // ConsumerConfig and its policy enums are in NATS.Client.JetStream.Models.\n" +
        "            var consumerName = \"vouchfx-\" + System.Guid.NewGuid().ToString(\"n\");\n" +
        "            var consumer = await js.CreateOrUpdateConsumerAsync(streamName,\n" +
        "                new NATS.Client.JetStream.Models.ConsumerConfig\n" +
        "                {\n" +
        "                    Name = consumerName,\n" +
        "                    DeliverPolicy = NATS.Client.JetStream.Models.ConsumerConfigDeliverPolicy.All,\n" +
        "                    FilterSubject = subject,\n" +
        "                    AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit,\n" +
        "                    MaxDeliver = 1,\n" +
        "                }, System.Threading.CancellationToken.None).ConfigureAwait(false);\n" +
        "            int scanned = 0;\n" +
        "            bool matched = false;\n" +
        "            // FetchAsync<T> requires an explicit deserializer (NATS.Net 2.4.0).\n" +
        "            // NatsRawSerializer<byte[]>.Default handles raw byte[] without transformation.\n" +
        "            await foreach (var msg in consumer.FetchAsync<byte[]>(\n" +
        "                new NATS.Client.JetStream.NatsJSFetchOpts\n" +
        "                {\n" +
        "                    MaxMsgs = 10000,\n" +
        "                    Expires = System.TimeSpan.FromSeconds(1),\n" +
        "                }, NATS.Client.Core.NatsRawSerializer<byte[]>.Default,\n" +
        "                System.Threading.CancellationToken.None))\n" +
        "            {\n" +
        "                if (msg.Error is not null)\n" +
        "                    break;\n" +
        "                scanned++;\n" +
        "                var msgPayload = System.Text.Encoding.UTF8.GetString(msg.Data ?? System.Array.Empty<byte>());\n" +
        "                if (MatchesPayload(msgPayload, payloadContains, jsonPaths, jsonValues))\n" +
        "                {\n" +
        "                    matched = true;\n" +
        "                    break;\n" +
        "                }\n" +
        "                try { await msg.AckAsync(opts: null, cancellationToken: System.Threading.CancellationToken.None).ConfigureAwait(false); } catch { }\n" +
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
        "        catch (NATS.Client.Core.NatsException ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactNatsUrl(natsUrl ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactNatsUrl(natsUrl ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): dispose the NATS connection before the collectible ALC unloads.\n" +
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
        "    /// Strips NATS credentials from an error message before it reaches the\n" +
        "    /// observation / event stream (§17).  Three-layer approach:\n" +
        "    ///   (a) Literal full-URI replacement.\n" +
        "    ///   (b) Parsed-userinfo replacement (System.Uri), incl. password-only portion.\n" +
        "    ///   (c) Regex fallback: (nats|tls)s?://[^/\\s]*@ -> nats://***@.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactNatsUrl(string natsUrl, string message)\n" +
        "    {\n" +
        "        var redacted = message ?? string.Empty;\n" +
        "        if (!string.IsNullOrEmpty(natsUrl))\n" +
        "            redacted = redacted.Replace(natsUrl, \"***\", System.StringComparison.Ordinal);\n" +
        "        try\n" +
        "        {\n" +
        "            var __uri = new System.Uri(natsUrl);\n" +
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
        "        redacted = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            redacted,\n" +
        "            \"(nats|tls)s?://[^/\\\\s]*@\",\n" +
        "            \"nats://***@\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return redacted;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqExpectNatsModel> ──────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqExpectNatsModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

        // Stream name: derived at emit time from the subject template (same rule as publish).
        var streamName = model.Stream ?? DeriveStreamName(model.Subject);
        var streamNameLiteral = JsonSerializer.Serialize(streamName);

        // Subject template: emitted as a RAW literal; resolved at runtime.
        var subjectTemplateLiteral = JsonSerializer.Serialize(model.Subject);

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
                await MqExpectNats_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{subjectTemplateLiteral}},
                    {{streamNameLiteral}},
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

    // ── IResourceContributor<MqExpectNatsModel> ───────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqExpectNatsModel model)
    {
        yield return new ResourceRequirement(
            Family: "nats",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            // NATS.Client.Core — NatsConnection, NatsOpts, NatsException.
            yield return typeof(NATS.Client.Core.NatsConnection).Assembly;
            // NATS.Client.JetStream — NatsJSContext, StreamConfig, ConsumerConfig,
            // NatsJSFetchOpts, NatsJSMsg<T>.
            yield return typeof(NATS.Client.JetStream.NatsJSContext).Assembly;
            // JsonPath.Net — Json.Path.JsonPath used in MatchesPayload.
            yield return typeof(Json.Path.JsonPath).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string DeriveStreamName(string subject)
    {
        var sb = new System.Text.StringBuilder(subject.Length);
        foreach (char c in subject)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        var raw = sb.ToString().ToUpperInvariant();
        while (raw.Contains("__"))
            raw = raw.Replace("__", "_");
        return raw.Trim('_');
    }

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
