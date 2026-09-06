// S07-B-02a — HttpRestProvider request BODY tests (non-docker).
//
// Brings the http.rest request body into scope. Before this task the body was
// parsed-but-dropped (Bind hardcoded Body: null), so POST/PUT scenarios could not
// send a payload. These tests prove:
//
//   1. Bind: a YAML scalar body binds to its literal string; a YAML mapping body
//      binds to a serialised JSON string. {placeholder} / ${secret:...} tokens
//      survive verbatim into the template (NOT resolved at bind time).
//   2. Emit lint: a POST with a body containing {placeholder} + ${secret:env/X}
//      emits the bodyTemplate as a RAW literal (not pre-resolved) and passes it to
//      the helper; no 'using var'; helper byte-identical across instances.
//   3. Emit lint: a GET with no body emits the bare 'null' bodyTemplate literal.
//   4. Execution (compile-round-trip, no Docker): a POST whose body templates a
//      captured var + an ${secret:env/...} reference resolves both at runtime —
//      the placeholder from Vars and the secret from the SecretAccessor — and the
//      resolved body is what the responder actually receives. The body value is
//      NOT echoed via any var (reference-only secret handling, §17).
//   5. Execution: a missing secret in the body → EnvironmentError with a
//      reference-only observation (source/path coordinates, no secret value).
//   6. Execution: a GET with no body still works unchanged (no content sent).
//
// Issue #346 additions (Bind, non-docker): the YAML-to-JSON walk behind a structured
// body is BOUNDED in depth and in total nodes produced, so a mistake in a suite file
// gets a catchable exception naming the step. Every row below calls Bind DIRECTLY, and
// that is not incidental: the engine validates a document against the composed JSON
// Schema first, and the YamlDotNet serialiser inside that conversion refuses anything
// past 49 nesting levels of its own accord, so the depth bound is a backstop for
// exactly this shape of caller. See HttpRestProvider's own block comment for the
// measurements.
//
//   7. A body nested one level past the depth bound is refused, with the line and
//      column of the offending node; a body AT the bound still binds.
//   8. An anchored-and-repeatedly-aliased body (the billion-laughs shape) is refused
//      by the node budget, and a shared anchor used a legitimate two or three times
//      still expands at every site — the budget is deliberately NOT a visited-node
//      set, which would change what the language means.
//   9. An ordinary structured body still round-trips byte-for-byte: the bound must be
//      invisible to a real suite.
//  10. The node budget counts NODES, NOT BYTES, and the row proves it rather than
//      leaving the limitation to prose: a large scalar aliased many times stays well
//      inside the budget and still produces a body orders of magnitude larger than the
//      node count suggests.
//
// The in-process responder is an extension of the JSON responder in
// HttpRestCaptureTests: it ECHOES the inbound request body back as the response
// body AND records it for direct assertion, so the sent body can be observed
// without Docker.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S07-B-02a: non-docker tests for the <see cref="HttpRestProvider"/> request body.
/// </summary>
public sealed class HttpRestBodyTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────────

    /// <summary>Marker <see cref="IBindingContext"/> stub for Bind tests.</summary>
    private sealed class StubBindingContext : IBindingContext { }

    /// <summary>
    /// <see cref="ICompileContext"/> stub; optionally carries a JSONPath capture map.
    /// </summary>
    private sealed class StubCtx : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCtx(
            string stepId,
            IReadOnlyDictionary<string, string>? captures = null)
        {
            StepId = stepId;
            Captures = captures
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            CaptureExprs = Captures.ToDictionary(
                kv => kv.Key,
                kv => new CaptureExpr(CaptureFormat.JsonPath, kv.Value),
                StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    // Additional Roslyn metadata references for the emitted CSX body.
    private static readonly IReadOnlyList<string> s_refs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,
    };

    // ── 1. Bind: scalar body and mapping body ─────────────────────────────────

    /// <summary>
    /// A YAML scalar <c>body</c> (a raw / inline JSON string) binds to its literal
    /// string, and any <c>{placeholder}</c> / <c>${secret:...}</c> token survives
    /// verbatim (no bind-time resolution).
    /// </summary>
    [Fact]
    public void Bind_ScalarBody_BindsToLiteralStringTemplate()
    {
        const string yaml = """
            target: svc
            method: POST
            path: /orders
            body: '{"id":"{orderId}","token":"${secret:env/API}"}'
            """;
        var model = BindYaml(yaml);

        Assert.Equal(
            "{\"id\":\"{orderId}\",\"token\":\"${secret:env/API}\"}",
            model.Body);
    }

    /// <summary>
    /// A YAML mapping <c>body</c> binds to a serialised JSON string; nested
    /// placeholder / secret tokens survive verbatim as JSON string values.
    /// </summary>
    [Fact]
    public void Bind_MappingBody_BindsToJsonStringTemplate()
    {
        const string yaml = """
            target: svc
            method: POST
            path: /orders
            body:
              id: "{orderId}"
              token: "${secret:env/API}"
              quantity: 3
              active: true
            """;
        var model = BindYaml(yaml);

        Assert.NotNull(model.Body);

        // The structured body is serialised to JSON; the tokens are preserved as
        // string values and the typed scalars keep their JSON types.
        var node = System.Text.Json.Nodes.JsonNode.Parse(model.Body!);
        Assert.NotNull(node);
        Assert.Equal("{orderId}", (string?)node!["id"]);
        Assert.Equal("${secret:env/API}", (string?)node["token"]);
        Assert.Equal(3, (int)node["quantity"]!);
        Assert.True((bool)node["active"]!);
    }

    /// <summary>
    /// A step with no <c>body</c> key binds to a <see langword="null"/> body.
    /// </summary>
    [Fact]
    public void Bind_NoBody_BindsToNull()
    {
        const string yaml = """
            target: svc
            method: GET
            path: /orders
            """;
        var model = BindYaml(yaml);

        Assert.Null(model.Body);
    }

    // ── 2. Emit lint: POST body is a RAW literal passed to the helper ─────────

    /// <summary>
    /// A POST with a body containing a <c>{placeholder}</c> and a
    /// <c>${secret:env/X}</c> reference must emit the body as a RAW literal (the
    /// tokens survive verbatim, NOT pre-resolved) and pass it to the helper.
    /// </summary>
    [Fact]
    public void Emit_PostWithBody_EmitsRawTemplateLiteral_NotPreResolved()
    {
        const string bodyTemplate = "{\"id\":\"{orderId}\",\"token\":\"${secret:env/API}\"}";

        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "POST",
            Path: "/orders",
            Headers: null,
            Body: bodyTemplate,
            Expect: new HttpExpect(201));
        var fragment = provider.Emit(model, new StubCtx("post-step"));

        var block = fragment.StatementBlock;

        // The RAW template tokens must be present verbatim in the emitted block
        // (proof the body is NOT pre-resolved at emit time).
        Assert.Contains("{orderId}", block, StringComparison.Ordinal);
        Assert.Contains("${secret:env/API}", block, StringComparison.Ordinal);

        // The helper resolves the body via Secret_Helpers.ResolveTemplate at runtime.
        var helperSource = string.Join("\n", fragment.RequiredHelpers);
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, bodyTemplate)",
            helperSource,
            StringComparison.Ordinal);
        Assert.Contains("bodyTemplate", helperSource, StringComparison.Ordinal);
        Assert.Contains("application/json", helperSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// No <c>using var</c> anywhere in a POST-with-body fragment (§13.3.1).
    /// </summary>
    [Fact]
    public void Emit_PostWithBody_NoUsingVar()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            "svc", "POST", "/orders", null, "{\"a\":1}", new HttpExpect(201));
        var fragment = provider.Emit(model, new StubCtx("post-step"));

        var full = fragment.StatementBlock +
                   "\n" +
                   string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>HttpRest_Helpers</c> source is byte-identical whether or not a step
    /// declares a body — the body is plumbed as a parameter, never as per-step
    /// helper interpolation (§13.3.1 dedup rule).
    /// </summary>
    [Fact]
    public void Emit_BodyAndNoBody_HelpersAreByteIdentical()
    {
        var provider = new HttpRestProvider();
        var withBody = provider.Emit(
            new HttpRestModel("svc", "POST", "/a", null, "{\"x\":1}", null),
            new StubCtx("step-a"));
        var noBody = provider.Emit(
            new HttpRestModel("svc", "GET", "/b", null, null, null),
            new StubCtx("step-b"));

        var helperWith = withBody.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));
        var helperNo = noBody.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));

        Assert.Equal(helperNo, helperWith, StringComparer.Ordinal);
    }

    // ── 3. Emit lint: GET without body emits bare 'null' ──────────────────────

    /// <summary>
    /// A GET with no body emits the bare <c>null</c> bodyTemplate literal — the
    /// helper receives <c>null</c> and sends no content.
    /// </summary>
    [Fact]
    public void Emit_GetWithoutBody_EmitsBareNullBodyLiteral()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/orders", null, null, null);
        var fragment = provider.Emit(model, new StubCtx("get-step"));

        // The ExecuteAsync call must pass a bare 'null' for the body argument; the
        // literal must NOT be the quoted string "null".
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\"null\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 4. Execution: POST body templates a captured var + a secret ───────────

    /// <summary>
    /// A POST whose body templates a captured var <c>{orderId}</c> and an
    /// <c>${secret:env/...}</c> reference must resolve BOTH at runtime — the
    /// placeholder from <c>Vars</c> and the secret from the
    /// <see cref="SecretAccessor"/> — and the resolved body must be exactly what the
    /// responder receives.  The secret value is fed only to the content sink and is
    /// never written back to any var (§17).
    /// </summary>
    [Fact]
    public async Task Execute_PostBody_ResolvesPlaceholderAndSecret_AtRuntime()
    {
        var envName = "VOUCHFX_BODY_SECRET_" + Guid.NewGuid().ToString("N");
        const string secretValue = "s3cr3t-body-token";

        Environment.SetEnvironmentVariable(envName, secretValue);
        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var bodyTemplate =
                $"{{\"id\":\"{{orderId}}\",\"token\":\"${{secret:env/{envName}}}\"}}";

            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "POST",
                Path: "/orders",
                Headers: null,
                Body: bodyTemplate,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("post-run"));
            var assembled = CsxAssembler.Assemble(new[] { ("post-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            // The captured var threaded forward from a prior step.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
                ["orderId"] = "order-42",
            };

            // Real env-backed secret accessor.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // Outcome is Pass (200 echo).
            var safeId = CsxFragment.SanitiseId("post-run");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // The responder received the FULLY RESOLVED body: placeholder filled from
            // Vars, secret revealed from the accessor.
            var received = responder.LastBody;
            Assert.Equal(
                "{\"id\":\"order-42\",\"token\":\"" + secretValue + "\"}",
                received);

            // The secret value must NOT have leaked into any var or the observation.
            foreach (var (k, v) in vars)
            {
                if (v is string sv)
                {
                    Assert.DoesNotContain(secretValue, sv, StringComparison.Ordinal);
                }
            }
            Assert.NotNull(outcome.Observation);
            Assert.DoesNotContain(secretValue, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            responder.Dispose();
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 5. Execution: missing secret in body → EnvironmentError ───────────────

    /// <summary>
    /// A missing secret referenced in the body must be caught inside the helper's
    /// guarded region and written as a per-step
    /// <see cref="Verdict.EnvironmentError"/> with a reference-only observation: the
    /// source/path coordinates appear, but no secret value (there is none) and the
    /// request must never be sent.
    /// </summary>
    [Fact]
    public async Task Execute_PostBody_MissingSecret_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_BODY_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);  // guaranteed absent

        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var bodyTemplate = $"{{\"token\":\"${{secret:env/{envName}}}\"}}";

            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "POST",
                Path: "/orders",
                Headers: null,
                Body: bodyTemplate,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("missing-secret-body"));
            var assembled = CsxAssembler.Assemble(
                new[] { ("missing-secret-body", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — the exception is contained inside the step.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("missing-secret-body");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);

            // Reference-only observation: the source/path coordinates appear; the
            // exception message is deliberately excluded (§17).
            Assert.NotNull(outcome.Observation);
            Assert.Contains("secret resolution failed", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("\"source\":\"env\"", outcome.Observation!, StringComparison.Ordinal);

            // The request was never sent — the responder recorded no body.
            Assert.Null(responder.LastBody);
        }
        finally
        {
            responder.Dispose();
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 6. Execution: GET without body unchanged ──────────────────────────────

    /// <summary>
    /// A GET with no body still works unchanged: the request is sent with no
    /// content and the outcome is <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    public async Task Execute_GetWithoutBody_SendsNoContent_Unchanged()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/health",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("get-run"));
            var assembled = CsxAssembler.Assemble(new[] { ("get-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("get-run");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // No content was sent (empty body recorded by the responder).
            Assert.Equal(string.Empty, responder.LastBody);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 7-9. Issue #346: the structured-body walk is bounded ──────────────────

    /// <summary>
    /// A structured <c>body:</c> nested exactly to the depth bound still binds. This is
    /// the row that keeps the bound honest: the limit is a ceiling, not a discount.
    /// </summary>
    [Fact]
    public void Bind_StructuredBodyAtDepthLimit_StillBinds()
    {
        var model = BindYaml(DeepBodyYaml("deep-but-legal", levels: 64));

        Assert.NotNull(model.Body);

        // 63 nested objects and one leaf string: the whole chain survived.
        Assert.Equal(63, CountOccurrences(model.Body!, "\"k\":"));
        Assert.Contains("\"k\":\"leaf\"", model.Body!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A structured <c>body:</c> nested one level past the depth bound is refused with a
    /// catchable exception that names the step and the limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document is 65 levels deep, one past the bound.
    /// </para>
    /// <para>
    /// The line/column assertion is here because the message ADVERTISES a position, and an
    /// advertised position that nobody checks is a claim rather than a feature. The offending
    /// node is the 65th <c>k:</c>'s value — a plain scalar the parser read at one place, so the
    /// mark is meaningful for this shape. It deliberately is not advertised for the node-budget
    /// message, where an alias site carries the anchor's mark instead; see
    /// <c>HttpRestProvider.DescribeMark</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Bind_StructuredBodyBeyondDepthLimit_ThrowsNamingStepAndLimit()
    {
        var yaml = DeepBodyYaml("deep-body", levels: 65);

        var ex = Assert.Throws<InvalidOperationException>(() => BindYaml(yaml));

        Assert.Contains("step 'deep-body'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nests more than 64 levels deep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("authoring fault", ex.Message, StringComparison.Ordinal);

        // DeepBodyYaml writes 4 header lines, then `body:`, then one `k:` per level indented
        // two spaces further each time, then the leaf. The 65th body node is therefore the
        // leaf scalar on line 70, indented 65 * 2 spaces, i.e. 1-based column 131.
        Assert.Contains("reached at line 70, column 131", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An anchored-and-repeatedly-aliased <c>body:</c> — the billion-laughs shape — is
    /// refused by the node budget, with a message that names the step and the limit.
    /// </summary>
    /// <remarks>
    /// The document below is nine lines and shallow, so nothing about it trips the depth
    /// bound; its expansion is over 120,000 nodes because YamlDotNet shares one node instance
    /// per anchor while the walk re-materialises a copy at every alias site. The budget stops
    /// the walk at 50,000 nodes, which is thousandths of a second of work. Note what the row
    /// does and does not prove: the same document has ALREADY been expanded once, unbounded,
    /// by schema validation on the engine path (measured at ~2.5 MB of JSON for this shape),
    /// so what the budget buys is a named refusal instead of this provider materialising a far
    /// heavier <c>JsonNode</c> tree — not the prevention of an out-of-memory condition. That
    /// larger gap is issue #505.
    /// </remarks>
    [Fact]
    public void Bind_AliasAmplifiedBody_ThrowsNamingStepAndLimit()
    {
        const string yaml = """
            id: laughing-body
            target: svc
            method: POST
            path: /orders
            body:
              a: &a ["x","x","x","x","x","x","x","x","x","x"]
              b: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]
              c: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]
              d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]
              e: [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => BindYaml(yaml));

        Assert.Contains("step 'laughing-body'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("expands to more than 50000 JSON nodes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("authoring fault", ex.Message, StringComparison.Ordinal);

        // No line/column: the node a budget breach lands on is a SHARED node carrying the
        // anchor's mark, not the alias site's, so the message deliberately advertises none.
        Assert.DoesNotContain("reached at line", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The node budget counts NODES, NOT BYTES: a large scalar aliased many times stays far
    /// inside the budget and still binds to a body orders of magnitude larger than the node
    /// count suggests.
    /// </summary>
    /// <remarks>
    /// This row exists because the limitation is easy to state and easy to lose. The
    /// billion-laughs row above uses a one-character payload, so it structurally cannot
    /// observe size at all; scale the payload instead of the branching factor and the budget
    /// sees nothing. <c>ScalarToJsonNode</c> wraps the scalar's existing string instance, so
    /// each alias site costs exactly one node however long that string is. The figures below
    /// are kept small enough to be a unit test (~2 MB) while making the ratio unmistakable;
    /// nothing stops an author scaling them further. Asserted as a PROPERTY — bytes per node —
    /// rather than an exact length, so it cannot break on a formatting change.
    /// </remarks>
    [Fact]
    public void Bind_LargeScalarAliasedManyTimes_StaysInsideTheNodeBudget()
    {
        const int aliasSites = 500;
        const int payloadLength = 4096;

        var sb = new StringBuilder();
        sb.Append("id: fat-scalar\n");
        sb.Append("target: svc\n");
        sb.Append("method: POST\n");
        sb.Append("path: /orders\n");
        sb.Append("body:\n");
        sb.Append("  seed: &s \"").Append('x', payloadLength).Append("\"\n");
        for (var i = 0; i < aliasSites; i++)
            sb.Append("  k").Append(i).Append(": *s\n");

        var model = BindYaml(sb.ToString());

        Assert.NotNull(model.Body);

        // The whole body is 1 mapping + 1 anchored scalar + 500 alias sites = 502 nodes,
        // about one per cent of the 50,000-node budget...
        const int nodesProduced = 1 + 1 + aliasSites;
        Assert.True(nodesProduced < 50_000 / 50, $"expected a small node count, got {nodesProduced}");

        // ...and yet the bound body is over two megabytes, i.e. thousands of bytes per node.
        Assert.True(
            model.Body!.Length > aliasSites * payloadLength,
            $"expected a body larger than {aliasSites * payloadLength} chars, got {model.Body.Length}");
        Assert.True(
            model.Body.Length / nodesProduced > 1000,
            $"expected far more than 1000 chars per node, got {model.Body.Length / nodesProduced}");
    }

    /// <summary>
    /// A shared anchor referenced from two sites still expands at BOTH — the defence is a
    /// node budget, not a visited-node set, and an alias is a shared node rather than a
    /// cycle.
    /// </summary>
    [Fact]
    public void Bind_SharedAnchorUsedTwice_ExpandsAtEverySite()
    {
        const string yaml = """
            id: shared-anchor
            target: svc
            method: POST
            path: /orders
            body:
              defaults: &d
                region: eu
                tier: gold
              primary: *d
              secondary: *d
            """;

        var model = BindYaml(yaml);

        Assert.NotNull(model.Body);
        var node = System.Text.Json.Nodes.JsonNode.Parse(model.Body!);
        Assert.NotNull(node);
        Assert.Equal("eu", (string?)node!["defaults"]!["region"]);
        Assert.Equal("eu", (string?)node["primary"]!["region"]);
        Assert.Equal("gold", (string?)node["secondary"]!["tier"]);
    }

    /// <summary>
    /// An ordinary structured <c>body:</c> — nested objects, an array of objects, mixed
    /// scalar types and a surviving <c>{placeholder}</c> — round-trips exactly as before
    /// the bound was added. The bound must be invisible to a real suite.
    /// </summary>
    [Fact]
    public void Bind_OrdinaryStructuredBody_RoundTripsUnchanged()
    {
        const string yaml = """
            id: place-order
            target: svc
            method: POST
            path: /orders
            body:
              customer:
                id: "{customerId}"
                loyalty:
                  tier: gold
                  points: 4210
              lines:
                - sku: ABC-1
                  quantity: 2
                  unitPrice: 19.99
                - sku: DEF-2
                  quantity: 1
                  unitPrice: 4.5
              express: true
              note: null
            """;

        var model = BindYaml(yaml);

        Assert.NotNull(model.Body);
        Assert.Equal(
            "{\"customer\":{\"id\":\"{customerId}\",\"loyalty\":{\"tier\":\"gold\",\"points\":4210}},"
            + "\"lines\":[{\"sku\":\"ABC-1\",\"quantity\":2,\"unitPrice\":19.99},"
            + "{\"sku\":\"DEF-2\",\"quantity\":1,\"unitPrice\":4.5}],"
            + "\"express\":true,\"note\":null}",
            model.Body);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a single-step YAML document whose <c>body:</c> is a chain of
    /// <paramref name="levels"/> nodes: <c>levels - 1</c> nested mappings and a leaf
    /// scalar. The body node itself is depth 1, so <c>levels = 64</c> sits exactly on the
    /// provider's bound and <c>levels = 65</c> is one past it.
    /// </summary>
    private static string DeepBodyYaml(string stepId, int levels)
    {
        var sb = new StringBuilder();
        sb.Append("id: ").Append(stepId).Append('\n');
        sb.Append("target: svc\n");
        sb.Append("method: POST\n");
        sb.Append("path: /orders\n");
        sb.Append("body:\n");
        for (var i = 0; i < levels - 1; i++)
            sb.Append(new string(' ', (i + 1) * 2)).Append("k:\n");
        sb.Append(new string(' ', levels * 2)).Append("leaf\n");
        return sb.ToString();
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Parses a single-step YAML mapping and binds it through the provider.
    /// </summary>
    private static HttpRestModel BindYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return new HttpRestProvider().Bind(root, new StubBindingContext());
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// In-process HTTP responder that records the inbound request body and echoes it
    /// back as a 200 response.  Lets a test assert the exact body the helper sent
    /// without Docker.
    /// </summary>
    private sealed class EchoResponder : IDisposable
    {
        private readonly Action _dispose;

        /// <summary>The most recently received request body (null if none yet).</summary>
        public volatile string? LastBody;

        public EchoResponder(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    private static (string BaseUrl, EchoResponder Responder) StartEchoResponder(int port)
    {
        var prefix = $"http://localhost:{port}/";
        var hl = new HttpListener();
        hl.Prefixes.Add(prefix);
        hl.Start();

        var cts = new CancellationTokenSource();
        var responder = new EchoResponder(() =>
        {
            cts.Cancel();
            try { hl.Stop(); } catch { /* already stopped */ }
            try { hl.Close(); } catch { /* already closed */ }
            cts.Dispose();
        });

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext? hctx = null;
                try
                {
                    hctx = await hl.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                if (hctx is null) continue;

                string body;
                using (var reader = new StreamReader(
                    hctx.Request.InputStream, hctx.Request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                responder.LastBody = body;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    hctx.Response.StatusCode = 200;
                    hctx.Response.ContentType = "application/json";
                    hctx.Response.ContentLength64 = bytes.Length;
                    hctx.Response.OutputStream.Write(bytes);
                    hctx.Response.Close();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                {
                    // The responder's Dispose() cancels cts BEFORE calling hl.Stop()/Close(), but
                    // that teardown can still race a response this loop is actively writing (see
                    // the equivalent raw-Thread guard in
                    // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                    // fire-and-forget Task, so an unhandled exception here would not itself crash
                    // the host — contained anyway so teardown stays deterministic rather than
                    // relying on the TPL's swallow-unobserved-exception behaviour.
                }
            }
        }, cts.Token);

        return (prefix.TrimEnd('/'), responder);
    }
}
