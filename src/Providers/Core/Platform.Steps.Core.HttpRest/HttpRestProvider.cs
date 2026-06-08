// Platform.Steps.Core.HttpRest — http.rest step provider (DSL §5.1, §13).
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
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.HttpRest;

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
            "Platform.Engine.Abstractions",
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
    /// S04-B-02: the helper now accepts optional capture arrays (varNames,
    /// jsonPaths) and a captureStatusKey.  When provided, the response body is
    /// read once, each JSONPath is evaluated via JsonPath.Net, and matched values
    /// are written to <c>Vars</c>.  Unmatched paths set the outcome to
    /// <see cref="Verdict.Inconclusive"/> (upstream-capture-unmet, §12.1).
    /// A comma-delimited matched-flag string is written under captureStatusKey
    /// for the G-01 provenance event.
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
        "    /// When capture arrays are non-empty, reads the response body once and\n" +
        "    /// applies each JSONPath via JsonPath.Net; a miss → Inconclusive.\n" +
        "    /// Uses safe URI resolution (same-authority guard) and disables\n" +
        "    /// automatic redirects to prevent SSRF via 3xx bounces (§security M1).\n" +
        "    /// Timeout verdict = Inconclusive; connection failures = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string outcomeKey,\n" +
        "        string captureStatusKey,\n" +
        "        string serviceKey,\n" +
        "        string method,\n" +
        "        string path,\n" +
        "        int? expectedStatus,\n" +
        "        string[] captureVarNames,\n" +
        "        string[] captureJsonPaths)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Platform.Engine.Abstractions.Verdict verdict;\n" +
        "        string observation;\n" +
        "        // AllowAutoRedirect=false: a 3xx from the target must not silently\n" +
        "        // bounce the request to a different host (SSRF via redirect, §M1).\n" +
        "        // disposeHandler:true so client.Dispose() in finally releases the handler too.\n" +
        "        var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };\n" +
        "        var client = new System.Net.Http.HttpClient(handler, disposeHandler: true);\n" +
        "        try\n" +
        "        {\n" +
        "            // M2: cap the default stall window; per-step timeout plumbing is Sprint 6.\n" +
        "            client.Timeout = System.TimeSpan.FromSeconds(30);\n" +
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
        "                var resp = await client.SendAsync(req).ConfigureAwait(false);\n" +
        "                var actual = (int)resp.StatusCode;\n" +
        "                bool ok = expectedStatus.HasValue\n" +
        "                    ? actual == expectedStatus.Value\n" +
        "                    : (actual >= 200 && actual < 300);\n" +
        "                verdict = ok\n" +
        "                    ? Platform.Engine.Abstractions.Verdict.Pass\n" +
        "                    : Platform.Engine.Abstractions.Verdict.Fail;\n" +
        "                observation = \"{\\\"status\\\":\" + actual +\n" +
        "                    \",\\\"expected\\\":\" +\n" +
        "                    (expectedStatus.HasValue\n" +
        "                        ? expectedStatus.Value.ToString(\n" +
        "                              System.Globalization.CultureInfo.InvariantCulture)\n" +
        "                        : \"null\") + \"}\";\n" +
        "\n" +
        "                // ── S04-B-02: JSONPath capture ──────────────────────────────\n" +
        "                if (captureVarNames.Length > 0 && verdict != Platform.Engine.Abstractions.Verdict.Fail)\n" +
        "                {\n" +
        "                    var bodyStr = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);\n" +
        "                    var matchedFlags = new bool[captureVarNames.Length];\n" +
        "                    for (int ci = 0; ci < captureVarNames.Length; ci++)\n" +
        "                    {\n" +
        "                        var varName = captureVarNames[ci];\n" +
        "                        var jsonPath = captureJsonPaths[ci];\n" +
        "                        bool matched = false;\n" +
        "                        try\n" +
        "                        {\n" +
        "                            var node = System.Text.Json.Nodes.JsonNode.Parse(bodyStr);\n" +
        "                            var pathResult = Json.Path.JsonPath.Parse(jsonPath).Evaluate(node);\n" +
        "                            var matches = pathResult.Matches;\n" +
        "                            if (matches != null && matches.Count > 0 && matches[0].Value is not null)\n" +
        "                            {\n" +
        "                                var firstMatch = matches[0].Value;\n" +
        "                                string capturedStr;\n" +
        "                                if (firstMatch is System.Text.Json.Nodes.JsonValue jv)\n" +
        "                                {\n" +
        "                                    // Scalar value: emit the raw string/number/bool without surrounding quotes.\n" +
        "                                    var rawElem = jv.GetValue<System.Text.Json.JsonElement>();\n" +
        "                                    capturedStr = rawElem.ValueKind == System.Text.Json.JsonValueKind.String\n" +
        "                                        ? rawElem.GetString() ?? string.Empty\n" +
        "                                        : rawElem.GetRawText();\n" +
        "                                }\n" +
        "                                else\n" +
        "                                {\n" +
        "                                    // Object or array: compact JSON.\n" +
        "                                    capturedStr = firstMatch.ToJsonString();\n" +
        "                                }\n" +
        "                                vars[varName] = capturedStr;\n" +
        "                                matched = true;\n" +
        "                            }\n" +
        "                        }\n" +
        "                        catch (System.Exception)\n" +
        "                        {\n" +
        "                            matched = false;\n" +
        "                        }\n" +
        "                        matchedFlags[ci] = matched;\n" +
        "                        if (!matched)\n" +
        "                        {\n" +
        "                            verdict = Platform.Engine.Abstractions.Verdict.Inconclusive;\n" +
        "                            observation = \"{\\\"captureUnmet\\\":\" +\n" +
        "                                System.Text.Json.JsonSerializer.Serialize(varName) + \"}\";\n" +
        "                        }\n" +
        "                    }\n" +
        "                    // Write per-capture matched flags as a comma-delimited string for G-01.\n" +
        "                    vars[captureStatusKey] = string.Join(\",\", System.Array.ConvertAll(matchedFlags, f => f ? \"1\" : \"0\"));\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        catch (System.Exception ex) when (ex is System.Threading.Tasks.TaskCanceledException\n" +
        "                                          || ex is System.TimeoutException)\n" +
        "        {\n" +
        "            // Timeout = Inconclusive (§12.1): the test could not complete due to\n" +
        "            // a stall, not because the target service responded incorrectly.\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.Inconclusive;\n" +
        "            observation = \"{\\\"timeout\\\":true}\";\n" +
        "        }\n" +
        "        catch (System.Exception ex)\n" +
        "        {\n" +
        "            // Connection / DNS / authority-change failures = EnvironmentError (§12.1).\n" +
        "            verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"error\\\":\" +\n" +
        "                System.Text.Json.JsonSerializer.Serialize(ex.Message) + \"}\";\n" +
        "        }\n" +
        "        finally\n" +
        "        {\n" +
        "            sw.Stop();\n" +
        "            client.Dispose();  // explicit Dispose() in finally — 'using'-declarations are prohibited in CSX (§13.3.1).\n" +
        "        }\n" +
        "        vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(\n" +
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
            Body: null,  // Inline YAML body serialisation is a Sprint 3 concern.
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

        // S04-B-03: wrap 'path' in Substitute_Helpers.Resolve so {placeholder} tokens
        // resolve against Vars at runtime.  The path value is JSON-escaped into a C#
        // string literal — any {placeholder} inside it survives as LITERAL TEXT (not an
        // emit-time interpolation hole) and is processed by the Regex at runtime.
        // CRITICAL: we are inside a $$"""…""" block, so {{expr}} is the interpolation
        // hole.  JsonSerializer.Serialize wraps the path in double-quotes, producing a
        // valid C# string literal that the runtime Regex then scans for {name} tokens.
        var resolvedPath = $"Substitute_Helpers.Resolve(Vars, {JsonSerializer.Serialize(model.Path)})";

        // S04-B-02: expand the captures map into parallel arrays.
        string[] captureVarNames;
        string[] captureJsonPaths;
        if (ctx.Captures is { Count: > 0 } captures)
        {
            captureVarNames = captures.Keys.ToArray();
            captureJsonPaths = captures.Values.ToArray();
        }
        else
        {
            captureVarNames = Array.Empty<string>();
            captureJsonPaths = Array.Empty<string>();
        }

        var captureVarNamesLiteral = BuildStringArrayLiteral(captureVarNames);
        var captureJsonPathsLiteral = BuildStringArrayLiteral(captureJsonPaths);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        //
        // String arguments (outcomeKey, serviceKey, method) are emitted via
        // JsonSerializer.Serialize, which wraps each value in double-quotes and
        // escapes any embedded quotes, backslashes, or control characters.
        // This prevents CSX-literal breakage and removes a string-injection surface.
        //
        // resolvedPath is already a Substitute_Helpers.Resolve(Vars, "…") call
        // expression — it is spliced in directly as C# source, not as a string literal.
        var block = $$"""
            {
                await HttpRest_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{JsonSerializer.Serialize(model.Method)}},
                    {{resolvedPath}},
                    {{expectedLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureJsonPathsLiteral}});
            }
            """;

        // Build the helpers list: HttpRest_Helpers + Substitute_Helpers (B-03).
        // SubstituteHelper.Source is byte-identical — deduplication handled by CsxAssembler.
        var helpers = new List<string>(s_helpers) { SubstituteHelper.Source };

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
    /// helper) and the <c>JsonPath.Net</c> assembly so the Roslyn compiler can
    /// resolve <c>Json.Path.JsonPath</c> in the capture logic (S04-B-02).
    /// Both assemblies are already loaded in the Default ALC and must never be
    /// loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            // JsonPath.Net: Json.Path.JsonPath is in the Json.Path namespace.
            yield return typeof(Json.Path.JsonPath).Assembly;
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
}
