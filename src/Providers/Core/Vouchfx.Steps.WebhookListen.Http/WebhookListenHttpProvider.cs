// Vouchfx.Steps.WebhookListen.Http — webhook-listen.http step provider (DSL §5, §13).
//
// The SIXTH and final Core provider (S07-F-01b).  One [StepProvider] class implements the
// binder / validator / compiler interfaces plus the OPTIONAL IHostResourceContributor<TModel>
// for the webhook-listen.http step kind.  It is the asserting half of the C1 host-listener
// foundation (S07-F-01a): C1 stands up the listener + stages its URL; this provider declares
// the listener (so C1 stands it up) and reads its captures at a later assertion step.
//
// RETRY model (§7): this is a verifyMode: RETRY consumer, exactly like mq-expect.kafka.  The
// engine wraps a RETRY step in a polling loop, so the emitted helper performs an IDEMPOTENT
// SINGLE scan of the captured-request buffer and writes a non-Pass StepOutcome (Fail) when no
// captured request matches THIS attempt.  It contains NO retry logic — the RetryRunner
// re-invokes the delegate and converts a sustained Fail to Inconclusive on timeout (§12.1).
// The helper NEVER writes Inconclusive.
//
// Substitution + secret model (canonical M2 pattern — mirrors mq-expect / http.rest):
//   • The expected method, path, bodyContains, and each header VALUE are emitted as RAW
//     template strings and passed to the helper.  Inside the helper's guarded try each is
//     resolved in a SINGLE pass via Secret_Helpers.ResolveTemplate(secrets, vars, …), which
//     handles BOTH {placeholder} substitution AND ${secret:source/path} resolution over the
//     original template text.  A missing secret throws SecretResolutionException → caught →
//     Verdict.EnvironmentError for THIS step only, REFERENCE-ONLY (§17).
//
// SECURITY — captured data is UNTRUSTED and outside SecretString redaction (the C1 review
// flag, §17):  the SUT may echo a secret into a webhook body or header value, and that
// captured data has no redaction guarantee.  The observation therefore carries ONLY match
// METADATA — {"matched":…,"scanned":N} plus, on a miss, WHICH criteria failed BY NAME — and
// NEVER any captured request body or header value verbatim.  Capturing the data in the
// host buffer is fine; emitting it to the event stream is the leak we must not commit.
//
// Memory model (§5): this provider owns NOTHING disposable.  The listener and its capture
// buffer are host-owned (Default ALC); the emitted helper reaches them only by-reference via
// the IWebhookCaptureAccessor passed in from ScriptGlobalVariables.Webhooks.  There is no
// native handle and no consumer to Close()/Dispose(); the helper is therefore simpler than
// mq-expect's (no finally-disposal, no 'using var' concern beyond the §13.3.1 ban).
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.WebhookListen.Http;

