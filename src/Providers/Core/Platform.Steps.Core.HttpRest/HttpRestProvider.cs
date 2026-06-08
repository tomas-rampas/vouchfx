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
/// </remarks>
[StepProvider]
public sealed class HttpRestProvider
    : IStepProvider,
      IStepBinder<HttpRestModel>,
      IStepValidator<HttpRestModel>,
      IStepCompiler<HttpRestModel>,
      IResourceContributor<HttpRestModel>
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
        "    /// Uses safe URI resolution (same-authority guard) and disables\n" +
        "    /// automatic redirects to prevent SSRF via 3xx bounces (§security M1).\n" +
        "    /// Timeout verdict = Inconclusive; connection failures = EnvironmentError (§12.1).\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string outcomeKey,\n" +
        "        string serviceKey,\n" +
        "        string method,\n" +
        "        string path,\n" +
        "        int? expectedStatus)\n" +
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
            var path = model.Path;
            if (Uri.TryCreate(path, UriKind.Absolute, out _))
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
            else if (!path.StartsWith('/'))
            {
                errors.Add(
                    "http.rest: 'path' must be a rooted relative path (start with '/'); " +
                    "absolute URLs and protocol-relative paths are not allowed.");
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
    /// Full implementation for Sprint 3 (S03-F-01/F-02): emits a CSX block whose
    /// execution issues an HTTP request, evaluates the response status against
    /// <c>expect.status</c>, and writes a typed <see cref="StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read
    /// after the script returns.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full <c>static class HttpRest_Helpers</c> definition; byte-identical across instances.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block; no <c>using var</c>.</item>
    ///   <item>Model values are emitted as <c>JsonSerializer.Serialize</c>-escaped C# string literals.</item>
    ///   <item>The <c>expect.status</c> integer (or <c>null</c>) is emitted as a bare literal, not as a string.</item>
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

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        //
        // String arguments (outcomeKey, serviceKey, method, path) are emitted via
        // JsonSerializer.Serialize, which wraps each value in double-quotes and
        // escapes any embedded quotes, backslashes, or control characters.
        // This prevents CSX-literal breakage and removes a string-injection surface.
        var block = $$"""
            {
                await HttpRest_Helpers.ExecuteAsync(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{JsonSerializer.Serialize(model.Method)}},
                    {{JsonSerializer.Serialize(model.Path)}},
                    {{expectedLiteral}});
            }
            """;

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: s_helpers,
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }
}
