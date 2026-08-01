// Vouchfx.Steps.MqPublish.Redis — mq-publish.redis step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-publish.redis step kind.
//
// Semantics: Redis Streams.  Publish = XADD to the declared stream key with the payload
// carried under one canonical field name, "payload" (a UTF-8 string).  XADD creates the
// stream automatically when it does not yet exist — unlike NATS JetStream there is no
// separate stream-creation step and no stream-name derivation from a "subject".
//
// Substitution + secret model (canonical M2 pattern — mirrors mq-publish.nats):
//   The stream key and payload are emitted as RAW template strings and passed to the
//   helper.  Inside the helper's guarded try, each is resolved in a SINGLE pass via
//   Secret_Helpers.ResolveTemplate(secrets, vars, …), which handles BOTH {placeholder}
//   substitution AND ${secret:source/path} resolution over the original template text.
//   A missing secret throws SecretResolutionException → caught → Verdict.EnvironmentError
//   for THIS step only (step-scoped blast radius, never baked into IL — §17).
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, stream, payload).
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers contains
//     the full provider-id-prefixed static class definition; StatementBlock is a C# 11
//     $$"""…""" block; 'using var' is illegal.
//
// Memory model (§5) — the leak-critical concern:
//   • ConnectionMultiplexer is IDisposable (mirrors cache-assert.redis exactly).  The
//     emitted helper creates exactly one multiplexer per step and Dispose()s it in a
//     finally block so no connection/heartbeat-timer/socket/reconnect-thread survives the
//     collectible AssemblyLoadContext.Unload().  'using var' is prohibited in CSX bodies
//     (§13.3.1); disposal is always explicit.
//
// Credential redaction (§17):
//   • A Redis connection string may embed password=/user= tokens.  Any caught exception
//     whose message echoes the connection string has its credentials stripped via
//     RedactCredentials (mirrors CacheAssertRedis_Helpers.RedactCredentials) before the
//     observation is written to Vars.
//
// Verdict taxonomy (§12.1): Pass on a successful XADD; EnvironmentError on a missing
// conn:: key, a missing secret, or any connection/protocol failure.  This provider never
// writes Inconclusive — that verdict is the RetryRunner's alone.
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Redis;

