// Vouchfx.Steps.MailExpect.Smtp — mail-expect.smtp step provider (DSL §5, §13).
//
// The NINTH Core provider (S?-F-01).  One [StepProvider] class implements six
// provider interfaces for the mail-expect.smtp step kind:
//   IStepProvider, IStepBinder<T>, IStepValidator<T>, IStepCompiler<T>,
//   IResourceContributor<T>, IStepDiffRenderer.
// ICompileReferenceContributor: the emitted helper uses System.Net.Http.HttpClient,
// System.Text.Json.JsonDocument, and System.Uri.EscapeDataString.  None of those
// assemblies is in the default TPA-only Roslyn reference set, so all three must be
// contributed explicitly: System.Net.Http, System.Text.Json, and System.Private.Uri
// (which defines System.Uri — type-forwarded from System.Runtime but Roslyn requires
// the actual defining assembly as a metadata reference to avoid CS1069).
//
// Architecture — Mailpit SMTP capture via HTTP API (DSL §5):
//   The dependency type "mailpit" (EnvironmentMapper) stands up an axllent/mailpit
//   container exposing:
//     • HTTP port 8025 — REST API (messages list, message detail, health)
//     • SMTP port 1025 — accepts email from the system under test
//   The engine stages the HTTP API URL at conn::<target> (VarKeys.Connection).
//   The emitted helper calls GET /api/v1/messages?limit=100 to enumerate messages
//   in the Mailpit inbox, filters by To / SubjectContains / BodyContains, and
//   counts matches.  BodyContains requires a second call: GET /api/v1/message/{ID}.
//
// RETRY model (§7): this provider is a verifyMode: RETRY consumer.  The emitted
//   helper performs an IDEMPOTENT scan, writing Pass if the count matches or Fail
//   if not.  It NEVER writes Inconclusive — the RetryRunner converts a sustained
//   Fail to Inconclusive on timeout (§12.1).
//
// Memory model (§5): the helper creates an HttpClient per invocation and Dispose()s
//   it in a finally (no 'using var' — §13.3.1 ban; plain var + explicit Dispose).
//   JsonDocument instances are similarly Dispose()d in finally blocks.
//   No static state bridges the Default/collectible ALC boundary.
//
// Substitution + secret model (canonical M2 pattern — mirrors webhook-listen.http /
// mq-expect.kafka):
//   • The match criteria (to / subjectContains / bodyContains) are emitted as RAW
//     template strings and passed to the helper.  Inside the helper's guarded try each
//     is resolved in a SINGLE pass via Secret_Helpers.ResolveTemplate(secrets, vars, …),
//     which handles BOTH {placeholder} substitution AND ${secret:source/path} resolution
//     over the original template text — at EXECUTION time, so no secret value is ever
//     baked into the emitted IL (§17).  A missing secret throws SecretResolutionException
//     → caught → Verdict.EnvironmentError for THIS step only, REFERENCE-ONLY (§17).
//
// CsxFragment rules (§13.3.1):
//   • RequiredUsings: bare namespace strings only (no inline 'using' lines).
//   • RequiredHelpers: 'static class MailExpectSmtp_Helpers' plus the shared
//     Substitute_Helpers and Secret_Helpers sources (byte-identical; CsxAssembler dedupes).
//   • StatementBlock: C# 11 $$"""…""" with {{expr}} interpolation holes; no 'using var'.
//   • SanitiseId applied to context.StepId before use in variable names.
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MailExpect.Smtp;

