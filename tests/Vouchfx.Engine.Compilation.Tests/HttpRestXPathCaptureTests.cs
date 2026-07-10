// S07-B-01b — HttpRestProvider XPath capture tests (non-docker).
//
// These tests cover the new XPath capture branch added to the http.rest provider
// alongside the existing JSONPath path (S04-B-02).  The capture MODEL already
// distinguishes JSONPath from XPath via CaptureExpr(CaptureFormat, Expression)
// (committed in S07-B-01a); these tests prove the http.rest provider now:
//
//   1. Emit lint: a mix of jsonpath + xpath captures emits THREE parallel arrays
//      (captureVarNames / captureExprs / captureKinds) with the kinds in the same
//      declaration order, no 'using var', and the HttpRest_Helpers class name is
//      unchanged.
//   2. Execution (compile-round-trip, no Docker): an XPath capture over an XML body
//      extracts the right value into Vars and sets the matched flag.
//   3. Execution: a non-XML body → the XPath capture MISSES → Inconclusive (no crash).
//   4. Execution: an invalid XPath expression → that capture MISSES → Inconclusive.
//   5. Execution: a mixed jsonpath + xpath capture set over a JSON body — the
//      JSONPath capture still works (xpath miss on a JSON body is tolerated).
//
// The in-process responder mirrors the JSON responder in HttpRestCaptureTests but
// serves an arbitrary content type / body so an XML payload can be staged without a
// live server or Docker.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S07-B-01b: non-docker tests for <see cref="HttpRestProvider"/> XPath capture.
/// </summary>
public sealed class HttpRestXPathCaptureTests
{
    // ── Stub compile context (format-aware) ───────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ICompileContext"/> that accepts a format-aware capture
    /// map directly, so a test can stage a mix of JSONPath and XPath captures.
    /// <see cref="ICompileContext.Captures"/> is projected from
    /// <see cref="ICompileContext.CaptureExprs"/> (same keys / same order) exactly
    /// as the engine does, so the back-compat view stays consistent.
    /// </summary>
    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(
            string stepId,
            IReadOnlyDictionary<string, CaptureExpr> captureExprs)
        {
            StepId = stepId;
            CaptureExprs = captureExprs;
            Captures = captureExprs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Expression,
                StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    // Additional Roslyn metadata references for the emitted CSX body.
    // Adds System.Private.Xml (XmlDocument / XPathNavigator) for the XPath branch
    // on top of the JSON/HTTP set already required by S04-B-02.
    private static readonly IReadOnlyList<string> s_refs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,           // System.Private.Xml — XPath branch (S07-B-01b)
    };

    // ── 1. Emit lint: three parallel arrays + kinds in order ──────────────────