/// <summary>
/// Core provider for the <c>webhook-listen.http</c> step kind (DSL §5) — the asserting half
/// of the host-owned webhook-listener foundation (S07-F-01a/b).
/// </summary>
/// <remarks>
/// <para>
/// The provider declares an ephemeral host-side HTTP listener (via
/// <see cref="IHostResourceContributor{TModel}"/>) keyed by the model's
/// <see cref="WebhookListenHttpModel.Listener"/> name; the engine stands it up before any
/// step runs and stages its bound URL at <c>svc::&lt;Listener&gt;</c> (and at the plain
/// <c>Vars[&lt;Listener&gt;]</c> key so an earlier step can interpolate
/// <c>{&lt;Listener&gt;}</c> to hand the callback URL to the system under test).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose emitted CSX reads
/// the listener's captured inbound requests via the
/// <c>ScriptGlobalVariables.Webhooks</c> accessor, evaluates each against the resolved
/// criteria, and writes a typed <see cref="Vouchfx.Engine.Abstractions.StepOutcome"/> into
/// <c>Vars</c> for the runner to read after execution.  The expected method, path,
/// bodyContains, and header values are emitted as RAW template literals and resolved at
/// runtime inside the helper's guarded region via <c>Secret_Helpers.ResolveTemplate</c>
/// (both <c>{placeholder}</c> substitution and <c>${secret:source/path}</c> resolution, §17).
/// </para>
/// <para>
/// This is a <c>verifyMode: RETRY</c> provider (§7): the emitted scan is IDEMPOTENT — a
/// non-match yields <see cref="Vouchfx.Engine.Abstractions.Verdict.Fail"/>, and the
/// engine-owned RetryRunner re-invokes the delegate and converts a sustained Fail to
/// <see cref="Vouchfx.Engine.Abstractions.Verdict.Inconclusive"/> on timeout.  The helper
/// never writes Inconclusive.
/// </para>
/// <para>
/// <strong>SECURITY (§17):</strong> the structured observation carries ONLY match metadata
/// (<c>matched</c> / <c>scanned</c> / which criteria <c>failed</c> by name) and NEVER any
/// captured request body or header value — captured data is untrusted (the SUT may echo a
/// secret into it) and is outside <c>SecretString</c> redaction.
/// </para>
/// </remarks>
[StepProvider]
public sealed class WebhookListenHttpProvider
    : IStepProvider,
      IStepBinder<WebhookListenHttpModel>,
      IStepValidator<WebhookListenHttpModel>,
      IStepCompiler<WebhookListenHttpModel>,
      IHostResourceContributor<WebhookListenHttpModel>
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("webhook-listen", "http");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<WebhookListenHttpModel> ───────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>webhook-listen.http</c>
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
          "description": "Asserts that a captured inbound HTTP request against a host-owned webhook listener matches the declared criteria (method, path, headers, and/or body substring).",
          "type": "object",
          "required": ["listener", "match"],
          "properties": {
            "listener": {
              "description": "Logical name of the host-owned webhook listener whose captured inbound requests this step asserts against.  The engine stands the listener up and stages its URL at svc::<listener> (and at the plain <listener> Vars key so an earlier step can interpolate {<listener>}).",
              "type": "string",
              "minLength": 1
            },
            "match": {
              "description": "The criteria a captured inbound request must satisfy.  At least one criterion (method, path, headers, or bodyContains) must be declared.",
              "type": "object",
              "minProperties": 1,
              "properties": {
                "method": {
                  "description": "Optional expected HTTP method (case-insensitive equality).  May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "path": {
                  "description": "Optional expected request path (the token-stripped, SUT-facing path; ordinal equality).  May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "headers": {
                  "description": "Optional map of expected header names to their expected string values.",
                  "type": "object",
                  "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
                },
                "bodyContains": {
                  "description": "Optional substring the captured request body must contain (ordinal).  May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is matched as text either way.",
                  "type": ["string", "integer", "number", "boolean"]
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    public WebhookListenHttpModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new WebhookListenHttpModel(
                Listener: string.Empty,
                Match: new WebhookMatch(Method: null, Path: null, Headers: null, BodyContains: null));
        }

        var listener = GetScalar(mapping, "listener");
        var match = BindMatch(mapping);

        return new WebhookListenHttpModel(Listener: listener, Match: match);
    }

    /// <summary>
    /// Parses the nested <c>match</c> mapping into a <see cref="WebhookMatch"/>.
    /// Tolerant of an absent or non-mapping <c>match</c> node — both yield an
    /// all-<see langword="null"/> match (which validation then rejects).
    /// </summary>
    private static WebhookMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new WebhookMatch(Method: null, Path: null, Headers: null, BodyContains: null);
        }

        var method = GetOptionalScalar(matchMap, "method");
        var path = GetOptionalScalar(matchMap, "path");
        var bodyContains = GetOptionalScalar(matchMap, "bodyContains");
        var headers = BindStringMap(matchMap, "headers");

        return new WebhookMatch(
            Method: method,
            Path: path,
            Headers: headers,
            BodyContains: bodyContains);
    }

    /// <summary>
    /// Reads an optional scalar field: present as a scalar → its value (empty string for
    /// a null scalar); absent or non-scalar → <see langword="null"/>.
    /// </summary>
    private static string? GetOptionalScalar(YamlMappingNode parent, string field)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(field), out var node)
            && node is YamlScalarNode scalar)
        {
            return scalar.Value ?? string.Empty;
        }
        return null;
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

    // ── IStepValidator<WebhookListenHttpModel> ────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// No dependency reconciliation is performed: the listener is a HOST resource this
    /// provider itself contributes (via <see cref="IHostResourceContributor{TModel}"/>),
    /// not a declared <c>environment.dependencies</c> entry the author must reconcile.
    /// </remarks>
    public ValidationResult Validate(WebhookListenHttpModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) listener must not be empty.
        if (string.IsNullOrWhiteSpace(model.Listener))
            errors.Add("webhook-listen.http: 'listener' must not be empty.");

        // (b) match must declare at least one criterion.
        if (!HasAnyCriterion(model.Match))
        {
            errors.Add(
                "webhook-listen.http: 'match' must declare at least one criterion " +
                "(method, path, headers, or bodyContains).");
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="match"/> declares at least
    /// one effective criterion: a non-null method, a non-null path, a non-empty headers
    /// map, or a non-null bodyContains.
    /// </summary>
    private static bool HasAnyCriterion(WebhookMatch match)
        => match.Method is not null
           || match.Path is not null
           || match.BodyContains is not null
           || (match.Headers is { Count: > 0 });

    // ── IHostResourceContributor<WebhookListenHttpModel> ──────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="HostResourceRequirement"/> of kind
    /// <c>"webhook-listener"</c> named by <see cref="WebhookListenHttpModel.Listener"/>.
    /// This is what makes the engine stand up the ephemeral host-owned HTTP listener and
    /// stage its URL before step 1 (S07-F-01a).  No <see cref="IResourceContributor{TModel}"/>
    /// is implemented: the listener is an in-process host resource, not a containerised
    /// Aspire dependency, so this provider contributes NO Aspire container.
    /// </remarks>
    public IEnumerable<HostResourceRequirement> HostResources(WebhookListenHttpModel model)
    {
        yield return new HostResourceRequirement("webhook-listener", model.Listener);
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
            "Vouchfx.Engine.Abstractions",
            "Vouchfx.Engine.Abstractions.Webhooks",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>WebhookListenHttp_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.  All types are
    /// fully-qualified so the helper compiles independently of the spliced <c>using</c>
    /// ordering.  <c>using var</c> is absent — this provider owns nothing to dispose, so no
    /// disposal pattern is needed at all (the host owns the listener + buffer, §5).
    /// </para>
    /// <para>
    /// IDEMPOTENT single scan (§7): the helper reads the captured-request snapshot once and
    /// writes <c>Pass</c> on a match or <c>Fail</c> on a non-match.  It NEVER writes
    /// <c>Inconclusive</c> — the engine-owned RetryRunner re-invokes the delegate and performs
    /// the Fail→Inconclusive-on-timeout conversion.
    /// </para>
    /// <para>
    /// SECURITY (§17): the observation carries ONLY match metadata
    /// (<c>matched</c>/<c>scanned</c>/which criteria <c>failed</c> by NAME) and never a
    /// captured request body or header value — captured data is untrusted and outside
    /// <c>SecretString</c> redaction.
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider within a
    /// suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class WebhookListenHttp_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Performs ONE idempotent scan over a host-owned webhook listener's captured\n" +
        "    /// inbound requests and writes a typed StepOutcome into Vars.\n" +
        "    /// A captured request matching ALL declared criteria = Pass; none match this\n" +
        "    /// attempt = Fail (the RETRY runner retries on non-Pass and converts a sustained\n" +
        "    /// Fail to Inconclusive on timeout — this helper NEVER writes Inconclusive,\n" +
        "    /// §7/§12.1).  A missing secret = EnvironmentError, reference-only (§12.1/§17).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// The provider owns NOTHING disposable: the listener and its capture buffer are\n" +
        "    /// host-owned (Default ALC) and reached only by-reference through the webhooks\n" +
        "    /// accessor.  There is no consumer/handle and no finally-disposal (§5).\n" +
        "    /// Expected method / path / bodyContains / header VALUES are resolved INSIDE the\n" +
        "    /// guarded region via Secret_Helpers.ResolveTemplate (single pass over the original\n" +
        "    /// template: both {placeholder} substitution AND ${secret:source/path} resolution,\n" +
        "    /// §17).  A missing secret throws SecretResolutionException → caught →\n" +
        "    /// EnvironmentError for THIS step only.\n" +
        "    /// SECURITY (§17): the observation carries ONLY match metadata (matched/scanned and,\n" +
        "    /// on a miss, the names of the failed criteria) — NEVER a captured request body or\n" +
        "    /// header value, which are untrusted (the SUT may echo a secret into them) and\n" +
        "    /// outside SecretString redaction.\n" +
        "    /// </remarks>\n" +
        "    public static async System.Threading.Tasks.Task AssertAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        Vouchfx.Engine.Abstractions.Webhooks.IWebhookCaptureAccessor webhooks,\n" +
        "        string outcomeKey,\n" +
        "        string listenerVarName,\n" +
        "        string? methodExpected,\n" +
        "        string? pathExpected,\n" +
        "        string? bodyContainsTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        System.Threading.CancellationToken ct)\n" +
        "    {\n" +
        "        // No I/O is performed here — the scan is purely over the in-memory snapshot —\n" +
        "        // but the method is async to match the awaited call-site shape used by every\n" +
        "        // RETRY consumer (the emitted block 'await's it).\n" +
        "        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        try\n" +
        "        {\n" +
        "            // Resolve every expected author-text field INSIDE the guarded region, in a\n" +
        "            // SINGLE pass via ResolveTemplate (both {placeholder} substitution and\n" +
        "            // ${secret:source/path} resolution over the original template).  A missing\n" +
        "            // secret throws SecretResolutionException here, before any matching runs.\n" +
        "            var method = methodExpected is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, methodExpected);\n" +
        "            var path = pathExpected is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, pathExpected);\n" +
        "            var bodyContains = bodyContainsTemplate is null\n" +
        "                ? null\n" +
        "                : Secret_Helpers.ResolveTemplate(secrets, vars, bodyContainsTemplate);\n" +
        "            var headerValues = new string[headerValueTemplates.Length];\n" +
        "            for (int hi = 0; hi < headerValueTemplates.Length; hi++)\n" +
        "            {\n" +
        "                // Header NAMES are used verbatim; only VALUES are resolved (§17).\n" +
        "                headerValues[hi] = Secret_Helpers.ResolveTemplate(secrets, vars, headerValueTemplates[hi]);\n" +
        "            }\n" +
        "            // Read the host-owned listener's captured-request snapshot.  GetCaptured\n" +
        "            // returns an empty list (never null) for an unregistered listener, so a\n" +
        "            // step naming an unknown listener simply scans zero requests and Fails.\n" +
        "            var captured = webhooks.GetCaptured(listenerVarName);\n" +
        "            int scanned = captured.Count;\n" +
        "            bool matched = false;\n" +
        "            // Track the criteria that the LAST scanned request failed, so a non-match\n" +
        "            // observation can name WHICH criteria failed (by name only, never value).\n" +
        "            System.Collections.Generic.List<string> lastFailed = null;\n" +
        "            for (int i = 0; i < captured.Count; i++)\n" +
        "            {\n" +
        "                var failed = new System.Collections.Generic.List<string>(4);\n" +
        "                if (MatchesRequest(captured[i], method, path, bodyContains, headerNames, headerValues, failed))\n" +
        "                {\n" +
        "                    matched = true;\n" +
        "                    break;\n" +
        "                }\n" +
        "                lastFailed = failed;\n" +
        "            }\n" +
        "            if (matched)\n" +
        "            {\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "                observation = \"{\\\"matched\\\":true,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                // No match THIS attempt = Fail.  The RETRY runner retries on non-Pass\n" +
        "                // and converts a sustained Fail to Inconclusive on timeout — never here.\n" +
        "                // The observation names which criteria the last scanned request failed,\n" +
        "                // BY NAME ONLY — never a captured body or header value (§17).\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"matched\\\":false,\\\"scanned\\\":\" +\n" +
        "                    scanned.ToString(System.Globalization.CultureInfo.InvariantCulture);\n" +
        "                if (lastFailed != null && lastFailed.Count > 0)\n" +
        "                {\n" +
        "                    observation += \",\\\"failed\\\":[\";\n" +
        "                    for (int fi = 0; fi < lastFailed.Count; fi++)\n" +
        "                    {\n" +
        "                        if (fi > 0) observation += \",\";\n" +
        "                        // The criterion NAME is a fixed identifier (method/path/headers/\n" +
        "                        // bodyContains), not captured data; JSON-serialise it defensively.\n" +
        "                        observation += System.Text.Json.JsonSerializer.Serialize(lastFailed[fi]);\n" +
        "                    }\n" +
        "                    observation += \"]\";\n" +
        "                }\n" +
        "                observation += \"}\";\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing / unknown secret = EnvironmentError (§12.1): a configuration\n" +
        "            // problem in the run environment, NOT a product defect.  REFERENCE-ONLY\n" +
        "            // observation (§17): a fixed message plus the discrete source/path\n" +
        "            // coordinates — never the value (none exists when resolution fails).\n" +
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
        "            // Any other failure = EnvironmentError (§12.1).  REFERENCE-ONLY: only the\n" +
        "            // exception TYPE name is emitted, never ex.Message — a message could, in\n" +
        "            // principle, echo captured/untrusted data, which must not reach the stream.\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Evaluates one captured request against the (already-resolved) criteria.  All\n" +
        "    /// criteria are conjunctive (logical AND); an absent criterion imposes no\n" +
        "    /// constraint.  On a non-match, the names of the failing criteria are appended to\n" +
        "    /// <paramref name=\"failed\"/> (names only — never captured values, §17).\n" +
        "    /// </summary>\n" +
        "    private static bool MatchesRequest(\n" +
        "        Vouchfx.Engine.Abstractions.Webhooks.CapturedWebhookRequest request,\n" +
        "        string? method,\n" +
        "        string? path,\n" +
        "        string? bodyContains,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValues,\n" +
        "        System.Collections.Generic.List<string> failed)\n" +
        "    {\n" +
        "        bool ok = true;\n" +
        "        // (1) method equality (case-insensitive) when an expected method is set.\n" +
        "        if (method is not null && !string.Equals(request.Method ?? string.Empty, method, System.StringComparison.OrdinalIgnoreCase))\n" +
        "        {\n" +
        "            failed.Add(\"method\");\n" +
        "            ok = false;\n" +
        "        }\n" +
        "        // (2) path equality (ordinal) when set — the captured Path is already\n" +
        "        // token-stripped/SUT-facing, so it is compared verbatim to the author's path.\n" +
        "        if (path is not null && !string.Equals(request.Path ?? string.Empty, path, System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            failed.Add(\"path\");\n" +
        "            ok = false;\n" +
        "        }\n" +
        "        // (3) body substring (ordinal) when set.\n" +
        "        if (bodyContains is not null && !(request.Body ?? string.Empty).Contains(bodyContains, System.StringComparison.Ordinal))\n" +
        "        {\n" +
        "            failed.Add(\"bodyContains\");\n" +
        "            ok = false;\n" +
        "        }\n" +
        "        // (4) each expected header must be present with a matching value.  Header-name\n" +
        "        // lookup is case-insensitive (HTTP header names are case-insensitive); the\n" +
        "        // value comparison is ordinal.  Recorded once under the 'headers' name.\n" +
        "        bool headersOk = true;\n" +
        "        for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "        {\n" +
        "            string actual = null;\n" +
        "            if (!TryGetHeaderCaseInsensitive(request.Headers, headerNames[hi], out actual)\n" +
        "                || !string.Equals(actual, headerValues[hi], System.StringComparison.Ordinal))\n" +
        "            {\n" +
        "                headersOk = false;\n" +
        "                break;\n" +
        "            }\n" +
        "        }\n" +
        "        if (!headersOk)\n" +
        "        {\n" +
        "            failed.Add(\"headers\");\n" +
        "            ok = false;\n" +
        "        }\n" +
        "        return ok;\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Looks up a header value by name, case-insensitively, without depending on the\n" +
        "    /// snapshot dictionary's comparer (which may be ordinal).  Returns the first\n" +
        "    /// case-insensitive name match.\n" +
        "    /// </summary>\n" +
        "    private static bool TryGetHeaderCaseInsensitive(\n" +
        "        System.Collections.Generic.IReadOnlyDictionary<string, string> headers,\n" +
        "        string name,\n" +
        "        out string value)\n" +
        "    {\n" +
        "        if (headers != null)\n" +
        "        {\n" +
        "            // Fast path: exact key present.\n" +
        "            if (headers.TryGetValue(name, out value))\n" +
        "                return true;\n" +
        "            // Slow path: scan for a case-insensitive name match.\n" +
        "            foreach (var kv in headers)\n" +
        "            {\n" +
        "                if (string.Equals(kv.Key, name, System.StringComparison.OrdinalIgnoreCase))\n" +
        "                {\n" +
        "                    value = kv.Value;\n" +
        "                    return true;\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        value = null;\n" +
        "        return false;\n" +
        "    }\n" +
        "}",
    };

    // ── IStepCompiler<WebhookListenHttpModel> ─────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution reads the host-owned listener's captured inbound
    /// requests via <c>Webhooks.GetCaptured(model.Listener)</c>, evaluates each against the
    /// resolved criteria, and writes a typed
    /// <see cref="Vouchfx.Engine.Abstractions.StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read after the
    /// script returns.
    /// </para>
    /// <para>
    /// Substitution + secret model (canonical M2 pattern): the expected method, path,
    /// bodyContains, and each header VALUE are emitted as RAW template literals (JSON-escaped
    /// C# string literals).  They are NOT pre-resolved at the call site — the helper resolves
    /// each in a single pass via <c>Secret_Helpers.ResolveTemplate</c> (both
    /// <c>{placeholder}</c> substitution and <c>${secret:source/path}</c> resolution) inside
    /// its guarded region, so a missing secret maps to a step-scoped
    /// <see cref="Vouchfx.Engine.Abstractions.Verdict.EnvironmentError"/> and no secret value
    /// is ever baked into the emitted IL (§17).
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>; the full
    /// <c>static class WebhookListenHttp_Helpers</c> definition plus the shared
    /// <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources in
    /// <see cref="CsxFragment.RequiredHelpers"/>; a single C# 11 <c>$$"""…"""</c>
    /// <see cref="CsxFragment.StatementBlock"/> with no <c>using var</c>; the step id
    /// sanitised via <c>CsxFragment.SanitiseId</c> before splicing.
    /// </para>
    /// </remarks>
    public CsxFragment Emit(WebhookListenHttpModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

        // Expand the header criteria into parallel name/value-template arrays.  Values are
        // emitted as RAW templates; the helper substitutes then secret-resolves each at
        // runtime inside the guarded region.  No secret value is ever baked into the emitted
        // IL — only the reference token text is.
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

        var headerNamesLiteral = BuildStringArrayLiteral(headerNames);
        var headerValueTemplatesLiteral = BuildStringArrayLiteral(headerValueTemplates);

        // The expected method / path / bodyContains are optional: emit the JSON-escaped
        // template literal when present, or the bare 'null' literal when absent.  Any
        // {placeholder} or ${secret:…} token inside survives as LITERAL TEXT here (not an
        // emit-time interpolation hole) and is processed at runtime.  CRITICAL: inside a
        // $$"""…""" block, {{expr}} is the interpolation hole; a lone {placeholder} or
        // ${secret:…} passes through verbatim.
        var methodLiteral = match.Method is null
            ? "null"
            : JsonSerializer.Serialize(match.Method);
        var pathLiteral = match.Path is null
            ? "null"
            : JsonSerializer.Serialize(match.Path);
        var bodyContainsLiteral = match.BodyContains is null
            ? "null"
            : JsonSerializer.Serialize(match.BodyContains);

        // The listener name is a plain field — no {placeholder}/${secret} resolution applies
        // to the listener name itself; it keys both the staged URL and the capture buffer.
        var listenerLiteral = JsonSerializer.Serialize(model.Listener);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        // 'Secrets' and 'Webhooks' are ScriptGlobalVariables instance properties.
        var block = $$"""
            {
                await WebhookListenHttp_Helpers.AssertAsync(
                    Vars,
                    Secrets,
                    Webhooks,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{listenerLiteral}},
                    {{methodLiteral}},
                    {{pathLiteral}},
                    {{bodyContainsLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    __stepCt_{{safeId}});
            }
            """;

        // Build the helpers list: WebhookListenHttp_Helpers + Substitute_Helpers +
        // Secret_Helpers.  Both shared helper sources are byte-identical across providers —
        // deduplication is handled by CsxAssembler.
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a C# array-initialiser literal from a string array, with each element
    /// individually JSON-serialised to escape embedded quotes, backslashes, and control
    /// characters before splicing into the CSX StatementBlock.
    /// </summary>
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
