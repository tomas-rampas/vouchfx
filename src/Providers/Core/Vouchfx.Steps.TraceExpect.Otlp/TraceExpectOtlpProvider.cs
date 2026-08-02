// Vouchfx.Steps.TraceExpect.Otlp — trace-expect.otlp step provider (DSL §5.10, Phase C, §13).
//
// The flagship distributed assertion and the FIRST (and only, in v1) provider in a brand-new
// family. One [StepProvider] class implements the four mandatory interfaces plus the OPTIONAL
// IHostResourceContributor<TModel> (declares the ephemeral OTLP/HTTP receiver, mirroring the
// webhook-listen.http host-resource blueprint exactly, S07-F-01a/b) and IStepDiffRenderer
// (renders the match-metadata-only Fail diff). Deliberately NO IResourceContributor (the
// receiver is a host resource, not a containerised Aspire dependency) and NO
// ICompileReferenceContributor (the emitted helper needs only BCL types already resolved from
// the default TRUSTED_PLATFORM_ASSEMBLIES set — see the CAPTURE SUPPORT note below for why this
// constrains the capture implementation).
//
// RETRY model (§7): trace-expect.otlp is a verifyMode: RETRY consumer — RETRY is this family's
// CANONICAL mode, because a real OpenTelemetry SDK batches and exports spans on a periodic
// timer (commonly ~5s), so the spans for a transaction the scenario just drove will almost
// never have arrived by the very next step. The emitted helper performs an IDEMPOTENT single
// scan of the captured-span buffer and writes a non-Pass StepOutcome (Fail) when no span
// matches THIS attempt. It contains NO retry logic — the engine-owned RetryRunner re-invokes
// the delegate and converts a sustained Fail to Inconclusive on timeout (§12.1). The helper
// itself only ever writes Pass or Fail from the match scan; the one exception is an unmet
// capture, which — exactly like http.rest / metrics-assert.prometheus — maps to
// Verdict.Inconclusive (upstream-capture-unmet, §12.1), never a crash. A host-resource startup
// failure (the receiver itself failing to bind) is handled by the SAME upstream path that
// already converts an OrchestrationException to a scenario-level EnvironmentError for
// webhook-listen.http — nothing provider-specific is needed here.
//
// Substitution + secret model (canonical M2 pattern — mirrors mq-expect / webhook-listen.http):
//   Each declared criterion (traceId / service / spanName / each attribute VALUE) is emitted as
//   a RAW template string and resolved INSIDE the helper's guarded region via
//   Secret_Helpers.ResolveTemplate (both {placeholder} substitution and ${secret:source/path}
//   resolution in a single pass). A missing secret throws SecretResolutionException → caught →
//   Verdict.EnvironmentError for THIS step only, REFERENCE-ONLY (§17).
//
// TRACE-ID EXTRACTION (documented decision): the resolved traceId criterion is matched either
// as a bare trace id OR, when it has the shape of a full W3C traceparent header
// (<2-hex>-<32-hex>-<16-hex>-<2-hex>), the 32-hex trace-id segment is extracted first. This
// lets an author simply thread the SAME traceparent value they injected on an earlier http.rest
// request straight into this step's traceId field — no manual substring extraction needed in
// the YAML.
//
// SECURITY (§17, matching the webhook-listen.http precedent): a captured span comes from the
// SUT's own instrumentation and is therefore UNTRUSTED — a span name or attribute value could
// in principle echo sensitive data. The MATCH observation therefore carries ONLY metadata
// (matched / scanned / matchedCriteria / totalCriteria / evicted counts) and NEVER a span's
// trace id, name, service, or attribute values. capture: is the one EXPLICIT, author-declared exception
// (exactly like http.rest's response-body capture): an author who declares
// `capture: { spanName: "$.name" }` is deliberately pulling that field into Vars, at their own
// risk, precisely as they already do for an HTTP response body.
//
// CAPTURE SUPPORT — DOCUMENTED DECISION: the matched span's fields ARE exposed to capture: via
// a small synthesised-observation object, mirroring metrics-assert.prometheus / storage-assert.s3
// (the "synthesised-observation capture" idiom, §5.8/§5.9). Unlike those two providers, however,
// this one deliberately does NOT reference JsonPath.Net (no ICompileReferenceContributor — see
// the file-header rationale). Instead it implements a MINIMAL, dependency-free dotted-field-path
// evaluator ($.field, $.nested.field) directly against System.Text.Json, which is exactly
// JSONPath-COMPATIBLE for the simple field-access shape the synthesised observation needs (a
// flat object plus one nested "attributes" object) — but does NOT support full JSONPath syntax
// (array indexing, wildcards, filters, recursive descent "..") . A suite that needs those should
// assert with script.csharp instead. An XPath-format capture (capture: { x: { xpath: ... } }) is
// always unmet — there is no XML content to XPath over.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.TraceExpect.Otlp;