/// <summary>
/// Core provider for the <c>mail-expect.smtp</c> step kind (DSL §5).
/// Queries the Mailpit HTTP API to assert that a given number of messages
/// matching the declared criteria (To address, subject substring, body substring)
/// have been received.
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
/// CSX reads the Mailpit HTTP API endpoint staged at
/// <c>Vars[VarKeys.Connection(model.Target)]</c>, enumerates messages, counts those
/// matching the criteria, and writes a typed <see cref="StepOutcome"/> into
/// <c>Vars</c> for the runner to read after execution (§13.3.1).  The match values
/// (<c>to</c> / <c>subject-contains</c> / <c>body-contains</c>) are emitted as RAW
/// template literals and resolved at runtime inside the helper's guarded region via
/// <c>Secret_Helpers.ResolveTemplate</c> — both <c>{placeholder}</c> substitution and
/// <c>${secret:source/path}</c> resolution, so no secret value is ever baked into the
/// emitted IL (§17).
/// </para>
/// <para>
/// This is a <c>verifyMode: RETRY</c> provider (§7): the emitted scan is
/// IDEMPOTENT — a count mismatch yields <see cref="Verdict.Fail"/>, and the
/// engine-owned RetryRunner re-invokes the delegate and converts a sustained
/// Fail to <see cref="Verdict.Inconclusive"/> on timeout.  The helper never
/// writes Inconclusive.
/// </para>
/// </remarks>
[StepProvider]
public sealed class MailExpectSmtpProvider
    : IStepProvider,
      IStepBinder<MailExpectSmtpModel>,
      IStepValidator<MailExpectSmtpModel>,
      IStepCompiler<MailExpectSmtpModel>,
      IResourceContributor<MailExpectSmtpModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>System.Net.Http</c> (for <c>HttpClient</c>),
    /// <c>System.Text.Json</c> (for <c>JsonDocument</c> / <c>JsonSerializer</c>),
    /// and <c>System.Private.Uri</c> (for <c>System.Uri.EscapeDataString</c>).
    /// <c>System.Uri</c> is defined in <c>System.Private.Uri</c> with a type forwarder
    /// in <c>System.Runtime</c>; Roslyn resolves the forwarder but emits CS1069 unless
    /// the actual defining assembly is in the explicit reference list.
    /// All assemblies are already loaded in the Default ALC and must never be
    /// loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public System.Collections.Generic.IEnumerable<System.Reflection.Assembly>
        CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            yield return typeof(System.Text.Json.JsonDocument).Assembly;
            // System.Uri is defined in System.Private.Uri (type-forwarded from System.Runtime).
            // Roslyn requires the defining assembly to be an explicit metadata reference.
            yield return typeof(System.Uri).Assembly;
        }
    }

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("mail-expect", "smtp");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MailExpectSmtpModel> ──────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>mail-expect.smtp</c>
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
          "description": "Queries a Mailpit inbox and asserts that at least one (or a declared count of) captured message matches the declared criteria.",
          "type": "object",
          "required": ["target", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the mailpit dependency (declared under environment.dependencies) whose HTTP API this step queries.",
              "type": "string",
              "minLength": 1
            },
            "expect": {
              "description": "The expectation block: how many messages must match the criteria.",
              "type": "object",
              "required": ["match"],
              "properties": {
                "count": {
                  "description": "Expected number of matching messages.  When absent the step passes when at least one matching message exists. When written as a string it must be all digits (e.g. \"1\"); a non-digit string is always a mistake, since the value is never {placeholder}-substituted.",
                  "type": ["integer", "string"],
                  "minimum": 1,
                  "pattern": "^[0-9]+$"
                },
                "match": {
                  "description": "Criteria a message must satisfy.  At least one criterion must be declared.",
                  "type": "object",
                  "minProperties": 1,
                  "properties": {
                    "to": {
                      "description": "Expected recipient address (case-insensitive equality).  May contain {placeholder} and ${secret:source/path} tokens.",
                      "type": "string"
                    },
                    "subject-contains": {
                      "description": "Substring the message subject must contain (ordinal).  May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is matched as text either way.",
                      "type": ["string", "integer", "number", "boolean"]
                    },
                    "body-contains": {
                      "description": "Substring the plain-text body must contain (ordinal).  May contain {placeholder} and ${secret:source/path} tokens.  Fetching the body requires a second Mailpit API call per candidate message. May be written as a bare number/boolean scalar; it is matched as text either way.",
                      "type": ["string", "integer", "number", "boolean"]
                    }
                  },
                  "additionalProperties": false
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    public MailExpectSmtpModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MailExpectSmtpModel(
                Target: string.Empty,
                Expect: new MailExpectation(Match: new MailMatch()));
        }

        var target = GetScalar(mapping, "target");
        var expect = BindExpectation(mapping);

        return new MailExpectSmtpModel(Target: target, Expect: expect);
    }

    private static MailExpectation BindExpectation(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            || expectNode is not YamlMappingNode expectMap)
        {
            return new MailExpectation(Match: new MailMatch());
        }

        int? count = null;
        if (expectMap.Children.TryGetValue(new YamlScalarNode("count"), out var countNode)
            && countNode is YamlScalarNode countScalar
            && int.TryParse(countScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
        {
            count = parsedCount;
        }

        var match = BindMatch(expectMap);
        return new MailExpectation(Match: match, Count: count);
    }

    private static MailMatch BindMatch(YamlMappingNode expectMap)
    {
        if (!expectMap.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new MailMatch();
        }

        var to = GetOptionalScalar(matchMap, "to");
        var subjectContains = GetOptionalScalar(matchMap, "subject-contains");
        var bodyContains = GetOptionalScalar(matchMap, "body-contains");

        return new MailMatch(
            To: to,
            SubjectContains: subjectContains,
            BodyContains: bodyContains);
    }

    // ── IStepValidator<MailExpectSmtpModel> ───────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MailExpectSmtpModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("mail-expect.smtp: 'target' must not be empty.");

        // (b) target must be declared as a 'mailpit' dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"mail-expect.smtp: dependency '{model.Target}' is not declared " +
                    "under environment.dependencies.");
            }
            else if (!string.Equals(depType, "mailpit", StringComparison.Ordinal))
            {
                errors.Add(
                    $"mail-expect.smtp: dependency '{model.Target}' has type '{depType}', " +
                    "but mail-expect.smtp requires type 'mailpit'.");
            }
        }

        // (c) at least one match criterion must be declared.
        var match = model.Expect.Match;
        if (match.To is null && match.SubjectContains is null && match.BodyContains is null)
        {
            errors.Add(
                "mail-expect.smtp: 'expect.match' must declare at least one criterion " +
                "(to, subject-contains, or body-contains).");
        }

        // (d) count, when specified, must be ≥ 1.
        if (model.Expect.Count is < 1)
        {
            errors.Add(
                $"mail-expect.smtp: 'expect.count' must be at least 1 (got {model.Expect.Count}).");
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── IResourceContributor<MailExpectSmtpModel> ─────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields one <see cref="ResourceRequirement"/> of family <c>"mailpit"</c>
    /// whose <see cref="ResourceRequirement.Name"/> equals the model's
    /// <see cref="MailExpectSmtpModel.Target"/>.  The engine uses this to validate
    /// the dependency reconciliation (§13): the named dependency must be declared
    /// with type <c>mailpit</c> in the environment block.
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(MailExpectSmtpModel model)
    {
        yield return new ResourceRequirement(
            Family: "mailpit",
            Name: model.Target,
            Image: null);
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.Globalization",
        "System.Threading.Tasks",
        "Vouchfx.Engine.Abstractions",
    };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>MailExpectSmtp_</c> to prevent collisions
    /// when multiple providers contribute helpers to the same Roslyn submission.
    /// All types are fully qualified so the helper compiles independently of the
    /// spliced <c>using</c> ordering.  <c>using var</c> is absent — disposal is
    /// explicit in <c>finally</c> blocks (§13.3.1 ban on <c>using var</c> in CSX).
    /// </para>
    /// <para>
    /// IDEMPOTENT single scan (§7): the helper queries Mailpit's HTTP API once and
    /// writes <c>Pass</c> on a count match or <c>Fail</c> on a count mismatch.  It
    /// NEVER writes <c>Inconclusive</c> — the engine-owned RetryRunner re-invokes
    /// the delegate and performs the Fail→Inconclusive-on-timeout conversion.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class MailExpectSmtp_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Queries the Mailpit HTTP API for messages matching the declared\n" +
        "    /// criteria and writes a typed StepOutcome into Vars.\n" +
        "    /// Pass when expectedCount is null and at least one message matches, or\n" +
        "    /// when expectedCount has a value and the count equals that value exactly;\n" +
        "    /// Fail when the condition is not met (RETRY runner converts sustained Fail to\n" +
        "    /// Inconclusive on timeout — this helper NEVER writes Inconclusive, §7/§12.1).\n" +
        "    /// EnvironmentError when the Mailpit HTTP URL is absent or the API fails.\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// The match templates (to / subjectContains / bodyContains) are resolved INSIDE\n" +
        "    /// the guarded region, BEFORE any Mailpit API call, via Secret_Helpers.ResolveTemplate\n" +
        "    /// (single pass over the original template: both {placeholder} substitution AND\n" +
        "    /// ${secret:source/path} resolution, §17).  A missing secret throws\n" +
        "    /// SecretResolutionException → caught → EnvironmentError for THIS step only,\n" +
        "    /// reference-only (source/path, never the value).\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task ExpectAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string? toTemplate,\n" +
        "        string? subjectContainsTemplate,\n" +
        "        string? bodyContainsTemplate,\n" +
        "        int? expectedCount,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "        string observation = \"{\\\"error\\\":\\\"unexpected\\\"}\"; \n" +
        "        try\n" +
        "        {\n" +
        "            var baseUrl = vars.TryGetValue(connKey, out var u) && u is string us\n" +
        "                ? us.TrimEnd('/')\n" +
        "                : null;\n" +
        "            if (string.IsNullOrEmpty(baseUrl))\n" +
        "            {\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                observation = \"{\\\"mailpitError\\\":\\\"Mailpit HTTP endpoint not found for conn key '\" + connKey + \"'\\\"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                // Resolve every match template INSIDE the guarded region and BEFORE any\n" +
        "                // Mailpit API call, in a SINGLE pass via ResolveTemplate (both\n" +
        "                // {placeholder} substitution and ${secret:source/path} resolution over the\n" +
        "                // original template).  This ordering means a missing secret throws\n" +
        "                // SecretResolutionException before any HTTP contact, so the missing-secret\n" +
        "                // path is reachable with no Mailpit server present (§17).\n" +
        "                var to = toTemplate is null\n" +
        "                    ? null\n" +
        "                    : Secret_Helpers.ResolveTemplate(secrets, vars, toTemplate);\n" +
        "                var subjectContains = subjectContainsTemplate is null\n" +
        "                    ? null\n" +
        "                    : Secret_Helpers.ResolveTemplate(secrets, vars, subjectContainsTemplate);\n" +
        "                var bodyContains = bodyContainsTemplate is null\n" +
        "                    ? null\n" +
        "                    : Secret_Helpers.ResolveTemplate(secrets, vars, bodyContainsTemplate);\n" +
        "                System.Net.Http.HttpClient http = new System.Net.Http.HttpClient();\n" +
        "                // Step-timeout convention (#232): a declared step budget governs this\n" +
        "                // call — lift the transport bound (infinite) and let the step token\n" +
        "                // (ct) be the sole enforcement mechanism; otherwise keep the 30s\n" +
        "                // stall-window convention.\n" +
        "                http.Timeout = budgetGoverned\n" +
        "                    ? System.Threading.Timeout.InfiniteTimeSpan\n" +
        "                    : System.TimeSpan.FromSeconds(30);\n" +
        "                try\n" +
        "                {\n" +
        "                    // Scan cap: ?limit=100 bounds the inbox enumeration to the 100 most\n" +
        "                    // recent messages Mailpit returns per attempt (newest-first).\n" +
        "                    var listJson = await http.GetStringAsync(baseUrl + \"/api/v1/messages?limit=100\", ct).ConfigureAwait(false);\n" +
        "                    var listDoc = System.Text.Json.JsonDocument.Parse(listJson);\n" +
        "                    int matched = 0;\n" +
        "                    try\n" +
        "                    {\n" +
        "                        if (listDoc.RootElement.TryGetProperty(\"messages\", out var messages))\n" +
        "                        {\n" +
        "                            int count = messages.GetArrayLength();\n" +
        "                            for (int i = 0; i < count; i++)\n" +
        "                            {\n" +
        "                                var msg = messages[i];\n" +
        "                                bool ok = true;\n" +
        "                                if (to is not null)\n" +
        "                                {\n" +
        "                                    bool toFound = false;\n" +
        "                                    if (msg.TryGetProperty(\"To\", out var toArr))\n" +
        "                                    {\n" +
        "                                        for (int ti = 0; ti < toArr.GetArrayLength(); ti++)\n" +
        "                                        {\n" +
        "                                            if (toArr[ti].TryGetProperty(\"Address\", out var addrEl)\n" +
        "                                                && string.Equals(addrEl.GetString(), to, System.StringComparison.OrdinalIgnoreCase))\n" +
        "                                            { toFound = true; break; }\n" +
        "                                        }\n" +
        "                                    }\n" +
        "                                    if (!toFound) ok = false;\n" +
        "                                }\n" +
        "                                if (ok && subjectContains is not null)\n" +
        "                                {\n" +
        "                                    string? subj = msg.TryGetProperty(\"Subject\", out var sEl) ? sEl.GetString() : null;\n" +
        "                                    if (subj is null || !subj.Contains(subjectContains, System.StringComparison.Ordinal))\n" +
        "                                        ok = false;\n" +
        "                                }\n" +
        "                                if (ok && bodyContains is not null)\n" +
        "                                {\n" +
        "                                    string? msgId = msg.TryGetProperty(\"ID\", out var idEl) ? idEl.GetString() : null;\n" +
        "                                    if (msgId is null)\n" +
        "                                    {\n" +
        "                                        ok = false;\n" +
        "                                    }\n" +
        "                                    else\n" +
        "                                    {\n" +
        "                                        // Per-candidate body fetch is resilient: a transient 4xx/5xx or\n" +
        "                                        // parse failure for THIS candidate is treated as NON-matching\n" +
        "                                        // (skip it) — never a terminal step EnvironmentError.  Only a\n" +
        "                                        // failure of the list call (outer try) is terminal.\n" +
        "                                        try\n" +
        "                                        {\n" +
        "                                            var bodyJson = await http.GetStringAsync(\n" +
        "                                                baseUrl + \"/api/v1/message/\" + System.Uri.EscapeDataString(msgId),\n" +
        "                                                ct\n" +
        "                                            ).ConfigureAwait(false);\n" +
        "                                            var bodyDoc = System.Text.Json.JsonDocument.Parse(bodyJson);\n" +
        "                                            try\n" +
        "                                            {\n" +
        "                                                string? text = bodyDoc.RootElement.TryGetProperty(\"Text\", out var tEl)\n" +
        "                                                    ? tEl.GetString() : null;\n" +
        "                                                if (text is null || !text.Contains(bodyContains, System.StringComparison.Ordinal))\n" +
        "                                                    ok = false;\n" +
        "                                            }\n" +
        "                                            finally { bodyDoc.Dispose(); }\n" +
        "                                        }\n" +
        "                                        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "                                        {\n" +
        "                                            // Step-token cut (#232): rethrow past this per-candidate scan\n" +
        "                                            // loop so the assembler's wrapper classifies it as\n" +
        "                                            // Inconclusive(step-timeout) instead of the resilient\n" +
        "                                            // non-matching branch below silently swallowing it and\n" +
        "                                            // looping on to the next candidate.\n" +
        "                                            throw;\n" +
        "                                        }\n" +
        "                                        catch (System.Exception)\n" +
        "                                        {\n" +
        "                                            // This candidate's body could not be fetched or parsed — treat\n" +
        "                                            // it as non-matching and continue scanning the remaining ones.\n" +
        "                                            ok = false;\n" +
        "                                        }\n" +
        "                                    }\n" +
        "                                }\n" +
        "                                if (ok) matched++;\n" +
        "                            }\n" +
        "                        }\n" +
        "                    }\n" +
        "                    finally { listDoc.Dispose(); }\n" +
        "                    bool passed = expectedCount.HasValue ? (matched == expectedCount.Value) : (matched >= 1);\n" +
        "                    if (passed)\n" +
        "                    {\n" +
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "                        observation = \"{\\\"matched\\\":true,\\\"count\\\":\" + matched.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "                    }\n" +
        "                    else\n" +
        "                    {\n" +
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                        observation = \"{\\\"matched\\\":false,\\\"expected\\\":\" + (expectedCount.HasValue ? expectedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : \"null\")\n" +
        "                            + \",\\\"actual\\\":\" + matched.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "                    }\n" +
        "                }\n" +
        "                finally\n" +
        "                {\n" +
        "                    http.Dispose();\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing / unknown secret = EnvironmentError (§12.1): a run-environment\n" +
        "            // configuration problem, NOT a product defect.  REFERENCE-ONLY observation\n" +
        "            // (§17): the discrete source/path coordinates only — never the value (none\n" +
        "            // exists when resolution fails).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this provider's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the generic-error branch below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<MailExpectSmtpModel> ────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution calls
    /// <c>MailExpectSmtp_Helpers.ExpectAsync</c> with the model's target connection
    /// key, match criteria (RAW template literals), and expected count.  The helper
    /// queries Mailpit's HTTP API, counts matching messages, and writes a typed
    /// <see cref="StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c>.
    /// </para>
    /// <para>
    /// Substitution + secret model (canonical M2 pattern): the match values
    /// (<c>to</c> / <c>subject-contains</c> / <c>body-contains</c>) are emitted as RAW
    /// template literals (JSON-escaped C# string literals).  They are NOT pre-resolved
    /// at the call site — the helper resolves each in a single pass via
    /// <c>Secret_Helpers.ResolveTemplate</c> (both <c>{placeholder}</c> substitution and
    /// <c>${secret:source/path}</c> resolution) inside its guarded region, BEFORE any
    /// Mailpit call, so a missing secret maps to a step-scoped
    /// <see cref="Verdict.EnvironmentError"/> and no secret value is ever baked into the
    /// emitted IL (§17).
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>; the full
    /// <c>static class MailExpectSmtp_Helpers</c> definition plus the shared
    /// <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources in
    /// <see cref="CsxFragment.RequiredHelpers"/>; a single C# 11 <c>$$"""…"""</c>
    /// <see cref="CsxFragment.StatementBlock"/> with no <c>using var</c>; the step
    /// id sanitised via <c>CsxFragment.SanitiseId</c> before splicing.
    /// </para>
    /// </remarks>
    public CsxFragment Emit(MailExpectSmtpModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Expect.Match;

        // The match criteria are optional: emit the JSON-escaped RAW template literal when
        // present, or the bare 'null' literal when absent.  Any {placeholder} or ${secret:…}
        // token inside survives as LITERAL TEXT here (not an emit-time interpolation hole)
        // and is resolved at runtime inside the helper.  CRITICAL: inside a $$"""…""" block,
        // {{expr}} is the interpolation hole; a lone {placeholder} or ${secret:…} passes
        // through verbatim — so no secret value is ever baked into the emitted IL (§17).
        var toLiteral = match.To is null
            ? "null"
            : JsonSerializer.Serialize(match.To);
        var subjectLiteral = match.SubjectContains is null
            ? "null"
            : JsonSerializer.Serialize(match.SubjectContains);
        var bodyLiteral = match.BodyContains is null
            ? "null"
            : JsonSerializer.Serialize(match.BodyContains);

        var countLiteral = model.Expect.Count.HasValue
            ? model.Expect.Count.Value.ToString(CultureInfo.InvariantCulture)
            : "null";

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        // 'Secrets' is the ScriptGlobalVariables.Secrets instance property.
        var block = $$"""
            {
                await MailExpectSmtp_Helpers.ExpectAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{toLiteral}},
                    {{subjectLiteral}},
                    {{bodyLiteral}},
                    {{countLiteral}},
                    __stepCt_{{safeId}},
                    __stepBudgetGoverned_{{safeId}});
            }
            """;

        // Build the helpers list: MailExpectSmtp_Helpers + Substitute_Helpers +
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

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is a
    /// <c>mail-expect.smtp</c> Fail-observation shape that can be rendered as
    /// an expected-vs-observed diff.
    /// </summary>
    /// <remarks>
    /// Recognised shape: <c>{"matched":false,"expected":E,"actual":A}</c>.
    /// The Pass shape <c>{"matched":true,"count":N}</c> and the EnvironmentError
    /// shape <c>{"error":…}</c> or <c>{"mailpitError":…}</c> are intentionally
    /// NOT renderable — there is no expected-vs-observed diff to draw.
    /// </remarks>
    public bool CanRender(JsonElement observation) =>
        TryReadCountDiff(observation, out _, out _);

    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (!TryReadCountDiff(observation, out var expected, out var actual))
            return null;

        return RenderCountTable(expected, actual);
    }

    // ── IStepDiffRenderer helpers ─────────────────────────────────────────────

    /// <summary>
    /// Attempts to read the count-mismatch shape
    /// <c>{"matched":false,"expected":E,"actual":A}</c> from
    /// <paramref name="observation"/>.
    /// </summary>
    private static bool TryReadCountDiff(
        JsonElement observation,
        out int expected,
        out int actual)
    {
        expected = 0;
        actual = 0;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("matched", out var matchedEl)
            || matchedEl.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        if (!observation.TryGetProperty("expected", out var expectedEl)
            || !observation.TryGetProperty("actual", out var actualEl))
        {
            return false;
        }

        if (!expectedEl.TryGetInt32(out expected) || !actualEl.TryGetInt32(out actual))
            return false;

        return true;
    }

    /// <summary>
    /// Renders a plain-text expected-vs-observed count diff as a small two-row table.
    /// </summary>
    private static string RenderCountTable(int expected, int actual)
    {
        var col = 10;
        var hdr = "  " + "count".PadRight(col);
        var exp = "  " + "expected".PadRight(col) + expected.ToString(CultureInfo.InvariantCulture);
        var act = "  " + "actual".PadRight(col) + actual.ToString(CultureInfo.InvariantCulture);
        var sep = "  " + new string('-', col + 6);

        return string.Join(System.Environment.NewLine, new[] { hdr, sep, exp, act });
    }

    // ── Private YAML helpers ──────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }

    private static string? GetOptionalScalar(YamlMappingNode parent, string field)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(field), out var node)
            && node is YamlScalarNode scalar)
        {
            return scalar.Value ?? string.Empty;
        }
        return null;
    }
}
