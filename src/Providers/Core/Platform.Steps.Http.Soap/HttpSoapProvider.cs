// Platform.Steps.Http.Soap — http.soap step provider (DSL §5.1, second http-family provider, §13).
//
// REUSES Platform.Steps.Core.HttpRest's patterns throughout (per the task brief): Target+Path
// addressing and the SSRF-hardening path guard, Secret_Helpers.ResolveTemplate templating, the
// hardened XmlReaderSettings block (DtdProcessing.Prohibit / XmlResolver=null / character caps),
// XPath evaluation via XPathNavigator.SelectSingleNode, HttpClient closure/disposal
// (AllowAutoRedirect=false + explicit Dispose in finally, no 'using var'), and the http.rest
// catch-chain verdict mapping (secret → EnvironmentError reference-only; timeout →
// Inconclusive; other → EnvironmentError with ex.Message, since a transport-level exception
// message is client-side diagnostic text, not untrusted SUT-originated data).
//
// RAW-ENVELOPE DECISION (v1, documented on HttpSoapModel.Envelope): the author supplies the
// FULL SOAP envelope XML; there is no auto-wrapping of a bare payload. This keeps the contract
// small and unambiguous (no guessing at namespace prefixes, SOAP version, or header
// placement).
//
// XPATH NAMESPACE HANDLING (documented decision, matching http.rest for consistency): NO
// System.Xml.IXmlNamespaceResolver / XmlNamespaceManager is registered — exactly like
// http.rest's XPath capture (HttpRest_Helpers never builds one either). Authors write
// namespace-agnostic expressions using local-name() (e.g.
// //*[local-name()='Body']/*[local-name()='GetUserResponse']); a prefixed expression
// (//soap:Body) is not guaranteed to resolve. This is a deliberate consistency choice with the
// established http.rest precedent, not an oversight.
//
// ENVELOPE VALIDATION STRICTNESS (documented decision — ValidationResult has no WARNING level,
// only Errors, so a lenient "warn but continue" path is not available): Validate ATTEMPTS A
// FULL XML PARSE of the envelope template TEXT (with {placeholder}/${secret:…} tokens still
// present as literal characters) using the SAME hardened XmlReaderSettings used at execution
// time, and rejects with a validation ERROR on failure. This is safe against false positives on
// legitimate placeholder usage: none of '{', '}', '$', ':', '/' are XML-reserved characters
// requiring escaping in element text or attribute-value content, so a {placeholder} or
// ${secret:source/path} token sitting inside ordinary envelope text/attribute content parses as
// perfectly well-formed XML. The only way a placeholder could break well-formedness is if an
// author used one to construct a TAG or ATTRIBUTE NAME dynamically — an unusual pattern outside
// this provider's intended usage — so a strict parse-and-reject check is both the STRICTEST
// available option and, by this character-level analysis, one that will not spuriously reject
// a legitimately templated envelope.
//
// SOAP FAULT / STATUS INTERPLAY (documented decision — the task's three bullets ["unexpected
// Fault → Fail", "expected Fault present → Pass (+ xpath)", "status mismatch → Fail"] are
// evaluated in that PRECEDENCE order): fault-expectation is checked FIRST (a
// mismatch — either an unexpected Fault present OR an expected Fault absent — is an immediate
// Fail); only once fault-expectation is satisfied (both true or both false) does the emitted
// helper check the HTTP status; only once BOTH pass does it evaluate any declared XPath
// assertions.  On an unexpected fault the observation carries ONLY the faultcode (a fixed
// QName-shaped token, e.g. "soap:Server" — safe to report verbatim per §17) and NEVER the
// faultstring (which may embed SUT-originated data).
using System.Globalization;
using System.Text;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.Http.Soap;