/// <summary>
/// Core provider for the <c>trace-expect.otlp</c> step kind (DSL §5.10) — the flagship
/// distributed assertion, proving a real trace with an expected span shape exists for the
/// business transaction the scenario just drove.
/// </summary>
/// <remarks>
/// <para>
/// The provider declares an ephemeral host-side OTLP/HTTP receiver (via
/// <see cref="IHostResourceContributor{TModel}"/>) keyed by the model's
/// <see cref="TraceExpectOtlpModel.Receiver"/> name; the engine stands it up before any step
/// runs and stages its base URL at <c>svc::&lt;Receiver&gt;</c> (and at the plain
/// <c>Vars[&lt;Receiver&gt;]</c> key), ready to be handed to the SUT's OpenTelemetry SDK
/// configuration (e.g. <c>OTEL_EXPORTER_OTLP_ENDPOINT: "{svc::traces}"</c>).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose emitted CSX reads
/// the receiver's captured spans via the <c>ScriptGlobalVariables.Traces</c> accessor,
/// evaluates each against the resolved criteria, and writes a typed
/// <see cref="Vouchfx.Engine.Abstractions.StepOutcome"/> into <c>Vars</c> for the runner to
/// read after execution.
/// </para>
/// </remarks>
[StepProvider]
public sealed class TraceExpectOtlpProvider
    : IStepProvider,
      IStepBinder<TraceExpectOtlpModel>,
      IStepValidator<TraceExpectOtlpModel>,
      IStepCompiler<TraceExpectOtlpModel>,
      IHostResourceContributor<TraceExpectOtlpModel>,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("trace-expect", "otlp");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<TraceExpectOtlpModel> ─────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>trace-expect.otlp</c> provider's
    /// own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it as an
    /// <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "Asserts that a captured OTLP span from a specific trace matches the declared criteria (service name, span name, and/or attributes).",
          "type": "object",
          "required": ["receiver", "match"],
          "properties": {
            "receiver": {
              "description": "Logical name of the host-owned OTLP/HTTP receiver whose captured spans this step asserts against. The engine stands the receiver up and stages its base URL at svc::<receiver> (and at the plain <receiver> Vars key so an earlier step can hand it to the SUT's OTel SDK configuration, e.g. OTEL_EXPORTER_OTLP_ENDPOINT: \"{svc::<receiver>}\").",
              "type": "string",
              "minLength": 1
            },
            "match": {
              "description": "The criteria a captured span must satisfy. 'traceId' is REQUIRED — this family asserts the causal chain of a SPECIFIC transaction, not a general shape any span might match; service/spanName/attributes are optional refinements layered on top of it.",
              "type": "object",
              "required": ["traceId"],
              "properties": {
                "traceId": {
                  "description": "Required expected trace id — ties this assertion to the SPECIFIC transaction under test (never omit it: a match with no trace id would accept any span with the right service/spanName/attributes, from any trace, ever exported). May be a full W3C traceparent value (the 32-hex trace-id segment is extracted automatically) or a bare 32-hex trace id. May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string",
                  "minLength": 1
                },
                "service": {
                  "description": "Optional expected exporting-resource service name (the span's resource-level service.name attribute). May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "spanName": {
                  "description": "Optional expected span name (operation name). May contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "string"
                },
                "attributes": {
                  "description": "Optional map of expected span-attribute key to expected value, compared as text (subset match — the real span may carry additional attributes not mentioned here) — a bare numeric or boolean scalar is read as its literal text. Each value may contain {placeholder} and ${secret:source/path} tokens.",
                  "type": "object",
                  "additionalProperties": { "type": ["string", "integer", "number", "boolean"] }
                }
              },
              "additionalProperties": false
            }
          }
        }
        """);

    /// <inheritdoc />
    public TraceExpectOtlpModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new TraceExpectOtlpModel(
                Receiver: string.Empty,
                Match: new TraceMatch(TraceId: null, Service: null, SpanName: null, Attributes: null));
        }

        var receiver = GetScalar(mapping, "receiver");
        var match = BindMatch(mapping);

        return new TraceExpectOtlpModel(Receiver: receiver, Match: match);
    }

    /// <summary>
    /// Parses the nested <c>match</c> mapping into a <see cref="TraceMatch"/>. Tolerant of an
    /// absent or non-mapping <c>match</c> node — both yield an all-<see langword="null"/> match
    /// (which validation then rejects).
    /// </summary>
    private static TraceMatch BindMatch(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
            || matchNode is not YamlMappingNode matchMap)
        {
            return new TraceMatch(TraceId: null, Service: null, SpanName: null, Attributes: null);
        }

        var traceId = GetOptionalScalar(matchMap, "traceId");
        var service = GetOptionalScalar(matchMap, "service");
        var spanName = GetOptionalScalar(matchMap, "spanName");
        var attributes = BindStringMap(matchMap, "attributes");

        return new TraceMatch(TraceId: traceId, Service: service, SpanName: spanName, Attributes: attributes);
    }

    /// <summary>
    /// Reads an optional scalar field: present as a scalar → its value (empty string for a
    /// null scalar); absent or non-scalar → <see langword="null"/>.
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
    /// Reads an optional string→string mapping field from <paramref name="parent"/>. Returns
    /// <see langword="null"/> when the field is absent or not a mapping.
    /// </summary>
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

    // ── IStepValidator<TraceExpectOtlpModel> ──────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// No dependency reconciliation is performed: the receiver is a HOST resource this provider
    /// itself contributes (via <see cref="IHostResourceContributor{TModel}"/>), not a declared
    /// <c>environment.dependencies</c> entry the author must reconcile.
    /// </remarks>
    /// <remarks>
    /// <see cref="TraceMatch.TraceId"/> is REQUIRED (not merely "at least one criterion of
    /// several") — see the class remarks for why: without it, a declared
    /// <c>service</c>/<c>spanName</c>/<c>attributes</c> combination would accept ANY span
    /// exported by the SUT that happens to match, from ANY trace, not the specific
    /// transaction the scenario is asserting about. This is the enforcement half of the
    /// no-forged-match security posture documented on the host-owned <c>OtlpReceiver</c> —
    /// without this check that posture would be aspirational prose, not an actual guarantee.
    /// </remarks>
    public ValidationResult Validate(TraceExpectOtlpModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Receiver))
            errors.Add("trace-expect.otlp: 'receiver' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Match.TraceId))
        {
            errors.Add(
                "trace-expect.otlp: 'match.traceId' is required. This family asserts the " +
                "causal chain of a SPECIFIC transaction, not a general span shape any trace " +
                "might satisfy: inject a traceparent header on an earlier step (e.g. an " +
                "http.rest request) and match its trace id here — a full traceparent value " +
                "or a bare 32-hex trace id both work, the 32-hex segment is extracted " +
                "automatically. 'service', 'spanName', and 'attributes' are optional " +
                "refinements layered on top of the trace id; none of them may substitute " +
                "for it.");
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── IHostResourceContributor<TraceExpectOtlpModel> ────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="HostResourceRequirement"/> of kind <c>"otlp-receiver"</c>
    /// named by <see cref="TraceExpectOtlpModel.Receiver"/>. This is what makes the engine
    /// stand up the ephemeral host-owned OTLP/HTTP receiver and stage its base URL before step 1
    /// (Phase C, mirroring S07-F-01a exactly). No <see cref="IResourceContributor{TModel}"/> is
    /// implemented: the receiver is an in-process host resource, not a containerised Aspire
    /// dependency, so this provider contributes NO Aspire container.
    /// </remarks>
    public IEnumerable<HostResourceRequirement> HostResources(TraceExpectOtlpModel model)
    {
        yield return new HostResourceRequirement("otlp-receiver", model.Receiver);
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block. Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.Threading.Tasks",
        "Vouchfx.Engine.Abstractions",
        "Vouchfx.Engine.Abstractions.Traces",
    };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a PLAIN (non-<c>$</c>) C# 11 raw string literal — it is ordinary top-level C#
    /// source text spliced verbatim into the assembled CSX submission, not a CSX
    /// <see cref="CsxFragment.StatementBlock"/>, so none of the <c>$$"""…"""</c>
    /// brace-escaping rules apply here; every <c>{</c>/<c>}</c> below is literal C# syntax.
    /// </para>
    /// <para>
    /// IDEMPOTENT single scan (§7): the helper reads the captured-span snapshot once and writes
    /// <c>Pass</c> on a match or <c>Fail</c> on a non-match. It never writes
    /// <c>Inconclusive</c> from the match scan itself — only an unmet <c>capture:</c>
    /// expression does that (upstream-capture-unmet, §12.1), exactly like
    /// <c>HttpRest_Helpers</c>/<c>MetricsAssertPrometheus_Helpers</c>.
    /// </para>
    /// <para>
    /// SECURITY (§17): the observation carries ONLY match metadata
    /// (<c>matched</c>/<c>scanned</c>/<c>matchedCriteria</c>/<c>totalCriteria</c>, plus
    /// <c>evicted</c> on a Fail) and never a captured span's trace id, name, service, or
    /// attribute value — captured spans are untrusted SUT-originated data (see the file
    /// header).
    /// </para>
    /// <para>
    /// The helper must be byte-identical across every instance of the same provider within a
    /// suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </remarks>
    private const string HelperSource = """
        static class TraceExpectOtlp_Helpers
        {
            /// <summary>
            /// Performs ONE idempotent scan over a host-owned OTLP receiver's captured spans
            /// and writes a typed StepOutcome into Vars. A span matching ALL declared criteria
            /// = Pass; none match this attempt = Fail (the RETRY runner retries on non-Pass and
            /// converts a sustained Fail to Inconclusive on timeout — this helper never writes
            /// Inconclusive from the scan itself, §7/§12.1). A missing secret = EnvironmentError,
            /// reference-only (§12.1/§17). An unmet capture: expression = Inconclusive
            /// (upstream-capture-unmet, §12.1), mirroring http.rest / metrics-assert.prometheus.
            /// </summary>
            public static async System.Threading.Tasks.Task AssertAsync(
                System.Collections.Generic.IDictionary<string, object?> vars,
                Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,
                Vouchfx.Engine.Abstractions.Traces.ITraceCaptureAccessor traces,
                string outcomeKey,
                string captureStatusKey,
                string receiverVarName,
                string? traceIdExpectedTemplate,
                string? serviceExpectedTemplate,
                string? spanNameExpectedTemplate,
                string[] attributeNames,
                string[] attributeValueTemplates,
                string[] captureVarNames,
                string[] captureExprs,
                string[] captureKinds,
                System.Threading.CancellationToken ct)
            {
                // No I/O is performed here — the scan is purely over the in-memory snapshot —
                // but the method is async to match the awaited call-site shape every RETRY
                // consumer uses (the emitted block 'await's it).
                await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Vouchfx.Engine.Abstractions.Verdict verdict;
                string observation;
                try
                {
                    // Resolve every expected author-text field INSIDE the guarded region, in a
                    // SINGLE pass via ResolveTemplate (both {placeholder} substitution and
                    // ${secret:source/path} resolution over the original template). A missing
                    // secret throws SecretResolutionException here, before any matching runs.
                    string? traceIdExpected = traceIdExpectedTemplate is null
                        ? null
                        : ExtractTraceId(Secret_Helpers.ResolveTemplate(secrets, vars, traceIdExpectedTemplate));
                    string? serviceExpected = serviceExpectedTemplate is null
                        ? null
                        : Secret_Helpers.ResolveTemplate(secrets, vars, serviceExpectedTemplate);
                    string? spanNameExpected = spanNameExpectedTemplate is null
                        ? null
                        : Secret_Helpers.ResolveTemplate(secrets, vars, spanNameExpectedTemplate);

                    var attributeValues = new string[attributeValueTemplates.Length];
                    for (int ai = 0; ai < attributeValueTemplates.Length; ai++)
                    {
                        attributeValues[ai] = Secret_Helpers.ResolveTemplate(secrets, vars, attributeValueTemplates[ai]);
                    }

                    // Read the host-owned receiver's captured-span snapshot. GetCaptured
                    // returns an empty list (never null) for an unregistered receiver, so a
                    // step naming an unknown receiver simply scans zero spans and Fails.
                    var captured = traces.GetCaptured(receiverVarName);
                    int scanned = captured.Count;
                    // TotalReceived is monotonic (never reset by eviction); the difference
                    // against the CURRENTLY retained count is exactly how many spans the ring
                    // buffer has evicted — the diagnostic that tells "scanned:10000" (buffer
                    // saturated, oldest spans evicted) apart from "the SUT genuinely exported
                    // only a few hundred spans, none matching" (see the Fail branch below).
                    long totalReceived = traces.GetTotalReceived(receiverVarName);
                    long evicted = totalReceived - scanned;

                    int totalCriteria = 0;
                    if (traceIdExpected != null) totalCriteria++;
                    if (serviceExpected != null) totalCriteria++;
                    if (spanNameExpected != null) totalCriteria++;
                    if (attributeNames.Length > 0) totalCriteria++;

                    bool matched = false;
                    int bestMatchedCriteria = 0;
                    Vouchfx.Engine.Abstractions.Traces.CapturedSpan? matchedSpan = null;

                    for (int i = 0; i < captured.Count; i++)
                    {
                        var span = captured[i];
                        int matchedCriteria = 0;
                        bool ok = true;

                        if (traceIdExpected != null)
                        {
                            if (string.Equals(span.TraceId, traceIdExpected, System.StringComparison.OrdinalIgnoreCase)) matchedCriteria++;
                            else ok = false;
                        }

                        if (serviceExpected != null)
                        {
                            if (string.Equals(span.ServiceName, serviceExpected, System.StringComparison.Ordinal)) matchedCriteria++;
                            else ok = false;
                        }

                        if (spanNameExpected != null)
                        {
                            if (string.Equals(span.Name, spanNameExpected, System.StringComparison.Ordinal)) matchedCriteria++;
                            else ok = false;
                        }

                        if (attributeNames.Length > 0)
                        {
                            bool attrsOk = true;
                            for (int ai = 0; ai < attributeNames.Length; ai++)
                            {
                                if (!span.Attributes.TryGetValue(attributeNames[ai], out var actualValue)
                                    || !string.Equals(actualValue, attributeValues[ai], System.StringComparison.Ordinal))
                                {
                                    attrsOk = false;
                                    break;
                                }
                            }

                            if (attrsOk) matchedCriteria++;
                            else ok = false;
                        }

                        if (matchedCriteria > bestMatchedCriteria)
                        {
                            bestMatchedCriteria = matchedCriteria;
                        }

                        if (ok)
                        {
                            matched = true;
                            matchedSpan = span;
                            break;
                        }
                    }

                    if (matched && matchedSpan != null)
                    {
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;
                        observation = "{\"matched\":true,\"scanned\":" +
                            scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ",\"matchedCriteria\":" + totalCriteria.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ",\"totalCriteria\":" + totalCriteria.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

                        // ── capture: (mini dotted-path evaluator over a synthesised observation) ──
                        // Cheap, dependency-free (System.Text.Json only — no JsonPath.Net, see
                        // the file header). Only "json"-format expressions are supported; an
                        // XPath-format capture always misses (no XML content to XPath over).
                        if (captureVarNames.Length > 0)
                        {
                            var attributesJson = new System.Text.StringBuilder("{");
                            bool firstAttr = true;
                            foreach (var kv in matchedSpan.Attributes)
                            {
                                if (!firstAttr) attributesJson.Append(',');
                                firstAttr = false;
                                attributesJson.Append(System.Text.Json.JsonSerializer.Serialize(kv.Key));
                                attributesJson.Append(':');
                                attributesJson.Append(System.Text.Json.JsonSerializer.Serialize(kv.Value));
                            }
                            attributesJson.Append('}');

                            var obsJson = "{" +
                                "\"traceId\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.TraceId) + "," +
                                "\"spanId\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.SpanId) + "," +
                                "\"parentSpanId\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.ParentSpanId) + "," +
                                "\"name\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.Name) + "," +
                                "\"serviceName\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.ServiceName) + "," +
                                "\"statusCode\":" + System.Text.Json.JsonSerializer.Serialize(matchedSpan.StatusCode) + "," +
                                "\"attributes\":" + attributesJson.ToString() +
                                "}";

                            var obsDoc = System.Text.Json.JsonDocument.Parse(obsJson);
                            try
                            {
                                var matchedFlags = new bool[captureVarNames.Length];
                                for (int ci = 0; ci < captureVarNames.Length; ci++)
                                {
                                    var varName = captureVarNames[ci];
                                    var captureExpr = captureExprs[ci];
                                    var captureKind = captureKinds[ci];
                                    bool capMatched = false;
                                    if (string.Equals(captureKind, "json", System.StringComparison.Ordinal)
                                        && TryEvaluateDottedPath(obsDoc.RootElement, captureExpr, out var capturedValue))
                                    {
                                        vars[varName] = capturedValue;
                                        capMatched = true;
                                    }

                                    matchedFlags[ci] = capMatched;
                                    if (!capMatched)
                                    {
                                        verdict = Vouchfx.Engine.Abstractions.Verdict.Inconclusive;
                                        observation = "{\"captureUnmet\":" +
                                            System.Text.Json.JsonSerializer.Serialize(varName) + "}";
                                    }
                                }

                                vars[captureStatusKey] = string.Join(
                                    ",", System.Array.ConvertAll(matchedFlags, f => f ? "1" : "0"));
                            }
                            finally
                            {
                                obsDoc.Dispose();
                            }
                        }
                    }
                    else
                    {
                        // No match THIS attempt = Fail. The RETRY runner retries on non-Pass and
                        // converts a sustained Fail to Inconclusive on timeout — never here.
                        // COUNTS ONLY (§17): matchedCriteria reports the best PARTIAL match found
                        // (never which span, never its trace id/name/attributes). "evicted"
                        // surfaces the ring-buffer denial-of-service trade-off (see
                        // TraceCaptureBuffer's remarks): a flood of forged/unrelated exports can
                        // push the real span out of the retained window before this step scans
                        // it — a LOUD Fail with evicted > 0, never a silent false Pass.
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                        observation = "{\"matched\":false,\"scanned\":" +
                            scanned.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ",\"matchedCriteria\":" + bestMatchedCriteria.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ",\"totalCriteria\":" + totalCriteria.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ",\"evicted\":" + evicted.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                    }
                }
                catch (Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException sre)
                {
                    // Missing / unknown secret = EnvironmentError (§12.1): a configuration
                    // problem in the run environment, NOT a product defect. REFERENCE-ONLY
                    // observation (§17): a fixed message plus the discrete source/path
                    // coordinates — never the value (none exists when resolution fails).
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"secretError\":\"secret resolution failed\"" +
                        ",\"source\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +
                        ",\"path\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + "}";
                }
                catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Step-token cut (#232): rethrow past this provider's own error handling so
                    // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead
                    // of the generic-error branch below misclassifying it.
                    throw;
                }
                catch (System.Exception ex)
                {
                    // Any other failure = EnvironmentError (§12.1). REFERENCE-ONLY: only the
                    // exception TYPE name is emitted, never ex.Message — a message could, in
                    // principle, echo captured/untrusted span data, which must not reach the
                    // stream.
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" +
                        System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + "}";
                }
                finally
                {
                    sw.Stop();
                }

                vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(
                    verdict, sw.ElapsedMilliseconds, observation);
            }

            /// <summary>
            /// Extracts the 32-hex trace-id segment from a full W3C traceparent value
            /// (<c>&lt;2-hex&gt;-&lt;32-hex&gt;-&lt;16-hex&gt;-&lt;2-hex&gt;</c>); when
            /// <paramref name="resolved"/> does not have that shape it is returned unchanged
            /// (lower-cased) — an author may thread a bare trace id directly.
            /// </summary>
            private static string ExtractTraceId(string resolved)
            {
                var m = s_traceparentPattern.Match(resolved);
                return (m.Success ? m.Groups[1].Value : resolved).ToLowerInvariant();
            }

            // No RegexOptions.Compiled: this pattern runs at most once per step attempt (never
            // a per-span hot loop, unlike the memory-leak closure probe's touches) so the
            // upfront IL-generation cost of a compiled regex is not worth paying — the
            // interpreted engine is fast enough at this call frequency.
            private static readonly System.Text.RegularExpressions.Regex s_traceparentPattern =
                new System.Text.RegularExpressions.Regex(
                    @"^[0-9a-fA-F]{2}-([0-9a-fA-F]{32})-[0-9a-fA-F]{16}-[0-9a-fA-F]{2}$");

            /// <summary>
            /// Evaluates a MINIMAL, JSONPath-compatible dotted field-path expression
            /// (<c>$.field</c> or <c>$.nested.field</c>) against <paramref name="root"/>. Does
            /// NOT support array indexing, wildcards, filters, or recursive descent — see the
            /// file-header CAPTURE SUPPORT note for the rationale. Returns
            /// <see langword="false"/> (a miss, never a crash) for any unsupported syntax, a
            /// missing property at any segment, or a JSON null value.
            /// </summary>
            private static bool TryEvaluateDottedPath(System.Text.Json.JsonElement root, string expr, out string value)
            {
                value = string.Empty;
                if (string.IsNullOrEmpty(expr) || expr[0] != '$')
                {
                    return false;
                }

                string remainder;
                if (expr.Length == 1)
                {
                    remainder = string.Empty;
                }
                else if (expr[1] == '.')
                {
                    remainder = expr.Substring(2);
                }
                else
                {
                    return false;
                }

                var current = root;
                if (remainder.Length > 0)
                {
                    var segments = remainder.Split('.');
                    for (int i = 0; i < segments.Length; i++)
                    {
                        if (segments[i].Length == 0
                            || current.ValueKind != System.Text.Json.JsonValueKind.Object
                            || !current.TryGetProperty(segments[i], out var next))
                        {
                            return false;
                        }

                        current = next;
                    }
                }

                if (current.ValueKind == System.Text.Json.JsonValueKind.Null)
                {
                    return false;
                }

                value = current.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (current.GetString() ?? string.Empty)
                    : current.GetRawText();
                return true;
            }
        }
        """;

    private static readonly IReadOnlyList<string> s_helpers = new[] { HelperSource };

    // ── IStepCompiler<TraceExpectOtlpModel> ───────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution reads the host-owned receiver's captured spans via
    /// <c>Traces.GetCaptured(model.Receiver)</c>, evaluates each against the resolved criteria,
    /// and writes a typed <see cref="Vouchfx.Engine.Abstractions.StepOutcome"/> into
    /// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read after the script
    /// returns.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>; the full <c>static class
    /// TraceExpectOtlp_Helpers</c> definition (plus <c>Secret_Helpers</c>) in
    /// <see cref="CsxFragment.RequiredHelpers"/>; a single C# 11 <c>$$"""…"""</c>
    /// <see cref="CsxFragment.StatementBlock"/> with no <c>using var</c>; the step id sanitised
    /// via <c>CsxFragment.SanitiseId</c> before splicing.
    /// </para>
    /// </remarks>
    public CsxFragment Emit(TraceExpectOtlpModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var match = model.Match;

        string[] attributeNames;
        string[] attributeValueTemplates;
        if (match.Attributes is { Count: > 0 } attributes)
        {
            attributeNames = attributes.Keys.ToArray();
            attributeValueTemplates = attributes.Values.ToArray();
        }
        else
        {
            attributeNames = Array.Empty<string>();
            attributeValueTemplates = Array.Empty<string>();
        }

        var attributeNamesLiteral = BuildStringArrayLiteral(attributeNames);
        var attributeValueTemplatesLiteral = BuildStringArrayLiteral(attributeValueTemplates);

        // The expected traceId / service / spanName are optional: emit the JSON-escaped
        // template literal when present, or the bare 'null' literal when absent. Any
        // {placeholder} or ${secret:…} token inside survives as LITERAL TEXT here (not an
        // emit-time interpolation hole) and is processed at runtime. CRITICAL: inside a
        // $$"""…""" block, {{expr}} is the interpolation hole; a lone {placeholder} or
        // ${secret:…} passes through verbatim.
        var traceIdLiteral = match.TraceId is null ? "null" : JsonSerializer.Serialize(match.TraceId);
        var serviceLiteral = match.Service is null ? "null" : JsonSerializer.Serialize(match.Service);
        var spanNameLiteral = match.SpanName is null ? "null" : JsonSerializer.Serialize(match.SpanName);

        // The receiver name is a plain field — no {placeholder}/${secret} resolution applies to
        // the receiver name itself; it keys both the staged URL and the capture buffer.
        var receiverLiteral = JsonSerializer.Serialize(model.Receiver);

        // Format-aware captures: mirrors HttpRestProvider / MetricsAssertPrometheusProvider's
        // expansion of ctx.CaptureExprs into three parallel arrays.
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
        // 'Secrets' and 'Traces' are ScriptGlobalVariables instance properties.
        var block = $$"""
            {
                await TraceExpectOtlp_Helpers.AssertAsync(
                    Vars,
                    Secrets,
                    Traces,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{receiverLiteral}},
                    {{traceIdLiteral}},
                    {{serviceLiteral}},
                    {{spanNameLiteral}},
                    {{attributeNamesLiteral}},
                    {{attributeValueTemplatesLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureExprsLiteral}},
                    {{captureKindsLiteral}},
                    __stepCt_{{safeId}});
            }
            """;

        // Build the helpers list: TraceExpectOtlp_Helpers + Secret_Helpers (ResolveTemplate is
        // self-contained — it resolves {placeholder} tokens against Vars itself, so
        // Substitute_Helpers is not needed here). Byte-identical across providers —
        // deduplication is handled by CsxAssembler.
        var helpers = new List<string>(s_helpers)
        {
            SecretHelper.Source,
        };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IStepDiffRenderer ──────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Recognises this provider's Fail-shaped observation: an object with <c>"matched":false</c>
    /// plus the <c>scanned</c>/<c>matchedCriteria</c>/<c>totalCriteria</c> counts. The renderer
    /// is looked up by the step's <c>kind</c> first (§14.5), so there is no ambiguity with any
    /// other provider's similarly-shaped observation.
    /// </remarks>
    public bool CanRender(JsonElement observation)
        => observation.ValueKind == JsonValueKind.Object
           && observation.TryGetProperty("matched", out var matchedEl)
           && matchedEl.ValueKind == JsonValueKind.False
           && observation.TryGetProperty("scanned", out _)
           && observation.TryGetProperty("matchedCriteria", out _)
           && observation.TryGetProperty("totalCriteria", out _);

    /// <inheritdoc />
    /// <remarks>
    /// SECURITY (§17): renders ONLY the counts already present in the observation — never a
    /// span's trace id, name, service, or attribute value, none of which the observation itself
    /// ever carries (see the file header).
    /// </remarks>
    public string? RenderDiff(JsonElement observation)
    {
        if (!CanRender(observation))
        {
            return null;
        }

        var scanned = observation.TryGetProperty("scanned", out var scannedEl) && scannedEl.ValueKind == JsonValueKind.Number
            ? scannedEl.GetInt32()
            : 0;
        var matchedCriteria = observation.TryGetProperty("matchedCriteria", out var matchedCriteriaEl) && matchedCriteriaEl.ValueKind == JsonValueKind.Number
            ? matchedCriteriaEl.GetInt32()
            : 0;
        var totalCriteria = observation.TryGetProperty("totalCriteria", out var totalCriteriaEl) && totalCriteriaEl.ValueKind == JsonValueKind.Number
            ? totalCriteriaEl.GetInt32()
            : 0;
        var evicted = observation.TryGetProperty("evicted", out var evictedEl) && evictedEl.ValueKind == JsonValueKind.Number
            ? evictedEl.GetInt64()
            : (long?)null;

        var sb = new StringBuilder();
        sb.Append("  trace-expect.otlp: no captured span matched every declared criterion").Append('\n');
        sb.Append("    spans scanned      : ").Append(scanned.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("    best partial match : ")
          .Append(matchedCriteria.ToString(CultureInfo.InvariantCulture))
          .Append('/')
          .Append(totalCriteria.ToString(CultureInfo.InvariantCulture))
          .Append(" criteria").Append('\n');
        if (evicted is > 0)
        {
            sb.Append("    spans evicted      : ")
              .Append(evicted.Value.ToString(CultureInfo.InvariantCulture))
              .Append(" (ring buffer saturated — the SUT exported more spans than the receiver")
              .Append(" retains; a flood may have pushed the real span out before this scan)")
              .Append('\n');
        }
        sb.Append("    (span attribute values, span names, and the trace id are never rendered — §17)");
        return sb.ToString();
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

        var sb = new StringBuilder("new string[] { ");
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