/// <summary>
/// Core provider for the <c>mq-publish.redis</c> step kind (DSL §5).
/// Publishes a single message (UTF-8 payload) to a declared Redis Streams dependency
/// via <c>XADD</c> and writes a <see cref="StepOutcome"/> carrying the stream key and
/// the generated entry id.
/// </summary>
[StepProvider]
public sealed class MqPublishRedisProvider
    : IStepProvider,
      IStepBinder<MqPublishRedisModel>,
      IStepValidator<MqPublishRedisModel>,
      IStepCompiler<MqPublishRedisModel>,
      IResourceContributor<MqPublishRedisModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-publish", "redis");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqPublishRedisModel> ──────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Publishes one UTF-8 message to a Redis Stream via XADD, carried under the canonical 'payload' stream field.  A Pass verdict confirms the entry was appended to the stream; delivery to a consumer is NOT further confirmed.  Verify delivery with a following mq-expect.redis step.",
          "type": "object",
          "required": ["target", "stream", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of the redis dependency to publish to, as declared under environment.dependencies.",
              "type": "string"
            },
            "stream": {
              "description": "The Redis Stream key to XADD to.  May contain {placeholder} and ${secret:source/path} tokens.  XADD creates the stream automatically when it does not yet exist.",
              "type": "string"
            },
            "payload": {
              "description": "The message payload, written as the UTF-8 string value of the canonical 'payload' stream field.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            }
          }
        }
        """);

    /// <inheritdoc />
    public MqPublishRedisModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqPublishRedisModel(
                Target: string.Empty,
                Stream: string.Empty,
                Payload: string.Empty);
        }

        var target = GetScalar(mapping, "target");
        var stream = GetScalar(mapping, "stream");
        var payload = GetScalar(mapping, "payload");

        return new MqPublishRedisModel(
            Target: target,
            Stream: stream,
            Payload: payload);
    }

    // ── IStepValidator<MqPublishRedisModel> ───────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqPublishRedisModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-publish.redis: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Stream))
            errors.Add("mq-publish.redis: 'stream' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Payload))
            errors.Add("mq-publish.redis: 'payload' must not be empty.");

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-publish.redis: 'target' '{model.Target}' is not a " +
                    "redis dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "redis", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"mq-publish.redis: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required redis dependency.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// StackExchange.Redis is deliberately absent: every Redis type below is
    /// fully-qualified (mirrors CacheAssertRedisProvider's s_usings exactly), and none
    /// of the Streams API members used here are extension methods, so no 'using' is
    /// required for them to resolve.
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MqPublishRedis_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// All types that are NOT in the s_usings namespaces are fully-qualified so the
    /// helper compiles correctly in the generated CSX.  <c>using var</c> is absent —
    /// explicit <c>Dispose()</c> in a <c>finally</c> is used instead.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqPublishRedis_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Publishes one UTF-8 message to a Redis Stream via XADD (carried under the\n" +
        "    /// canonical 'payload' field) and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing connection string = EnvironmentError (§12.1).\n" +
        "    /// Successful XADD = Pass (observation carries stream/id).\n" +
        "    /// A missing secret, a Redis exception, or any other failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): ConnectionMultiplexer is IDisposable.  The multiplexer is\n" +
        "    /// disposed via Dispose() in a finally block.  'using var' is prohibited in CSX\n" +
        "    /// bodies (§13.3.1); disposal is always explicit in emitted helpers.\n" +
        "    /// Credential redaction: the connection string may embed password=/user= tokens;\n" +
        "    /// caught exception messages are sanitised before writing to the observation so no\n" +
        "    /// credentials reach the event stream.\n" +
        "    /// Stream and payload VALUES are resolved INSIDE the guarded region via\n" +
        "    /// Secret_Helpers.ResolveTemplate (§17).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task PublishAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string streamTemplate,\n" +
        "        string payloadTemplate)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(connStr))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"connection string not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        StackExchange.Redis.ConnectionMultiplexer? mux = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every author-text field INSIDE the guarded region (§17) via\n" +
        "            // ResolveTemplate (single pass: {placeholder} substitution + ${secret} resolution).\n" +
        "            // A missing secret throws SecretResolutionException -> caught -> EnvironmentError.\n" +
        "            var stream = Secret_Helpers.ResolveTemplate(secrets, vars, streamTemplate);\n" +
        "            var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);\n" +
        "            mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);\n" +
        "            var db = mux.GetDatabase();\n" +
        "            // XADD <stream> * payload <payload> — the canonical single-field encoding\n" +
        "            // this family uses for the UTF-8 payload (documented on the model).  XADD\n" +
        "            // creates the stream automatically when it does not yet exist.\n" +
        "            var id = await db.StreamAddAsync(stream, \"payload\", payload).ConfigureAwait(false);\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"stream\\\":\" + System.Text.Json.JsonSerializer.Serialize(stream) +\n" +
        "                \",\\\"id\\\":\" + System.Text.Json.JsonSerializer.Serialize((string?)id) + \"}\";\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (StackExchange.Redis.RedisConnectionException ex)\n" +
        "        {\n" +
        "            // Connection failure = EnvironmentError (§12.1).  Redact credentials (§17).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (StackExchange.Redis.RedisTimeoutException ex)\n" +
        "        {\n" +
        "            // Operation timeout = EnvironmentError (§12.1).  Redact credentials (§17).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection/protocol/parse failure = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): ConnectionMultiplexer owns a heartbeat timer, a socket, and\n" +
        "            // a reconnect thread.  Dispose within this step, before the collectible ALC\n" +
        "            // unloads.  Swallow disposal failures so they do not mask the step outcome\n" +
        "            // already captured above.\n" +
        "            if (mux is not null)\n" +
        "            {\n" +
        "                try { mux.Dispose(); } catch { }\n" +
        "            }\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Redacts credential material from an exception message before it reaches the\n" +
        "    /// observation / event stream (§17).  Mirrors\n" +
        "    /// CacheAssertRedis_Helpers.RedactCredentials: Redis connection strings carry\n" +
        "    /// password=/user= tokens (comma-separated).  Removes: (1) the full connection\n" +
        "    /// string if it appears literally; (2) password=/pwd= key-value pairs up to the\n" +
        "    /// next comma or semicolon; (3) user= key-value pairs likewise.\n" +
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

    // ── IStepCompiler<MqPublishRedisModel> ────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqPublishRedisModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Stream and payload are emitted as RAW template literals.  Any {placeholder}
        // or ${secret:…} token inside survives as LITERAL TEXT here and is processed at
        // runtime by Secret_Helpers.ResolveTemplate (§17).  Inside a $$"""…""" block,
        // {{expr}} is the interpolation hole; a lone {placeholder} passes through verbatim.
        var streamTemplateLiteral = JsonSerializer.Serialize(model.Stream);
        var payloadTemplateLiteral = JsonSerializer.Serialize(model.Payload);

        var block = $$"""
            {
                await MqPublishRedis_Helpers.PublishAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{streamTemplateLiteral}},
                    {{payloadTemplateLiteral}});
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

    // ── IResourceContributor<MqPublishRedisModel> ─────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqPublishRedisModel model)
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
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
