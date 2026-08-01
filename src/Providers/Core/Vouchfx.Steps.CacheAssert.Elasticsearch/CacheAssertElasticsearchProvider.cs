// Vouchfx.Steps.CacheAssert.Elasticsearch — cache-assert.elasticsearch step provider
// (DSL §5, §13).
//
// The FOURTEENTH Core provider and the SECOND member of the cache-assert family
// (cache-assert.redis and cache-assert.elasticsearch; steps always name the dotted
// form — bare family names are not part of the language).
//
// One [StepProvider] class implements seven provider interfaces:
//   IStepProvider, IStepBinder<T>, IStepValidator<T>, IStepCompiler<T>,
//   IResourceContributor<T>, ICompileReferenceContributor, IStepDiffRenderer.
//
// Client strategy (§19 hard invariant — BCL HttpClient only):
//   The emitted helper uses ONLY System.Net.Http.HttpClient and System.Text.Json
//   (both BCL) to POST {index}/_search to the Elasticsearch HTTP API.
//   NO Elastic.Clients.Elasticsearch typed client is used — it is not part of the
//   pinned dependency set and would add a significant transitive footprint.
//   ICompileReferenceContributor contributes the three BCL assemblies that are NOT
//   in the default TPA-only Roslyn reference set:
//     System.Net.Http, System.Text.Json, System.Private.Uri (System.Uri).
//
// Memory model (§5):
//   The emitted helper creates an HttpClient per invocation and Dispose()s it in
//   a finally.  No static singleton crosses the Default/collectible ALC boundary.
//   'using var' is prohibited in a Roslyn script body (§13.3.1) — plain var +
//   explicit Dispose() in finally.  JsonDocument instances are also Dispose()d.
//
// Injection safety (§17):
//   The model.Query field may contain {placeholder} tokens.  The helper resolves
//   them via ResolveQuery, which JSON-serializes the resolved value and strips the
//   outer quotes, so any " or \ in the value is escaped — preventing JSON breakout.
//   Dot-notation is rejected in field assertions at validation time.
//
// Secret model (§17):
//   Three layers protect credentials in observations:
//     1. URL userinfo is extracted on entry and used only as a Basic-auth header;
//        baseUrl is rebuilt as scheme://host:port (no credentials in any log path).
//     2. The generic System.Exception catch writes only ex.GetType().Name — not
//        the URL, message, or stack — so HttpRequestException etc. leak nothing.
//     3. Field expected-value resolved by Secret_Helpers.ResolveTemplate (§17 ledger
//        scrub in BuildStepObservation covers the "expected" field in Fail observations).
//   The emitted helper uses Secret_Helpers.ResolveTemplate for field expected-value
//   templates, so ${secret:source/path} tokens are resolved at execution time.
//
// CsxFragment rules (§13.3.1):
//   • RequiredUsings: bare namespace strings only (no inline 'using' lines).
//   • RequiredHelpers: 'static class CacheAssertElasticsearch_Helpers' plus the
//     shared Substitute_Helpers and Secret_Helpers sources (byte-identical;
//     CsxAssembler dedupes by class name).
//   • StatementBlock: a single C# 11 $$"""…""" block with {{expr}} holes; no
//     'using var'; step id sanitised via CsxFragment.SanitiseId before splicing.
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.CacheAssert.Elasticsearch;