    /// <summary>
    /// A step with a mix of JSONPath and XPath captures must emit the three parallel
    /// arrays — var-names, expressions, and kinds — with the kinds ("json"/"xpath")
    /// in the SAME declaration order as the captures.  No <c>using var</c> may appear,
    /// and the helper class name must remain <c>HttpRest_Helpers</c>.
    /// </summary>
    [Fact]
    public void Emit_MixedCaptures_EmitsThreeParallelArraysWithKindsInOrder()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);

        // Ordered dictionary semantics: insertion order is preserved by Dictionary<>.
        var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["jsonId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
            ["xmlId"] = new CaptureExpr(CaptureFormat.XPath, "//order/id"),
            ["jsonName"] = new CaptureExpr(CaptureFormat.JsonPath, "$.name"),
        };
        var ctx = new StubCtx("mixed-capture", captureExprs);
        var fragment = provider.Emit(model, ctx);

        var block = fragment.StatementBlock;

        // Var-names and expressions appear as JSON-escaped literals.
        Assert.Contains("\"jsonId\"", block, StringComparison.Ordinal);
        Assert.Contains("\"$.id\"", block, StringComparison.Ordinal);
        Assert.Contains("\"xmlId\"", block, StringComparison.Ordinal);
        Assert.Contains("\"//order/id\"", block, StringComparison.Ordinal);
        Assert.Contains("\"jsonName\"", block, StringComparison.Ordinal);
        Assert.Contains("\"$.name\"", block, StringComparison.Ordinal);

        // Kinds must appear in declaration order: json, xpath, json.
        // Locate the kinds array literal and assert the element ordering.
        var jsonFirst = block.IndexOf("\"json\"", StringComparison.Ordinal);
        var xpathIdx = block.IndexOf("\"xpath\"", StringComparison.Ordinal);
        Assert.True(jsonFirst >= 0, "Expected a \"json\" kind literal in the block.");
        Assert.True(xpathIdx >= 0, "Expected an \"xpath\" kind literal in the block.");
        // The kinds array must contain json before xpath before the second json,
        // matching the capture declaration order.
        var jsonSecond = block.IndexOf("\"json\"", xpathIdx, StringComparison.Ordinal);
        Assert.True(jsonSecond > xpathIdx,
            "Expected a second \"json\" kind after the \"xpath\" kind (declaration order json, xpath, json).");
        Assert.True(jsonFirst < xpathIdx,
            "Expected the first \"json\" kind before the \"xpath\" kind (declaration order json, xpath, json).");

        // The CaptureStatus key must be emitted.
        var safeId = CsxFragment.SanitiseId("mixed-capture");
        Assert.Contains(VarKeys.CaptureStatus(safeId), block, StringComparison.Ordinal);

        // No 'using var' anywhere (helpers + block).
        var full = block + "\n" + string.Join("\n", fragment.RequiredHelpers);
        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);

        // Helper class name unchanged.
        Assert.Contains("HttpRest_Helpers", full, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compile-reference assemblies must include <c>System.Private.Xml</c> so the
    /// Roslyn compiler can resolve <c>System.Xml.XmlDocument</c> / <c>XPathNavigator</c>
    /// used by the XPath branch.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsSystemPrivateXml()
    {
        var contributor = (ICompileReferenceContributor)new HttpRestProvider();
        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("System.Private.Xml", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Two steps with different XPath captures must still produce a byte-identical
    /// <c>HttpRest_Helpers</c> source (dedup invariant, §13.3.1) — the helper carries
    /// no per-step interpolation even with the XPath branch added.
    /// </summary>
    [Fact]
    public void Emit_TwoXPathSteps_HelpersAreByteIdentical()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);

        var c1 = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["a"] = new CaptureExpr(CaptureFormat.XPath, "//a"),
        };
        var c2 = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["b"] = new CaptureExpr(CaptureFormat.XPath, "//b/c"),
        };

        var f1 = provider.Emit(model, new StubCtx("s1", c1));
        var f2 = provider.Emit(model, new StubCtx("s2", c2));

        var h1 = f1.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));
        var h2 = f2.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));

        Assert.Equal(h1, h2, StringComparer.Ordinal);
    }

    // ── 2. Execution: XPath over XML body extracts value + matched flag ───────

    /// <summary>
    /// Full compile-and-run: an XPath capture over an XML response body must write the
    /// extracted node value into <c>Vars</c> and set the matched flag to <c>"1"</c>.
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_XmlBody_WritesVarAndSetsMatchedFlag()
    {
        const string xmlBody = "<order><id>order-xml-7</id><status>shipped</status></order>";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(xmlBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["capturedId"] = new CaptureExpr(CaptureFormat.XPath, "/order/id"),
            };
            var ctx = new StubCtx("xpath-run", captureExprs);
            var fragment = provider.Emit(model, ctx);

            var assembled = CsxAssembler.Assemble(new[] { ("xpath-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("xpath-run");

            Assert.True(vars.ContainsKey("capturedId"),
                $"Expected Vars to contain 'capturedId'. Keys: [{string.Join(", ", vars.Keys)}]");
            Assert.Equal("order-xml-7", vars["capturedId"]);

            var statusKey = VarKeys.CaptureStatus(safeId);
            Assert.True(vars.ContainsKey(statusKey));
            Assert.Equal("1", vars[statusKey]);

            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    /// <summary>
    /// An XPath that selects an attribute value must extract the attribute's string
    /// value (proves the node-string-value extraction is not element-only).
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_Attribute_ExtractsAttributeValue()
    {
        const string xmlBody = "<order id=\"attr-42\"><status>new</status></order>";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(xmlBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null,
                new HttpExpect(Status: 200));

            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["orderId"] = new CaptureExpr(CaptureFormat.XPath, "/order/@id"),
            };
            var ctx = new StubCtx("xpath-attr", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-attr", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            Assert.Equal("attr-42", vars["orderId"]);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 3. Execution: non-XML body → xpath miss → Inconclusive ────────────────

    /// <summary>
    /// When the response body is not valid XML, an XPath capture must MISS (not throw)
    /// and the step must resolve to <see cref="Verdict.Inconclusive"/>
    /// (upstream-capture-unmet, §12.1), with the matched flag set to <c>"0"</c>.
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_NonXmlBody_MissesAndReturnsInconclusive()
    {
        const string body = "this is definitely not xml <<<";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(body, "text/plain", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null, null);
            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["x"] = new CaptureExpr(CaptureFormat.XPath, "/order/id"),
            };
            var ctx = new StubCtx("xpath-nonxml", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-nonxml", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("xpath-nonxml");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);

            Assert.Equal("0", vars[VarKeys.CaptureStatus(safeId)]);
            Assert.False(vars.ContainsKey("x"),
                "A missed XPath capture must not write a value into Vars.");
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 4. Execution: invalid XPath → miss → Inconclusive (no throw) ──────────

    /// <summary>
    /// An invalid XPath expression must be guarded inside the helper: that capture
    /// MISSES and the step resolves to <see cref="Verdict.Inconclusive"/> — the
    /// exception must never escape the helper.
    /// </summary>
    [Fact]
    public async Task Execute_InvalidXPath_MissesAndReturnsInconclusive()
    {
        const string xmlBody = "<order><id>1</id></order>";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(xmlBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null, null);
            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                // Syntactically invalid XPath — unbalanced bracket.
                ["bad"] = new CaptureExpr(CaptureFormat.XPath, "/order[@@@"),
            };
            var ctx = new StubCtx("xpath-bad", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-bad", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("xpath-bad");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            Assert.Equal("0", vars[VarKeys.CaptureStatus(safeId)]);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 5. Execution: mixed json + xpath over JSON body — json still works ────

    /// <summary>
    /// A mixed capture set (JSONPath + XPath) over a JSON body: the JSONPath capture
    /// must still resolve correctly (byte-for-byte unchanged behaviour) while the
    /// XPath capture misses on the non-XML body.  The overall step is Inconclusive
    /// because the XPath capture is unmet, but the JSON value is still written.
    /// </summary>
    [Fact]
    public async Task Execute_MixedCaptures_JsonBody_JsonPathStillWorks()
    {
        const string jsonBody = "{\"id\":\"order-json-9\"}";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(jsonBody, "application/json", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null,
                new HttpExpect(Status: 200));

            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["jsonId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
                ["xmlId"] = new CaptureExpr(CaptureFormat.XPath, "/order/id"),
            };
            var ctx = new StubCtx("mixed-run", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("mixed-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("mixed-run");

            // JSONPath capture must still resolve.
            Assert.Equal("order-json-9", vars["jsonId"]);

            // XPath over a JSON body misses → step Inconclusive, flags "1,0".
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            Assert.Equal("1,0", vars[VarKeys.CaptureStatus(safeId)]);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 6. Security: inline-DTD entity-expansion DoS (billion laughs) → miss ──

    /// <summary>
    /// A response body carrying an inline DTD with nested entity expansion (a
    /// "billion laughs" payload) must NOT be expanded: <c>DtdProcessing.Prohibit</c>
    /// makes the hardened <c>XmlReader</c> throw on the <c>&lt;!DOCTYPE&gt;</c>, the
    /// XPath capture MISSES, and the step resolves to <see cref="Verdict.Inconclusive"/>
    /// — fast, with no OOM, hang, or crash.  Proves the entity-expansion DoS hardening
    /// degrades gracefully into the unchanged 'malformed body = miss' contract.
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_BillionLaughs_MissesFastNoOom()
    {
        // Classic nested-entity payload: each level references the previous ten times.
        // Without DtdProcessing.Prohibit this expands geometrically and OOMs the runner;
        // with it the reader throws on the DOCTYPE before any expansion occurs.
        const string lolBody =
            "<?xml version=\"1.0\"?>\n" +
            "<!DOCTYPE lolz [\n" +
            " <!ENTITY lol \"lol\">\n" +
            " <!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">\n" +
            " <!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\">\n" +
            " <!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">\n" +
            " <!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\">\n" +
            "]>\n" +
            "<lolz>&lol4;</lolz>";

        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(lolBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null, null);
            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["x"] = new CaptureExpr(CaptureFormat.XPath, "/lolz"),
            };
            var ctx = new StubCtx("xpath-billion-laughs", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-billion-laughs", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);

            // Bound the run: if DtdProcessing.Prohibit ever regressed, expansion would
            // OOM/hang long before this elapses.  A clean miss completes near-instantly.
            var runTask = RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);
            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(
                ReferenceEquals(completed, runTask),
                "Billion-laughs body must complete fast as a clean miss — it did not finish within 15s, " +
                "which indicates inline-DTD entity expansion was NOT prohibited.");
            await runTask;

            var safeId = CsxFragment.SanitiseId("xpath-billion-laughs");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            Assert.Equal("0", vars[VarKeys.CaptureStatus(safeId)]);
            Assert.False(vars.ContainsKey("x"),
                "A DTD-bearing body must not yield a captured value.");
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 7. Security: XXE-style external entity → miss, no file read ────────────

    /// <summary>
    /// A body declaring an external <c>SYSTEM</c> entity (<c>file:///etc/passwd</c>)
    /// must MISS: <c>DtdProcessing.Prohibit</c> rejects the DTD before any resolution,
    /// so no local file is read and the step is <see cref="Verdict.Inconclusive"/>,
    /// completing fast.  Locks the XXE hardening.
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_ExternalEntityXxe_MissesNoFileRead()
    {
        const string xxeBody =
            "<?xml version=\"1.0\"?>\n" +
            "<!DOCTYPE foo [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>\n" +
            "<foo>&x;</foo>";

        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(xxeBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null, null);
            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["leak"] = new CaptureExpr(CaptureFormat.XPath, "/foo"),
            };
            var ctx = new StubCtx("xpath-xxe", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-xxe", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("xpath-xxe");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            Assert.Equal("0", vars[VarKeys.CaptureStatus(safeId)]);
            Assert.False(vars.ContainsKey("leak"),
                "An external-entity body must not resolve the entity — no value may be captured.");
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 8. Semantics: node-set with multiple matches → first node ─────────────

    /// <summary>
    /// An XPath that selects a node-set (multiple matching nodes) must capture the
    /// FIRST node's value (<c>SelectSingleNode</c> document-order semantics).  Locks
    /// the first-node contract so a node-set expression is deterministic.
    /// </summary>
    [Fact]
    public async Task Execute_XPathCapture_NodeSetMultipleMatches_CapturesFirstNode()
    {
        const string xmlBody = "<list><item>a</item><item>b</item></list>";
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(xmlBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null,
                new HttpExpect(Status: 200));
            var captureExprs = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["first"] = new CaptureExpr(CaptureFormat.XPath, "//item"),
            };
            var ctx = new StubCtx("xpath-nodeset", captureExprs);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("xpath-nodeset", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("xpath-nodeset");

            // //item matches two nodes; SelectSingleNode returns the first in document order.
            Assert.Equal("a", vars["first"]);

            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.Equal("1", vars[VarKeys.CaptureStatus(safeId)]);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Starts an in-process HTTP responder that returns 200 OK with the supplied
    /// body and content type for every incoming request.  Mirrors the JSON responder
    /// in <c>HttpRestCaptureTests</c> but takes an arbitrary content type so an XML
    /// (or non-XML) payload can be staged without a live server.
    /// </summary>
    private static (string BaseUrl, IDisposable Responder) StartResponder(
        string body,
        string contentType,
        int port)
    {
        var prefix = $"http://localhost:{port}/";
        HttpListener? hl = null;
        try
        {
            hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            var cts = new CancellationTokenSource();
            var listener = hl;
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    HttpListenerContext? hctx = null;
                    try
                    {
                        hctx = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }

                    if (hctx is not null)
                    {
                        try
                        {
                            hctx.Response.StatusCode = 200;
                            hctx.Response.ContentType = contentType;
                            hctx.Response.ContentLength64 = bodyBytes.Length;
                            hctx.Response.OutputStream.Write(bodyBytes);
                            hctx.Response.Close();
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                        {
                            // The responder's Dispose() cancels cts BEFORE calling listener.Stop()/
                            // Close(), but that teardown can still race a response this loop is
                            // actively writing (see the equivalent raw-Thread guard in
                            // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                            // fire-and-forget Task, so an unhandled exception here would not itself
                            // crash the host — contained anyway so teardown stays deterministic
                            // rather than relying on the TPL's swallow-unobserved-exception behaviour.
                        }
                    }
                }
            }, cts.Token);

            var disposed = false;
            var responder = new CallbackDisposable(() =>
            {
                if (!disposed)
                {
                    disposed = true;
                    cts.Cancel();
                    try { listener.Stop(); } catch { /* already stopped */ }
                    try { listener.Close(); } catch { /* already closed */ }
                    cts.Dispose();
                }
            });

            return (prefix.TrimEnd('/'), responder);
        }
        catch (Exception)
        {
            try { hl?.Stop(); } catch { /* best-effort */ }
            try { hl?.Close(); } catch { /* best-effort */ }
        }

        // Fallback: raw TcpListener that sends one HTTP/1.1 200 response.
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        var tcpCts = new CancellationTokenSource();
        var tcpBodyBytes = Encoding.UTF8.GetBytes(body);

        _ = Task.Run(async () =>
        {
            while (!tcpCts.Token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await tcp.AcceptTcpClientAsync(tcpCts.Token)
                        .ConfigureAwait(false);
                }
                catch { break; }

                if (client is null) break;

                _ = Task.Run(async () =>
                {
                    var c = client;
                    try
                    {
                        var stream = c.GetStream();
                        var buf = new byte[4096];
                        var total = 0;
                        while (total < buf.Length)
                        {
                            var n = await stream.ReadAsync(buf.AsMemory(total), CancellationToken.None)
                                .ConfigureAwait(false);
                            if (n == 0) break;
                            total += n;
                            if (Encoding.ASCII.GetString(buf, 0, total)
                                .Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                        }

                        var header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {tcpBodyBytes.Length}\r\n" +
                            "Connection: close\r\n" +
                            "\r\n");
                        await stream.WriteAsync(header.AsMemory(), CancellationToken.None)
                            .ConfigureAwait(false);
                        await stream.WriteAsync(tcpBodyBytes.AsMemory(), CancellationToken.None)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally { c.Dispose(); }
                });
            }
        }, tcpCts.Token);

        return ($"http://localhost:{port}", new CallbackDisposable(() =>
        {
            tcpCts.Cancel();
            try { tcp.Stop(); } catch { /* best-effort */ }
            tcpCts.Dispose();
        }));
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _dispose;
        public CallbackDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
