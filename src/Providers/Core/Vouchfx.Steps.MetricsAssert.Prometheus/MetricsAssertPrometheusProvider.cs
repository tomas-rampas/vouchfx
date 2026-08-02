// Vouchfx.Steps.MetricsAssert.Prometheus — metrics-assert.prometheus step provider
// (DSL §5, brand-new metrics-assert family; §13).
//
// This is the FIRST provider of the metrics-assert family (the same new-family
// precedent mail-expect.smtp and cache-assert.redis established).  Steps always name
// the dotted form (`metrics-assert.prometheus`) — bare family names are not part of
// the language.
//
// Intent: scrape a Prometheus text-exposition endpoint (normally the SUT's own
// /metrics) and assert on one metric's numeric value, optionally scoped by a label
// subset.  This is how a suite proves that a business transaction moved a counter or
// gauge the SUT exposes — the "did the system actually do the thing" signal that a
// 2xx HTTP response alone cannot prove.
//
// DEVIATION FROM THE ORIGINAL PLAN SKETCH: see MetricsAssertPrometheusModel's file
// header for the full rationale.  In short — the plan sketch specified a single
// required absolute "url" field; this provider instead uses Target (a service name
// declared under environment.services) + Path (defaulting to "/metrics"), mirroring
// http.rest's Target/Path shape exactly, because that is the only mechanism the
// current engine offers for resolving a dynamically-assigned Aspire endpoint. No
// environment.dependencies entry is required — the family adds "no infrastructure"
// exactly as the plan intended; it reuses the http-family's existing service
// resolution instead of declaring its own dependency kind.
//
// Capture support (v1 decision): the matched metric's value IS exposed to `capture:`,
// evaluated as JSONPath (mirrors http.rest's JSON branch) against a small synthesised
// observation object {"metric":name,"value":n,"labels":{...}} — the SAME object this
// provider already builds for the Pass-verdict observation, so this is "cheaply
// consistent" with the existing capture machinery (ICompileContext.CaptureExprs) and
// required no new engine plumbing.  A capture declared with the XPath format is not
// supported by this family (there is no XML shape to select from) and always misses,
// mapping to Verdict.Inconclusive exactly like an unmatched http.rest JSONPath capture
// (§12.1 upstream-capture-unmet) — never a crash.
//
// Verdict taxonomy (§12.1) — mirrors http.rest's catch chain exactly (see
// HttpRestProvider's ExecuteAsync helper, which this provider's ScrapeAsync
// deliberately replicates):
//   • SecretResolutionException                       → EnvironmentError.
//   • TaskCanceledException / TimeoutException (stall) → Inconclusive.
//   • any other exception (DNS/connection/parse/etc.)  → EnvironmentError.
//   • non-200 scrape response                          → Fail (the endpoint exists and
//     answered; it just did not serve metrics — this is observable SUT state, unlike
//     http.rest where a non-matching status is the PRIMARY assertion axis; here it is
//     a precondition failure on the way to the real assertion, but the taxonomy still
//     calls it Fail, not EnvironmentError, because the SUT was reachable and responded).
//   • metric not found / ambiguous selection / value|min|max mismatch → Fail.
//   • match (and, if declared, every capture matched)   → Pass.
//
// Ambiguous-selection policy (v1 decision): if MORE THAN ONE sample matches the
// declared name + label subset, the step is a Fail (not an arbitrary "first match wins"
// pick) — under-specified labels are an authoring error, and silently picking one
// sample would make the assertion's target ambiguous to a reader.  The observation
// carries only the match COUNT, never the label values of the candidates (labels are
// SUT telemetry, not secrets, but the raw scrape body is still never echoed verbatim).
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields (target, path, metric,
//     labels, expect).  The type const discriminator is injected by the SchemaComposer
//     from Kind — never from the fragment text.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers
//     contains the full provider-id-prefixed static class definition (a single raw
//     string literal — no per-step interpolation, so no $$"""…""" needed for the
//     helper itself); StatementBlock is a C# 11 $$"""…""" block; 'using var' is illegal.
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MetricsAssert.Prometheus;

/// <summary>
/// Core provider for the <c>metrics-assert.prometheus</c> step kind — the first member
/// of the new <c>metrics-assert</c> family.  Scrapes a Prometheus text-exposition
/// endpoint and asserts on one metric's numeric value, optionally scoped by labels.
/// </summary>
[StepProvider]
public sealed class MetricsAssertPrometheusProvider
    : IStepProvider,
      IStepBinder<MetricsAssertPrometheusModel>,
      IStepValidator<MetricsAssertPrometheusModel>,
      IStepCompiler<MetricsAssertPrometheusModel>,
      IResourceContributor<MetricsAssertPrometheusModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    /// <summary>The scrape path used when the author omits <c>path</c>.</summary>
    private const string DefaultPath = "/metrics";

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("metrics-assert", "prometheus");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<MetricsAssertPrometheusModel> ─────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Scrapes a Prometheus text-exposition endpoint (normally the SUT's own /metrics) and asserts on one metric's numeric value, optionally scoped by a label subset.  Exactly one sample must match the declared metric name plus every declared label; zero or more than one match is a Fail (an under-specified label set is an authoring error, never silently resolved).",
          "type": "object",
          "required": ["target", "metric", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the service to scrape, as declared under environment.services — normally the system under test.",
              "type": "string",
              "minLength": 1
            },
            "path": {
              "description": "The scrape path.  May contain {placeholder} and ${secret:source/path} tokens.  Defaults to '/metrics' when omitted. Must be a rooted relative path (start with a single '/'); absolute URLs, protocol-relative paths ('//…'), and backslashes are rejected as an SSRF guard.",
              "type": "string",
              "pattern": "^/(?!/)[^\\\\]*$",
              "default": "/metrics"
            },
            "metric": {
              "description": "The Prometheus sample (metric) name to select, e.g. 'orders_processed_total'.  May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string",
              "minLength": 1
            },
            "labels": {
              "description": "Optional map of required label name to expected value.  A sample matches only when it carries every declared label with exactly the expected value (a subset match — the sample may carry additional labels).  Values may contain {placeholder} and ${secret:source/path} tokens.",
              "type": "object",
              "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
            },
            "expect": {
              "description": "The value assertion block.  At least one of value, min, or max must be declared; all three may combine.  Each is a decimal string (may contain {placeholder} / ${secret:source/path} tokens) parsed as a double at execution time.",
              "type": "object",
              "minProperties": 1,
              "properties": {
                "value": {
                  "description": "Expected exact value.  Compared with a 1e-9 relative tolerance (see the provider's ScrapeAsync helper).",
                  "type": ["number", "string"]
                },
                "min": {
                  "description": "Inclusive lower bound for the matched sample's value.",
                  "type": ["number", "string"]
                },
                "max": {
                  "description": "Inclusive upper bound for the matched sample's value.",
                  "type": ["number", "string"]
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    public MetricsAssertPrometheusModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new MetricsAssertPrometheusModel(
                Target: string.Empty,
                Path: DefaultPath,
                Metric: string.Empty,
                Labels: null,
                Expect: new MetricsExpectation(Value: null, Min: null, Max: null));
        }

        var target = GetScalar(mapping, "target");
        var path = GetOptionalScalar(mapping, "path") ?? DefaultPath;
        var metric = GetScalar(mapping, "metric");
        var labels = BindStringMap(mapping, "labels");
        var expect = BindExpectation(mapping);

        return new MetricsAssertPrometheusModel(
            Target: target,
            Path: path,
            Metric: metric,
            Labels: labels,
            Expect: expect);
    }

    private static MetricsExpectation BindExpectation(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            || expectNode is not YamlMappingNode expectMap)
        {
            return new MetricsExpectation(Value: null, Min: null, Max: null);
        }

        var value = GetOptionalScalar(expectMap, "value");
        var min = GetOptionalScalar(expectMap, "min");
        var max = GetOptionalScalar(expectMap, "max");

        return new MetricsExpectation(Value: value, Min: min, Max: max);
    }

    private static Dictionary<string, string>? BindStringMap(YamlMappingNode parent, string field)
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

    // ── IStepValidator<MetricsAssertPrometheusModel> ─────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(MetricsAssertPrometheusModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
        {
            errors.Add("metrics-assert.prometheus: 'target' must not be empty.");
        }
        else if (!ctx.DeclaredServices.ContainsKey(model.Target))
        {
            if (ctx.DeclaredDependencies.ContainsKey(model.Target))
            {
                // M1 fix (fix round 2) — mirrors HttpRestProvider.Validate's own identical
                // fix and rationale: metrics-assert.prometheus resolves 'target' exclusively
                // against declared services; a dependency target validated PASS before this
                // fix and could never work at run time.
                var services = ctx.DeclaredServices.Count == 0
                    ? "(none)"
                    : string.Join(", ", ctx.DeclaredServices.Keys.OrderBy(k => k, StringComparer.Ordinal));
                errors.Add(
                    $"metrics-assert.prometheus: 'target' '{model.Target}' names a dependency " +
                    "declared in environment.dependencies, which metrics-assert.prometheus " +
                    "cannot reach — it resolves 'target' only against declared services. " +
                    "Declared services: " + services + ".");
            }
            else
            {
                // REQ-012/EDGE-009 (services-generalisation spec): close the previously
                // unvalidated-target hole — mirrors HttpRestProvider.Validate's own identical
                // fix and rationale.
                errors.Add(
                    $"metrics-assert.prometheus: 'target' '{model.Target}' names neither a " +
                    "declared service in environment.services nor a declared dependency in " +
                    "environment.dependencies. " +
                    ProjectContextDescriptions.DescribeDeclaredSurfaces(ctx));
            }
        }

        if (string.IsNullOrWhiteSpace(model.Path))
        {
            errors.Add("metrics-assert.prometheus: 'path' must not be empty.");
        }
        else
        {
            // SSRF guard, mirrors HttpRestProvider.Validate's rooted-path checks exactly
            // (M1): path must be a safe rooted relative reference.  Reject absolute URLs,
            // protocol-relative paths, backslash paths, and paths that do not start with
            // '/'.
            //
            // NOTE: Uri.TryCreate(path, UriKind.Absolute, …) is intentionally absent — on
            // Linux a leading '/' parses as a file URI (file:///foo), so that check would
            // reject valid rooted paths on that platform.  The three guards below are
            // fully platform-independent and together cover every unsafe form:
            //   • !StartsWith('/')     — rejects scheme-bearing URLs (http://…) and bare
            //                            relative paths such as "metrics".
            //   • StartsWith("//", …)  — rejects protocol-relative paths (//evil/…).
            //   • Contains('\\', …)    — rejects backslash paths.
            // A scheme-bearing absolute URL always starts with a letter, not '/', so the
            // first guard catches it without any Uri parsing.  The runtime same-authority
            // guard in MetricsAssertPrometheus_Helpers.ScrapeAsync remains as defence in
            // depth (a resolved {placeholder}/${secret:...} value is only known at
            // execution time and cannot be checked here).
            var path = model.Path;
            if (!path.StartsWith('/'))
            {
                errors.Add(
                    "metrics-assert.prometheus: 'path' must be a rooted relative path " +
                    "(start with '/'); absolute URLs and protocol-relative paths are not " +
                    "allowed.");
            }
            else if (path.StartsWith("//", StringComparison.Ordinal))
            {
                errors.Add(
                    "metrics-assert.prometheus: 'path' must be a rooted relative path " +
                    "(start with '/'); absolute URLs and protocol-relative paths are not " +
                    "allowed.");
            }
            else if (path.Contains('\\', StringComparison.Ordinal))
            {
                errors.Add(
                    "metrics-assert.prometheus: 'path' must not contain backslashes; " +
                    "use forward slashes for URL path separators.");
            }
        }

        if (string.IsNullOrWhiteSpace(model.Metric))
            errors.Add("metrics-assert.prometheus: 'metric' must not be empty.");

        var expect = model.Expect;
        if (expect.Value is null && expect.Min is null && expect.Max is null)
        {
            errors.Add(
                "metrics-assert.prometheus: 'expect' must declare at least one of " +
                "'value', 'min', or 'max'.");
        }

        // Coherence check (min <= max): only meaningful when BOTH are static numeric
        // literals (no {placeholder} / ${secret:...} token) — a templated bound is
        // resolved only at step-execution time and cannot be validated here.
        if (expect.Min is { } minText && expect.Max is { } maxText
            && IsStaticLiteral(minText) && IsStaticLiteral(maxText)
            && double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minVal)
            && double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxVal)
            && minVal > maxVal)
        {
            errors.Add(
                $"metrics-assert.prometheus: 'expect.min' ({minText}) must be <= " +
                $"'expect.max' ({maxText}).");
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }


    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> contains no
    /// <c>{placeholder}</c> or <c>${secret:...}</c> token, i.e. it is safe to parse and
    /// validate statically at compile time.
    /// </summary>
    private static bool IsStaticLiteral(string text) =>
        !text.Contains('{', StringComparison.Ordinal) && !text.Contains('$', StringComparison.Ordinal);

    // ── CsxFragment components ────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "System.Net.Http",
            "System.Threading.Tasks",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// </summary>
    /// <remarks>
    /// The class name begins with <c>MetricsAssertPrometheus_</c> to prevent collisions
    /// when multiple providers contribute helpers to the same Roslyn submission.  There
    /// is no per-step interpolation in this text, so a plain C# 11 triple-quote raw
    /// string literal is used (mirrors <c>CacheAssertRedisProvider.s_helpers</c>); the
    /// double-dollar interpolated form is reserved for <see cref="Emit"/>'s
    /// <see cref="CsxFragment.StatementBlock"/>, which DOES need per-step holes.
    /// </remarks>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        """
        static class MetricsAssertPrometheus_Helpers
        {
            // Executes a single Prometheus scrape, evaluates the metric/label/value
            // assertion, optionally applies capture: (JSONPath only, over a small
            // synthesised JSON observation object), and writes a typed StepOutcome into
            // Vars.  See the provider's file header for the full verdict-mapping table.
            // AllowAutoRedirect=false + disposeHandler:true mirror http.rest's SSRF-via-
            // redirect guard and handler-lifetime discipline exactly.
            public static async System.Threading.Tasks.Task ScrapeAsync(
                System.Collections.Generic.IDictionary<string, object?> vars,
                Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,
                string outcomeKey,
                string captureStatusKey,
                string serviceKey,
                string pathTemplate,
                string metricTemplate,
                string[] labelNames,
                string[] labelValueTemplates,
                string? expectValueTemplate,
                string? expectMinTemplate,
                string? expectMaxTemplate,
                string[] captureVarNames,
                string[] captureExprs,
                string[] captureKinds,
                System.Threading.CancellationToken ct,
                bool budgetGoverned)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Vouchfx.Engine.Abstractions.Verdict verdict;
                string observation;
                var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };
                var client = new System.Net.Http.HttpClient(handler, disposeHandler: true);
                try
                {
                    // Step-timeout convention (#232): a declared step budget governs this call —
                    // lift the transport bound (infinite) and let the step token (ct) be the sole
                    // enforcement mechanism; otherwise keep the 30s stall-window convention.
                    client.Timeout = budgetGoverned
                        ? System.Threading.Timeout.InfiniteTimeSpan
                        : System.TimeSpan.FromSeconds(30);
                    // §security parity with http.rest: bound the untrusted response body so a
                    // hostile /metrics endpoint cannot stream an unbounded body and OOM the
                    // runner before parsing even starts.
                    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;

                    // Resolve every author-text field INSIDE the guarded region (§17) via
                    // ResolveTemplate (single pass: {placeholder} substitution + ${secret}
                    // resolution).  A missing secret throws SecretResolutionException ->
                    // caught below -> EnvironmentError.
                    var path = Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate);
                    var baseUrl = vars.TryGetValue(serviceKey, out var bu) && bu is string s ? s : string.Empty;
                    // Safe URI composition (mirrors http.rest's M1 guard): resolve path against
                    // the base URI and confirm the resulting authority matches the original.  An
                    // empty/invalid baseUrl throws UriFormatException -> caught -> EnvironmentError.
                    var baseUri = new System.Uri(baseUrl, System.UriKind.Absolute);
                    var full = new System.Uri(baseUri, path);
                    if (full.GetLeftPart(System.UriPartial.Authority) != baseUri.GetLeftPart(System.UriPartial.Authority))
                    {
                        throw new System.InvalidOperationException(
                            "metrics-assert.prometheus: resolved URL authority '" + full.Authority +
                            "' does not match base authority '" + baseUri.Authority +
                            "'; path attempted to change host.");
                    }

                    var metric = Secret_Helpers.ResolveTemplate(secrets, vars, metricTemplate);
                    var labels = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
                    for (int li = 0; li < labelNames.Length; li++)
                    {
                        labels[labelNames[li]] = Secret_Helpers.ResolveTemplate(secrets, vars, labelValueTemplates[li]);
                    }

                    double? expectValue = expectValueTemplate is null
                        ? (double?)null
                        : ParseExpected(Secret_Helpers.ResolveTemplate(secrets, vars, expectValueTemplate));
                    double? expectMin = expectMinTemplate is null
                        ? (double?)null
                        : ParseExpected(Secret_Helpers.ResolveTemplate(secrets, vars, expectMinTemplate));
                    double? expectMax = expectMaxTemplate is null
                        ? (double?)null
                        : ParseExpected(Secret_Helpers.ResolveTemplate(secrets, vars, expectMaxTemplate));

                    var resp = await client.GetAsync(full, ct).ConfigureAwait(false);
                    var status = (int)resp.StatusCode;
                    if (status != 200)
                    {
                        // The endpoint exists and answered — it just did not serve metrics.
                        // This is observable SUT state, so it is a Fail, not an
                        // EnvironmentError (§12.1: only unreachable/broken infra is
                        // EnvironmentError; a well-formed non-200 response is not that).
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                        observation = "{\"status\":" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                    }
                    else
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var samples = ParseExposition(body);

                        var matching = new System.Collections.Generic.List<(System.Collections.Generic.Dictionary<string, string> Labels, double Value)>();
                        for (int si = 0; si < samples.Count; si++)
                        {
                            var sample = samples[si];
                            if (!string.Equals(sample.Name, metric, System.StringComparison.Ordinal))
                                continue;
                            bool labelsOk = true;
                            foreach (var kv in labels)
                            {
                                if (!sample.Labels.TryGetValue(kv.Key, out var actualLabel)
                                    || !string.Equals(actualLabel, kv.Value, System.StringComparison.Ordinal))
                                {
                                    labelsOk = false;
                                    break;
                                }
                            }
                            if (labelsOk)
                                matching.Add((sample.Labels, sample.Value));
                        }

                        if (matching.Count == 0)
                        {
                            // Not found: report counts only — never echo the raw scrape body,
                            // which could be large or (in principle) carry unrelated telemetry.
                            verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                            observation = "{\"found\":false,\"metric\":" + System.Text.Json.JsonSerializer.Serialize(metric) +
                                ",\"labelsProvided\":" + labels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                        else if (matching.Count > 1)
                        {
                            // Ambiguous selection (v1 policy): more than one sample matches the
                            // declared name + label subset is an authoring error (under-specified
                            // labels) — Fail with the match COUNT only, never the candidates'
                            // label values.
                            verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                            observation = "{\"ambiguous\":true,\"metric\":" + System.Text.Json.JsonSerializer.Serialize(metric) +
                                ",\"count\":" + matching.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                        else
                        {
                            var actual = matching[0].Value;

                            if ((double.IsNaN(actual) || double.IsInfinity(actual))
                                && (expectValue.HasValue || expectMin.HasValue || expectMax.HasValue))
                            {
                                // A NaN/±Inf sample value defeats every IEEE comparison below: NaN
                                // compares false to everything (a value/min/max mismatch would
                                // never be detected) and ±Inf can silently satisfy an unbounded
                                // min/max — without this guard the step would silently Pass on a
                                // non-finite value.  System.Text.Json also has no valid JSON token
                                // for NaN/Infinity, so FormatNumber("R") would embed an invalid
                                // literal in the Pass observation.  Report a distinct, JSON-safe
                                // Fail shape instead (never the raw non-finite double).
                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                                observation = "{\"metric\":" + System.Text.Json.JsonSerializer.Serialize(metric) +
                                    ",\"nonFinite\":" + System.Text.Json.JsonSerializer.Serialize(NonFiniteToken(actual)) + "}";
                            }
                            else
                            {
                            string? mismatch = null;
                            if (expectValue.HasValue)
                            {
                                // Exact equality with a 1e-9 RELATIVE tolerance (documented on the
                                // model): tolerance = max(|expected| * 1e-9, 1e-12) so an expected
                                // value of exactly 0 still gets a tiny absolute floor.
                                var tolerance = System.Math.Max(System.Math.Abs(expectValue.Value) * 1e-9, 1e-12);
                                if (System.Math.Abs(actual - expectValue.Value) > tolerance)
                                {
                                    mismatch = "{\"value\":{\"expected\":" + FormatNumber(expectValue.Value) +
                                        ",\"actual\":" + FormatNumber(actual) + "}}";
                                }
                            }
                            if (mismatch is null && expectMin.HasValue && actual < expectMin.Value)
                            {
                                mismatch = "{\"min\":{\"expected\":" + FormatNumber(expectMin.Value) +
                                    ",\"actual\":" + FormatNumber(actual) + "}}";
                            }
                            if (mismatch is null && expectMax.HasValue && actual > expectMax.Value)
                            {
                                mismatch = "{\"max\":{\"expected\":" + FormatNumber(expectMax.Value) +
                                    ",\"actual\":" + FormatNumber(actual) + "}}";
                            }

                            if (mismatch is not null)
                            {
                                verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                                observation = mismatch;
                            }
                            else
                            {
                                verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;
                                observation = "{\"metric\":" + System.Text.Json.JsonSerializer.Serialize(metric) +
                                    ",\"value\":" + FormatNumber(actual) +
                                    ",\"labels\":" + SerialiseLabels(matching[0].Labels) + "}";

                                // ── capture: (JSONPath only) over the observation object ───────
                                // Cheaply consistent with the existing capture machinery: the
                                // matched value is exposed at JSONPath $.value against the SAME
                                // object just built for the Pass observation (v1 decision — see
                                // the provider's file header).  An XPath-declared capture always
                                // misses (there is no XML shape here) -> Inconclusive, exactly like
                                // an unmatched http.rest JSONPath capture (§12.1).
                                if (captureVarNames.Length > 0)
                                {
                                    System.Text.Json.Nodes.JsonNode? obsNode;
                                    try
                                    {
                                        obsNode = System.Text.Json.Nodes.JsonNode.Parse(observation);
                                    }
                                    catch (System.Exception)
                                    {
                                        obsNode = null;
                                    }
                                    var matchedFlags = new bool[captureVarNames.Length];
                                    for (int ci = 0; ci < captureVarNames.Length; ci++)
                                    {
                                        var varName = captureVarNames[ci];
                                        var captureExpr = captureExprs[ci];
                                        var captureKind = captureKinds[ci];
                                        bool matched = false;
                                        if (obsNode is not null && string.Equals(captureKind, "json", System.StringComparison.Ordinal))
                                        {
                                            try
                                            {
                                                var pathResult = Json.Path.JsonPath.Parse(captureExpr).Evaluate(obsNode);
                                                var matches = pathResult.Matches;
                                                if (matches != null && matches.Count > 0 && matches[0].Value is not null)
                                                {
                                                    var firstMatch = matches[0].Value;
                                                    string capturedStr;
                                                    if (firstMatch is System.Text.Json.Nodes.JsonValue jv)
                                                    {
                                                        var rawElem = jv.GetValue<System.Text.Json.JsonElement>();
                                                        capturedStr = rawElem.ValueKind == System.Text.Json.JsonValueKind.String
                                                            ? rawElem.GetString() ?? string.Empty
                                                            : rawElem.GetRawText();
                                                    }
                                                    else
                                                    {
                                                        capturedStr = firstMatch.ToJsonString();
                                                    }
                                                    vars[varName] = capturedStr;
                                                    matched = true;
                                                }
                                            }
                                            catch (System.Exception)
                                            {
                                                matched = false;
                                            }
                                        }
                                        matchedFlags[ci] = matched;
                                        if (!matched)
                                        {
                                            verdict = Vouchfx.Engine.Abstractions.Verdict.Inconclusive;
                                            observation = "{\"captureUnmet\":" + System.Text.Json.JsonSerializer.Serialize(varName) + "}";
                                        }
                                    }
                                    vars[captureStatusKey] = string.Join(",", System.Array.ConvertAll(matchedFlags, f => f ? "1" : "0"));
                                }
                            }
                            }
                        }
                    }
                }
                catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)
                {
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"secretError\":\"secret resolution failed\"" +
                        ",\"source\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +
                        ",\"path\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + "}";
                }
                catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Step-token cut (#232): rethrow past this provider's own error handling so
                    // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead
                    // of the connection-timeout branch below misclassifying it.
                    throw;
                }
                catch (System.Exception ex) when (ex is System.Threading.Tasks.TaskCanceledException
                                                  || ex is System.TimeoutException)
                {
                    // Timeout = Inconclusive (§12.1): mirrors http.rest exactly — a stall is not
                    // evidence the SUT is wrong, just that the run could not complete in time.
                    verdict = Vouchfx.Engine.Abstractions.Verdict.Inconclusive;
                    observation = "{\"timeout\":true}";
                }
                catch (System.Exception ex)
                {
                    // Connection / DNS / authority-change / parse failures = EnvironmentError
                    // (§12.1) — mirrors http.rest's generic catch exactly.
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
                }
                finally
                {
                    sw.Stop();
                    client.Dispose();  // explicit Dispose() in finally — 'using'-declarations are prohibited in CSX (§13.3.1).
                }
                vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);
            }

            // Parses a decimal-string template (already {placeholder}/${secret}-resolved)
            // into a double.  Accepts Prometheus's own textual specials (NaN, +Inf, -Inf,
            // Inf) in addition to ordinary invariant-culture numeric literals.
            private static double ParseExpected(string text)
            {
                var trimmed = text.Trim();
                if (string.Equals(trimmed, "NaN", System.StringComparison.OrdinalIgnoreCase))
                    return double.NaN;
                if (string.Equals(trimmed, "+Inf", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "Inf", System.StringComparison.OrdinalIgnoreCase))
                    return double.PositiveInfinity;
                if (string.Equals(trimmed, "-Inf", System.StringComparison.OrdinalIgnoreCase))
                    return double.NegativeInfinity;
                return double.Parse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            }

            private static string FormatNumber(double value) =>
                value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            // Renders a non-finite double as Prometheus's own textual token (NaN / +Inf /
            // -Inf), mirroring ParseExpected / ParseExposition's accepted vocabulary.  Used
            // ONLY for the "nonFinite" Fail observation — never fed to FormatNumber, which
            // would emit an invalid JSON numeric literal for these values.
            private static string NonFiniteToken(double value)
            {
                if (double.IsNaN(value))
                    return "NaN";
                return value > 0 ? "+Inf" : "-Inf";
            }

            private static string SerialiseLabels(System.Collections.Generic.Dictionary<string, string> labels)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (var kv in labels)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(kv.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }

            // Parses the Prometheus text exposition format into (name, labels, value)
            // samples.
            //   • Lines beginning with '#' (HELP/TYPE/comments) are skipped outright.
            //   • A sample line is 'name{label="v",...} value [timestamp]' or bare
            //     'name value'.
            //   • Escaped label-value characters \\, \", \n are unescaped per the
            //     exposition format spec.
            //   • Any '# ...' exemplar suffix trailing the value/timestamp is dropped.
            //   • A malformed line (no parseable value) is skipped, never throws — a
            //     hostile or truncated body degrades to fewer samples, not a crash.
            private static System.Collections.Generic.List<(string Name, System.Collections.Generic.Dictionary<string, string> Labels, double Value)> ParseExposition(string body)
            {
                var result = new System.Collections.Generic.List<(string, System.Collections.Generic.Dictionary<string, string>, double)>();
                var lines = body.Split('\n');
                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r').Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;

                    int pos = 0;
                    int nameStart = pos;
                    while (pos < line.Length && line[pos] != '{' && !char.IsWhiteSpace(line[pos]))
                        pos++;
                    if (pos == nameStart)
                        continue;
                    var name = line.Substring(nameStart, pos - nameStart);

                    var labels = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
                    if (pos < line.Length && line[pos] == '{')
                    {
                        pos++;
                        while (pos < line.Length && line[pos] != '}')
                        {
                            while (pos < line.Length && (line[pos] == ',' || char.IsWhiteSpace(line[pos])))
                                pos++;
                            if (pos >= line.Length || line[pos] == '}')
                                break;
                            int labelNameStart = pos;
                            while (pos < line.Length && line[pos] != '=')
                                pos++;
                            if (pos >= line.Length)
                                break;
                            var labelName = line.Substring(labelNameStart, pos - labelNameStart).Trim();
                            pos++;
                            if (pos >= line.Length || line[pos] != '"')
                                break;
                            pos++;
                            var valueBuilder = new System.Text.StringBuilder();
                            while (pos < line.Length && line[pos] != '"')
                            {
                                if (line[pos] == '\\' && pos + 1 < line.Length)
                                {
                                    var next = line[pos + 1];
                                    if (next == '\\') { valueBuilder.Append('\\'); pos += 2; continue; }
                                    if (next == '"') { valueBuilder.Append('"'); pos += 2; continue; }
                                    if (next == 'n') { valueBuilder.Append('\n'); pos += 2; continue; }
                                    valueBuilder.Append(line[pos]);
                                    pos++;
                                    continue;
                                }
                                valueBuilder.Append(line[pos]);
                                pos++;
                            }
                            if (pos < line.Length)
                                pos++;
                            labels[labelName] = valueBuilder.ToString();
                        }
                        if (pos < line.Length && line[pos] == '}')
                            pos++;
                    }

                    var remainder = pos < line.Length ? line.Substring(pos) : string.Empty;
                    var hashIdx = remainder.IndexOf('#');
                    if (hashIdx >= 0)
                        remainder = remainder.Substring(0, hashIdx);
                    var tokens = remainder.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0)
                        continue;

                    double value;
                    var valueToken = tokens[0];
                    if (string.Equals(valueToken, "NaN", System.StringComparison.OrdinalIgnoreCase))
                        value = double.NaN;
                    else if (string.Equals(valueToken, "+Inf", System.StringComparison.OrdinalIgnoreCase))
                        value = double.PositiveInfinity;
                    else if (string.Equals(valueToken, "-Inf", System.StringComparison.OrdinalIgnoreCase))
                        value = double.NegativeInfinity;
                    else if (!double.TryParse(valueToken, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                        continue;

                    result.Add((name, labels, value));
                }
                return result;
            }
        }
        """,
    };

    // ── IStepCompiler<MetricsAssertPrometheusModel> ───────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(MetricsAssertPrometheusModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        var pathTemplateLiteral = JsonSerializer.Serialize(model.Path);
        var metricTemplateLiteral = JsonSerializer.Serialize(model.Metric);

        string[] labelNames;
        string[] labelValueTemplates;
        if (model.Labels is { Count: > 0 } labels)
        {
            labelNames = labels.Keys.ToArray();
            labelValueTemplates = labels.Values.ToArray();
        }
        else
        {
            labelNames = Array.Empty<string>();
            labelValueTemplates = Array.Empty<string>();
        }

        var labelNamesLiteral = BuildStringArrayLiteral(labelNames);
        var labelValueTemplatesLiteral = BuildStringArrayLiteral(labelValueTemplates);

        var expectValueLiteral = model.Expect.Value is null ? "null" : JsonSerializer.Serialize(model.Expect.Value);
        var expectMinLiteral = model.Expect.Min is null ? "null" : JsonSerializer.Serialize(model.Expect.Min);
        var expectMaxLiteral = model.Expect.Max is null ? "null" : JsonSerializer.Serialize(model.Expect.Max);

        // Format-aware captures: mirrors HttpRestProvider.Emit's expansion of
        // ctx.CaptureExprs into three parallel arrays.  Only "json" (JsonPath) is
        // meaningfully evaluated by the helper; an "xpath"-kind entry always misses.
        string[] captureVarNames;
        string[] captureExprsArr;
        string[] captureKinds;
        if (ctx.CaptureExprs is { Count: > 0 } captureMap)
        {
            captureVarNames = new string[captureMap.Count];
            captureExprsArr = new string[captureMap.Count];
            captureKinds = new string[captureMap.Count];
            var ci = 0;
            foreach (var (name, expr) in captureMap)
            {
                captureVarNames[ci] = name;
                captureExprsArr[ci] = expr.Expression;
                captureKinds[ci] = expr.Format == CaptureFormat.XPath ? "xpath" : "json";
                ci++;
            }
        }
        else
        {
            captureVarNames = Array.Empty<string>();
            captureExprsArr = Array.Empty<string>();
            captureKinds = Array.Empty<string>();
        }

        var captureVarNamesLiteral = BuildStringArrayLiteral(captureVarNames);
        var captureExprsLiteral = BuildStringArrayLiteral(captureExprsArr);
        var captureKindsLiteral = BuildStringArrayLiteral(captureKinds);

        var block = $$"""
            {
                await MetricsAssertPrometheus_Helpers.ScrapeAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{pathTemplateLiteral}},
                    {{metricTemplateLiteral}},
                    {{labelNamesLiteral}},
                    {{labelValueTemplatesLiteral}},
                    {{expectValueLiteral}},
                    {{expectMinLiteral}},
                    {{expectMaxLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureExprsLiteral}},
                    {{captureKindsLiteral}},
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

    // ── IResourceContributor<MetricsAssertPrometheusModel> ────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors <c>HttpRestProvider.Resources</c> exactly: <c>Family="http"</c>,
    /// <c>Name=model.Target</c> — the family adds no NEW infrastructure kind, it
    /// reuses the http-family's existing service-target resolution.
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(MetricsAssertPrometheusModel model)
    {
        yield return new ResourceRequirement(
            Family: "http",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            // JsonPath.Net — Json.Path.JsonPath used by the optional capture: evaluation.
            yield return typeof(Json.Path.JsonPath).Assembly;
        }
    }

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is one of the
    /// <c>metrics-assert.prometheus</c> Fail-observation shapes that carry a clean
    /// expected-vs-actual pair this provider can render as a diff (<c>value</c>,
    /// <c>min</c>, or <c>max</c> mismatch).  The <c>found</c>/<c>ambiguous</c>/
    /// <c>status</c> shapes have no single expected-vs-actual pair and fall back to the
    /// renderer's plain verdict line.
    /// </summary>
    /// <inheritdoc cref="IStepDiffRenderer.CanRender" />
    public bool CanRender(JsonElement observation) =>
        TryReadDiff(observation, out _, out _, out _);

    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (!TryReadDiff(observation, out var label, out var expected, out var actual))
            return null;

        return RenderTable(label, expected, actual);
    }

    private static readonly string[] s_diffKeys = { "value", "min", "max" };

    private static bool TryReadDiff(
        JsonElement observation,
        out string label,
        out string expected,
        out string actual)
    {
        label = string.Empty;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var candidate in s_diffKeys)
        {
            if (observation.TryGetProperty(candidate, out var diffEl)
                && diffEl.ValueKind == JsonValueKind.Object
                && diffEl.TryGetProperty("expected", out var expectedEl)
                && diffEl.TryGetProperty("actual", out var actualEl))
            {
                label = candidate;
                expected = ScalarText(expectedEl);
                actual = ScalarText(actualEl);
                return true;
            }
        }

        return false;
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "null",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string RenderTable(string label, string expected, string actual)
    {
        var headers = new[] { label, "expected", "actual" };
        var values = new[] { "(metric)", expected, actual };

        var widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
            widths[i] = Math.Max(headers[i].Length, values[i].Length);

        var sb = new System.Text.StringBuilder();

        AppendRow(sb, headers, widths);

        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0)
                sb.Append('┼');
            sb.Append(new string('─', widths[i] + 2));
        }
        sb.Append('\n');

        AppendRow(sb, values, widths);

        return sb.ToString();
    }

    private static void AppendRow(System.Text.StringBuilder sb, string[] cells, int[] widths)
    {
        for (int i = 0; i < cells.Length; i++)
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

    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
            return "new string[] { }";

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