/// <summary>
/// Core provider for the <c>cache-assert.elasticsearch</c> step kind (DSL §5).
/// Posts an Elasticsearch Query DSL body to the declared index and asserts on the
/// returned hit count and optional first-hit <c>_source</c> field values.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.  The
/// engine's <c>SchemaComposer</c> assembles the unified schema by injecting a
/// <c>const</c>-keyed <c>if</c>/<c>then</c> discriminator derived from
/// <see cref="Kind"/> (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose
/// emitted CSX reads the Elasticsearch URL staged at
/// <c>Vars[VarKeys.Connection(model.Target)]</c>, resolves any <c>{placeholder}</c>
/// tokens in the query body, POSTs to <c>{url}/{index}/_search</c>, parses
/// <c>hits.total.value</c>, optionally checks first-hit <c>_source</c> fields,
/// and writes a typed <see cref="StepOutcome"/> into
/// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner (§13.3.1).
/// </para>
/// <para>
/// This is a <c>verifyMode: RETRY</c> provider (§7): the emitted scan is
/// IDEMPOTENT — a count/field mismatch yields <see cref="Verdict.Fail"/>, and
/// the engine-owned RetryRunner re-invokes the delegate and converts a sustained
/// Fail to <see cref="Verdict.Inconclusive"/> on timeout.  The helper never
/// writes Inconclusive.
/// </para>
/// </remarks>
[StepProvider]
public sealed class CacheAssertElasticsearchProvider
    : IStepProvider,
      IStepBinder<CacheAssertElasticsearchModel>,
      IStepValidator<CacheAssertElasticsearchModel>,
      IStepCompiler<CacheAssertElasticsearchModel>,
      IResourceContributor<CacheAssertElasticsearchModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>System.Net.Http</c> (for <c>HttpClient</c>),
    /// <c>System.Text.Json</c> (for <c>JsonDocument</c> / <c>JsonSerializer</c>),
    /// and <c>System.Private.Uri</c> (for <c>System.Uri</c> — defined there with
    /// a type forwarder in <c>System.Runtime</c>; Roslyn requires the actual
    /// defining assembly to avoid CS1069).
    /// All three are BCL assemblies already loaded in the Default ALC — they are
    /// never loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public System.Collections.Generic.IEnumerable<System.Reflection.Assembly>
        CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            yield return typeof(System.Text.Json.JsonDocument).Assembly;
            // System.Uri is defined in System.Private.Uri (type-forwarded from
            // System.Runtime).  Roslyn requires the defining assembly as an explicit
            // metadata reference to avoid CS1069.
            yield return typeof(System.Uri).Assembly;
        }
    }

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("cache-assert", "elasticsearch");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<CacheAssertElasticsearchModel> ────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>cache-assert.elasticsearch</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it as an
    /// <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "index", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the elasticsearch dependency declared under environment.dependencies whose HTTP API this step queries.",
              "type": "string"
            },
            "index": {
              "description": "Elasticsearch index to query.",
              "type": "string"
            },
            "query": {
              "description": "Full Elasticsearch Query DSL JSON body (the entire request body, e.g. '{\"query\":{\"match\":{\"status\":\"active\"}}}').  When absent a match_all query is used.  May contain {placeholder} tokens resolved at execution time.",
              "type": "string"
            },
            "expect": {
              "description": "Expected result-set characteristics.",
              "type": "object",
              "properties": {
                "count": {
                  "description": "Exact number of hits expected.  When set, overrides min-count.",
                  "type": "integer",
                  "minimum": 0
                },
                "min-count": {
                  "description": "Minimum number of hits expected (default 1).  Ignored when count is set.",
                  "type": "integer",
                  "minimum": 0,
                  "default": 1
                },
                "fields": {
                  "description": "Optional field assertions evaluated against the first hit's _source.  Each entry checks that the named top-level field equals the expected value.",
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["field", "value"],
                    "properties": {
                      "field": {
                        "description": "Top-level _source field name.  Dot-notation is not supported.",
                        "type": "string"
                      },
                      "value": {
                        "description": "Expected string value.  May contain {placeholder} and ${secret:source/path} tokens.",
                        "type": "string"
                      }
                    },
                    "additionalProperties": false
                  }
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    /// <remarks>
    /// Defensive bind: returns a safe empty model rather than throwing on a non-mapping
    /// node or absent optional fields (mirrors the Redis / MongoDB provider pattern).
    /// The validator catches all structural problems and surfaces human-readable errors.
    /// </remarks>
    public CacheAssertElasticsearchModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new CacheAssertElasticsearchModel(
                Target: string.Empty,
                Index: string.Empty,
                Query: null,
                Expect: new EsExpectation(Count: null, MinCount: 1, Fields: null));
        }

        // target (required — empty string is caught by Validate)
        var target = GetScalar(mapping, "target");

        // index (required — empty string is caught by Validate)
        var index = GetScalar(mapping, "index");

        // query (optional)
        var query = GetOptionalScalar(mapping, "query");

        // expect (returns defaults if absent or non-mapping — Validate catches the gap)
        var (count, minCount, fields) = BindExpect(mapping);

        return new CacheAssertElasticsearchModel(
            Target: target,
            Index: index,
            Query: query,
            Expect: new EsExpectation(Count: count, MinCount: minCount, Fields: fields));
    }

    private static (int? Count, int MinCount, IReadOnlyList<EsFieldAssertion>? Fields)
        BindExpect(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            || expectNode is not YamlMappingNode expectMapping)
        {
            return (null, 1, null);
        }

        int? count = null;
        if (expectMapping.Children.TryGetValue(new YamlScalarNode("count"), out var countNode)
            && countNode is YamlScalarNode countScalar
            && int.TryParse(countScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
        {
            count = parsedCount;
        }

        var minCount = 1;
        if (expectMapping.Children.TryGetValue(new YamlScalarNode("min-count"), out var minNode)
            && minNode is YamlScalarNode minScalar
            && int.TryParse(minScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMin))
        {
            minCount = parsedMin;
        }

        IReadOnlyList<EsFieldAssertion>? fields = null;
        if (expectMapping.Children.TryGetValue(new YamlScalarNode("fields"), out var fieldsNode)
            && fieldsNode is YamlSequenceNode fieldsSeq)
        {
            var list = new List<EsFieldAssertion>();
            foreach (var item in fieldsSeq)
            {
                if (item is not YamlMappingNode fieldMap)
                    continue;   // skip malformed entries (Validate will catch blank names)

                var fieldName = GetScalar(fieldMap, "field");
                var fieldValue = GetOptionalScalar(fieldMap, "value") ?? string.Empty;

                list.Add(new EsFieldAssertion(fieldName, fieldValue));
            }
            fields = list.Count > 0 ? list : null;
        }

        return (count, minCount, fields);
    }

    private static string GetScalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    private static string? GetOptionalScalar(YamlMappingNode mapping, string key)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar)
        {
            return scalar.Value ?? string.Empty;
        }
        return null;
    }

    // ── IStepValidator<CacheAssertElasticsearchModel> ─────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(
        CacheAssertElasticsearchModel model,
        IProjectContext ctx)
    {
        var errors = new List<string>();

        // target must name a declared elasticsearch dependency.
        if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType)
            || !string.Equals(depType, "elasticsearch", StringComparison.Ordinal))
        {
            errors.Add(
                $"cache-assert.elasticsearch: 'target' '{model.Target}' is not an " +
                "elasticsearch dependency declared in environment.dependencies.");
        }

        // index must not be blank.
        if (string.IsNullOrWhiteSpace(model.Index))
            errors.Add("cache-assert.elasticsearch: 'index' must not be blank.");

        // index must not contain characters that make a malformed URL path segment.
        // Whitespace, '?', '#', and control characters are rejected.
        // ',' (multi-index syntax) and '*' (wildcard pattern) are intentionally allowed.
        if (!string.IsNullOrWhiteSpace(model.Index))
        {
            foreach (var ch in model.Index)
            {
                if (char.IsWhiteSpace(ch) || ch == '?' || ch == '#' || char.IsControl(ch))
                {
                    errors.Add(
                        "cache-assert.elasticsearch: 'index' contains an invalid character. " +
                        "Whitespace, '?', '#', and control characters are not permitted in index names; " +
                        "use ',' for multi-index and '*' for wildcard patterns.");
                    break;
                }
            }
        }

        // query, if provided, must be parseable JSON.
        if (model.Query is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(model.Query);
                // Must be an object (the ES request body).
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    errors.Add("cache-assert.elasticsearch: 'query' must be a JSON object (the full ES request body).");
            }
            catch (JsonException ex)
            {
                errors.Add($"cache-assert.elasticsearch: 'query' is not valid JSON: {ex.Message}");
            }
        }

        // expect.count / min-count must be non-negative.
        if (model.Expect.Count.HasValue && model.Expect.Count.Value < 0)
            errors.Add("cache-assert.elasticsearch: 'expect.count' must be >= 0.");
        if (model.Expect.MinCount < 0)
            errors.Add("cache-assert.elasticsearch: 'expect.min-count' must be >= 0.");

        // field assertions: reject dot-notation field names.
        if (model.Expect.Fields is not null)
        {
            foreach (var fa in model.Expect.Fields)
            {
                if (string.IsNullOrWhiteSpace(fa.Field))
                    errors.Add("cache-assert.elasticsearch: 'expect.fields[].field' must not be blank.");
                else if (fa.Field.Contains('.'))
                    errors.Add(
                        $"cache-assert.elasticsearch: 'expect.fields[].field' value '{fa.Field}' " +
                        "contains a dot.  Only top-level _source fields are supported; " +
                        "nested paths via dot-notation are not.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── IResourceContributor<CacheAssertElasticsearchModel> ───────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Declares the <c>elasticsearch</c> dependency by name.  The orchestrator
    /// maps it to an Elasticsearch container and stages the connection URL at
    /// <c>VarKeys.Connection(model.Target)</c> before the delegate runs.
    /// </remarks>
    public System.Collections.Generic.IEnumerable<ResourceRequirement> Resources(
        CacheAssertElasticsearchModel model)
    {
        yield return new ResourceRequirement(
            Family: "elasticsearch",
            Name: model.Target,
            Image: null);
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlyList<string> s_usings =
        new[]
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
    /// </summary>
    /// <remarks>
    /// <para>
    /// The class name begins with <c>CacheAssertElasticsearch_</c> to prevent
    /// collisions when multiple providers contribute helpers to the same Roslyn
    /// submission.  All types are fully qualified so the helper compiles
    /// independently of the spliced <c>using</c> ordering.  <c>using var</c> is
    /// absent — disposal is explicit in <c>finally</c> blocks (§13.3.1 ban).
    /// </para>
    /// <para>
    /// IDEMPOTENT single scan (§7): the helper queries Elasticsearch once and
    /// writes <see cref="Verdict.Pass"/> on a count/field match or
    /// <see cref="Verdict.Fail"/> on a mismatch.  It NEVER writes
    /// <see cref="Verdict.Inconclusive"/> — the engine-owned RetryRunner
    /// re-invokes and performs the Fail→Inconclusive-on-timeout conversion.
    /// </para>
    /// <para>
    /// INJECTION SAFETY (§17): <c>ResolveQuery</c> resolves <c>{placeholder}</c>
    /// tokens by JSON-serializing the resolved value and stripping the outer
    /// quotes, so any <c>"</c> or <c>\</c> in the value is safely escaped —
    /// preventing JSON breakout (mirrors the MongoDB provider's <c>ResolveFilter</c>).
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider
    /// within a suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Generic.IReadOnlyList<string> s_helpers = new[]
    {
        "static class CacheAssertElasticsearch_Helpers\n" +
        "{\n" +
        "    private static readonly System.Text.RegularExpressions.Regex _placeholderRegex =\n" +
        "        new System.Text.RegularExpressions.Regex(\n" +
        "            @\"\\{([A-Za-z_][A-Za-z0-9_\\-]*)\\}\");\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Executes a cache-assert.elasticsearch step: queries the Elasticsearch HTTP API\n" +
        "    /// and writes a typed StepOutcome into Vars.  HttpClient is disposed in finally.\n" +
        "    /// Pass when the hit count and optional field assertions are satisfied;\n" +
        "    /// Fail when not (RETRY runner converts sustained Fail to Inconclusive on timeout —\n" +
        "    /// this helper NEVER writes Inconclusive, §7/§12.1);\n" +
        "    /// EnvironmentError when the ES URL is absent, the index does not exist, or\n" +
        "    /// the HTTP call fails.\n" +
        "    /// </summary>\n" +
        "    public static async System.Threading.Tasks.Task ExecuteAsync(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string outcomeKey,\n" +
        "        string connKey,\n" +
        "        string index,\n" +
        "        string queryTemplate,\n" +
        "        int minCount,\n" +
        "        int? exactCount,\n" +
        "        string[]? assertFieldNames,\n" +
        "        string[]? assertFieldValues,\n" +
        "        System.Threading.CancellationToken ct,\n" +
        "        bool budgetGoverned)\n" +
        "    {\n" +
        "        var sw = System.Diagnostics.Stopwatch.StartNew();\n" +
        "        Vouchfx.Engine.Abstractions.Verdict verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "        string observation = \"{\\\"error\\\":\\\"unexpected\\\"}\";\n" +
        "        try\n" +
        "        {\n" +
        "            var rawUrl = vars.TryGetValue(connKey, out var u) && u is string us ? us : null;\n" +
        "            if (string.IsNullOrEmpty(rawUrl))\n" +
        "            {\n" +
        "                verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                observation = \"{\\\"error\\\":\\\"Elasticsearch endpoint not found for conn key '\" + connKey + \"'\\\"}\";\n" +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        "                System.Uri parsedUri;\n" +
        "                try { parsedUri = new System.Uri(rawUrl); }\n" +
        "                catch (System.UriFormatException uex)\n" +
        "                {\n" +
        "                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                    observation = \"{\\\"error\\\":\" + System.Text.Json.JsonSerializer.Serialize(uex.GetType().Name) + \"}\";\n" +
        "                    sw.Stop();\n" +
        "                    vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "                    return;\n" +
        "                }\n" +
        "\n" +
        "                // Build the base URL without credentials; extract Basic auth if present.\n" +
        "                string baseUrl;\n" +
        "                string? authHeader = null;\n" +
        "                if (!string.IsNullOrEmpty(parsedUri.UserInfo))\n" +
        "                {\n" +
        "                    var parts = parsedUri.UserInfo.Split(':', 2);\n" +
        "                    var user = System.Uri.UnescapeDataString(parts[0]);\n" +
        "                    var pass = parts.Length > 1 ? System.Uri.UnescapeDataString(parts[1]) : string.Empty;\n" +
        "                    authHeader = System.Convert.ToBase64String(\n" +
        "                        System.Text.Encoding.ASCII.GetBytes(user + \":\" + pass));\n" +
        "                    baseUrl = parsedUri.Scheme + \"://\" + parsedUri.Host + \":\" + parsedUri.Port;\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    baseUrl = rawUrl.TrimEnd('/');\n" +
        "                }\n" +
        "\n" +
        "                // Resolve {placeholder} tokens in the query body (injection-safe: §17).\n" +
        "                var resolvedQuery = ResolveQuery(vars, queryTemplate);\n" +
        "\n" +
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
        "                    if (authHeader is not null)\n" +
        "                        http.DefaultRequestHeaders.Authorization =\n" +
        "                            new System.Net.Http.Headers.AuthenticationHeaderValue(\"Basic\", authHeader);\n" +
        "\n" +
        "                    var url = baseUrl + \"/\" + index + \"/_search\";\n" +
        "                    var content = new System.Net.Http.StringContent(\n" +
        "                        resolvedQuery, System.Text.Encoding.UTF8, \"application/json\");\n" +
        "\n" +
        "                    var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);\n" +
        "                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);\n" +
        "\n" +
        "                    if (!response.IsSuccessStatusCode)\n" +
        "                    {\n" +
        "                        verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                        observation = \"{\\\"error\\\":\\\"HTTP \" + (int)response.StatusCode + \"\\\",\\\"index\\\":\" + System.Text.Json.JsonSerializer.Serialize(index) + \"}\";\n" +
        "                    }\n" +
        "                    else\n" +
        "                    {\n" +
        "                        System.Text.Json.JsonDocument doc;\n" +
        "                        try { doc = System.Text.Json.JsonDocument.Parse(body); }\n" +
        "                        catch (System.Text.Json.JsonException jex)\n" +
        "                        {\n" +
        "                            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                            observation = \"{\\\"error\\\":\\\"Failed to parse Elasticsearch response: \" + System.Text.Json.JsonSerializer.Serialize(jex.Message) + \"\\\"}\";\n" +
        "                            sw.Stop();\n" +
        "                            vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "                            return;\n" +
        "                        }\n" +
        "                        try\n" +
        "                        {\n" +
        "                            int totalHits;\n" +
        "                            try\n" +
        "                            {\n" +
        "                                totalHits = doc.RootElement\n" +
        "                                    .GetProperty(\"hits\")\n" +
        "                                    .GetProperty(\"total\")\n" +
        "                                    .GetProperty(\"value\")\n" +
        "                                    .GetInt32();\n" +
        "                            }\n" +
        "                            catch (System.Exception)\n" +
        "                            {\n" +
        "                                verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "                                observation = \"{\\\"error\\\":\\\"Elasticsearch response missing expected hits.total.value structure\\\"}\";\n" +
        "                                sw.Stop();\n" +
        "                                vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "                                return;\n" +
        "                            }\n" +
        "\n" +
        "                            // Count assertion.\n" +
        "                            bool countPassed;\n" +
        "                            if (exactCount.HasValue)\n" +
        "                                countPassed = totalHits == exactCount.Value;\n" +
        "                            else\n" +
        "                                countPassed = totalHits >= minCount;\n" +
        "\n" +
        "                            if (!countPassed)\n" +
        "                            {\n" +
        "                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                                string expectedDesc = exactCount.HasValue\n" +
        "                                    ? exactCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)\n" +
        "                                    : \">=\" + minCount.ToString(System.Globalization.CultureInfo.InvariantCulture);\n" +
        "                                observation = \"{\\\"matched\\\":false,\\\"expected\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectedDesc) + \",\\\"actual\\\":\" + totalHits.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\\\"index\\\":\" + System.Text.Json.JsonSerializer.Serialize(index) + \"}\";\n" +
        "                            }\n" +
        "                            else\n" +
        "                            {\n" +
        "                                // Optional field assertions on first hit's _source.\n" +
        "                                verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;\n" +
        "                                observation = \"{\\\"matched\\\":true,\\\"hits\\\":\" + totalHits.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"}\";\n" +
        "\n" +
        "                                if (assertFieldNames != null && assertFieldNames.Length > 0)\n" +
        "                                {\n" +
        "                                    var hitsArr = doc.RootElement.GetProperty(\"hits\").GetProperty(\"hits\");\n" +
        "                                    if (hitsArr.ValueKind == System.Text.Json.JsonValueKind.Array && hitsArr.GetArrayLength() > 0)\n" +
        "                                    {\n" +
        "                                        var firstHit = hitsArr[0];\n" +
        "                                        System.Text.Json.JsonElement srcEl;\n" +
        "                                        bool hasSource = firstHit.TryGetProperty(\"_source\", out srcEl);\n" +
        "\n" +
        "                                        for (int fi = 0; fi < assertFieldNames.Length; fi++)\n" +
        "                                        {\n" +
        "                                            var fieldName = assertFieldNames[fi];\n" +
        "                                            var expectedRaw = assertFieldValues != null && fi < assertFieldValues.Length\n" +
        "                                                ? assertFieldValues[fi] : string.Empty;\n" +
        "                                            var expectedResolved = Secret_Helpers.ResolveTemplate(secrets, vars, expectedRaw);\n" +
        "\n" +
        "                                            if (!hasSource)\n" +
        "                                            {\n" +
        "                                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                                                observation = \"{\\\"matched\\\":false,\\\"fieldError\\\":\\\"_source absent in first hit\\\",\\\"field\\\":\" + System.Text.Json.JsonSerializer.Serialize(fieldName) + \"}\";\n" +
        "                                                break;\n" +
        "                                            }\n" +
        "\n" +
        "                                            System.Text.Json.JsonElement fieldEl;\n" +
        "                                            if (!srcEl.TryGetProperty(fieldName, out fieldEl))\n" +
        "                                            {\n" +
        "                                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                                                observation = \"{\\\"matched\\\":false,\\\"fieldError\\\":\\\"field not found in _source\\\",\\\"field\\\":\" + System.Text.Json.JsonSerializer.Serialize(fieldName) + \"}\";\n" +
        "                                                break;\n" +
        "                                            }\n" +
        "\n" +
        "                                            var actualValue = fieldEl.ValueKind == System.Text.Json.JsonValueKind.String\n" +
        "                                                ? fieldEl.GetString() ?? string.Empty\n" +
        "                                                : fieldEl.ToString();\n" +
        "\n" +
        "                                            if (!string.Equals(actualValue, expectedResolved, System.StringComparison.Ordinal))\n" +
        "                                            {\n" +
        "                                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                                                observation = \"{\\\"matched\\\":false,\\\"field\\\":\" + System.Text.Json.JsonSerializer.Serialize(fieldName)\n" +
        "                                                    + \",\\\"expected\\\":\" + System.Text.Json.JsonSerializer.Serialize(expectedResolved)\n" +
        "                                                    + \",\\\"actual\\\":\" + System.Text.Json.JsonSerializer.Serialize(actualValue) + \"}\";\n" +
        "                                                break;\n" +
        "                                            }\n" +
        "                                        }\n" +
        "                                    }\n" +
        "                                    else\n" +
        "                                    {\n" +
        "                                        // Count passed but hits.hits is empty (e.g. size:0 query) — cannot evaluate field assertions.\n" +
        "                                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;\n" +
        "                                        observation = \"{\\\"matched\\\":false,\\\"fieldError\\\":\\\"no hit document available (hits.hits empty)\\\"}\";\n" +
        "                                    }\n" +
        "                                }\n" +
        "                            }\n" +
        "                        }\n" +
        "                        finally\n" +
        "                        {\n" +
        "                            doc.Dispose();\n" +
        "                        }\n" +
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
        "            verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;\n" +
        "            observation = \"{\\\"secretError\\\":\\\"secret resolution failed\\\"\"\n" +
        "                + \",\\\"source\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource)\n" +
        "                + \",\\\"path\\\":\" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + \"}\";\n" +
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
        "        vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Resolves {placeholder} tokens in the query template using injection-safe JSON\n" +
        "    /// escaping (mirrors the MongoDB provider's ResolveFilter — §13, §17).\n" +
        "    /// Each {name} is replaced with the JSON-serialized string value of vars[name]\n" +
        "    /// (outer quotes stripped), so any '\"' or '\\' in the value is safely escaped.\n" +
        "    /// Unresolved placeholders (unknown var name or null value) are left verbatim.\n" +
        "    /// </summary>\n" +
        "    public static string ResolveQuery(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string queryTemplate)\n" +
        "    {\n" +
        "        return _placeholderRegex.Replace(queryTemplate, m =>\n" +
        "        {\n" +
        "            var name = m.Groups[1].Value;\n" +
        "            if (!vars.TryGetValue(name, out var val) || val is null)\n" +
        "                return m.Value;\n" +
        "            var strVal = val is string sv ? sv\n" +
        "                : System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;\n" +
        "            var serialised = System.Text.Json.JsonSerializer.Serialize(strVal);\n" +
        "            return serialised.Length >= 2\n" +
        "                ? serialised.Substring(1, serialised.Length - 2)\n" +
        "                : strVal;\n" +
        "        });\n" +
        "    }\n" +
        "\n" +
        "}",
    };

    // ── IStepCompiler<CacheAssertElasticsearchModel> ──────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution calls
    /// <c>CacheAssertElasticsearch_Helpers.ExecuteAsync</c> with the model's
    /// connection key, index, raw query template, count constraints, and optional
    /// field assertion arrays.  The helper posts to the Elasticsearch <c>_search</c>
    /// endpoint, parses the hit count, and writes a typed <see cref="StepOutcome"/>
    /// into <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c>.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>; the full
    /// <c>static class CacheAssertElasticsearch_Helpers</c> definition plus the
    /// shared <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources in
    /// <see cref="CsxFragment.RequiredHelpers"/>; a single C# 11 <c>$$"""…"""</c>
    /// <see cref="CsxFragment.StatementBlock"/> with no <c>using var</c>; the step
    /// id sanitised via <c>CsxFragment.SanitiseId</c> before splicing.
    /// </para>
    /// </remarks>
    public CsxFragment Emit(CacheAssertElasticsearchModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // The query template is emitted as a RAW JSON-escaped C# string literal.
        // Any {placeholder} inside survives as LITERAL text (not an emit-time
        // interpolation hole — inside $$"""…""", a lone {name} passes through
        // verbatim) and is resolved at runtime inside the helper (§17).
        var defaultQuery = """{"query":{"match_all":{}}}""";
        var queryLiteral = JsonSerializer.Serialize(model.Query ?? defaultQuery);

        var minCountLiteral = model.Expect.MinCount.ToString(CultureInfo.InvariantCulture);
        var exactCountLiteral = model.Expect.Count.HasValue
            ? model.Expect.Count.Value.ToString(CultureInfo.InvariantCulture)
            : "null";

        string fieldNamesLiteral;
        string fieldValuesLiteral;
        if (model.Expect.Fields is { Count: > 0 } fields)
        {
            fieldNamesLiteral = "new string[]{ "
                + string.Join(", ", fields.Select(f => JsonSerializer.Serialize(f.Field)))
                + " }";
            fieldValuesLiteral = "new string[]{ "
                + string.Join(", ", fields.Select(f => JsonSerializer.Serialize(f.Value)))
                + " }";
        }
        else
        {
            fieldNamesLiteral = "null";
            fieldValuesLiteral = "null";
        }

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        // 'Secrets' is the ScriptGlobalVariables.Secrets instance property.
        var block = $$"""
            {
                await CacheAssertElasticsearch_Helpers.ExecuteAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{JsonSerializer.Serialize(model.Index)}},
                    {{queryLiteral}},
                    {{minCountLiteral}},
                    {{exactCountLiteral}},
                    {{fieldNamesLiteral}},
                    {{fieldValuesLiteral}},
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

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is a
    /// <c>cache-assert.elasticsearch</c> Fail-observation shape that can be rendered
    /// as an expected-vs-observed diff table.
    /// </summary>
    /// <remarks>
    /// Recognised shapes:
    /// <list type="bullet">
    /// <item><c>{"matched":false,"expected":E,"actual":N,"index":I}</c> — count mismatch.</item>
    /// <item><c>{"matched":false,"field":F,"expected":E,"actual":A}</c> — field mismatch.</item>
    /// </list>
    /// The <c>{"matched":true,...}</c> Pass shape and the <c>{"error":...}</c>
    /// EnvironmentError shape are intentionally NOT renderable.
    /// </remarks>
    public bool CanRender(JsonElement observation) =>
        TryReadFail(observation, out _, out _, out _);

    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (!TryReadFail(observation, out var expected, out var actual, out var field))
            return null;

        return field is not null
            ? RenderFieldTable(field, expected, actual)
            : RenderTable(expected, actual);
    }

    // ── IStepDiffRenderer helpers ─────────────────────────────────────────────

    private static bool TryReadFail(
        JsonElement observation,
        out string expected,
        out string actual,
        out string? field)
    {
        field = null;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("matched", out var matchedEl)
            || matchedEl.ValueKind != JsonValueKind.False)
            return false;

        // Both count-mismatch and field-mismatch shapes contain "expected" + "actual".
        if (observation.TryGetProperty("expected", out var expEl)
            && observation.TryGetProperty("actual", out var actEl))
        {
            expected = expEl.ValueKind == JsonValueKind.String
                ? expEl.GetString() ?? string.Empty
                : expEl.ToString();
            actual = actEl.ValueKind == JsonValueKind.String
                ? actEl.GetString() ?? string.Empty
                : actEl.ToString();

            // Field-mismatch shape additionally contains "field".
            if (observation.TryGetProperty("field", out var fieldEl)
                && fieldEl.ValueKind == JsonValueKind.String)
            {
                field = fieldEl.GetString();
            }

            return true;
        }

        return false;
    }

    private static string RenderTable(string expected, string actual) =>
        $"| | Value |\n|---|---|\n| expected | `{expected}` |\n| actual | `{actual}` |";

    private static string RenderFieldTable(string field, string expected, string actual) =>
        $"| field | {field} |\n|---|---|\n| expected | `{expected}` |\n| actual | `{actual}` |";
}
