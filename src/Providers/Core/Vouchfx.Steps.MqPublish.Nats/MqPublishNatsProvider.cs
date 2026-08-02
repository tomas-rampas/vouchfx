// Vouchfx.Steps.MqPublish.Nats — mq-publish.nats step provider (DSL §5, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class implements
// all five provider interfaces plus ICompileReferenceContributor for the
// mq-publish.nats step kind.
//
// PLAIN-payload slice: the payload is a UTF-8 string (literal or inline JSON) published
// to a NATS JetStream stream via NATS.Net 2.x.  No Avro / schema-registry in v1.
// No message headers in v1 (simplification — focus on core JetStream publish).
//
// Substitution + secret model (canonical M2 pattern — mirrors http.rest, mq-publish.kafka):
//   The subject and payload are emitted as RAW template strings and passed to the helper.
//   Inside the helper's guarded try, each is resolved in a SINGLE pass via
//   Secret_Helpers.ResolveTemplate(secrets, vars, …), which handles BOTH {placeholder}
//   substitution AND ${secret:source/path} resolution over the original template text.
//   A missing secret throws SecretResolutionException → caught → Verdict.EnvironmentError
//   for THIS step only (step-scoped blast radius, never baked into IL — §17).
//
// Stream-name derivation: when the author omits the 'stream' field, the provider derives
//   a NATS-safe uppercase identifier from the subject at EMIT TIME (compile time) by
//   replacing non-alphanumeric characters with '_', collapsing consecutive underscores,
//   and trimming edge underscores.  The derived name is embedded as a string literal in
//   the StatementBlock and passed to the helper unchanged at runtime.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, subject, stream, payload).
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers contains
//     the full provider-id-prefixed static class definition; StatementBlock is a C# 11
//     $$"""…""" block; 'using var' is illegal.
//
// Memory model (§5) — the leak-critical concern:
//   • NatsConnection is IAsyncDisposable.  The emitted helper creates exactly one connection
//     per step and calls await conn.DisposeAsync().ConfigureAwait(false) in a finally block
//     so no connection survives the collectible AssemblyLoadContext.Unload().
//     'using var' is prohibited in CSX bodies (§13.3.1); disposal is always explicit.
//
// Credential redaction (§17):
//   • The NATS URL may embed user:pass (nats://user:pass@host or tls://user:pass@host).
//     Any caught exception whose message echoes the URL has its credentials stripped via
//     RedactNatsUrl (3-layer: literal, System.Uri userinfo, regex) before the observation
//     is written to Vars.
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Nats;