/// <summary>
/// Core provider for the <c>http.soap</c> step kind (DSL §5.1) — the second member of the
/// <c>http</c> family. Issues a SOAP 1.1 request (raw author-supplied envelope) to a logically
/// named service and asserts on the response's HTTP status, SOAP Fault presence, and optional
/// XPath-located values.
/// </summary>
[StepProvider]
public sealed class HttpSoapProvider
    : IStepProvider,
      IStepBinder<HttpSoapModel>,
      IStepValidator<HttpSoapModel>,
      IStepCompiler<HttpSoapModel>,
      IResourceContributor<HttpSoapModel>,
      ICompileReferenceContributor
{
    /// <summary>Default expected HTTP status when <c>expect.status</c> is omitted (SOAP 1.1 convention).</summary>
    private const int DefaultExpectedStatus = 200;

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("http", "soap");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<HttpSoapModel> ─────────────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>http.soap</c> provider's own
    /// fields.
    /// </summary>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "path", "envelope"],
          "properties": {
            "target": {
              "description": "Logical name of the service to call, as declared under environment.services.",
              "type": "string"
            },
            "path": {
              "description": "The request path; may contain {placeholder} and ${secret:...} tokens.",
              "type": "string"
            },
            "action": {
              "description": "Optional SOAPAction header value (SOAP 1.1 convention), sent quoted per spec. May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "envelope": {
              "description": "The FULL SOAP request envelope XML, given as a raw template string (no auto-wrapping). Sent as Content-Type: text/xml; charset=utf-8. May contain {placeholder} and ${secret:...} tokens.",
              "type": "string"
            },
            "expect": {
              "description": "Optional assertion block applied to the SOAP response.",
              "type": "object",
              "properties": {
                "status": {
                  "description": "Expected HTTP status code. Defaults to 200 when omitted.",
                  "type": "integer"
                },
                "fault": {
                  "description": "Whether the response is expected to contain a SOAP Fault element. Defaults to false.",
                  "type": "boolean"
                },
                "xpath": {
                  "description": "List of {path, value} assertions evaluated against the response envelope, after the status and fault-expectation checks both pass.",
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["path", "value"],
                    "properties": {
                      "path": { "type": "string" },
                      "value": { "type": "string" }
                    }
                  }
                }
              }
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public HttpSoapModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new HttpSoapModel(
                Target: string.Empty,
                Path: string.Empty,
                Action: null,
                Envelope: string.Empty,
                Expect: null);
        }

        var target = GetScalar(mapping, "target");
        var path = GetScalar(mapping, "path");
        var action = GetOptionalScalar(mapping, "action");

        // The envelope is kept as the literal template string — the author owns the exact
        // bytes. {placeholder} / ${secret:source/path} tokens survive verbatim into the model
        // and are resolved at execution time inside the emitted helper's guarded region,
        // exactly like http.rest's body (§17).
        var envelope = GetScalar(mapping, "envelope");

        SoapExpectation? expect = null;
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

            bool? fault = null;
            if (expectMap.Children.TryGetValue(new YamlScalarNode("fault"), out var faultNode)
                && faultNode is YamlScalarNode faultScalar
                && bool.TryParse(faultScalar.Value, out var faultBool))
            {
                fault = faultBool;
            }

            List<SoapXPathAssertion>? xpath = null;
            if (expectMap.Children.TryGetValue(new YamlScalarNode("xpath"), out var xpathNode)
                && xpathNode is YamlSequenceNode xpathSeq)
            {
                xpath = new List<SoapXPathAssertion>();
                foreach (var item in xpathSeq.Children)
                {
                    if (item is not YamlMappingNode itemMap)
                        continue;

                    var itemPath = GetScalar(itemMap, "path");
                    var itemValue = GetScalar(itemMap, "value");
                    xpath.Add(new SoapXPathAssertion(itemPath, itemValue));
                }
            }

            expect = new SoapExpectation(Status: status, Fault: fault, Xpath: xpath);
        }

        return new HttpSoapModel(
            Target: target,
            Path: path,
            Action: action,
            Envelope: envelope,
            Expect: expect);
    }

    // ── IStepValidator<HttpSoapModel> ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// See the file header ("ENVELOPE VALIDATION STRICTNESS") for why a full XML parse of the
    /// envelope TEMPLATE (placeholders untouched) is a safe, strict, false-positive-free
    /// compile-time check.
    /// </remarks>
    public ValidationResult Validate(HttpSoapModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("http.soap: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Path))
        {
            errors.Add("http.soap: 'path' must not be empty.");
        }
        else
        {
            // SSRF guard (mirrors http.rest exactly): path must be a safe rooted relative
            // reference. See HttpRestProvider.Validate for the platform-independence rationale
            // (no Uri.TryCreate(..., UriKind.Absolute, ...) probe — a leading '/' parses as a
            // file URI on Linux).
            var path = model.Path;
            if (!path.StartsWith('/'))
            {
                errors.Add(
                    "http.soap: 'path' must be a rooted relative path (start with '/'); " +
                    "absolute URLs and protocol-relative paths are not allowed.");
            }
            else if (path.StartsWith("//", StringComparison.Ordinal))
            {
                errors.Add(
                    "http.soap: 'path' must be a rooted relative path (start with '/'); " +
                    "absolute URLs and protocol-relative paths are not allowed.");
            }
            else if (path.Contains('\\', StringComparison.Ordinal))
            {
                errors.Add(
                    "http.soap: 'path' must not contain backslashes; " +
                    "use forward slashes for URL path separators.");
            }
        }

        if (string.IsNullOrWhiteSpace(model.Envelope))
        {
            errors.Add("http.soap: 'envelope' must not be empty.");
        }
        else if (!IsWellFormedXmlTemplate(model.Envelope))
        {
            errors.Add(
                "http.soap: 'envelope' is not well-formed XML (checked with placeholders left " +
                "as literal text — {placeholder} and ${secret:...} tokens inside element text " +
                "or attribute values do not affect well-formedness). Verify the envelope parses " +
                "as XML independent of any substitution.");
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    /// <summary>
    /// Attempts to parse <paramref name="envelopeTemplate"/> as XML using the same hardened
    /// settings the execution-time XPath evaluation uses (DTD prohibited, no external
    /// resolver, bounded document size). Returns <see langword="false"/> on any parse failure —
    /// never throws.
    /// </summary>
    private static bool IsWellFormedXmlTemplate(string envelopeTemplate)
    {
        System.Xml.XmlReader? reader = null;
        try
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 10_000_000,
            };
            reader = System.Xml.XmlReader.Create(new System.IO.StringReader(envelopeTemplate), settings);
            var doc = new System.Xml.XmlDocument { XmlResolver = null };
            doc.Load(reader);
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block. Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Net.Http",
        "System.Diagnostics",
        "System.Threading.Tasks",
        "Platform.Engine.Abstractions",
    };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1). A PLAIN (non-<c>$</c>)
    /// C# 11 raw string literal — ordinary top-level C# source spliced verbatim into the
    /// assembled CSX submission, not a <see cref="CsxFragment.StatementBlock"/>, so none of the
    /// <c>$$"""…"""</c> brace-escaping rules apply; every brace below is literal C# syntax.
    /// </summary>
    private const string HelperSource = """
        static class HttpSoap_Helpers
        {
            /// <summary>
            /// Issues a SOAP 1.1 POST, evaluates the response's fault-expectation, HTTP status,
            /// and any XPath assertions in that precedence order, and writes a typed
            /// StepOutcome into Vars. Mirrors HttpRest_Helpers.ExecuteAsync's transport/catch
            /// chain and hardened-XML-reader discipline exactly (see the provider's file
            /// header for the documented fault/status precedence decision).
            /// </summary>
            public static async System.Threading.Tasks.Task ExecuteAsync(
                System.Collections.Generic.IDictionary<string, object?> vars,
                Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,
                string outcomeKey,
                string captureStatusKey,
                string serviceKey,
                string pathTemplate,
                string? actionTemplate,
                string envelopeTemplate,
                int expectedStatus,
                bool expectedFault,
                string[] xpathPaths,
                string[] xpathValueTemplates,
                string[] captureVarNames,
                string[] captureExprs,
                string[] captureKinds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Platform.Engine.Abstractions.Verdict verdict;
                string observation;
                // AllowAutoRedirect=false: a 3xx from the target must not silently bounce the
                // request to a different host (SSRF via redirect — mirrors http.rest's M1 guard).
                var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };
                var client = new System.Net.Http.HttpClient(handler, disposeHandler: true);
                try
                {
                    client.Timeout = System.TimeSpan.FromSeconds(30);
                    // Bound the untrusted response body exactly like http.rest (§security S07).
                    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;

                    // Resolve the path INSIDE the guarded region (§17), single pass (both
                    // {placeholder} substitution and ${secret:...} resolution).
                    var path = Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate);
                    var baseUrl = vars.TryGetValue(serviceKey, out var bu) && bu is string s ? s : "";
                    var baseUri = new System.Uri(baseUrl, System.UriKind.Absolute);
                    var full = new System.Uri(baseUri, path);
                    if (full.GetLeftPart(System.UriPartial.Authority) != baseUri.GetLeftPart(System.UriPartial.Authority))
                    {
                        throw new System.InvalidOperationException(
                            "http.soap: resolved URL authority '" + full.Authority +
                            "' does not match base authority '" + baseUri.Authority +
                            "'; path attempted to change host.");
                    }

                    using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, full))
                    {
                        if (actionTemplate != null)
                        {
                            var action = Secret_Helpers.ResolveTemplate(secrets, vars, actionTemplate);
                            // SOAP 1.1 convention: the SOAPAction header value is a quoted string.
                            req.Headers.TryAddWithoutValidation("SOAPAction", "\"" + action + "\"");
                        }

                        // Resolve + attach the envelope INSIDE the guarded region, mirroring
                        // http.rest's body handling exactly. Content-Type: text/xml;
                        // charset=utf-8 (SOAP 1.1 — SOAP 1.2's application/soap+xml is a
                        // documented v1 limitation, not sent).
                        var envelope = Secret_Helpers.ResolveTemplate(secrets, vars, envelopeTemplate);
                        req.Content = new System.Net.Http.StringContent(envelope, System.Text.Encoding.UTF8, "text/xml");

                        var resp = await client.SendAsync(req).ConfigureAwait(false);
                        var actualStatus = (int)resp.StatusCode;
                        var bodyStr = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        // ── Hardened XML parse (identical hardening to http.rest's XPath capture) ──
                        System.Xml.XPath.XPathNavigator? nav = null;
                        System.Xml.XmlReader? xmlReader = null;
                        try
                        {
                            var xmlSettings = new System.Xml.XmlReaderSettings();
                            xmlSettings.DtdProcessing = System.Xml.DtdProcessing.Prohibit;
                            xmlSettings.XmlResolver = null;
                            xmlSettings.MaxCharactersFromEntities = 0;
                            xmlSettings.MaxCharactersInDocument = 10_000_000;
                            xmlReader = System.Xml.XmlReader.Create(new System.IO.StringReader(bodyStr), xmlSettings);
                            var xmlDoc = new System.Xml.XmlDocument();
                            xmlDoc.XmlResolver = null;
                            xmlDoc.Load(xmlReader);
                            nav = xmlDoc.CreateNavigator();
                        }
                        catch (System.Exception)
                        {
                            // Non-XML / malformed / DTD-bearing / oversized body: nav stays
                            // null — every downstream check treats this as "no fault detected,
                            // no XPath resolvable" (never a crash).
                            nav = null;
                        }
                        finally
                        {
                            xmlReader?.Dispose();
                        }

                        // ── Fault detection (no namespace manager — local-name() match, see header) ──
                        bool actualFault = false;
                        string faultCode = "";
                        if (nav != null)
                        {
                            var faultNode = nav.SelectSingleNode("//*[local-name()='Fault']");
                            if (faultNode != null)
                            {
                                actualFault = true;
                                var codeNode = faultNode.SelectSingleNode("*[local-name()='faultcode']");
                                if (codeNode != null)
                                {
                                    faultCode = codeNode.Value ?? "";
                                }
                            }
                        }

                        // ── Precedence: fault-expectation FIRST, then status, then xpath ──
                        if (actualFault != expectedFault)
                        {
                            verdict = Platform.Engine.Abstractions.Verdict.Fail;
                            observation = actualFault
                                ? "{\"unexpectedFault\":true,\"faultcode\":" + System.Text.Json.JsonSerializer.Serialize(faultCode) + "}"
                                : "{\"expectedFaultMissing\":true}";
                        }
                        else if (actualStatus != expectedStatus)
                        {
                            verdict = Platform.Engine.Abstractions.Verdict.Fail;
                            observation = "{\"status\":" + actualStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                ",\"expected\":" + expectedStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                        else
                        {
                            bool xpathOk = true;
                            if (xpathPaths.Length > 0)
                            {
                                if (nav == null)
                                {
                                    xpathOk = false;
                                }
                                else
                                {
                                    for (int xi = 0; xi < xpathPaths.Length; xi++)
                                    {
                                        var expectedValue = Secret_Helpers.ResolveTemplate(secrets, vars, xpathValueTemplates[xi]);
                                        System.Xml.XPath.XPathNavigator? picked = null;
                                        try
                                        {
                                            picked = nav.SelectSingleNode(xpathPaths[xi]);
                                        }
                                        catch (System.Exception)
                                        {
                                            picked = null;
                                        }

                                        if (picked == null || !string.Equals(picked.Value, expectedValue, System.StringComparison.Ordinal))
                                        {
                                            xpathOk = false;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (!xpathOk)
                            {
                                verdict = Platform.Engine.Abstractions.Verdict.Fail;
                                observation = "{\"xpathMismatch\":true}";
                            }
                            else
                            {
                                verdict = Platform.Engine.Abstractions.Verdict.Pass;
                                observation = "{\"status\":" + actualStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                    ",\"fault\":" + (actualFault ? "true" : "false") + "}";

                                // ── capture: (XPath only — no JSON body exists to JSONPath over) ──
                                if (captureVarNames.Length > 0)
                                {
                                    var matchedFlags = new bool[captureVarNames.Length];
                                    for (int ci = 0; ci < captureVarNames.Length; ci++)
                                    {
                                        var varName = captureVarNames[ci];
                                        var captureExpr = captureExprs[ci];
                                        var captureKind = captureKinds[ci];
                                        bool matched = false;
                                        if (nav != null && string.Equals(captureKind, "xpath", System.StringComparison.Ordinal))
                                        {
                                            try
                                            {
                                                var picked = nav.SelectSingleNode(captureExpr);
                                                if (picked != null)
                                                {
                                                    var capturedStr = picked.Value;
                                                    if (!string.IsNullOrEmpty(capturedStr))
                                                    {
                                                        vars[varName] = capturedStr;
                                                        matched = true;
                                                    }
                                                }
                                            }
                                            catch (System.Exception)
                                            {
                                                matched = false;
                                            }
                                        }

                                        // A bare (JSONPath-format) capture entry, or any capture
                                        // when the body did not parse as XML, is always unmet —
                                        // http.soap supports XPath captures only (no JSON body).
                                        matchedFlags[ci] = matched;
                                        if (!matched)
                                        {
                                            verdict = Platform.Engine.Abstractions.Verdict.Inconclusive;
                                            observation = "{\"captureUnmet\":" + System.Text.Json.JsonSerializer.Serialize(varName) + "}";
                                        }
                                    }

                                    vars[captureStatusKey] = string.Join(",", System.Array.ConvertAll(matchedFlags, f => f ? "1" : "0"));
                                }
                            }
                        }
                    }
                }
                catch (Platform.Engine.Abstractions.Secrets.SecretResolutionException sre)
                {
                    // Missing / unknown secret = EnvironmentError (§12.1), REFERENCE-ONLY (§17).
                    verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"secretError\":\"secret resolution failed\"" +
                        ",\"source\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +
                        ",\"path\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + "}";
                }
                catch (System.Exception ex) when (ex is System.Threading.Tasks.TaskCanceledException
                                                  || ex is System.TimeoutException)
                {
                    // Timeout = Inconclusive (§12.1): the test could not complete due to a
                    // stall, not because the target service responded incorrectly.
                    verdict = Platform.Engine.Abstractions.Verdict.Inconclusive;
                    observation = "{\"timeout\":true}";
                }
                catch (System.Exception ex)
                {
                    // Connection / DNS / authority-change failures = EnvironmentError (§12.1).
                    // ex.Message here is client-side transport diagnostic text (never captured
                    // SUT body/response content), mirroring http.rest's own final catch exactly.
                    verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
                }
                finally
                {
                    sw.Stop();
                    client.Dispose();
                }

                vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(
                    verdict, sw.ElapsedMilliseconds, observation);
            }
        }
        """;

    private static readonly IReadOnlyList<string> s_helpers = new[] { HelperSource };

    // ── IStepCompiler<HttpSoapModel> ──────────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(HttpSoapModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);
        var expect = model.Expect;

        var expectedStatusLiteral = (expect?.Status ?? DefaultExpectedStatus)
            .ToString(CultureInfo.InvariantCulture);
        var expectedFaultLiteral = (expect?.Fault ?? false) ? "true" : "false";

        var pathTemplateLiteral = JsonSerializer.Serialize(model.Path);
        var envelopeTemplateLiteral = JsonSerializer.Serialize(model.Envelope);
        var actionLiteral = model.Action is null ? "null" : JsonSerializer.Serialize(model.Action);

        string[] xpathPaths;
        string[] xpathValueTemplates;
        if (expect?.Xpath is { Count: > 0 } xpathList)
        {
            xpathPaths = new string[xpathList.Count];
            xpathValueTemplates = new string[xpathList.Count];
            for (var i = 0; i < xpathList.Count; i++)
            {
                xpathPaths[i] = xpathList[i].Path;
                xpathValueTemplates[i] = xpathList[i].Value;
            }
        }
        else
        {
            xpathPaths = Array.Empty<string>();
            xpathValueTemplates = Array.Empty<string>();
        }

        var xpathPathsLiteral = BuildStringArrayLiteral(xpathPaths);
        var xpathValueTemplatesLiteral = BuildStringArrayLiteral(xpathValueTemplates);

        // Format-aware captures: mirrors HttpRestProvider.Emit's expansion of ctx.CaptureExprs.
        // Only "xpath"-format entries are ever honoured by the helper (see HelperSource) — a
        // bare (JSONPath-format) entry is still emitted (so its varName participates in the
        // captureStatusKey accounting) but the helper marks it unmet.
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
        var block = $$"""
            {
                await HttpSoap_Helpers.ExecuteAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Service(model.Target))}},
                    {{pathTemplateLiteral}},
                    {{actionLiteral}},
                    {{envelopeTemplateLiteral}},
                    {{expectedStatusLiteral}},
                    {{expectedFaultLiteral}},
                    {{xpathPathsLiteral}},
                    {{xpathValueTemplatesLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureExprsLiteral}},
                    {{captureKindsLiteral}});
            }
            """;

        // Build the helpers list: HttpSoap_Helpers + Secret_Helpers (ResolveTemplate is
        // self-contained — Substitute_Helpers is not needed). Byte-identical across providers —
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

    // ── IResourceContributor<HttpSoapModel> ───────────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(HttpSoapModel model)
    {
        yield return new ResourceRequirement(
            Family: "http",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>System.Net.Http</c> assembly and <c>System.Private.Xml</c> (so the
    /// Roslyn compiler can resolve <c>System.Xml.XmlDocument</c> /
    /// <c>System.Xml.XPath.XPathNavigator</c>), mirroring <c>HttpRestProvider</c> exactly. No
    /// JsonPath.Net reference — this provider supports XPath captures only. Both assemblies are
    /// already loaded in the Default ALC and must never be loaded into the collectible ALC (§5).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(System.Net.Http.HttpClient).Assembly;
            yield return typeof(System.Xml.XmlDocument).Assembly;
        }
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
        for (var i = 0; i < values.Length; i++)
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
