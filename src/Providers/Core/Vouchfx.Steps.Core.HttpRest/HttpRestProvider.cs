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
/// </remarks>
[StepProvider]
public sealed class HttpRestProvider
    : IStepProvider,
      IStepBinder<HttpRestModel>,
      IStepValidator<HttpRestModel>,
      IStepCompiler<HttpRestModel>,
      IResourceContributor<HttpRestModel>,
      ICompileReferenceContributor
{
    // ── Allowed HTTP verbs ────────────────────────────────────────────────────

    private static readonly HashSet<string> s_allowedMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS",
        };

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
    /// The helper must be byte-identical across every instance of the same
    /// provider within a suite (§13.3.1 dedup rule); it contains no
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
        "        string outcomeKey,\n" +
        "        string captureStatusKey,\n" +
        "        string serviceKey,\n" +
        "        string method,\n" +
        "        string pathTemplate,\n" +
        "        string? bodyTemplate,\n" +
        "        string[] headerNames,\n" +
        "        string[] headerValueTemplates,\n" +
        "        int? expectedStatus,\n" +
        "        string[] captureVarNames,\n" +
        "        string[] captureExprs,\n" +
        "        string[] captureKinds,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // AllowAutoRedirect=false: a 3xx from the target must not silently\n" +
        "        // bounce the request to a different host (SSRF via redirect, §M1).\n" +
        "        // disposeHandler:true so client.Dispose() in finally releases the handler too.\n" +
        "        var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };\n" +
        "        var client = new System.Net.Http.HttpClient(handler, disposeHandler: true);\n" +
        "        try\n" +
        "        {\n" +
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
        "                var resp = await client.SendAsync(req, ct).ConfigureAwait(false);\n" +
        "                var actual = (int)resp.StatusCode;\n" +
        "                bool ok = expectedStatus.HasValue\n" +
        "                    ? actual == expectedStatus.Value\n" +
        "                    : (actual >= 200 && actual < 300);\n" +
        "                verdict = ok\n" +
        "                    ? Vouchfx.Engine.Abstractions.Verdict.Pass\n" +
        "                    : Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"status\\\":\" + actual +\n" +
        "                    \",\\\"expected\\\":\" +\n" +
        "                    (expectedStatus.HasValue\n" +
        "                        ? expectedStatus.Value.ToString(\n" +
        "                              System.Globalization.CultureInfo.InvariantCulture)\n" +
        "                        : \"null\") + \"}\";\n" +
        "\n" +
        "                // ── S04-B-02 + S07-B-01b: format-aware capture (JSONPath / XPath) ──\n" +
        "                if (captureVarNames.Length > 0 && verdict != Vouchfx.Engine.Abstractions.Verdict.Fail)\n" +
        "                {\n" +
        "                    var bodyStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);\n" +
        "                    // Parse the JSON body ONCE before the per-capture loop (lazily — only\n" +
        "                    // when the first JSONPath capture is hit). A malformed body sets the\n" +
        "                    // cached node to null, which marks every JSONPath capture unmet.\n" +
        "                    System.Text.Json.Nodes.JsonNode? jsonNode = null;\n" +
        "                    bool jsonParsed = false;\n" +
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
          "type": "object",
          "required": ["target", "method", "path"],
          "properties": {
            "target": {
              "description": "Logical name of the service to call, as declared under environment.services.",
              "type": "string"
            },
            "method": {
              "description": "The HTTP verb.",
              "type": "string",
              "enum": ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"]
            },
            "path": {
              "description": "The request path; may contain variable placeholders.",
              "type": "string"
            },
            "headers": {
              "description": "Optional map of request header names to values.",
              "type": "object",
              "additionalProperties": { "type": "string" }
            },
            "body": {
              "description": "Optional request body, given inline as YAML and serialised to JSON."
            },
            "expect": {
              "description": "Optional assertion block applied to the HTTP response.",
              "type": "object",
              "properties": {
                "status": {
                  "description": "Expected HTTP status code.",
                  "type": "integer"
                }
              }
            }
          },
          "additionalProperties": true
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
            body = bodyNode switch
            {
                YamlScalarNode scalar => scalar.Value ?? string.Empty,
                // Mapping / sequence: serialise the YAML structure to a JSON string.
                _ => JsonSerializer.Serialize(YamlToJsonElement(bodyNode)),
            };
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
            expect = new HttpExpect(status);
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
            errors.Add("http.rest: 'target' must not be empty.");

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

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
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
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{JsonSerializer.Serialize(model.Method)}},
                    {{pathTemplateLiteral}},
                    {{bodyTemplateLiteral}},
                    {{headerNamesLiteral}},
                    {{headerValueTemplatesLiteral}},
                    {{expectedLiteral}},
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a C# array-initialiser literal from a string array, with each
    /// element individually JSON-serialised to escape embedded quotes, backslashes,
    /// and control characters before splicing into the CSX StatementBlock.
    /// </summary>
    /// <remarks>
    /// Example: <c>["a", "b\"c"]</c> →
    /// <c>new string[] { "a", "b\"c" }</c>
    /// where the inner quotes are escaped by <see cref="JsonSerializer.Serialize"/>.
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
    /// </remarks>
    private static System.Text.Json.Nodes.JsonNode? YamlToJsonElement(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                {
                    var obj = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (k, v) in map.Children)
                    {
                        var key = k is YamlScalarNode ks ? ks.Value ?? string.Empty : k.ToString();
                        obj[key] = YamlToJsonElement(v);
                    }
                    return obj;
                }
            case YamlSequenceNode seq:
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var item in seq.Children)
                        arr.Add(YamlToJsonElement(item));
                    return arr;
                }
            case YamlScalarNode scalar:
                return ScalarToJsonNode(scalar);
            default:
                return System.Text.Json.Nodes.JsonValue.Create(node.ToString());
        }
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
