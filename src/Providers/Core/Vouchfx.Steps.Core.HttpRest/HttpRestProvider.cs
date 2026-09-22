// Vouchfx.Steps.Core.HttpRest — http.rest step provider (DSL §5.1, §13).
//
// Implements the consolidated-provider pattern: one [StepProvider] class
// implements all five provider interfaces for the http.rest step kind.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, method,
//     path, headers, body, expect).  The type const discriminator is injected
//     by the SchemaComposer from Kind — never from the fragment text.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition; StatementBlock
//     is a C# 11 $$"""…""" block; 'using var' is illegal.
//
// S04-B-02 additions: JSONPath capture — when ctx.Captures is non-empty the emitted
//   block reads the response body and applies JsonPath.Net to extract named variables.
//   A JSONPath miss → Verdict.Inconclusive (upstream-capture-unmet, §12.1).
//   Matched flags are written to VarKeys.CaptureStatus(safeId) for G-01.
//
// S04-B-03 additions: {placeholder} substitution — the 'path' field (and header
//   values if present) are wrapped in Substitute_Helpers.Resolve(Vars, …) so that
//   {name} tokens are resolved at runtime against Vars.
//
// Response-body assertions (#558): 'expect.json' (a map of JSONPath → expected value,
//   or → { exists: true|false }) and 'expect.bodyContains' (an ordinal substring) are
//   evaluated after the status check passes and before any capture runs. The vocabulary
//   is borrowed, not invented: 'json' is mq-expect's match.json key and shape, compared
//   against the same canonical text 'capture' already writes; 'exists' is the word
//   cache-assert.redis / db-assert.dynamodb / storage-assert.s3 use; 'bodyContains' is
//   webhook-listen.http's. The evaluation lives in a SECOND helper class,
//   HttpRest_BodyAssertions, so its C# stays readable as a raw string. A failing
//   assertion is Fail; its observation names the path, the reason, the author's expected
//   TEMPLATE and the observed node's JSON kind — never text taken from the response body
//   (the SUT may echo a secret; webhook-listen.http's observation rule, §17).
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.HttpRest;

