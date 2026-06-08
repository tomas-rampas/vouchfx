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
using System.Text.Json;
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
/// The <see cref="Emit"/> method produces a minimal but syntactically valid
/// <see cref="CsxFragment"/>.  Full HTTP execution is deferred to Sprint 3.
/// This sprint proves the schema-composition mechanism and the fragment
/// composition rules.
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

    private static readonly IReadOnlyList<string> s_usings =
        new[] { "System", "System.Collections.Generic" };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// The class name begins with <c>HttpRest_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class HttpRest_Helpers\n" +
        "{\n" +
        "    /// <summary>Records the planned http.rest request into the Vars dictionary.</summary>\n" +
        "    public static void RecordPlanned(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string stepId, string target, string method, string path)\n" +
        "    {\n" +
        "        vars[$\"http_rest_{stepId}_target\"] = target;\n" +
        "        vars[$\"http_rest_{stepId}_method\"] = method;\n" +
        "        vars[$\"http_rest_{stepId}_path\"]   = path;\n" +
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
            errors.Add("http.rest: 'path' must not be empty.");

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── IStepCompiler<HttpRestModel> ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Minimal implementation for Sprint 2: records the planned request into
    /// <c>Vars</c> so that the composed Roslyn script is syntactically and
    /// semantically valid.  Full HTTP execution (HttpClient, response capture,
    /// assertion evaluation) is deferred to Sprint 3.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1):
    /// <list type="bullet">
    ///   <item><see cref="CsxFragment.RequiredUsings"/> — bare namespace strings.</item>
    ///   <item><see cref="CsxFragment.RequiredHelpers"/> — full <c>static class HttpRest_Helpers</c> definition.</item>
    ///   <item><see cref="CsxFragment.StatementBlock"/> — C# 11 <c>$$"""…"""</c> block; no <c>using var</c>.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public CsxFragment Emit(HttpRestModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        var block = $$"""
            {
                HttpRest_Helpers.RecordPlanned(
                    Vars,
                    "{{safeId}}",
                    "{{model.Target}}",
                    "{{model.Method}}",
                    "{{model.Path}}");
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