/// <summary>
/// Core provider for the <c>mq-publish.nats</c> step kind (DSL §5).
/// Publishes a single message (UTF-8 payload) to a declared NATS JetStream dependency
/// and writes a <see cref="StepOutcome"/> carrying the stream name and sequence number.
/// </summary>
[StepProvider]
public sealed class MqPublishNatsProvider
    : IStepProvider,
      IStepBinder<MqPublishNatsModel>,
      IStepValidator<MqPublishNatsModel>,
      IStepCompiler<MqPublishNatsModel>,
      IResourceContributor<MqPublishNatsModel>,
      ICompileReferenceContributor
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mq-publish", "nats");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MqPublishNatsModel> ───────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Publishes one UTF-8 message to a NATS JetStream subject.  A Pass verdict confirms the publish was accepted by the server (JetStream ack); delivery is NOT further confirmed.  Verify delivery with a following mq-expect.nats step.",
          "type": "object",
          "required": ["target", "subject", "payload"],
          "properties": {
            "target": {
              "description": "Logical name of the nats dependency to publish to, as declared under environment.dependencies.",
              "type": "string",
              "minLength": 1
            },
            "subject": {
              "description": "The NATS JetStream subject to publish to.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string",
              "minLength": 1
            },
            "stream": {
              "description": "Optional NATS JetStream stream name.  When absent, derived from 'subject' by uppercasing and replacing non-alphanumeric characters with underscores (consecutive underscores collapsed).",
              "type": "string"
            },
            "payload": {
              "description": "The message payload sent as UTF-8 bytes.  May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is sent as text either way.",
              "$comment": "minLength constrains the string branch of the type union only — a no-op against a number/boolean instance (JSON Schema draft 2020-12 §6.3.1); it still catches an empty-string payload, the meaningful case, regardless of the widening.",
              "type": ["string", "integer", "number", "boolean"],
              "minLength": 1
            }
          }
        }
        """);

    /// <inheritdoc />
    public MqPublishNatsModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MqPublishNatsModel(
                Target: string.Empty,
                Subject: string.Empty,
                Stream: null,
                Payload: string.Empty);
        }

        var target = GetScalar(mapping, "target");
        var subject = GetScalar(mapping, "subject");
        var payload = GetScalar(mapping, "payload");

        // 'stream' is optional: present as a scalar → its value; absent → null.
        string? stream = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("stream"), out var streamNode)
            && streamNode is YamlScalarNode streamScalar)
        {
            stream = streamScalar.Value ?? string.Empty;
        }

        return new MqPublishNatsModel(
            Target: target,
            Subject: subject,
            Stream: stream,
            Payload: payload);
    }

    // ── IStepValidator<MqPublishNatsModel> ────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MqPublishNatsModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mq-publish.nats: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Subject))
            errors.Add("mq-publish.nats: 'subject' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Payload))
            errors.Add("mq-publish.nats: 'payload' must not be empty.");

        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mq-publish.nats: 'target' '{model.Target}' is not a " +
                    "nats dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "nats", StringComparison.Ordinal))
            {
                errors.Add(
                    $"mq-publish.nats: 'target' '{model.Target}' is declared as a " +
                    $"'{depType}' dependency, not the required nats dependency.");
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
            "NATS.Client.Core",
            "NATS.Client.JetStream",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MqPublishNats_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// All types that are NOT in the s_usings namespaces are fully-qualified so the
    /// helper compiles correctly in the generated CSX.  <c>using var</c> is absent —
    /// explicit <c>await conn.DisposeAsync()</c> in a <c>finally</c> is used instead.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MqPublishNats_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Publishes one UTF-8 message to a NATS JetStream subject via NATS.Net 2.x\n" +
        "    /// and writes a typed StepOutcome into Vars.\n" +
        "    /// Missing NATS URL = EnvironmentError (§12.1).\n" +
        "    /// Successful JetStream ack = Pass (observation carries stream/seq).\n" +
        "    /// A missing secret, a NATS exception, or any other failure = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// LEAK GATE (§5): NatsConnection is IAsyncDisposable.  The connection is\n" +
        "    /// disposed via await conn.DisposeAsync().ConfigureAwait(false) in a finally\n" +
        "    /// block.  'using var' / 'await using var' are prohibited in CSX bodies\n" +
        "    /// (§13.3.1); disposal is always explicit in emitted helpers.\n" +
        "    /// Credential redaction: the NATS URL may embed user:pass; caught exception\n" +
        "    /// messages are sanitised (nats://user:pass@ -> nats://***@) before writing\n" +
        "    /// to the observation so no credentials reach the event stream.\n" +
        "    /// Subject and payload VALUES are resolved INSIDE the guarded region via\n" +
        "    /// Secret_Helpers.ResolveTemplate (§17).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task PublishAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string subjectTemplate,\n" +
        "        string streamName,\n" +
        "        string payloadTemplate,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        // No hard-coded transport timeout to lift here — the step token plus\n" +
        "        // the assembler's late supersession are the bound (#232).\n" +
        "        _ = budgetGoverned;\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        var natsUrl = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;\n" +
        "        if (string.IsNullOrEmpty(natsUrl))\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(\"NATS connection not found for key '\" + connKey + \"'\") + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        NATS.Client.Core.NatsConnection? conn = null;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every author-text field INSIDE the guarded region (§17) via\n" +
        "            // ResolveTemplate (single pass: {placeholder} substitution + ${secret} resolution).\n" +
        "            // A missing secret throws SecretResolutionException -> caught -> EnvironmentError.\n" +
        "            var subject = Secret_Helpers.ResolveTemplate(secrets, vars, subjectTemplate);\n" +
        "            var payload = Secret_Helpers.ResolveTemplate(secrets, vars, payloadTemplate);\n" +
        "            conn = new NATS.Client.Core.NatsConnection(new NATS.Client.Core.NatsOpts { Url = natsUrl });\n" +
        "            // NatsJSContext constructor: NatsJSContext(NatsConnection) — NATS.Net 2.7.x API.\n" +
        "            // The CreateJetStreamContext() extension method does not exist in 2.4.x.\n" +
        "            var js = new NATS.Client.JetStream.NatsJSContext(conn);\n" +
        "            // CreateStreamAsync in NATS.Net 2.7.x returns the existing stream when the name\n" +
        "            // matches (idempotent by design).  ErrCode 10058 ('stream name already in use')\n" +
        "            // is a safe-to-ignore race condition where two concurrent publish steps try to\n" +
        "            // create the same stream with different subject lists.  Any other ErrCode (e.g.\n" +
        "            // 10076 'JetStream not enabled') re-throws so the outer catch maps it to\n" +
        "            // EnvironmentError — FIX N6: do NOT swallow all JetStream errors.\n" +
        "            try\n" +
        "            {\n" +
        "                await js.CreateStreamAsync(\n" +
        "                    new NATS.Client.JetStream.Models.StreamConfig(streamName, new string[] { subject }),\n" +
        "                    ct).ConfigureAwait(false);\n" +
        "            }\n" +
        "            catch (NATS.Client.JetStream.NatsJSApiException ex) when (ex.Error.ErrCode == 10058) { }\n" +
        "            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);\n" +
        "            // PublishAsync<T> requires an explicit serializer (NATS.Net 2.4.0).\n" +
        "            // NatsRawSerializer<byte[]>.Default serialises/deserialises raw bytes verbatim.\n" +
        "            var ack = await js.PublishAsync<byte[]>(subject, payloadBytes,\n" +
        "                NATS.Client.Core.NatsRawSerializer<byte[]>.Default,\n" +
        "                opts: null,\n" +
        "                headers: null,\n" +
        "                cancellationToken: ct).ConfigureAwait(false);\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "            observation = \"{\\\"stream\\\":\" + System.Text.Json.JsonSerializer.Serialize(ack.Stream) +\n" +
        "                \",\\\"seq\\\":\" + ack.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (NATS.Client.Core.NatsException ex)\n" +
        "        {\n" +
        "            // NATS broker / connection / publish failure = EnvironmentError (§12.1).\n" +
        "            // Redact NATS credentials from any reflected URI in the error message.\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactNatsUrl(natsUrl ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this provider's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the connection-failure branch below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Any other connection / configuration failure = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(RedactNatsUrl(natsUrl ?? string.Empty, ex.Message)) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            // LEAK GATE (§5): NatsConnection is IAsyncDisposable.  Dispose within this\n" +
        "            // step, before the collectible ALC unloads.  Swallow disposal failures so\n" +
        "            // they do not mask the step outcome already captured above.\n" +
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
        "    /// Strips NATS credentials from an error message before it reaches the\n" +
        "    /// observation / event stream (§17).  Three-layer approach:\n" +
        "    ///   (a) Literal full-URI replacement (catches URIs echoed verbatim).\n" +
        "    ///   (b) Parsed-userinfo replacement (System.Uri), incl. password-only portion.\n" +
        "    ///   (c) Regex fallback: (nats|tls)s?://[^/\\s]*@ -> nats://***@.\n" +
        "    /// </summary>\n" +
        "    internal static string RedactNatsUrl(string natsUrl, string message)\n" +
        "    {\n" +
        "        var redacted = message ?? string.Empty;\n" +
        "        // (a) Literal full URI replacement.\n" +
        "        if (!string.IsNullOrEmpty(natsUrl))\n" +
        "            redacted = redacted.Replace(natsUrl, \"***\", System.StringComparison.Ordinal);\n" +
        "        // (b) Parsed userinfo replacement.\n" +
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
        "        // (c) Regex fallback — greedy [^/\\s]* matches past interior '@' to the LAST one.\n" +
        "        redacted = System.Text.RegularExpressions.Regex.Replace(\n" +
        "            redacted,\n" +
        "            \"(nats|tls)s?://[^/\\\\s]*@\",\n" +
        "            \"nats://***@\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.IgnoreCase);\n" +
        "        return redacted;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MqPublishNatsModel> ─────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MqPublishNatsModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Stream name is determined at emit time (compile time): use the authored name
        // when present, otherwise derive from the subject template literal.  The derived
        // name is embedded as a string literal in the StatementBlock so the helper
        // receives an already-resolved, NATS-safe stream identifier at runtime.
        var streamName = model.Stream ?? DeriveStreamName(model.Subject);
        var streamNameLiteral = JsonSerializer.Serialize(streamName);

        // Subject and payload are emitted as RAW template literals.  Any {placeholder}
        // or ${secret:…} token inside survives as LITERAL TEXT here and is processed at
        // runtime by Secret_Helpers.ResolveTemplate (§17).  Inside a $$"""…""" block,
        // {{expr}} is the interpolation hole; a lone {placeholder} passes through verbatim.
        var subjectTemplateLiteral = JsonSerializer.Serialize(model.Subject);
        var payloadTemplateLiteral = JsonSerializer.Serialize(model.Payload);

        var block = $$"""
            {
                await MqPublishNats_Helpers.PublishAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{subjectTemplateLiteral}},
                    {{streamNameLiteral}},
                    {{payloadTemplateLiteral}},
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

    // ── IResourceContributor<MqPublishNatsModel> ──────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(MqPublishNatsModel model)
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
            // NATS.Client.JetStream — NatsJSContext (NatsJSContext(NatsConnection) ctor),
            // Models.StreamConfig, NatsJSApiException.
            yield return typeof(NATS.Client.JetStream.NatsJSContext).Assembly;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Derives a NATS-safe stream name from a JetStream subject by uppercasing and
    /// replacing non-alphanumeric characters (other than <c>-</c>) with <c>_</c>,
    /// collapsing consecutive underscores, and trimming edge underscores.
    /// </summary>
    /// <example>
    /// <c>orders.created</c> → <c>ORDERS_CREATED</c><br/>
    /// <c>orders.{id}</c> → <c>ORDERS_ID</c>
    /// </example>
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

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