/// <summary>
/// Core provider for the <c>http.rest</c> step kind (DSL §5.1).
/// Issues an HTTP request to a logically-named service and optionally asserts
/// on the response.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.
/// The engine's <c>SchemaComposer</c> assembles the unified schema by injecting
/// a <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> — the fragment text never repeats that discriminator
/// (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a real <see cref="CsxFragment"/>
/// whose emitted CSX issues an HTTP GET (or other method) to the target service's
/// base URL + path, compares the response status to <c>expect.status</c>, and
/// writes a typed <see cref="StepOutcome"/> into <c>Vars</c> for the runner
/// to read after execution (§13.3.1).
/// </para>
/// <para>
/// Sprint-4 (S04-B-02): when the YAML step declares a <c>capture</c> block, the
/// emitted CSX reads the response body, evaluates each JSONPath expression via
/// JsonPath.Net, and writes matching values into <c>Vars</c>.  A path that yields
/// no match sets the outcome to <see cref="Verdict.Inconclusive"/> with reason
/// <c>upstream-capture-unmet</c> (§12.1).
/// </para>
/// <para>
/// Sprint-4 (S04-B-03): the <c>path</c> field and any header values are wrapped
/// at emit time in <c>Substitute_Helpers.Resolve(Vars, …)</c> so that
/// <c>{placeholder}</c> tokens resolve against <c>Vars</c> at runtime.
/// </para>
/// <para>
/// Response-body assertions (#558): <c>expect.json</c> maps a JSONPath to an expected
/// value or to <c>{ exists: true|false }</c>, and <c>expect.bodyContains</c> names an
/// ordinal substring. Both are evaluated once the status check has held and before any
/// capture runs; one that does not hold is <see cref="Verdict.Fail"/>, rendered by this
/// provider's <see cref="IStepDiffRenderer"/> implementation.
/// </para>
/// </remarks>
[StepProvider]
public sealed class HttpRestProvider
    : IStepProvider,
      IStepBinder<HttpRestModel>,
      IStepValidator<HttpRestModel>,
      IStepCompiler<HttpRestModel>,
      IResourceContributor<HttpRestModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── Allowed HTTP verbs ────────────────────────────────────────────────────

    private static readonly HashSet<string> s_allowedMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS",
        };

    // ── Structured-body bounds (issue #346) ────────────────────────
    //
    // YamlToJsonElement walks an author-written structured `body:` and materialises a
    // JsonNode tree. Two shapes of input make that walk expensive without looking expensive
    // in the file: deep nesting, which recurses once per YAML level, and an
    // anchored-and-repeatedly-aliased body, which expands multiplicatively because
    // YamlDotNet's representation model shares ONE node instance across every alias site
    // while this walk re-materialises a fresh copy at each. Only the second is reachable
    // through the engine — see below, and do not let that distinction quietly go missing
    // again.
    //
    // A suite author is trusted input (script.csharp hands them arbitrary C#), so these
    // bounds are mistake-catching and robustness, NOT a security boundary. What they buy
    // is a CATCHABLE throw: issue #413 already made a throwing Bind a reported
    // Inconclusive carrying a diagnostic that names the step, so raising an ordinary
    // exception lands in machinery that already works. No new error channel is invented.
    //
    // WHERE EACH BOUND ACTUALLY SITS RELATIVE TO THE ENGINE — measured, because the first
    // version of this comment justified both bounds by failure modes that no engine path
    // can reach, which made the shipped author-facing messages wrong.
    //
    // The engine validates a document against the composed JSON Schema BEFORE it parses it
    // and long before it binds any step. SchemaComposer.Validate calls
    // SchemaResources.ConvertYamlToJsonDocument, whose step 2 re-serialises the object
    // graph with `new SerializerBuilder().JsonCompatible().Build()`; that serialiser's
    // MaximumRecursion default is 50. Measured on YamlDotNet 16.3.0 against exactly that
    // builder: 49 nesting levels convert cleanly, 50 throw
    // MaximumRecursionLevelReachedException. Validate wraps the call in `catch (Exception)`
    // and returns SchemaValidationResult.Invalid("Failed to parse YAML: ..."), so a body
    // deep enough to breach that ceiling ALREADY produced a clean, reported diagnostic
    // before #346. That ceiling is on the WHOLE DOCUMENT graph, of which `body` is only a
    // subtree, so the depth a body may reach is smaller still — by however many levels sit
    // above it. The direction is conservative, which is the only property the argument needs,
    // and that is why no figure for it is written here.
    // No engine path that BINDS a step can reach MaxBodyDepth at 64 — ScenarioRunner runs
    // DocumentValidator.Validate at its step 2, ahead of ProviderPipeline.Compile, and the
    // pre-topology parse paths that deliberately skip validation (EnvironmentMapper,
    // SecuredEndpointProbe) do not compile steps at all. That last sentence is read from
    // those call sites, not measured end to end; the 49/50 boundary above is measured.
    // The bound is kept as a backstop for a caller that binds without validating (the unit
    // rows do exactly that) and for the day the upstream constant moves. It is NOT the
    // thing standing between a deep document and an uncatchable StackOverflowException,
    // and no message here says it is. The upstream limit is a FIXED LIBRARY CONSTANT, not
    // a function of stack size, so the ordering between the two cannot invert on a smaller
    // stack.
    //
    // MaxBodyNodes is the bound that a real suite can still reach. The validation-time
    // conversion above carries a ceiling of its own — SchemaResources.MaxJsonChars, 16 Mi
    // characters of emitted JSON — so on the engine path a document that reaches this
    // provider is one that conversion already survived. What the budget buys is that this
    // provider does not then pay the same document a second time as a much heavier JsonNode
    // tree, and that the author gets a refusal naming the step and the limit rather than a
    // generic upstream one. It does NOT make an out-of-memory condition impossible; it bounds
    // this provider's own walk, and nothing more.
    //
    // WHY A NODE BUDGET AND NOT A VISITED-NODE SET. An alias is a SHARED node, not
    // necessarily a cycle. `*defaults` under two keys is legitimate YAML that MUST expand
    // twice, and the representation model offers no way to tell that apart from the
    // amplifying case except by measuring the result. A visited set would change the
    // language's semantics — the second and later expansions of a shared node would be
    // dropped or refused — so the defence is a budget on the nodes PRODUCED, which is
    // exactly the quantity amplification blows up. A legitimate re-expansion costs its
    // own nodes and nothing more; a billion-laughs body runs the budget out.

    /// <summary>
    /// Maximum nesting depth accepted for a structured <c>body:</c>; the body node itself
    /// is depth 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Unreachable through the engine, and kept anyway as a backstop.</strong>
    /// Schema validation converts the document with a YamlDotNet serialiser whose
    /// <c>MaximumRecursion</c> default is 50 and refuses a 50-level body before parse and
    /// long before <see cref="Bind(YamlNode, IBindingContext)"/> — measured; see the block
    /// comment above for the figures. Only a caller that binds a step WITHOUT validating
    /// the document first can reach this bound, which in this repository means the unit
    /// rows. It earns its place because it is one comparison per node and because the
    /// upstream constant is a library default this provider does not control.
    /// </para>
    /// <para>
    /// The figure is chosen against the SERIALISER, one level inside it. Measured on the
    /// pinned runtime: a 65-level <c>JsonNode</c> tree serialises and a 66-level one throws
    /// <see cref="System.Text.Json.JsonException"/>, matching the documented default of
    /// <see cref="System.Text.Json.JsonSerializerOptions.MaxDepth"/>. So 64 is one level
    /// STRICTER than what the <c>JsonSerializer.Serialize</c> call in <c>Bind</c> could
    /// emit — a round number just inside the ceiling, not a match for it. The one body that
    /// difference refuses (exactly 65 levels) was already refused upstream at 50.
    /// </para>
    /// </remarks>
    private const int MaxBodyDepth = 64;

    /// <summary>
    /// Maximum number of JSON nodes a structured <c>body:</c> may expand into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PICKED, not measured — there is no natural ceiling to measure against here, only a
    /// judgement about headroom. A hand-authored request body runs to tens or a few hundred
    /// nodes and a generated bulk payload to a few thousand, so 50,000 leaves between one
    /// and three orders of magnitude of room. The count is of nodes PRODUCED, so an aliased
    /// subtree is charged its full expansion at every site, which is what makes this the
    /// amplification defence rather than a size limit.
    /// </para>
    /// <para>
    /// <strong>IT COUNTS NODES, NOT BYTES, and the difference is reachable rather than
    /// theoretical.</strong> <c>ScalarToJsonNode</c> wraps the scalar's existing string
    /// instance, so an aliased scalar costs ONE node per alias site however long that string is.
    /// A body that aliases one large scalar many times therefore stays well inside this
    /// budget while serialising to far more than its node count suggests. How much more is
    /// bounded by the 1 MiB document cap ScenarioDiscovery applies before reading a file,
    /// not by this constant — and that cap is the CLI's, not every caller's:
    /// <c>ProviderTestHarness.RunSingleStepAsync</c> reaches <c>Bind</c> with an in-memory
    /// string no cap applies to. This constant therefore does not cap the materialised tree at any
    /// particular size, and no claim here says it does. Widening it to a byte budget would be a
    /// separate change with its own message and its own rows.
    /// </para>
    /// </remarks>
    private const int MaxBodyNodes = 50_000;

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Net.Http",
            "System.Diagnostics",
            "System.Threading.Tasks",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>HttpRest_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// All types are fully-qualified so the helper compiles independently of
    /// the spliced <c>using</c> ordering.  <c>using var</c> is absent — a
    /// <c>using (…) { }</c> statement-with-parens is used where needed (which
    /// is a <c>using</c> statement, not a <c>using var</c> declaration).
    /// </para>
    /// <para>
    /// S04-B-02 + S07-B-01b: the helper accepts three parallel capture arrays
    /// (varNames, exprs, kinds) and a captureStatusKey.  Each capture's
    /// <c>kind</c> token (<c>"json"</c> or <c>"xpath"</c>) selects the evaluator:
    /// <list type="bullet">
    ///   <item><c>"json"</c> — the body is parsed into a <c>JsonNode</c> ONCE
    ///   (lazily, on the first JSONPath capture) and each JSONPath is evaluated via
    ///   JsonPath.Net against the cached node.</item>
    ///   <item><c>"xpath"</c> — the body is parsed into an <c>XPathNavigator</c> ONCE
    ///   (lazily, on the first XPath capture) through a hardened <c>XmlReader</c>:
    ///   <c>DtdProcessing.Prohibit</c> defeats inline-DTD entity-expansion DoS
    ///   (billion laughs), <c>XmlResolver=null</c> blocks external entities (XXE),
    ///   and <c>MaxCharactersInDocument</c> caps a hostile body. Each XPath is then
    ///   evaluated via <c>SelectSingleNode</c>, taking the selected node's string value.</item>
    /// </list>
    /// A malformed/non-matching body (JSON or XML) sets the corresponding cached
    /// handle to <c>null</c>, marking those captures unmet — never a crash.  An
    /// invalid expression (bad JSONPath or bad XPath) is caught per-capture and also
    /// marks a miss.  Matched values are written to <c>Vars</c>; any unmatched
    /// capture sets the outcome to <see cref="Verdict.Inconclusive"/>
    /// (upstream-capture-unmet, §12.1).  A comma-delimited matched-flag string is
    /// written under captureStatusKey for the G-01 provenance event.
    /// </para>
    /// <para>
    /// Response-body assertions (#558) live in a SECOND entry, the
    /// <c>HttpRest_BodyAssertions</c> class, which <c>HttpRest_Helpers</c> calls: the
    /// unset-placeholder check that runs before anything is sent, the single JSON parse
    /// (which the JSONPath captures then reuse), and the evaluation of the <c>json</c> entries
    /// and <c>bodyContains</c> into the <c>body</c> member of a Fail observation. It is kept
    /// as a raw string so it reads as ordinary C#, and it names nothing from the response
    /// body in any string it returns — only paths, expected templates, reasons, JSON kinds
    /// and node counts (§17).
    /// </para>
    /// <para>
    /// Both helpers must be byte-identical across every instance of the same
    /// provider within a suite (§13.3.1 dedup rule); neither contains any
    /// per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class HttpRest_Helpers\n" +
        "{\n" +
        "    /// <summary>\n" +
        "    /// Issues an HTTP request, evaluates the response status against the\n" +
        "    /// optional expectation, and writes a typed StepOutcome into Vars.\n" +
        "    /// When capture arrays are non-empty, reads the response body once (capped)\n" +
        "    /// and dispatches each capture by kind: a \"json\" capture is applied via\n" +
        "    /// JsonPath.Net and an \"xpath\" capture via a hardened XmlReader/XPath load;\n" +
        "    /// a miss (either kind) → Inconclusive.\n" +
        "    /// Uses safe URI resolution (same-authority guard) and disables\n" +
        "    /// automatic redirects to prevent SSRF via 3xx bounces (§security M1).\n" +
        "    /// Timeout verdict = Inconclusive; connection failures = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,\n" +
        "        string outcomeKey,\n" +
        "        string captureStatusKey,\n" +
        "        string serviceKey,\n" +
        "        string targetName,\n" +
        "        string method,\n" +
        "        string pathTemplate,\n" +
        "        string? bodyTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        int? expectedStatus,\n" +
        "        string[] jsonPaths,\n" +
        "        string[] jsonExpectedTemplates,\n" +
        "        string[] jsonModes,\n" +
        "        string? bodyContainsTemplate,\n" +
        "        string[] captureVarNames,\n" +
        "        string[] captureExprs,\n" +
        "        string[] captureKinds,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        // Response-body assertions (#558): an expected value naming a {placeholder} that\n" +
        "        // is absent from Vars (or null) would resolve to the empty string, which makes a\n" +
        "        // bodyContains pass vacuously or blames the service for a value that was never\n" +
        "        // captured. The input this step's assertion needs does not exist, so the step is\n" +
        "        // Inconclusive (§12.1, upstream capture unmet) and NOTHING is resolved or sent.\n" +
        "        // The observation carries the placeholder NAME — author text, never a value.\n" +
        "        var unsetPlaceholder = HttpRest_BodyAssertions.FindUnsetPlaceholder(\n" +
        "            vars, jsonModes, jsonExpectedTemplates, bodyContainsTemplate);\n" +
        "        if (unsetPlaceholder != null)\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "                Vouchfx.Engine.Abstractions.Verdict.Inconclusive,\n" +
        "                sw.ElapsedMilliseconds,\n" +
        "                \"{\\\"placeholderUnmet\\\":\" + System.Text.Json.JsonSerializer.Serialize(unsetPlaceholder) + \"}\");\n" +
        "            return;\n" +
        "        }\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // AllowAutoRedirect=false: a 3xx from the target must not silently\n" +
        "        // bounce the request to a different host (SSRF via redirect, §M1).\n" +
        "        // disposeHandler:true so client.Dispose() in finally releases the handler too.\n" +
        "        var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };\n" +
        "        var client = new System.Net.Http.HttpClient(handler, disposeHandler: true);\n" +
        "        try\n" +
        "        {\n" +
        "            // REQ-024: present the declared client certificate and trust the declared CA\n" +
        "            // for THIS step's target. Inside the try, deliberately: a declared-but-\n" +
        "            // malformed certificate throws SecurityMaterialException, which the general\n" +
        "            // catch below maps to a step-scoped EnvironmentError (§12.1) rather than\n" +
        "            // escaping the step. Before the first SendAsync, so the underlying handler is\n" +
        "            // still mutable. A target with no security block leaves the handler untouched.\n" +
        "            Security_Helpers.ConfigureHandler(security, targetName, handler);\n" +
        "            // Step-timeout convention (#232): a declared step budget governs this call —\n" +
        "            // lift the transport bound (infinite) and let the step token (ct) be the sole\n" +
        "            // enforcement mechanism; otherwise keep the M2 30s stall-window convention.\n" +
        "            client.Timeout = budgetGoverned\n" +
        "                ? System.Threading.Timeout.InfiniteTimeSpan\n" +
        "                : System.TimeSpan.FromSeconds(30);\n" +
        "            // §security S07: bound the untrusted response body. The default\n" +
        "            // MaxResponseContentBufferSize is ~2 GB, so a hostile target could stream a\n" +
        "            // huge body and OOM the runner before the JSON/XPath branch even parses it.\n" +
        "            // 16 MiB is generous for an assertion/capture body; an oversize response\n" +
        "            // overflows the buffer and ReadAsStringAsync throws HttpRequestException →\n" +
        "            // caught by the general catch below → EnvironmentError (a graceful miss, no\n" +
        "            // unhandled throw). This bounds BOTH the JSON and XPath capture branches.\n" +
        "            client.MaxResponseContentBufferSize = 16 * 1024 * 1024;\n" +
        "            // Resolve the path INSIDE the guarded region (§17) in a SINGLE pass:\n" +
        "            // ResolveTemplate handles BOTH {placeholder} substitution and\n" +
        "            // ${secret:source/path} resolution over the original template text, so a\n" +
        "            // substituted placeholder value can never be re-scanned as a secret token\n" +
        "            // (no secret-reference injection) and a revealed secret can never be\n" +
        "            // re-scanned as a placeholder (no corruption). A missing secret throws\n" +
        "            // SecretResolutionException → caught below → EnvironmentError.\n" +
        "            var path = Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate);\n" +
        "            var baseUrl = vars.TryGetValue(serviceKey, out var bu) && bu is string s ? s : \"\";\n" +
        "            // Safe URI composition (M1): resolve path against the base URI and\n" +
        "            // confirm the resulting authority matches the original base URI.\n" +
        "            // An empty or invalid baseUrl throws UriFormatException → caught → EnvironmentError.\n" +
        "            var baseUri = new System.Uri(baseUrl, System.UriKind.Absolute);\n" +
        "            var full = new System.Uri(baseUri, path);\n" +
        "            if (full.GetLeftPart(System.UriPartial.Authority) != baseUri.GetLeftPart(System.UriPartial.Authority))\n" +
        "            {\n" +
        "                throw new System.InvalidOperationException(\n" +
        "                    \"http.rest: resolved URL authority '\" + full.Authority +\n" +
        "                    \"' does not match base authority '\" + baseUri.Authority +\n" +
        "                    \"'; path attempted to change host.\");\n" +
        "            }\n" +
        "            using (var req = new System.Net.Http.HttpRequestMessage(\n" +
        "                       new System.Net.Http.HttpMethod(method), full))\n" +
        "            {\n" +
        "                // Resolve + set request headers INSIDE the guarded region (§17):\n" +
        "                // each VALUE is resolved in a single pass via ResolveTemplate (both\n" +
        "                // {placeholder} substitution and ${secret:...} resolution over the\n" +
        "                // original template). The revealed value feeds the header sink directly\n" +
        "                // and is never stored. Header NAMES are used VERBATIM and are\n" +
        "                // intentionally NOT placeholder- or secret-resolved — only values are.\n" +
        "                for (int hi = 0; hi < headerNames.Length; hi++)\n" +
        "                {\n" +
        "                    var headerName = headerNames[hi];\n" +
        "                    var headerValue = Secret_Helpers.ResolveTemplate(\n" +
        "                        secrets, vars, headerValueTemplates[hi]);\n" +
        "                    // TryAddWithoutValidation (not Add): it permits restricted and\n" +
        "                    // content headers, and does not throw on unusual header names —\n" +
        "                    // Add validates the name/value and rejects content headers on a\n" +
        "                    // request-header collection.\n" +
        "                    req.Headers.TryAddWithoutValidation(headerName, headerValue);\n" +
        "                }\n" +
        "                // Resolve + attach the request body INSIDE the guarded region (§17),\n" +
        "                // mirroring the path/header handling EXACTLY. When bodyTemplate is\n" +
        "                // non-null the original template is resolved in a SINGLE pass via\n" +
        "                // ResolveTemplate (both {placeholder} substitution AND ${secret:...}\n" +
        "                // resolution over the original text), so a substituted placeholder is\n" +
        "                // never re-scanned as a secret token and a revealed secret is never\n" +
        "                // re-scanned as a placeholder. The revealed body feeds the content sink\n" +
        "                // directly and is never written back to Vars. A missing secret throws\n" +
        "                // SecretResolutionException → caught below → EnvironmentError. The\n" +
        "                // StringContent is owned by the HttpRequestMessage and is disposed when\n" +
        "                // the request is disposed by the 'using' block above (a using-declaration\n" +
        "                // is prohibited in a CSX body, §13.3.1).\n" +
        "                // MVP content type: application/json.\n" +
        "                if (bodyTemplate != null)\n" +
        "                {\n" +
        "                    var body = Secret_Helpers.ResolveTemplate(secrets, vars, bodyTemplate);\n" +
        "                    req.Content = new System.Net.Http.StringContent(\n" +
        "                        body, System.Text.Encoding.UTF8, \"application/json\");\n" +
        "                }\n" +
        "                // Resolve the EXPECTED values of the response-body assertions (#558) here,\n" +
        "                // INSIDE the guarded region and BEFORE the request is sent — the same single\n" +
        "                // ResolveTemplate pass as the path, headers and body. A missing secret throws\n" +
        "                // SecretResolutionException → caught below → EnvironmentError, nothing sent.\n" +
        "                // A revealed value is only a comparison operand: it is never written to Vars\n" +
        "                // and never placed in an observation, which carries the TEMPLATE text (§17).\n" +
        "                var jsonExpected = new string[jsonExpectedTemplates.Length];\n" +
        "                for (int ji = 0; ji < jsonExpectedTemplates.Length; ji++)\n" +
        "                {\n" +
        "                    jsonExpected[ji] = string.Equals(jsonModes[ji], \"equals\", System.StringComparison.Ordinal)\n" +
        "                        ? Secret_Helpers.ResolveTemplate(secrets, vars, jsonExpectedTemplates[ji])\n" +
        "                        : string.Empty;\n" +
        "                }\n" +
        "                var bodyContains = bodyContainsTemplate == null\n" +
        "                    ? null\n" +
        "                    : Secret_Helpers.ResolveTemplate(secrets, vars, bodyContainsTemplate);\n" +
        "                var resp = await client.SendAsync(req, ct).ConfigureAwait(false);\n" +
        "                var actual = (int)resp.StatusCode;\n" +
        "                bool ok = expectedStatus.HasValue\n" +
        "                    ? actual == expectedStatus.Value\n" +
        "                    : (actual >= 200 && actual < 300);\n" +
        "                verdict = ok\n" +
        "                    ? Vouchfx.Engine.Abstractions.Verdict.Pass\n" +
        "                    : Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                var statusFields = \"\\\"status\\\":\" + actual +\n" +
        "                    \",\\\"expected\\\":\" +\n" +
        "                    (expectedStatus.HasValue\n" +
        "                        ? expectedStatus.Value.ToString(\n" +
        "                              System.Globalization.CultureInfo.InvariantCulture)\n" +
        "                        : \"null\");\n" +
        "                observation = \"{\" + statusFields + \"}\";\n" +
        "\n" +
        "                // ONE body read, shared by the response-body assertions and the captures, and\n" +
        "                // only once the status check has held — a status mismatch keeps today's\n" +
        "                // observation and evaluates nothing else.\n" +
        "                bool hasBodyAssertions = jsonPaths.Length > 0 || bodyContains != null;\n" +
        "                string bodyStr = string.Empty;\n" +
        "                if (verdict != Vouchfx.Engine.Abstractions.Verdict.Fail\n" +
        "                    && (hasBodyAssertions || captureVarNames.Length > 0))\n" +
        "                {\n" +
        "                    bodyStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);\n" +
        "                }\n" +
        "                // ONE JSON parse, shared the same way. jsonParsed records that a parse was\n" +
        "                // attempted; jsonNode is null when it failed OR when the body is the literal\n" +
        "                // `null` — the captures below treat both as unmatched, exactly as before.\n" +
        "                System.Text.Json.Nodes.JsonNode? jsonNode = null;\n" +
        "                bool jsonParsed = false;\n" +
        "\n" +
        "                // ── Response-body assertions (#558): json entries in declaration order,\n" +
        "                // then bodyContains. Any failure is Fail and skips the captures. ──\n" +
        "                if (hasBodyAssertions && verdict != Vouchfx.Engine.Abstractions.Verdict.Fail)\n" +
        "                {\n" +
        "                    bool jsonOk = false;\n" +
        "                    if (jsonPaths.Length > 0)\n" +
        "                    {\n" +
        "                        jsonParsed = true;\n" +
        "                        jsonOk = HttpRest_BodyAssertions.TryParseJson(bodyStr, out jsonNode);\n" +
        "                    }\n" +
        "                    var bodyFailure = HttpRest_BodyAssertions.Evaluate(\n" +
        "                        bodyStr, jsonOk, jsonNode, jsonPaths, jsonExpected, jsonModes,\n" +
        "                        jsonExpectedTemplates, bodyContains, bodyContainsTemplate);\n" +
        "                    if (bodyFailure != null)\n" +
        "                    {\n" +
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                        observation = \"{\" + statusFields + \",\\\"body\\\":\" + bodyFailure + \"}\";\n" +
        "                    }\n" +
        "                }\n" +
        "\n" +
        "                // ── S04-B-02 + S07-B-01b: format-aware capture (JSONPath / XPath) ──\n" +
        "                if (captureVarNames.Length > 0 && verdict != Vouchfx.Engine.Abstractions.Verdict.Fail)\n" +
        "                {\n" +
        "                    // The body was read once above. The JSON body is parsed ONCE (lazily —\n" +
        "                    // only when the first JSONPath capture is hit, unless the assertions\n" +
        "                    // already parsed it). A malformed body leaves the cached node null,\n" +
        "                    // which marks every JSONPath capture unmet.\n" +
        "                    // Parse the XML body ONCE (lazily — only when the first XPath capture\n" +
        "                    // is hit). A parse failure / non-XML body sets the navigator to null,\n" +
        "                    // which marks every XPath capture unmet (NOT a crash).\n" +
        "                    System.Xml.XPath.XPathNavigator? xmlNav = null;\n" +
        "                    bool xmlParsed = false;\n" +
        "                    var matchedFlags = new bool[captureVarNames.Length];\n" +
        "                    for (int ci = 0; ci < captureVarNames.Length; ci++)\n" +
        "                    {\n" +
        "                        var varName = captureVarNames[ci];\n" +
        "                        var captureExpr = captureExprs[ci];\n" +
        "                        var captureKind = captureKinds[ci];\n" +
        "                        bool matched = false;\n" +
        "                        if (string.Equals(captureKind, \"xpath\", System.StringComparison.Ordinal))\n" +
        "                        {\n" +
        "                            // ── XPath branch (S07-B-01b) ─────────────────────────────\n" +
        "                            if (!xmlParsed)\n" +
        "                            {\n" +
        "                                xmlParsed = true;\n" +
        "                                // Hardened XML load (§security S07): a hostile body must not be\n" +
        "                                // able to OOM/CPU-pin the runner via inline-DTD entity expansion\n" +
        "                                // (billion laughs) or fetch external resources (XXE). XmlResolver=null\n" +
        "                                // alone blocks EXTERNAL entities but NOT inline-DTD expansion, so the\n" +
        "                                // body is loaded through an XmlReader whose settings prohibit DTD\n" +
        "                                // processing outright, cap entity characters, and bound the document\n" +
        "                                // size. A DTD-bearing / oversized / malformed body throws → caught\n" +
        "                                // → xmlNav stays null → every XPath capture misses (unchanged\n" +
        "                                // 'malformed body = miss = Inconclusive' contract).\n" +
        "                                System.Xml.XmlReader xmlReader = null;\n" +
        "                                try\n" +
        "                                {\n" +
        "                                    var xmlSettings = new System.Xml.XmlReaderSettings();\n" +
        "                                    xmlSettings.DtdProcessing = System.Xml.DtdProcessing.Prohibit;   // inline DTD -> XmlException -> clean miss\n" +
        "                                    xmlSettings.XmlResolver = null;                                  // no external fetch (defence in depth)\n" +
        "                                    xmlSettings.MaxCharactersFromEntities = 0;                       // belt-and-braces\n" +
        "                                    xmlSettings.MaxCharactersInDocument = 10_000_000;                // hard ceiling on a hostile body\n" +
        "                                    xmlReader = System.Xml.XmlReader.Create(new System.IO.StringReader(bodyStr), xmlSettings);\n" +
        "                                    var xmlDoc = new System.Xml.XmlDocument();\n" +
        "                                    xmlDoc.XmlResolver = null;\n" +
        "                                    xmlDoc.Load(xmlReader);\n" +
        "                                    xmlNav = xmlDoc.CreateNavigator();\n" +
        "                                }\n" +
        "                                catch (System.Exception)\n" +
        "                                {\n" +
        "                                    // Non-XML / malformed / DTD-bearing / oversized body — every\n" +
        "                                    // XPath capture misses (never a crash). A using-declaration is\n" +
        "                                    // prohibited in a CSX body, so the reader is disposed in the\n" +
        "                                    // finally below instead.\n" +
        "                                    xmlNav = null;\n" +
        "                                }\n" +
        "                                finally\n" +
        "                                {\n" +
        "                                    if (xmlReader != null) { xmlReader.Dispose(); }\n" +
        "                                }\n" +
        "                            }\n" +
        "                            if (xmlNav != null)\n" +
        "                            {\n" +
        "                                try\n" +
        "                                {\n" +
        "                                    // SelectSingleNode evaluates the XPath and returns the first\n" +
        "                                    // matching node (element / attribute / text). A syntactically\n" +
        "                                    // invalid expression throws System.Xml.XPath.XPathException,\n" +
        "                                    // caught below → miss (never escapes the helper).\n" +
        "                                    var picked = xmlNav.SelectSingleNode(captureExpr);\n" +
        "                                    if (picked != null)\n" +
        "                                    {\n" +
        "                                        var capturedStr = picked.Value;\n" +
        "                                        if (!string.IsNullOrEmpty(capturedStr))\n" +
        "                                        {\n" +
        "                                            vars[varName] = capturedStr;\n" +
        "                                            matched = true;\n" +
        "                                        }\n" +
        "                                    }\n" +
        "                                }\n" +
        "                                catch (System.Exception)\n" +
        "                                {\n" +
        "                                    // Invalid XPath expression / evaluation error → miss.\n" +
        "                                    matched = false;\n" +
        "                                }\n" +
        "                            }\n" +
        "                        }\n" +
        "                        else\n" +
        "                        {\n" +
        "                            // ── JSONPath branch (S04-B-02, unchanged behaviour) ──────\n" +
        "                            if (!jsonParsed)\n" +
        "                            {\n" +
        "                                jsonParsed = true;\n" +
        "                                try\n" +
        "                                {\n" +
        "                                    jsonNode = System.Text.Json.Nodes.JsonNode.Parse(bodyStr);\n" +
        "                                }\n" +
        "                                catch (System.Exception)\n" +
        "                                {\n" +
        "                                    jsonNode = null;\n" +
        "                                }\n" +
        "                            }\n" +
        "                            if (jsonNode != null)\n" +
        "                            {\n" +
        "                            try\n" +
        "                            {\n" +
        "                                var pathResult = Json.Path.JsonPath.Parse(captureExpr).Evaluate(jsonNode);\n" +
        "                                var matches = pathResult.Matches;\n" +
        "                                if (matches != null && matches.Count > 0 && matches[0].Value is not null)\n" +
        "                                {\n" +
        "                                    var firstMatch = matches[0].Value;\n" +
        "                                    string capturedStr;\n" +
        "                                    if (firstMatch is System.Text.Json.Nodes.JsonValue jv)\n" +
        "                                    {\n" +
        "                                        // Scalar value: emit the raw string/number/bool without surrounding quotes.\n" +
        "                                        var rawElem = jv.GetValue<System.Text.Json.JsonElement>();\n" +
        "                                        capturedStr = rawElem.ValueKind == System.Text.Json.JsonValueKind.String\n" +
        "                                            ? rawElem.GetString() ?? string.Empty\n" +
        "                                            : rawElem.GetRawText();\n" +
        "                                    }\n" +
        "                                    else\n" +
        "                                    {\n" +
        "                                        // Object or array: compact JSON.\n" +
        "                                        capturedStr = firstMatch.ToJsonString();\n" +
        "                                    }\n" +
        "                                    vars[varName] = capturedStr;\n" +
        "                                    matched = true;\n" +
        "                                }\n" +
        "                            }\n" +
        "                            catch (System.Exception)\n" +
        "                            {\n" +
        "                                matched = false;\n" +
        "                            }\n" +
        "                            }\n" +
        "                        }\n" +
        "                        matchedFlags[ci] = matched;\n" +
        "                        if (!matched)\n" +
        "                        {\n" +
        "                            verdict = Vouchfx.Engine.Abstractions.Verdict.Inconclusive;\n" +
        "                            observation = \"{\\\"captureUnmet\\\":\" +\n" +
        "                                System.Text.Json.JsonSerializer.Serialize(varName) + \"}\";\n" +
        "                        }\n" +
        "                    }\n" +
        "                    // Write per-capture matched flags as a comma-delimited string for G-01.\n" +
        "                    vars[captureStatusKey] = string.Join(\",\", System.Array.ConvertAll(matchedFlags, f => f ? \"1\" : \"0\"));\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)\n" +
        "        {\n" +
        "            // Missing / unknown secret = EnvironmentError (§12.1): a configuration\n" +
        "            // problem in the run environment, NOT a product defect (not Fail) and NOT\n" +
        "            // a scenario-level abort (caught here, written as a per-step outcome).\n" +
        "            // The observation is REFERENCE-ONLY: a fixed message plus the discrete\n" +
        "            // source/path coordinates. The exception's own Message is deliberately\n" +
        "            // NOT included — a future resolver's Message could embed partial value\n" +
        "            // data, and this observation must never carry a value (§17).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\" +\n" +
        "                \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +\n" +
        "                \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
        "        }\n" +
        "        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)\n" +
        "        {\n" +
        "            // Step-token cut (#232): rethrow past this provider's own error handling so\n" +
        "            // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead\n" +
        "            // of the connection-timeout branch below misclassifying it.\n" +
        "            throw;\n" +
        "        }\n" +
        "        catch (System.Exception ex) when (ex is System.Threading.Tasks.TaskCanceledException\n" +
        "                                          || ex is System.TimeoutException)\n" +
        "        {\n" +
        "            // Timeout = Inconclusive (§12.1): the test could not complete due to\n" +
        "            // a stall, not because the target service responded incorrectly.\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.Inconclusive;\n" +
        "            observation = \"{\\\"timeout\\\":true}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Connection / DNS / authority-change failures = EnvironmentError (§12.1).\n" +
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            client.Dispose();  // explicit Dispose() in finally — 'using'-declarations are prohibited in CSX (§13.3.1).\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
        "            verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "}",
        """
        static class HttpRest_BodyAssertions
        {
            // Response-body assertions for http.rest (#558): expect.json and expect.bodyContains.
            // A SECOND provider-prefixed helper class, so this logic reads as ordinary C#. Like
            // HttpRest_Helpers it is provider-private — not an SDK `const Source`, so it is outside
            // the frozen helper-source golden — and byte-identical across every http.rest step of
            // one provider build, which is what CsxAssembler's helper dedupe requires (§13.3.1).
            //
            // OBSERVATION RULE (§17): nothing read from the response body reaches a string this
            // class returns — no value, no excerpt, no member name. A failure names the entry's
            // path and the author's expected TEMPLATE (a ${secret:...} reference stays a reference,
            // a {placeholder} stays a name), a reason, and at most the JSON KIND of the node found
            // or a node COUNT. The system under test may echo a secret back in its response.

            // Secret_Helpers' own combined pattern, copied byte for byte: the placeholder check
            // below must see exactly the {placeholder} tokens ResolveTemplate substitutes and —
            // because the secret alternative is tried first — never a {name}-shaped run inside a
            // ${secret:...} token. HttpRestBodyAssertionTests reads the pattern out of
            // SecretHelper.Source at run time and fails if the two ever differ.
            private static readonly System.Text.RegularExpressions.Regex s_combined =
                new System.Text.RegularExpressions.Regex(
                    @"(?<secret>\$\{secret:[A-Za-z0-9_-]+/[^}]+\})|(?<ph>\{(?<phName>[A-Za-z_][A-Za-z0-9_]*)\})",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            // Bound on author text (a path or an expected template) echoed into an observation.
            private const int MaxEchoChars = 256;

            /// <summary>
            /// Returns the name of the first {placeholder} an expected value names that is absent
            /// from vars or bound to null, or null when every one is set. Only the equality form of
            /// a json entry and bodyContains carry a template; exists entries carry none.
            /// </summary>
            internal static string? FindUnsetPlaceholder(
                System.Collections.Generic.IDictionary<string, object?> vars,
                string[] jsonModes,
                string[] jsonExpectedTemplates,
                string? bodyContainsTemplate)
            {
                for (int i = 0; i < jsonExpectedTemplates.Length; i++)
                {
                    if (!string.Equals(jsonModes[i], "equals", System.StringComparison.Ordinal))
                        continue;
                    var name = FirstUnsetPlaceholder(vars, jsonExpectedTemplates[i]);
                    if (name != null)
                        return name;
                }
                return bodyContainsTemplate == null
                    ? null
                    : FirstUnsetPlaceholder(vars, bodyContainsTemplate);
            }

            private static string? FirstUnsetPlaceholder(
                System.Collections.Generic.IDictionary<string, object?> vars,
                string template)
            {
                for (var m = s_combined.Match(template); m.Success; m = m.NextMatch())
                {
                    // A ${secret:...} token is ResolveTemplate's business: it resolves, or it
                    // throws and the step is an EnvironmentError before anything is sent.
                    if (!m.Groups["ph"].Success)
                        continue;
                    var name = m.Groups["phName"].Value;
                    if (!vars.TryGetValue(name, out var value) || value is null)
                        return name;
                }
                return null;
            }

            /// <summary>
            /// Parses the body once for the json entries. False when the body is not a JSON
            /// document: empty, whitespace, HTML, trailing text, nested deeper than the parser's
            /// default of 64, or holding an object with a duplicate member name. The JSON literal
            /// null parses, as a null node.
            /// </summary>
            internal static bool TryParseJson(string body, out System.Text.Json.Nodes.JsonNode? node)
            {
                node = null;
                System.Text.Json.Nodes.JsonNode? parsed;
                try
                {
                    parsed = System.Text.Json.Nodes.JsonNode.Parse(body);
                }
                catch (System.Text.Json.JsonException)
                {
                    return false;
                }
                // JsonNode builds an object's member table lazily, so a duplicate member name would
                // otherwise surface as an ArgumentException from inside whichever JSONPath evaluation
                // first reads that object (measured). Materialise the whole tree once, here, so such
                // a body is "not JSON" for every entry alike.
                try
                {
                    var pending = new System.Collections.Generic.Stack<System.Text.Json.Nodes.JsonNode>();
                    if (parsed != null)
                        pending.Push(parsed);
                    while (pending.Count > 0)
                    {
                        var current = pending.Pop();
                        if (current is System.Text.Json.Nodes.JsonObject obj)
                        {
                            foreach (var member in obj)
                            {
                                if (member.Value != null)
                                    pending.Push(member.Value);
                            }
                        }
                        else if (current is System.Text.Json.Nodes.JsonArray arr)
                        {
                            foreach (var item in arr)
                            {
                                if (item != null)
                                    pending.Push(item);
                            }
                        }
                    }
                }
                catch (System.ArgumentException)
                {
                    return false;
                }
                node = parsed;
                return true;
            }

            /// <summary>
            /// Evaluates the json entries in declaration order, then bodyContains. Returns null when
            /// every one holds; otherwise the observation's body member,
            /// {"failed":k,"of":n,"first":{...}}, where first describes the first that did not.
            /// </summary>
            internal static string? Evaluate(
                string body,
                bool jsonOk,
                System.Text.Json.Nodes.JsonNode? json,
                string[] jsonPaths,
                string[] jsonExpected,
                string[] jsonModes,
                string[] jsonExpectedTemplates,
                string? bodyContains,
                string? bodyContainsTemplate)
            {
                int total = jsonPaths.Length + (bodyContains == null ? 0 : 1);
                int failed = 0;
                string? first = null;
                for (int i = 0; i < jsonPaths.Length; i++)
                {
                    var failure = EvaluateJson(
                        jsonOk, json, jsonPaths[i], jsonExpected[i], jsonModes[i], jsonExpectedTemplates[i]);
                    if (failure != null)
                    {
                        failed++;
                        first ??= failure;
                    }
                }
                if (bodyContains != null
                    && body.IndexOf(bodyContains, System.StringComparison.Ordinal) < 0)
                {
                    failed++;
                    first ??= "{\"assertion\":\"bodyContains\",\"reason\":\"notFound\",\"expected\":"
                        + Bounded(bodyContainsTemplate ?? string.Empty) + "}";
                }
                return failed == 0
                    ? null
                    : "{\"failed\":" + failed + ",\"of\":" + total + ",\"first\":" + first + "}";
            }

            private static string? EvaluateJson(
                bool jsonOk,
                System.Text.Json.Nodes.JsonNode? json,
                string path,
                string expected,
                string mode,
                string expectedTemplate)
            {
                bool exists = string.Equals(mode, "exists", System.StringComparison.Ordinal);
                bool absent = string.Equals(mode, "absent", System.StringComparison.Ordinal);
                // The author's claim, echoed the way it was written: the expected TEMPLATE for the
                // equality form, the exists flag for the other two.
                var head = "{\"assertion\":\"json\",\"path\":" + Bounded(path);
                var claim = exists
                    ? ",\"exists\":true"
                    : absent
                        ? ",\"exists\":false"
                        : ",\"expected\":" + Bounded(expectedTemplate);
                if (!jsonOk)
                    return head + ",\"reason\":\"notJson\"" + claim + "}";
                int count;
                System.Text.Json.Nodes.JsonNode? single = null;
                try
                {
                    var matches = Json.Path.JsonPath.Parse(path).Evaluate(json).Matches;
                    count = matches == null ? 0 : matches.Count;
                    if (count == 1)
                        single = matches![0].Value;
                }
                catch (System.Exception)
                {
                    // Measured on JsonPath.Net 3.0.2: a filter comparing a number beyond the
                    // library's numeric range throws FormatException over a valid body. The claim
                    // cannot be checked against this body. The exception's own text is not
                    // guaranteed free of body content, so it is not reported.
                    return head + ",\"reason\":\"unevaluable\"" + claim + "}";
                }
                if (exists)
                    return count > 0 ? null : head + ",\"reason\":\"missing\"" + claim + "}";
                if (absent)
                    return count == 0 ? null : head + ",\"reason\":\"present\"" + claim + ",\"count\":" + count + "}";
                if (count == 0)
                    return head + ",\"reason\":\"missing\"" + claim + "}";
                if (count > 1)
                    return head + ",\"reason\":\"multipleNodes\"" + claim + ",\"count\":" + count + "}";
                if (string.Equals(CanonicalText(single), expected, System.StringComparison.Ordinal))
                    return null;
                return head + ",\"reason\":\"mismatch\"" + claim + ",\"actualKind\":\"" + KindOf(single) + "\"}";
            }

            // The text `capture` stores for a node — a string by its unescaped value, a number or
            // boolean by its JSON spelling, an object or array as compact JSON — plus the one case
            // capture never stores: a present JSON null, compared as the text null.
            private static string CanonicalText(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is null)
                    return "null";
                if (node is System.Text.Json.Nodes.JsonValue value)
                {
                    var raw = value.GetValue<System.Text.Json.JsonElement>();
                    return raw.ValueKind == System.Text.Json.JsonValueKind.String
                        ? raw.GetString() ?? string.Empty
                        : raw.GetRawText();
                }
                return node.ToJsonString();
            }

            private static string KindOf(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is null)
                    return "null";
                switch (node.GetValueKind())
                {
                    case System.Text.Json.JsonValueKind.Object: return "object";
                    case System.Text.Json.JsonValueKind.Array: return "array";
                    case System.Text.Json.JsonValueKind.String: return "string";
                    case System.Text.Json.JsonValueKind.Number: return "number";
                    case System.Text.Json.JsonValueKind.True:
                    case System.Text.Json.JsonValueKind.False: return "boolean";
                    default: return "null";
                }
            }

            // Author text as a JSON string literal, cut at MaxEchoChars on a code-point boundary
            // and marked with a trailing ellipsis when cut.
            private static string Bounded(string text)
            {
                if (text.Length > MaxEchoChars)
                {
                    int cut = MaxEchoChars;
                    if (char.IsHighSurrogate(text[cut - 1]))
                        cut--;
                    text = text.Substring(0, cut) + (char)0x2026;
                }
                return System.Text.Json.JsonSerializer.Serialize(text);
            }
        }
        """,
    };

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("http", "rest");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<HttpRestModel> ────────────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>http.rest</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it
    /// as an <c>if</c>/<c>then</c> clause (§13.6).  The <c>method</c> property
    /// is constrained to a closed enum of HTTP verbs so that the validator and
    /// the IDE can reject invalid verbs early.
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Issues an HTTP request to a logically-named service and optionally asserts on the response status and body.",
          "type": "object",
          "required": ["target", "method", "path"],
          "properties": {
            "target": {
              "description": "Logical name of the service to call, as declared under environment.services.",
              "type": "string",
              "minLength": 1
            },
            "method": {
              "description": "The HTTP verb.",
              "type": "string",
              "enum": ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"]
            },
            "path": {
              "description": "The request path; may contain variable placeholders. Must be a rooted relative path (start with a single '/'); absolute URLs, protocol-relative paths ('//…'), and backslashes are rejected as an SSRF guard.",
              "type": "string",
              "pattern": "^/(?!/)[^\\\\]*$"
            },
            "headers": {
              "description": "Optional map of request header names to values.",
              "type": "object",
              "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
            },
            "body": {
              "description": "Optional request body, given inline as YAML and serialised to JSON."
            },
            "expect": {
              "description": "Optional assertion block applied to the HTTP response: status, json (JSONPath assertions over a JSON body) and bodyContains (a body substring). Body assertions are evaluated only once the status check has passed.",
              "type": "object",
              "properties": {
                "status": {
                  "description": "Expected HTTP status code. When written as a string it must be all digits (e.g. \"200\"); a non-digit string is always a mistake, since the value is never {placeholder}-substituted.",
                  "type": ["integer", "string"],
                  "pattern": "^[0-9]+$"
                },
                "json": {
                  "description": "Optional map of JSONPath expressions (RFC 9535, used verbatim) to expectations over the response body parsed as JSON. A scalar asserts that the path selects exactly one node whose value, compared as text, equals it (a string by its unescaped value, a number or boolean by its JSON spelling, null as the text null, an object or array as compact JSON); a bare numeric or boolean scalar is read as its literal text. { exists: true } / { exists: false } asserts that the path selects at least one node / none. Values may contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "object",
                  "minProperties": 1,
                  "propertyNames": { "pattern": "^\\$" },
                  "additionalProperties": {
                    "type": ["string", "integer", "number", "boolean", "object"],
                    "properties": {
                      "exists": {
                        "description": "true: the path must select at least one node; false: none.",
                        "type": "boolean"
                      }
                    },
                    "minProperties": 1,
                    "maxProperties": 1,
                    "additionalProperties": false
                  }
                },
                "bodyContains": {
                  "description": "Optional substring the response body, decoded as text, must contain (ordinal). May contain {placeholder} and ${secret:source/path} tokens. May be written as a bare number/boolean scalar; it is matched as text either way.",
                  "type": ["string", "integer", "number", "boolean"],
                  "minLength": 1
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    public HttpRestModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new HttpRestModel(
                Target: string.Empty,
                Method: string.Empty,
                Path: string.Empty,
                Headers: null,
                Body: null,
                Expect: null);
        }

        var target = GetScalar(mapping, "target");
        var method = GetScalar(mapping, "method");
        var path = GetScalar(mapping, "path");

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

        // S07-B-02a: bring the request body into scope as a RAW template string.
        //   • A YAML scalar body (raw string / inline JSON) is kept as its literal
        //     string — the author owns the exact bytes (e.g. an inline JSON document).
        //   • A YAML mapping/sequence body is serialised to a JSON string here so the
        //     author can write structured YAML and have it sent as JSON.
        // Either way the result is a TEMPLATE: any {placeholder} / ${secret:source/path}
        // token survives verbatim into the model and is resolved at execution time
        // inside the emitted helper's guarded region (never at bind/compile time, §17).
        string? body = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("body"), out var bodyNode))
        {
            if (bodyNode is YamlScalarNode bodyScalar)
            {
                body = bodyScalar.Value ?? string.Empty;
            }
            else
            {
                // Mapping / sequence: serialise the YAML structure to a JSON string, under
                // the depth and node bounds documented on MaxBodyDepth / MaxBodyNodes
                // (issue #346).  The step mapping is threaded in so a breach can name the
                // step; it is read only on the failure path.
                var budget = MaxBodyNodes;
                body = JsonSerializer.Serialize(
                    YamlToJsonElement(bodyNode, mapping, depth: 1, ref budget));
            }
        }

        HttpExpect? expect = null;
        if (mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            && expectNode is YamlMappingNode expectMap)
        {
            int? status = null;
            if (expectMap.Children.TryGetValue(new YamlScalarNode("status"), out var statusNode)
                && statusNode is YamlScalarNode statusScalar
                && int.TryParse(statusScalar.Value, out var statusCode))
            {
                status = statusCode;
            }

            // Response-body assertions (#558). Both are bound as RAW author text — a
            // {placeholder} / ${secret:source/path} token survives verbatim and is resolved only
            // at step-execution time, inside the emitted helper's guarded region (§17). Bind
            // never throws: a malformed shape binds to something Validate refuses by name.
            IReadOnlyList<HttpJsonAssertion>? json = null;
            if (expectMap.Children.TryGetValue(new YamlScalarNode("json"), out var jsonNode))
            {
                json = BindJsonAssertions(jsonNode);
            }

            string? bodyContains = null;
            if (expectMap.Children.TryGetValue(new YamlScalarNode("bodyContains"), out var containsNode))
            {
                // A YAML null or a non-scalar binds to the empty string, which Validate refuses.
                bodyContains = containsNode is YamlScalarNode containsScalar && !IsYamlNull(containsScalar)
                    ? containsScalar.Value ?? string.Empty
                    : string.Empty;
            }

            expect = new HttpExpect(status, json, bodyContains);
        }

        return new HttpRestModel(
            Target: target,
            Method: method,
            Path: path,
            Headers: headers,
            Body: body,
            Expect: expect);
    }

    // ── IStepValidator<HttpRestModel> ─────────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(HttpRestModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
        {
            errors.Add("http.rest: 'target' must not be empty.");
        }
        else if (!ctx.DeclaredServices.ContainsKey(model.Target))
        {
            if (ctx.DeclaredDependencies.ContainsKey(model.Target))
            {
                // M1 fix (fix round 2, narrows REQ-012's literal wording — see
                // specs/authenticated-infrastructure-mtls.md's REQ-012 note): http.rest
                // resolves 'target' EXCLUSIVELY against declared services
                // (VarKeys.Service(model.Target), staged only for services — never
                // conn::<target>, which a dependency stages into instead). Before this fix a
                // target naming a declared dependency validated PASS and then could never
                // work at run time: exactly the class of gap the split EDGE-009 exists to
                // close, just on the dependency side instead of the "unknown name" side.
                var services = ctx.DeclaredServices.Count == 0
                    ? "(none)"
                    : string.Join(", ", ctx.DeclaredServices.Keys.OrderBy(k => k, StringComparer.Ordinal));
                errors.Add(
                    $"http.rest: 'target' '{model.Target}' names a dependency declared in " +
                    "environment.dependencies, which http.rest cannot reach — it resolves " +
                    "'target' only against declared services. Declared services: " +
                    services + ".");
            }
            else
            {
                // REQ-012/EDGE-009 (services-generalisation spec): close the previously
                // unvalidated-target hole — http.rest accepted ANY target string, with no
                // reconciliation against declared infrastructure at all, because IProjectContext
                // had no DeclaredServices before REQ-010. Fires at validate time, not later as a
                // runtime "bootstrap not found" EnvironmentError.
                errors.Add(
                    $"http.rest: 'target' '{model.Target}' names neither a declared service in " +
                    "environment.services nor a declared dependency in environment.dependencies. " +
                    ProjectContextDescriptions.DescribeDeclaredSurfaces(ctx));
            }
        }

        if (string.IsNullOrWhiteSpace(model.Method))
            errors.Add("http.rest: 'method' must not be empty.");
        else if (!s_allowedMethods.Contains(model.Method))
            errors.Add($"http.rest: 'method' must be one of GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS; got '{model.Method}'.");

        if (string.IsNullOrWhiteSpace(model.Path))
        {
            errors.Add("http.rest: 'path' must not be empty.");
        }
        else
        {
            // SSRF guard (M1): path must be a safe rooted relative reference.
            // Reject absolute URLs, protocol-relative paths, backslash paths, and
            // paths that do not start with '/' (§security hardening PR #131).
            //
            // NOTE: Uri.TryCreate(path, UriKind.Absolute, …) is intentionally absent.
            // On Linux a leading '/' parses as a file URI (file:///foo), so that check
            // would reject valid rooted paths on that platform.  The three guards below
            // are fully platform-independent and together cover every unsafe form:
            //   • !StartsWith('/')           — rejects scheme-bearing URLs (http://…) and
            //                                  bare relative paths such as "users/123".
            //   • StartsWith("//", …)        — rejects protocol-relative paths (//evil/…).
            //   • Contains('\\', …)          — rejects backslash paths.
            // A scheme-bearing absolute URL always starts with a letter, not '/', so the
            // first guard catches it without any Uri parsing.
            var path = model.Path;
            if (!path.StartsWith('/'))
            {
                errors.Add(
                    "http.rest: 'path' must be a rooted relative path (start with '/'); " +
                    "absolute URLs and protocol-relative paths are not allowed.");
            }
            else if (path.StartsWith("//", StringComparison.Ordinal))
            {
                errors.Add(
                    "http.rest: 'path' must be a rooted relative path (start with '/'); " +
                    "absolute URLs and protocol-relative paths are not allowed.");
            }
            else if (path.Contains('\\', StringComparison.Ordinal))
            {
                errors.Add(
                    "http.rest: 'path' must not contain backslashes; " +
                    "use forward slashes for URL path separators.");
            }
        }

        // Response-body assertions (#558). Every refusal is a ValidationResult failure, never a
        // throw (#480: a throw from Validate is a provider fault), so `vouchfx validate` names
        // the problem before any container starts.
        if (model.Expect is { } expect)
        {
            ValidateBodyAssertions(model.Method, expect, errors);
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    /// <summary>
    /// Adds the refusals for <c>expect.json</c> and <c>expect.bodyContains</c> (#558) to
    /// <paramref name="errors"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the engine's own path the composed schema has already refused an empty map, a
    /// key that does not start with <c>$</c>, a YAML-null or malformed value and an empty
    /// <c>bodyContains</c>; those rows here are the backstop for a caller that binds without
    /// validating. The two rules the schema cannot express live only here: a key that starts
    /// with <c>$</c> but is not a JSONPath, and a body assertion on a request or status whose
    /// response carries no content.
    /// </para>
    /// <para>
    /// The JSONPath check uses <c>TryParse</c>, never <c>Parse</c>, so a malformed path is
    /// reported by name and never escapes as an exception. MEASURED on JsonPath.Net 3.0.2:
    /// the parse time of nested filter selectors (<c>[?@[?@…]]</c>) roughly quadruples every
    /// two levels — about 0.4 s at twenty levels. That is author text, the same parse
    /// <c>capture</c> already pays at run time, and far beyond any path a suite needs, so it
    /// is recorded here rather than bounded.
    /// </para>
    /// </remarks>
    private static void ValidateBodyAssertions(string method, HttpExpect expect, List<string> errors)
    {
        if (expect.Json is { } entries)
        {
            if (entries.Count == 0)
            {
                errors.Add(
                    "http.rest: 'expect.json' must be a non-empty map of JSONPath expressions to " +
                    "expected values.");
            }

            foreach (var entry in entries)
            {
                // RFC 9535 refuses, for instance, "$.a[" (unterminated), "a.b" (no root),
                // "$.a.length()" (not a JSONPath function) and "{orderId}" — the key is used
                // verbatim and is never {placeholder}-substituted.
                if (!Json.Path.JsonPath.TryParse(entry.Path, out _))
                {
                    errors.Add(
                        $"http.rest: 'expect.json' key '{entry.Path}' is not a valid JSONPath " +
                        "(RFC 9535). A path starts at the root '$', for example '$.id' or " +
                        "'$.lines[0].sku', and is used verbatim: it is never " +
                        "{placeholder}-substituted.");
                }

                if ((entry.Expected is null) == (entry.Exists is null))
                {
                    errors.Add(
                        $"http.rest: 'expect.json' entry '{entry.Path}' must be either a scalar " +
                        "expected value or { exists: true } / { exists: false }. A JSON null is " +
                        "written as the quoted text \"null\"; an unquoted null or an empty value " +
                        "is refused as a forgotten value.");
                }
            }
        }

        if (expect.BodyContains is { Length: 0 })
        {
            errors.Add(
                "http.rest: 'expect.bodyContains' must not be empty: every body contains the " +
                "empty string, so the assertion could never fail.");
        }

        if (expect.Json is not { Count: > 0 } && expect.BodyContains is null)
            return;

        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "http.rest: 'expect.json' and 'expect.bodyContains' cannot be used with method " +
                "HEAD: a HEAD response carries no content (RFC 9110), so a body assertion could " +
                "never hold. Assert on the body of a GET instead.");
        }

        if (expect.Status is 204 or 304)
        {
            var code = expect.Status.Value.ToString(CultureInfo.InvariantCulture);
            errors.Add(
                "http.rest: 'expect.json' and 'expect.bodyContains' cannot be used with " +
                "'expect.status: " + code + "': a " + code + " response carries no content " +
                "(RFC 9110), so a body assertion could never hold.");
        }
    }

    // ── IStepCompiler<HttpRestModel> ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Sprint-3 (S03-F-01/F-02) + Sprint-4 (S04-B-02/B-03): emits a CSX block
    /// whose execution:
    /// <list type="bullet">
    ///   <item>Resolves <c>{placeholder}</c> tokens in the <c>path</c> via
    ///   <c>Substitute_Helpers.Resolve</c> (B-03).</item>
    ///   <item>Issues the HTTP request.</item>
    ///   <item>When <c>expect.json</c> or <c>expect.bodyContains</c> is declared (#558),
    ///   evaluates them against the response body once the status check has held: any
    ///   that does not hold → <see cref="Verdict.Fail"/>, and the captures do not run.  An
    ///   expected value naming an unset <c>{placeholder}</c> →
    ///   <see cref="Verdict.Inconclusive"/> before anything is sent.</item>
    ///   <item>When <see cref="ICompileContext.Captures"/> is non-empty, reads the
    ///   response body and evaluates each JSONPath via JsonPath.Net.  A miss →
    ///   <see cref="Verdict.Inconclusive"/> (upstream-capture-unmet, §12.1).</item>
    ///   <item>Writes a typed <see cref="StepOutcome"/> into
    ///   <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c>.</item>
    ///   <item>Writes per-capture matched flags to
    ///   <c>Vars[VarKeys.CaptureStatus(sanitisedStepId)]</c> for G-01.</item>
    /// </list>
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full helper class definitions;
    ///   byte-identical across instances.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block;
    ///   no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped
    ///   C# string literals.</item>
    ///   <item>The <c>expect.status</c> integer (or <c>null</c>) is emitted as a bare
    ///   literal, not as a string.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(HttpRestModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Emit expect.status as a bare int literal or 'null' — not a quoted string.
        // This is safe because it is a bounded integer value, not user-controlled text.
        var expectedLiteral = model.Expect?.Status is int st
            ? st.ToString(CultureInfo.InvariantCulture)
            : "null";

        // S04-B-03 + S05-B-02: the 'path' is emitted as the RAW template literal
        // (JSON-escaped C# string literal).  Substitution + secret resolution now happen
        // INSIDE ExecuteAsync's guarded region (so a missing secret maps to a per-step
        // EnvironmentError, never escapes the step).  Any {placeholder} or
        // ${secret:source/path} token inside the literal survives as LITERAL TEXT here
        // (not an emit-time interpolation hole) and is processed at runtime.
        // CRITICAL: we are inside a $$"""…""" block, so {{expr}} is the interpolation
        // hole; a lone {placeholder} or ${secret:…} passes through verbatim.
        var pathTemplateLiteral = JsonSerializer.Serialize(model.Path);

        // S07-B-02a: the request body is emitted as the RAW template literal too, or as
        // the bare C# literal 'null' when no body is declared.  Like the path/header
        // values, the body is NOT pre-resolved at emit time — ExecuteAsync substitutes
        // {placeholder} tokens and reveals ${secret:source/path} references at runtime,
        // inside the guarded region, so a missing secret in the body is a step-scoped
        // EnvironmentError and substitution reads runtime Vars.  No secret value is ever
        // baked into the emitted IL — only the reference token text is.
        var bodyTemplateLiteral = model.Body is null
            ? "null"
            : JsonSerializer.Serialize(model.Body);

        // S05-B-02: expand the headers map into parallel name/value-template arrays.
        // Values are emitted as RAW templates; ExecuteAsync substitutes then secret-
        // resolves each at runtime, inside the guarded region.  No secret value is ever
        // baked into the emitted IL — only the reference token text is.
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

        // S04-B-02 + S07-B-01b: expand the FORMAT-AWARE captures map into THREE
        // parallel arrays — var-names, expressions, and kinds ("json"/"xpath") — in
        // the same declaration (iteration) order.  ctx.CaptureExprs supersedes the
        // back-compat ctx.Captures view: it carries CaptureExpr.Format so http.rest
        // can dispatch JSONPath vs XPath at runtime.  Keys/order match ctx.Captures
        // exactly (both are projections of one capture map, §ICompileContext).
        string[] captureVarNames;
        string[] captureExprs;
        string[] captureKinds;
        if (ctx.CaptureExprs is { Count: > 0 } captureMap)
        {
            captureVarNames = new string[captureMap.Count];
            captureExprs = new string[captureMap.Count];
            captureKinds = new string[captureMap.Count];
            var ci = 0;
            foreach (var (name, expr) in captureMap)
            {
                captureVarNames[ci] = name;
                captureExprs[ci] = expr.Expression;
                // Kind tokens are a FIXED closed vocabulary ("json"/"xpath"), never
                // author-controlled text — emitted verbatim and matched in the helper.
                captureKinds[ci] = expr.Format == CaptureFormat.XPath ? "xpath" : "json";
                ci++;
            }
        }
        else
        {
            captureVarNames = Array.Empty<string>();
            captureExprs = Array.Empty<string>();
            captureKinds = Array.Empty<string>();
        }

        var captureVarNamesLiteral = BuildStringArrayLiteral(captureVarNames);
        var captureExprsLiteral = BuildStringArrayLiteral(captureExprs);
        var captureKindsLiteral = BuildStringArrayLiteral(captureKinds);

        // Response-body assertions (#558): expect.json expands into THREE parallel arrays in
        // declaration order — the JSONPath (verbatim), the expected-value TEMPLATE ("" for the
        // exists forms) and a mode token — plus the bodyContains template or the bare literal
        // null. Mode tokens are a FIXED closed vocabulary (equals / exists / absent), never
        // author text. Templates are emitted RAW, exactly like the path, headers and body: the
        // helper resolves them at step-execution time inside its guarded region, so no secret
        // value is ever baked into the emitted IL — only the reference text is (§17).
        var jsonEntries = model.Expect?.Json ?? Array.Empty<HttpJsonAssertion>();
        var jsonPaths = new string[jsonEntries.Count];
        var jsonExpectedTemplates = new string[jsonEntries.Count];
        var jsonModes = new string[jsonEntries.Count];
        for (var ji = 0; ji < jsonEntries.Count; ji++)
        {
            var entry = jsonEntries[ji];
            jsonPaths[ji] = entry.Path;
            jsonExpectedTemplates[ji] = entry.Expected ?? string.Empty;
            jsonModes[ji] = entry.Exists switch
            {
                true => "exists",
                false => "absent",
                null => "equals",
            };
        }

        var jsonPathsLiteral = BuildStringArrayLiteral(jsonPaths);
        var jsonExpectedTemplatesLiteral = BuildStringArrayLiteral(jsonExpectedTemplates);
        var jsonModesLiteral = BuildStringArrayLiteral(jsonModes);
        var bodyContainsLiteral = model.Expect?.BodyContains is { } bodyContains
            ? JsonSerializer.Serialize(bodyContains)
            : "null";

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        //
        // String arguments are emitted via JsonSerializer.Serialize, which wraps each
        // value in double-quotes and escapes embedded quotes, backslashes, or control
        // characters — preventing CSX-literal breakage and removing a string-injection
        // surface.  'Secrets' is the ScriptGlobalVariables.Secrets instance property.
        var block = $$"""
            {
                await HttpRest_Helpers.ExecuteAsync(
                    Vars,
                    Secrets,
                    Security,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{JsonSerializer.Serialize(model.Target)}},
                    {{JsonSerializer.Serialize(model.Method)}},
                    {{pathTemplateLiteral}},
                    {{bodyTemplateLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    {{expectedLiteral}},
                    {{jsonPathsLiteral}},
                    {{jsonExpectedTemplatesLiteral}},
                    {{jsonModesLiteral}},
                    {{bodyContainsLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureExprsLiteral}},
                    {{captureKindsLiteral}},
                    __stepCt_{{safeId}},
                    __stepBudgetGoverned_{{safeId}});
            }
            """;

        // Build the helpers list: HttpRest_Helpers + Substitute_Helpers (B-03) +
        // Secret_Helpers (S05-B-02).  Both helper sources are byte-identical across
        // providers — deduplication is handled by CsxAssembler.
        var helpers = new List<string>(s_helpers)
        {
            SubstituteHelper.Source,
            SecretHelper.Source,
            SecurityHelper.Source,
        };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<HttpRestModel> ───────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(HttpRestModel model)
    {
        yield return new ResourceRequirement(
            Family: "http",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>System.Net.Http</c> assembly (already required for the
    /// helper), the <c>JsonPath.Net</c> assembly so the Roslyn compiler can
    /// resolve <c>Json.Path.JsonPath</c> in the JSONPath capture logic (S04-B-02),
    /// and <c>System.Private.Xml</c> so it can resolve
    /// <c>System.Xml.XmlDocument</c> / <c>System.Xml.XPath.XPathNavigator</c> in the
    /// XPath capture logic (S07-B-01b).
    /// All assemblies are already loaded in the Default ALC and must never be
    /// loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            // JsonPath.Net: Json.Path.JsonPath is in the Json.Path namespace.
            yield return typeof(Json.Path.JsonPath).Assembly;
            // System.Private.Xml: XmlDocument + XPathNavigator (XPath capture, S07-B-01b).
            yield return typeof(System.Xml.XmlDocument).Assembly;
        }
    }

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is the response-body Fail shape
    /// (#558) that this provider renders as an expected-vs-observed row.
    /// </summary>
    /// <remarks>
    /// Recognised: <c>{"status":…,"expected":…,"body":{"failed":k,"of":n,"first":{…}}}</c>,
    /// whose <c>first</c> names an <c>assertion</c> and a <c>reason</c>. Every other shape
    /// this provider emits — pass, status mismatch, capture miss, unset placeholder, timeout,
    /// secret and transport errors — returns <see langword="false"/>, so each renders exactly
    /// as it did before the body assertions existed: with no diff.
    /// </remarks>
    /// <inheritdoc cref="IStepDiffRenderer.CanRender" />
    public bool CanRender(JsonElement observation) => TryReadBodyFailure(observation, out _);

    /// <summary>
    /// Renders the first failing response-body assertion as a one-row
    /// <c>assertion │ expected │ actual</c> table, or returns <see langword="null"/> when
    /// <paramref name="observation"/> is not that shape (see <see cref="CanRender"/>).
    /// </summary>
    /// <remarks>
    /// The actual column is never text from the response body, because the observation
    /// carries none (§17). It says what was found instead: the JSON kind of a node whose
    /// value differed, a node count, or that the body is not JSON. A line under the table
    /// counts any further failing assertions, and a mismatch on a number or a boolean adds a
    /// fixed note that values compare by their exact JSON spelling — the one trap a
    /// kind-only diff would otherwise hide.
    /// </remarks>
    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (!TryReadBodyFailure(observation, out var row))
            return null;

        var sb = new System.Text.StringBuilder(RenderTable(
            s_diffHeaders,
            new[] { row.Assertion, row.Expected, row.Actual }));

        if (row.Failed > 1)
        {
            var more = row.Failed - 1;
            sb.Append("(+")
              .Append(more.ToString(CultureInfo.InvariantCulture))
              .Append(more == 1 ? " more failing body assertion)" : " more failing body assertions)")
              .Append('\n');
        }

        if (row.SpellingNote)
        {
            sb.Append(
                "note: numbers and booleans compare by their exact JSON spelling " +
                "(2 is not 2.0, true is not True)\n");
        }

        return sb.ToString();
    }

    /// <summary>The diff table's column headers.</summary>
    private static readonly string[] s_diffHeaders = { "assertion", "expected", "actual" };

    /// <summary>One rendered row of a response-body Fail observation.</summary>
    private readonly record struct BodyFailureRow(
        string Assertion,
        string Expected,
        string Actual,
        int Failed,
        bool SpellingNote);

    /// <summary>
    /// Reads the <c>body</c> member of a response-body Fail observation into the cells of
    /// the diff row. Tolerant of fields it does not know; false for any other shape.
    /// </summary>
    private static bool TryReadBodyFailure(JsonElement observation, out BodyFailureRow row)
    {
        row = default;
        if (observation.ValueKind != JsonValueKind.Object
            || !observation.TryGetProperty("body", out var body)
            || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("first", out var first)
            || first.ValueKind != JsonValueKind.Object
            || !TryGetString(first, "assertion", out var assertion)
            || !TryGetString(first, "reason", out var reason))
        {
            return false;
        }

        var failed = TryGetInt(body, "failed", out var f) ? f : 1;
        var count = TryGetInt(first, "count", out var c) ? c : 0;
        TryGetString(first, "actualKind", out var actualKind);

        var label = assertion == "json" && TryGetString(first, "path", out var path)
            ? "json " + path
            : assertion;

        string expected;
        if (first.TryGetProperty("exists", out var existsEl)
            && existsEl.ValueKind is (JsonValueKind.True or JsonValueKind.False))
        {
            expected = existsEl.ValueKind == JsonValueKind.True ? "(present)" : "(absent)";
        }
        else
        {
            expected = TryGetString(first, "expected", out var e) ? e : string.Empty;
        }

        var countText = count.ToString(CultureInfo.InvariantCulture);
        var actual = reason switch
        {
            "mismatch" => actualKind.Length > 0 ? "(" + actualKind + ")" : "(a different value)",
            "missing" => "(no node)",
            "multipleNodes" or "present" => count == 1 ? "(1 node)" : "(" + countText + " nodes)",
            "notJson" => "(body is not JSON)",
            "unevaluable" => "(path could not be evaluated)",
            "notFound" => "(not found)",
            _ => "(" + reason + ")",
        };

        row = new BodyFailureRow(
            Assertion: Cell(label),
            Expected: Cell(expected),
            Actual: Cell(actual),
            Failed: failed,
            SpellingNote: reason == "mismatch" && actualKind is ("number" or "boolean"));
        return true;
    }

    /// <summary>Reads a string property, or yields the empty string and false.</summary>
    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Reads an Int32 property, or yields zero and false.</summary>
    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    /// <summary>
    /// Keeps a table cell on one line and in one column. Author text can hold a line break,
    /// which would split the row, or the <c>│</c> column delimiter, which would add a column;
    /// each is written as an escape sequence instead: <c>\r</c>, <c>\n</c> and <c>\t</c> by
    /// name, and the delimiter and the other line terminators (vertical tab, form feed, NEL,
    /// and the Unicode line and paragraph separators) as <c>\uXXXX</c>.
    /// </summary>
    private static string Cell(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '│':
                case '\v':
                case '\f':
                case '\u0085':
                case '\u2028':
                case '\u2029':
                    sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a single-row, fixed-column box-drawing table: a header row, a separator
    /// rule, then one value row — the style the relational diff renderers use.
    /// </summary>
    private static string RenderTable(string[] headers, string[] values)
    {
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = Math.Max(headers[i].Length, values[i].Length);
        }

        var sb = new System.Text.StringBuilder();
        AppendRow(sb, headers, widths);
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0)
                sb.Append('┼');
            sb.Append(new string('─', widths[i] + 2));
        }

        sb.Append('\n');
        AppendRow(sb, values, widths);
        return sb.ToString();
    }

    /// <summary>
    /// Appends one padded, '│'-separated table row terminated by a newline.
    /// </summary>
    private static void AppendRow(System.Text.StringBuilder sb, string[] cells, int[] widths)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append('│');
            sb.Append(' ');
            sb.Append(cells[i].PadRight(widths[i]));
            sb.Append(' ');
        }

        sb.Append('\n');
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a C# array-initialiser literal from a string array, with each
    /// element individually JSON-serialised to escape embedded quotes, backslashes,
    /// and control characters before splicing into the CSX StatementBlock.
    /// </summary>
    /// <remarks>
    /// Example: <c>["a", "b\"c"]</c> →
    /// <c>new string[] { "a", "b\"c" }</c>
    /// where the inner quotes are escaped by <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions)"/>.
    /// </remarks>
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

    /// <summary>
    /// Binds <c>expect.json</c> into its entries, in declaration order (#558).
    /// </summary>
    /// <remarks>
    /// A scalar value is the equality form and keeps its literal text, so a bare <c>2</c>
    /// is the text <c>2</c> and a bare <c>True</c> the text <c>True</c>.
    /// <c>{ exists: true|false }</c> is the existence form, and only with a PLAIN boolean: the
    /// rule the schema bridge (<c>SchemaResources</c>' scalar type resolver) uses to type a
    /// scalar as a JSON boolean, so a quoted <c>"true"</c> is a string here exactly as it is
    /// there. Every other value — a YAML null, a sequence, a mapping with any other key or a
    /// non-boolean <c>exists</c>, quoted or not — binds with neither member set, and a
    /// <c>json</c> that is not a mapping binds to no entries;
    /// <see cref="Validate"/> refuses both. On the engine's own path the schema has already
    /// refused every one of those shapes.
    /// </remarks>
    private static IReadOnlyList<HttpJsonAssertion> BindJsonAssertions(YamlNode node)
    {
        if (node is not YamlMappingNode map)
            return Array.Empty<HttpJsonAssertion>();

        var entries = new List<HttpJsonAssertion>(map.Children.Count);
        foreach (var (k, v) in map.Children)
        {
            var path = k is YamlScalarNode ks ? ks.Value ?? string.Empty : string.Empty;
            switch (v)
            {
                case YamlScalarNode vs when !IsYamlNull(vs):
                    entries.Add(new HttpJsonAssertion(path, vs.Value ?? string.Empty, null));
                    break;
                case YamlMappingNode vm
                    when vm.Children.Count == 1
                         && vm.Children.TryGetValue(new YamlScalarNode("exists"), out var existsNode)
                         && existsNode is YamlScalarNode existsScalar
                         && IsPlain(existsScalar)
                         && bool.TryParse(existsScalar.Value, out var exists):
                    entries.Add(new HttpJsonAssertion(path, null, exists));
                    break;
                default:
                    entries.Add(new HttpJsonAssertion(path, null, null));
                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// True for a plain (unquoted) YAML scalar. <see cref="YamlDotNet.Core.ScalarStyle.Any"/>
    /// counts as plain because a node built in code, rather than parsed, carries no style.
    /// </summary>
    private static bool IsPlain(YamlScalarNode scalar) =>
        scalar.Style is YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any;

    /// <summary>
    /// True for a plain (unquoted) YAML scalar that is a null token: empty, <c>~</c>, or
    /// <c>null</c> in any of its three spellings.
    /// </summary>
    /// <remarks>
    /// A quoted scalar is never null — quoting is how an author writes the TEXT <c>null</c>
    /// (which is how a JSON null is asserted) or an empty string.
    /// </remarks>
    private static bool IsYamlNull(YamlScalarNode scalar)
    {
        if (!IsPlain(scalar))
            return false;

        return scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";
    }

    /// <summary>
    /// Converts a structured YAML node (mapping / sequence / scalar) into a
    /// <see cref="System.Text.Json.Nodes.JsonNode"/> tree so it can be serialised to a
    /// JSON string for a <c>body</c> declared as inline YAML (S07-B-02a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scalars are emitted as the matching JSON type when YAML 1.1 typing is
    /// unambiguous (<c>true</c>/<c>false</c> → boolean, <c>null</c>/<c>~</c> → null,
    /// an integer/decimal literal → number), and as a JSON string otherwise.  This
    /// keeps a structured YAML body's types faithful while leaving any
    /// <c>{placeholder}</c> / <c>${secret:source/path}</c> token as a quoted string
    /// for execution-time resolution.
    /// </para>
    /// <para>
    /// A scalar whose YAML style is quoted is always treated as a string (the author
    /// explicitly quoted it), so a quoted <c>"123"</c> survives as a JSON string.
    /// </para>
    /// <para>
    /// The walk is BOUNDED in two independent dimensions (issue #346): nesting depth
    /// against <see cref="MaxBodyDepth"/>, and total nodes produced against
    /// <see cref="MaxBodyNodes"/>. A breach of either throws
    /// <see cref="InvalidOperationException"/> — an ordinary, catchable exception, which
    /// the engine already reports as an Inconclusive scenario naming the step (issue #413).
    /// See the constants for what each bound is and is not reachable by, and why an alias
    /// is met with a node budget rather than a visited-node set.
    /// </para>
    /// </remarks>
    /// <param name="node">The YAML node to convert.</param>
    /// <param name="step">
    /// The whole step mapping, carried purely so a breach can name the offending step in
    /// its message; read only on the failure path.
    /// </param>
    /// <param name="depth">Nesting depth of <paramref name="node"/>; the body node is 1.</param>
    /// <param name="budget">
    /// Remaining node allowance, decremented once per node PRODUCED and shared across the
    /// whole walk. Passed by reference because an alias expansion has to be charged against
    /// the same allowance as its siblings — that sharing is the amplification defence.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The body nests deeper than <see cref="MaxBodyDepth"/>, or expands to more than
    /// <see cref="MaxBodyNodes"/> nodes.
    /// </exception>
    private static System.Text.Json.Nodes.JsonNode? YamlToJsonElement(
        YamlNode node,
        YamlMappingNode step,
        int depth,
        ref int budget)
    {
        if (depth > MaxBodyDepth)
            throw new InvalidOperationException(DescribeBodyTooDeep(step, node));

        // Charge the node BEFORE materialising it: the budget bounds work still to be done,
        // not work already completed, so an amplifying alias stops AT the ceiling rather
        // than one whole subtree past it.
        if (--budget < 0)
            throw new InvalidOperationException(DescribeBodyTooLarge(step));

        switch (node)
        {
            case YamlMappingNode map:
                {
                    var obj = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (k, v) in map.Children)
                    {
                        var key = k is YamlScalarNode ks ? ks.Value ?? string.Empty : k.ToString();
                        obj[key] = YamlToJsonElement(v, step, depth + 1, ref budget);
                    }
                    return obj;
                }
            case YamlSequenceNode seq:
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var item in seq.Children)
                        arr.Add(YamlToJsonElement(item, step, depth + 1, ref budget));
                    return arr;
                }
            case YamlScalarNode scalar:
                return ScalarToJsonNode(scalar);
            default:
                return System.Text.Json.Nodes.JsonValue.Create(node.ToString());
        }
    }

    /// <summary>
    /// Names the step a diagnostic is about, for the bound-breach messages below.
    /// </summary>
    /// <remarks>
    /// The step id is read from the step mapping rather than from
    /// <see cref="IBindingContext"/>, which is an empty marker interface in the frozen v1
    /// contract and carries no id. <c>StepSpec.RawNode</c> is documented as the FULL step
    /// mapping including the common fields, so <c>id</c> is present on every step the
    /// engine binds. A test that binds a bare provider fragment directly has no <c>id</c>
    /// key; that case gets a neutral phrase rather than an empty quoted string.
    /// </remarks>
    private static string DescribeStep(YamlMappingNode step)
    {
        var id = GetScalar(step, "id");
        return id.Length == 0 ? "an http.rest step" : "step '" + id + "'";
    }

    /// <summary>
    /// Renders the 1-based line and column of <paramref name="node"/> for a diagnostic.
    /// </summary>
    /// <remarks>
    /// Used ONLY by the depth message, where the offending node is a node the parser read at
    /// that position. It is deliberately NOT used by the node-budget message: YamlDotNet's
    /// representation model shares one node instance across every alias site, so the node a
    /// budget breach lands on carries the ANCHOR's mark, and printing it would point the author
    /// at the definition rather than at the <c>*alias</c> that multiplied it.
    /// </remarks>
    private static string DescribeMark(YamlNode node)
    {
        return "line "
            + node.Start.Line.ToString(CultureInfo.InvariantCulture)
            + ", column "
            + node.Start.Column.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Message for a structured <c>body:</c> that nests deeper than
    /// <see cref="MaxBodyDepth"/>.
    /// </summary>
    private static string DescribeBodyTooDeep(YamlMappingNode step, YamlNode offending)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " nests more than "
            + MaxBodyDepth.ToString(CultureInfo.InvariantCulture)
            + " levels deep (reached at "
            + DescribeMark(offending)
            + "). This is an authoring fault, not a fault in the service under test: "
            + "flatten the body, or send it as a pre-serialised JSON string, and re-run. "
            + "On the engine's own path a body this deep is refused earlier still, by "
            + "schema validation; this limit is the backstop for a caller that binds a "
            + "step without validating the document first.";
    }

    /// <summary>
    /// Message for a structured <c>body:</c> that expands past
    /// <see cref="MaxBodyNodes"/> nodes.
    /// </summary>
    private static string DescribeBodyTooLarge(YamlMappingNode step)
    {
        return "http.rest: the 'body' of "
            + DescribeStep(step)
            + " expands to more than "
            + MaxBodyNodes.ToString(CultureInfo.InvariantCulture)
            + " JSON nodes. This is an authoring fault, not a fault in the service under "
            + "test. A YAML anchor expands in full at every '*alias' that refers to it, so "
            + "a few lines can describe an enormous body: check for aliases under 'body', "
            + "or split the payload across steps. The limit exists so this step refuses a "
            + "body it would otherwise materialise in full, and names the step while doing "
            + "it.";
    }

    /// <summary>
    /// Maps a YAML scalar to the closest JSON node, preserving YAML 1.1 typing for
    /// unquoted plain scalars and treating any explicitly-quoted scalar as a string.
    /// </summary>
    private static System.Text.Json.Nodes.JsonValue? ScalarToJsonNode(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;

        // An explicitly-quoted scalar is always a string — honour the author's intent
        // (e.g. "123" stays a string, an inline JSON fragment stays a string).
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted
            or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
        {
            return System.Text.Json.Nodes.JsonValue.Create(value);
        }

        if (value.Length == 0)
            return System.Text.Json.Nodes.JsonValue.Create(string.Empty);

        // YAML 1.1 null tokens.
        if (value is "null" or "Null" or "NULL" or "~")
            return null;

        // YAML 1.1 boolean tokens.
        if (value is "true" or "True" or "TRUE")
            return System.Text.Json.Nodes.JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE")
            return System.Text.Json.Nodes.JsonValue.Create(false);

        // Integer / decimal literals (invariant culture) become JSON numbers.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return System.Text.Json.Nodes.JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return System.Text.Json.Nodes.JsonValue.Create(d);

        // Everything else (including {placeholder} / ${secret:...} tokens) is a string.
        return System.Text.Json.Nodes.JsonValue.Create(value);
    }
}
