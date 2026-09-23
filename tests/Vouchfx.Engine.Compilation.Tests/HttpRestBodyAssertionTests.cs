// #558 — http.rest response-body assertions: expect.json and expect.bodyContains (non-docker).
//
// Four layers, one file, each row named for the one fact it pins:
//   1. Bind      — the YAML shapes an author writes, bound in declaration order, never throwing.
//   2. Validate  — every refusal is a ValidationResult, never a throw (#480's provenance rule).
//   3. Emit      — the parallel arrays and the fixed mode vocabulary, templates emitted RAW, the
//                  second helper class byte-identical across steps, and the tripwire that keeps
//                  its placeholder pattern equal to Secret_Helpers' own.
//   4. Execute   — compile + run against an in-process raw-socket responder that COUNTS requests,
//                  so "nothing was sent" is a measured fact rather than an inference. One row per
//                  verdict and observation shape the design names, RETRY included.
//
// Secret-bearing rows (provenance, pre-compile refusal, envelope, the observation carrying the
// reference and never the value) live in Vouchfx.Engine.Runtime.Tests'
// HttpRestBodyAssertionSecretTests, beside the runner internals they exercise.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// #558: <c>http.rest</c> response-body assertions — bind, validate, emit and execute.
/// </summary>
public sealed class HttpRestBodyAssertionTests
{
    private const string StepId = "body-step";

    private static readonly string SafeId = CsxFragment.SanitiseId(StepId);

    /// <summary>
    /// The references the emitted helpers need beyond the compiler's TPA subset — the same set
    /// <c>HttpRestExecutionTests</c> supplies. The body assertions add none of their own:
    /// System.Text.Json and JsonPath.Net were already in the closure for <c>capture</c>.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
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

    private static readonly StubProjectContext s_svcDeclared = new(
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
        {
            ["svc"] = new DeclaredServiceInfo(new List<string> { "http" }),
        });

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 1. Bind
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every <c>expect.json</c> entry binds in declaration order. A scalar keeps its LITERAL
    /// text — a bare <c>2</c> is the text <c>2</c>, a bare <c>True</c> the text <c>True</c> —
    /// a quoted <c>"null"</c> is the text <c>null</c>, and the two <c>exists</c> forms bind
    /// to <see cref="HttpJsonAssertion.Exists"/>.
    /// </summary>
    [Fact]
    public void Bind_Json_BindsEveryEntryInDeclarationOrder()
    {
        var model = BindYaml("""
            target: svc
            method: GET
            path: /orders/1
            expect:
              status: 200
              json:
                "$.status": PENDING
                "$.quantity": 2
                "$.flag": True
                "$.cancelledAt": "null"
                "$.note": ""
                "$.id": { exists: true }
                "$..password": { exists: false }
              bodyContains: "Hostname: {hostname}"
            """);

        var expect = Assert.IsType<HttpExpect>(model.Expect);
        var json = Assert.IsAssignableFrom<IReadOnlyList<HttpJsonAssertion>>(expect.Json);
        Assert.Equal(
            new[]
            {
                new HttpJsonAssertion("$.status", "PENDING", null),
                new HttpJsonAssertion("$.quantity", "2", null),
                new HttpJsonAssertion("$.flag", "True", null),
                new HttpJsonAssertion("$.cancelledAt", "null", null),
                new HttpJsonAssertion("$.note", string.Empty, null),
                new HttpJsonAssertion("$.id", null, true),
                new HttpJsonAssertion("$..password", null, false),
            },
            json);
        Assert.Equal("Hostname: {hostname}", expect.BodyContains);
        Assert.Equal(200, expect.Status);
    }

    /// <summary>
    /// A value that is neither a scalar nor exactly <c>{ exists: true|false }</c> — a YAML
    /// null, a sequence, a mapping with another key or a non-boolean <c>exists</c>, including a
    /// QUOTED <c>"true"</c>, which the schema types as a string — binds with NEITHER member set,
    /// which <see cref="HttpRestProvider.Validate"/> refuses. Bind never throws on it.
    /// </summary>
    [Theory]
    [InlineData("\"$.a\": ~")]
    [InlineData("\"$.a\": null")]
    [InlineData("\"$.a\": NULL")]
    [InlineData("\"$.a\":")]
    [InlineData("\"$.a\": [1, 2]")]
    [InlineData("\"$.a\": { exists: true, extra: 1 }")]
    [InlineData("\"$.a\": { exists: maybe }")]
    [InlineData("\"$.a\": { exists: \"true\" }")]
    [InlineData("\"$.a\": { exists: 'false' }")]
    [InlineData("\"$.a\": { value: 1 }")]
    public void Bind_Json_MalformedValue_BindsWithNeitherMemberSet(string entry)
    {
        var model = BindYaml(
            "target: svc\nmethod: GET\npath: /x\nexpect:\n  json:\n    " + entry + "\n");

        var only = Assert.Single(model.Expect!.Json!);
        Assert.Equal(new HttpJsonAssertion("$.a", null, null), only);
    }

    /// <summary>A <c>json</c> that is not a mapping binds to no entries, which Validate refuses.</summary>
    [Fact]
    public void Bind_Json_NotAMapping_BindsToNoEntries()
    {
        var model = BindYaml("""
            target: svc
            method: GET
            path: /x
            expect:
              json: [ "$.a" ]
            """);

        Assert.Empty(model.Expect!.Json!);
    }

    /// <summary>
    /// <c>bodyContains</c> binds as its literal text, whether written as a string or as a bare
    /// number; a YAML null binds to the empty string, which Validate refuses.
    /// </summary>
    [Theory]
    [InlineData("bodyContains: 42", "42")]
    [InlineData("bodyContains: \"Hostname: {hostname}\"", "Hostname: {hostname}")]
    [InlineData("bodyContains: \"null\"", "null")]
    [InlineData("bodyContains:", "")]
    [InlineData("bodyContains: ~", "")]
    public void Bind_BodyContains_BindsItsLiteralText(string line, string expected)
    {
        var model = BindYaml("target: svc\nmethod: GET\npath: /x\nexpect:\n  " + line + "\n");

        Assert.Equal(expected, model.Expect!.BodyContains);
    }

    /// <summary>
    /// An <c>expect</c> block with neither new member binds both to <see langword="null"/> —
    /// the shape every suite written before #558 has.
    /// </summary>
    [Fact]
    public void Bind_StatusOnly_LeavesBodyAssertionsNull()
    {
        var model = BindYaml("""
            target: svc
            method: GET
            path: /x
            expect:
              status: 200
            """);

        Assert.Equal(new HttpExpect(200), model.Expect);
        Assert.Null(model.Expect!.Json);
        Assert.Null(model.Expect.BodyContains);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 2. Validate
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Well-formed body assertions validate.</summary>
    [Fact]
    public void Validate_WellFormedBodyAssertions_Pass()
    {
        var model = Model(
            "POST",
            new HttpExpect(
                201,
                new[]
                {
                    new HttpJsonAssertion("$.id", null, true),
                    new HttpJsonAssertion("$.lines[?@.sku == 'SKU-1'].quantity", "2", null),
                    new HttpJsonAssertion("$..internalNotes", null, false),
                },
                "created"));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    /// <summary>
    /// A key that is not an RFC 9535 JSONPath is refused BY NAME at validate time — measured
    /// refusals of JsonPath.Net 3.0.2 — so <c>vouchfx validate</c> names it before any container
    /// starts. <c>{orderId}</c> is the slip of expecting a key to be substituted: keys never are.
    /// </summary>
    [Theory]
    [InlineData("$.a[")]
    [InlineData("a.b")]
    [InlineData("$.a.length()")]
    [InlineData("{orderId}")]
    [InlineData("")]
    public void Validate_InvalidJsonPath_IsRefusedByName(string path)
    {
        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion(path, "x", null) }));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("'expect.json' key '" + path + "' is not a valid JSONPath", StringComparison.Ordinal));
    }

    /// <summary>
    /// An entry with neither an expected value nor an <c>exists</c> flag (a YAML null bound
    /// without validation), or with both, is refused — and the message says how to write a
    /// JSON null.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("x", true)]
    public void Validate_MalformedEntry_IsRefused(string? expected, bool? exists)
    {
        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.a", expected, exists) }));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'expect.json' entry '$.a' must be either", error, StringComparison.Ordinal);
        Assert.Contains("the quoted text \"null\"", error, StringComparison.Ordinal);
    }

    /// <summary>An empty <c>json</c> map is refused.</summary>
    [Fact]
    public void Validate_EmptyJsonMap_IsRefused()
    {
        var model = Model("GET", new HttpExpect(200, Array.Empty<HttpJsonAssertion>()));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'expect.json' must be a non-empty map", StringComparison.Ordinal));
    }

    /// <summary>An empty <c>bodyContains</c> could never fail, so it is refused.</summary>
    [Fact]
    public void Validate_EmptyBodyContains_IsRefused()
    {
        var model = Model("GET", new HttpExpect(200, null, string.Empty));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'expect.bodyContains' must not be empty", StringComparison.Ordinal));
    }

    /// <summary>
    /// A body assertion on HEAD is refused: a HEAD response carries no content (RFC 9110), so
    /// the assertion could never hold. Case-insensitive, like the method check itself.
    /// </summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("head")]
    public void Validate_BodyAssertionOnHead_IsRefused(string method)
    {
        var model = Model(method, new HttpExpect(200, null, "anything"));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cannot be used with method HEAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// A body assertion alongside <c>status: 204</c> or <c>304</c> is refused: neither response
    /// carries content (RFC 9110).
    /// </summary>
    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    public void Validate_BodyAssertionWithAContentlessStatus_IsRefused(int status)
    {
        var model = Model("GET", new HttpExpect(status, new[] { new HttpJsonAssertion("$.id", null, true) }));

        var result = new HttpRestProvider().Validate(model, s_svcDeclared);

        Assert.False(result.IsValid);
        var code = status.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(result.Errors, e => e.Contains("'expect.status: " + code + "'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The contentless-response rules key on the body assertions alone: HEAD and 204 without
    /// them validate exactly as they did before #558.
    /// </summary>
    [Fact]
    public void Validate_HeadOr204WithoutBodyAssertions_StillPasses()
    {
        Assert.True(new HttpRestProvider().Validate(Model("HEAD", new HttpExpect(200)), s_svcDeclared).IsValid);
        Assert.True(new HttpRestProvider().Validate(Model("DELETE", new HttpExpect(204)), s_svcDeclared).IsValid);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 3. Emit
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>expect.json</c> is emitted as three parallel arrays in DECLARATION order — paths,
    /// expected templates ("" for the exists forms) and a mode token from the fixed vocabulary
    /// equals / exists / absent — followed by the <c>bodyContains</c> template.
    /// </summary>
    [Fact]
    public void Emit_BodyAssertions_EmitsParallelArraysInDeclarationOrder()
    {
        var model = Model(
            "GET",
            new HttpExpect(
                200,
                new[]
                {
                    new HttpJsonAssertion("$.b", "x", null),
                    new HttpJsonAssertion("$.a", null, true),
                    new HttpJsonAssertion("$.c", null, false),
                },
                "hi"));

        var block = Squash(new HttpRestProvider().Emit(model, new StubCompileContext(StepId)).StatementBlock);

        Assert.Contains(
            "200," +
            "newstring[]{\"$.b\",\"$.a\",\"$.c\"}," +
            "newstring[]{\"x\",\"\",\"\"}," +
            "newstring[]{\"equals\",\"exists\",\"absent\"}," +
            "\"hi\",",
            block,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A step with no body assertions emits three empty arrays and the bare literal
    /// <c>null</c> in their place — the helper then takes exactly today's path.
    /// </summary>
    [Fact]
    public void Emit_NoBodyAssertions_EmitsEmptyArraysAndNull()
    {
        var model = Model("GET", new HttpExpect(200));

        var block = Squash(new HttpRestProvider().Emit(model, new StubCompileContext(StepId)).StatementBlock);

        Assert.Contains(
            "200,newstring[]{},newstring[]{},newstring[]{},null,newstring[]{},newstring[]{},newstring[]{},__stepCt_",
            block,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Expected values are emitted as RAW template text: a secret reference and a placeholder
    /// reach the IL as their reference text, never as a resolved value (§17).
    /// </summary>
    [Fact]
    public void Emit_ExpectedValues_AreEmittedAsRawTemplates()
    {
        var model = Model(
            "GET",
            new HttpExpect(
                200,
                new[] { new HttpJsonAssertion("$.token", "${secret:env/API_TOKEN}", null) },
                "order {orderId}"));

        var block = new HttpRestProvider().Emit(model, new StubCompileContext(StepId)).StatementBlock;

        Assert.Contains("\"${secret:env/API_TOKEN}\"", block, StringComparison.Ordinal);
        Assert.Contains("\"order {orderId}\"", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body-assertion helper is a second, provider-prefixed class: present once per
    /// fragment, BYTE-IDENTICAL across two differently-configured steps (the §13.3.1 dedupe
    /// premise), carried once by the assembled script, free of <c>using var</c>, and the whole
    /// two-step script compiles.
    /// </summary>
    [Fact]
    public void Emit_BodyAssertionsHelper_IsByteIdenticalAcrossSteps_AndAssemblesOnce()
    {
        var provider = new HttpRestProvider();
        var f1 = provider.Emit(
            Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.a", "1", null) })),
            new StubCompileContext("step-a"));
        var f2 = provider.Emit(
            Model("GET", new HttpExpect(null, null, "text")),
            new StubCompileContext("step-b"));

        var h1 = Assert.Single(f1.RequiredHelpers, IsBodyAssertionsHelper);
        var h2 = Assert.Single(f2.RequiredHelpers, IsBodyAssertionsHelper);
        Assert.Equal(h1, h2, StringComparer.Ordinal);
        Assert.StartsWith("static class HttpRest_BodyAssertions", h1, StringComparison.Ordinal);

        var assembled = CsxAssembler.Assemble(new[] { ("step-a", f1), ("step-b", f2) });
        Assert.Equal(1, CountOccurrences(assembled.CsxSource, "static class HttpRest_BodyAssertions"));
        Assert.DoesNotContain("using var", assembled.CsxSource, StringComparison.Ordinal);

        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: s_additionalRefs);
        Assert.NotEmpty(compiled.Image);
    }

    /// <summary>
    /// TRIPWIRE: the helper's unset-placeholder check must see exactly the <c>{placeholder}</c>
    /// tokens <c>Secret_Helpers.ResolveTemplate</c> substitutes, so its pattern is a byte-for-byte
    /// copy of <c>Secret_Helpers</c>' combined pattern. The pattern is read out of
    /// <see cref="SecretHelper.Source"/> at run time; if either copy changes, this fails.
    /// </summary>
    [Fact]
    public void BodyAssertionsHelper_PlaceholderPattern_EqualsSecretHelpersCombinedPattern()
    {
        const string patternStart = "@\"(?<secret>";
        var source = SecretHelper.Source;
        var start = source.IndexOf(patternStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "SecretHelper.Source no longer carries the combined pattern this test extracts.");
        var end = source.IndexOf('"', start + 2);
        Assert.True(end > start, "The combined pattern literal in SecretHelper.Source is not terminated.");
        var combinedLiteral = source.Substring(start, end - start + 1);

        var helper = Assert.Single(
            new HttpRestProvider()
                .Emit(Model("GET", new HttpExpect(200)), new StubCompileContext(StepId))
                .RequiredHelpers,
            IsBodyAssertionsHelper);

        Assert.Contains(combinedLiteral, helper, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — pass and status
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Equality compares the canonical text <c>capture</c> writes: a string by its unescaped
    /// value, a number or boolean by its JSON spelling, an object or array as compact JSON —
    /// and a PRESENT JSON null as the text <c>null</c>. A present null also satisfies
    /// <c>exists: true</c>. The pass observation is byte-identical to today's.
    /// </summary>
    [Fact]
    public async Task Execute_EveryEqualityAndExistsForm_Holds_ObservationUnchanged()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json(
            "{\"id\":\"A-1\",\"quantity\":2,\"price\":1.50,\"flag\":true,\"cancelledAt\":null," +
            "\"meta\":{\"a\":1,\"b\":[true,null]},\"tags\":[\"x\",\"y\"],\"note\":\"\"}"));

        var model = Model(
            "GET",
            new HttpExpect(
                200,
                new[]
                {
                    new HttpJsonAssertion("$.id", "A-1", null),
                    new HttpJsonAssertion("$.quantity", "2", null),
                    new HttpJsonAssertion("$.price", "1.50", null),
                    new HttpJsonAssertion("$.flag", "true", null),
                    new HttpJsonAssertion("$.cancelledAt", "null", null),
                    new HttpJsonAssertion("$.meta", "{\"a\":1,\"b\":[true,null]}", null),
                    new HttpJsonAssertion("$.tags", "[\"x\",\"y\"]", null),
                    new HttpJsonAssertion("$.note", string.Empty, null),
                    new HttpJsonAssertion("$.cancelledAt", null, true),
                    new HttpJsonAssertion("$..password", null, false),
                },
                "\"quantity\":2"));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal("{\"status\":200,\"expected\":200}", outcome.Observation);
        Assert.Equal(1, responder.RequestCount);
    }

    /// <summary>
    /// Comparison is by text, so it is TYPE-blind: <c>2</c> matches the number 2 and the
    /// string "2" alike.
    /// </summary>
    [Fact]
    public async Task Execute_Equality_IsTypeBlind()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"n\":2,\"s\":\"2\"}"));

        var model = Model("GET", new HttpExpect(200, new[]
        {
            new HttpJsonAssertion("$.n", "2", null),
            new HttpJsonAssertion("$.s", "2", null),
        }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// A status mismatch is Fail with TODAY's observation, byte for byte — the body is not
    /// evaluated at all, even though every body assertion here would also fail.
    /// </summary>
    [Fact]
    public async Task Execute_StatusMismatch_KeepsTodaysObservation_AndSkipsTheBody()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("<html>not found</html>", status: 404));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.id", null, true) }, "orders"));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("{\"status\":404,\"expected\":200}", outcome.Observation);
    }

    /// <summary>
    /// With no <c>status</c> the 2xx default still gates the body: a 201 evaluates it, and a
    /// failing assertion reports <c>"expected":null</c> exactly as today's shape does.
    /// </summary>
    [Fact]
    public async Task Execute_DefaultStatus_GatesTheBody()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":\"A-1\"}", status: 201));

        var model = Model("POST", new HttpExpect(null, new[] { new HttpJsonAssertion("$.id", "B-2", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.StartsWith("{\"status\":201,\"expected\":null,\"body\":", outcome.Observation, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — failure reasons
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The mismatch observation, pinned byte for byte: the author's path and expected text, a
    /// reason, and the KIND of the node found — never its value.
    /// </summary>
    [Fact]
    public async Task Execute_Mismatch_ObservationShape_IsPinned()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"status\":\"PENDING\"}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.status", "SHIPPED", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":{\"assertion\":\"json\"," +
            "\"path\":\"$.status\",\"reason\":\"mismatch\",\"expected\":\"SHIPPED\",\"actualKind\":\"string\"}}}",
            outcome.Observation);
    }

    /// <summary>
    /// <c>failed</c>/<c>of</c> count every assertion, and <c>first</c> is the first that did not
    /// hold in declaration order — the json entries before <c>bodyContains</c>.
    /// </summary>
    [Fact]
    public async Task Execute_SeveralFailures_CountsThemAndReportsTheFirstInDeclarationOrder()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"a\":1,\"b\":2}"));

        var model = Model(
            "GET",
            new HttpExpect(
                200,
                new[]
                {
                    new HttpJsonAssertion("$.a", "1", null),
                    new HttpJsonAssertion("$.b", "3", null),
                },
                "absent-text"));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        var body = BodyOf(outcome);
        Assert.Equal(2, body.GetProperty("failed").GetInt32());
        Assert.Equal(3, body.GetProperty("of").GetInt32());
        var first = body.GetProperty("first");
        Assert.Equal("$.b", first.GetProperty("path").GetString());
        Assert.Equal("mismatch", first.GetProperty("reason").GetString());
        Assert.Equal("number", first.GetProperty("actualKind").GetString());
    }

    /// <summary>
    /// A path selecting nothing is <c>missing</c>; equality over MORE than one node is
    /// <c>multipleNodes</c> with the count — never "the first one", so
    /// <c>$.items[*].status: SHIPPED</c> cannot pass while only one item shipped.
    /// </summary>
    [Theory]
    [InlineData("$.nope", "missing", -1)]
    [InlineData("$.items[*].status", "multipleNodes", 2)]
    public async Task Execute_EqualityOverZeroOrManyNodes_Fails(string path, string reason, int count)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json(
            "{\"items\":[{\"status\":\"SHIPPED\"},{\"status\":\"PENDING\"}]}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion(path, "SHIPPED", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        var first = BodyOf(outcome).GetProperty("first");
        Assert.Equal(reason, first.GetProperty("reason").GetString());
        Assert.Equal("SHIPPED", first.GetProperty("expected").GetString());
        if (count >= 0)
            Assert.Equal(count, first.GetProperty("count").GetInt32());
        else
            Assert.False(first.TryGetProperty("count", out _));
    }

    /// <summary>
    /// <c>exists: true</c> over nothing is <c>missing</c>; <c>exists: false</c> over something
    /// is <c>present</c> with the count. Both echo the author's flag, not an expected value.
    /// </summary>
    [Theory]
    [InlineData("$.nope", true, "missing", -1)]
    [InlineData("$..password", false, "present", 2)]
    public async Task Execute_ExistsForms_Fail(string path, bool exists, string reason, int count)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json(
            "{\"password\":\"x\",\"nested\":{\"password\":\"y\"}}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion(path, null, exists) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        var first = BodyOf(outcome).GetProperty("first");
        Assert.Equal(reason, first.GetProperty("reason").GetString());
        Assert.Equal(exists, first.GetProperty("exists").GetBoolean());
        Assert.False(first.TryGetProperty("expected", out _));
        if (count >= 0)
            Assert.Equal(count, first.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// A body that is not JSON fails EVERY json entry as <c>notJson</c> — <c>exists: false</c>
    /// included, which would otherwise pass vacuously — and that covers an empty body, HTML,
    /// trailing text and an object with a duplicate member name (which JsonNode would otherwise
    /// only surface lazily, mid-evaluation).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<html><body>ok</body></html>")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"outer\":{\"a\":1,\"a\":2}}")]
    public async Task Execute_BodyIsNotJson_FailsEveryJsonEntryAsNotJson(string body)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text(body, "text/html"));

        var model = Model("GET", new HttpExpect(200, new[]
        {
            new HttpJsonAssertion("$.a", "1", null),
            new HttpJsonAssertion("$..secret", null, false),
        }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        var bodyMember = BodyOf(outcome);
        Assert.Equal(2, bodyMember.GetProperty("failed").GetInt32());
        Assert.Equal("notJson", bodyMember.GetProperty("first").GetProperty("reason").GetString());
    }

    /// <summary>
    /// A filter JsonPath.Net 3.0.2 cannot evaluate over a VALID body (measured: a comparison
    /// against a number beyond its numeric range throws) is <c>unevaluable</c> — Fail, and the
    /// exception's own text is not reported.
    /// </summary>
    [Fact]
    public async Task Execute_FilterTheLibraryCannotEvaluate_IsUnevaluable()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("[{\"n\":1e400}]"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$[?@.n > 1]", null, true) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("unevaluable", BodyOf(outcome).GetProperty("first").GetProperty("reason").GetString());
    }

    /// <summary>
    /// The library's OTHER measured fault shape is likewise <c>unevaluable</c>: reflecting
    /// JsonPath.Net 3.0.2's assembly shows it declares exactly one exception type of its own,
    /// <see cref="Json.Path.PathParseException"/> — what <c>JsonPath.Parse</c> itself throws on
    /// a malformed expression, measured with an unterminated bracket (<c>$.a[</c>). A numeric
    /// overflow shape raising <see cref="OverflowException"/> was also measured and found NOT to
    /// occur: every overflow-adjacent probe (huge/negative/subnormal literals on either side of a
    /// filter comparison, oversized integers, an out-of-range bracket index) raised the same
    /// <see cref="FormatException"/> as <see cref="Execute_FilterTheLibraryCannotEvaluate_IsUnevaluable"/>,
    /// never a distinct <see cref="OverflowException"/> — so PathParseException, not overflow, is
    /// this codebase's second measured shape. 'vouchfx validate' already refuses a malformed
    /// 'expect.json' key with the same JsonPath.TryParse (<c>ValidateBodyAssertions</c>), so this
    /// exception is unreachable once a suite has been validated; this row binds directly —
    /// bypassing Validate, exactly as every other Execute row in this file does — to prove the
    /// catch still classifies it correctly on the defensive path.
    /// </summary>
    [Fact]
    public async Task Execute_MalformedPathReachingEvaluate_IsUnevaluable()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"a\":1}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.a[", null, true) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("unevaluable", BodyOf(outcome).GetProperty("first").GetProperty("reason").GetString());
    }

    /// <summary>
    /// An exception that is NOT one of the two measured library-native "cannot evaluate this
    /// claim" shapes must never read as <c>unevaluable</c> (the review finding on #562: a bare
    /// <c>catch (System.Exception)</c> here swallowed host/cancellation faults too). There is no
    /// way to make JsonPath.Net 3.0.2 itself raise a third shape — the probe behind the two Fail
    /// rows above was exhaustive over numeric-overflow, malformed-path and pathological-filter
    /// inputs and never found one — so this row asserts the catch's TYPE SET structurally,
    /// straight out of the emitted helper source, rather than trying to provoke one at runtime.
    /// The sibling capture-path catches in the FIRST helper class, <c>HttpRest_Helpers</c> (S04-
    /// B-02, unchanged by #562's finding, which named only this helper's <c>EvaluateJson</c>),
    /// still use a bare <c>catch (System.Exception)</c> for a miss-is-not-a-crash capture
    /// contract and are deliberately left alone.
    /// </summary>
    [Fact]
    public void Emit_UnevaluableCatch_IsRestrictedToTheMeasuredJsonPathFaults()
    {
        var helper = Assert.Single(
            new HttpRestProvider()
                .Emit(
                    Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.a", "1", null) })),
                    new StubCompileContext(StepId))
                .RequiredHelpers,
            IsBodyAssertionsHelper);

        Assert.Contains(
            "catch (System.Exception ex) when (ex is System.FormatException or Json.Path.PathParseException)",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catch (System.Exception)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception)", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>bodyContains</c> is an ordinal, case-sensitive substring of the decoded body: found
    /// passes, a different case is <c>notFound</c> — on a text/plain body, where it is the only
    /// assertion there is.
    /// </summary>
    [Theory]
    [InlineData("Hostname: web-1", Verdict.Pass)]
    [InlineData("hostname: web-1", Verdict.Fail)]
    public async Task Execute_BodyContains_IsOrdinal(string needle, Verdict verdict)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text("Hostname: web-1\nIP: 10.0.0.2\n", "text/plain"));

        var model = Model("GET", new HttpExpect(200, null, needle));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(verdict, outcome.Verdict);
        if (verdict == Verdict.Fail)
        {
            var first = BodyOf(outcome).GetProperty("first");
            Assert.Equal("bodyContains", first.GetProperty("assertion").GetString());
            Assert.Equal("notFound", first.GetProperty("reason").GetString());
            Assert.Equal(needle, first.GetProperty("expected").GetString());
        }
    }

    /// <summary>
    /// NOTHING read from the response body reaches the observation — not on a mismatch, a
    /// multi-node match, a notFound or a notJson — only kinds and counts do.
    /// </summary>
    [Theory]
    [InlineData("{\"v\":\"SENTINEL-7f3a\"}", "$.v", "other")]
    [InlineData("{\"v\":[\"SENTINEL-7f3a\",\"SENTINEL-7f3a\"]}", "$.v[*]", "other")]
    [InlineData("SENTINEL-7f3a is not json", "$.v", "other")]
    public async Task Execute_ResponseText_NeverReachesTheObservation(string body, string path, string expected)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text(body, "application/json"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion(path, expected, null) }, "absent"));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.True(
            !(outcome.Observation ?? string.Empty).Contains("SENTINEL-7f3a", StringComparison.Ordinal),
            "Text taken from the response body reached the step observation.");
    }

    /// <summary>
    /// Author text echoed into the observation is bounded: an expected template longer than 256
    /// characters is cut there, on a code-point boundary, and marked with an ellipsis.
    /// </summary>
    [Fact]
    public async Task Execute_LongExpectedTemplate_IsBoundedInTheObservation()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"v\":\"x\"}"));

        // 255 ASCII characters then a surrogate PAIR straddling the 256-character cut.
        var template = new string('A', 255) + char.ConvertFromUtf32(0x1F600) + new string('B', 40);
        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.v", template, null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        var echoed = BodyOf(outcome).GetProperty("first").GetProperty("expected").GetString();
        Assert.Equal(new string('A', 255) + (char)0x2026, echoed);
    }

    /// <summary>
    /// The 256-character cap is on the TOTAL echoed text, the ellipsis included (DSL §5.1):
    /// with no surrogate pair astride the cut, that is 255 kept characters plus the ellipsis
    /// — never 256 kept characters plus the ellipsis, which would total 257.
    /// </summary>
    [Fact]
    public async Task Execute_LongExpectedTemplate_NoSurrogateAtTheCut_TotalIsStill256()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"v\":\"x\"}"));

        var template = new string('A', 300);
        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.v", template, null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        var echoed = BodyOf(outcome).GetProperty("first").GetProperty("expected").GetString();
        Assert.Equal(new string('A', 255) + (char)0x2026, echoed);
        Assert.Equal(256, echoed!.Length);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — interplay with capture
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A failing assertion is the step's Fail and the captures do not run: nothing is written,
    /// not even the capture-status flags — today's "no capture on Fail" rule.
    /// </summary>
    [Fact]
    public async Task Execute_FailingAssertion_SkipsCapture()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":\"A-1\",\"status\":\"PENDING\"}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.status", "SHIPPED", null) }));
        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["orderId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
        };

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, captures: captures);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.False(vars.ContainsKey("orderId"));
        Assert.False(vars.ContainsKey(VarKeys.CaptureStatus(SafeId)));
    }

    /// <summary>
    /// Once the assertions hold the captures run over the SAME parse and write their values.
    /// </summary>
    [Fact]
    public async Task Execute_PassingAssertions_ThenCapture()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":\"A-1\",\"status\":\"SHIPPED\"}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.status", "SHIPPED", null) }));
        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["orderId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
        };

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, captures: captures);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal("A-1", vars["orderId"]);
        Assert.Equal("1", vars[VarKeys.CaptureStatus(SafeId)]);
    }

    /// <summary>
    /// The shared parse keeps a literal <c>null</c> body's capture behaviour unchanged: the
    /// root is a present null (so <c>$</c> equals the text null), and a capture over it is
    /// still unmet — Inconclusive, exactly as before the assertions shared the parse.
    /// </summary>
    [Fact]
    public async Task Execute_NullBody_AssertionHolds_CaptureStillUnmet()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("null"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$", "null", null) }));
        var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
        {
            ["orderId"] = new CaptureExpr(CaptureFormat.JsonPath, "$.id"),
        };

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, captures: captures);

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.Equal("{\"captureUnmet\":\"orderId\"}", outcome.Observation);
        Assert.False(vars.ContainsKey("orderId"));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — placeholders and secrets
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An expected value's <c>{placeholder}</c> resolves from Vars at run time (a number
    /// through the invariant culture), while a failing observation still carries the
    /// TEMPLATE, never the resolved value.
    /// </summary>
    [Fact]
    public async Task Execute_Placeholder_ResolvesAtRunTime_ObservationKeepsTheTemplate()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":42,\"customer\":\"c-9\"}"));

        var model = Model("GET", new HttpExpect(200, new[]
        {
            new HttpJsonAssertion("$.id", "{orderId}", null),
            new HttpJsonAssertion("$.customer", "{customerId}", null),
        }));
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["orderId"] = 42,
            ["customerId"] = "c-1",
        };

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, seed: seed);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        var body = BodyOf(outcome);
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        Assert.Equal("{customerId}", body.GetProperty("first").GetProperty("expected").GetString());
    }

    /// <summary>
    /// An expected value naming a placeholder that is ABSENT from Vars, or bound to null, would
    /// resolve to "" — a vacuous <c>bodyContains</c> pass or a misattributed Fail. Instead the
    /// step is Inconclusive naming the placeholder, and NOTHING is sent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UnsetPlaceholder_IsInconclusive_AndNothingIsSent(bool boundToNull)
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text("Hostname: web-1", "text/plain"));

        var model = Model("GET", new HttpExpect(200, null, "Hostname: {hostname}"));
        var seed = boundToNull
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["hostname"] = null }
            : null;

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, seed: seed);

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.Equal("{\"placeholderUnmet\":\"hostname\"}", outcome.Observation);
        Assert.Equal(0, responder.RequestCount);
    }

    /// <summary>
    /// A <c>bodyContains</c> template such as <c>"{token}"</c> whose placeholder IS set, but
    /// to the empty string, resolves to <c>""</c> — the unset-placeholder guard does not catch
    /// it (the value is present, not null). Unless guarded separately this would pass
    /// vacuously (<c>body.IndexOf("", Ordinal)</c> is 0 in any body); instead the step is
    /// Inconclusive and NOTHING is sent, mirroring the unset-placeholder classification (#562).
    /// </summary>
    [Fact]
    public async Task Execute_BodyContainsResolvesEmpty_IsInconclusive_AndNothingIsSent()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text("Hostname: web-1", "text/plain"));

        var model = Model("GET", new HttpExpect(200, null, "{token}"));
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal) { ["token"] = string.Empty };

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, seed: seed);

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.Equal("{\"bodyContainsResolvedEmpty\":true}", outcome.Observation);
        Assert.Equal(0, responder.RequestCount);
    }

    /// <summary>
    /// A <c>bodyContains</c> placeholder that resolves to genuine non-empty text still passes
    /// when the body carries it: the empty-resolution guard does not disturb the ordinary case.
    /// </summary>
    [Fact]
    public async Task Execute_BodyContainsResolvesNonEmpty_StillPasses()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text("Hostname: web-1", "text/plain"));

        var model = Model("GET", new HttpExpect(200, null, "Hostname: {hostname}"));
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal) { ["hostname"] = "web-1" };

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, seed: seed);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal(1, responder.RequestCount);
    }

    /// <summary>
    /// A <c>bodyContains</c> placeholder that resolves to genuine non-empty text the body does
    /// NOT carry still fails as <c>notFound</c> — the empty-resolution guard is specific to the
    /// empty string and never widens into a general exemption for a placeholder-bearing value.
    /// </summary>
    [Fact]
    public async Task Execute_BodyContainsResolvesNonEmpty_StillFailsWhenAbsent()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Text("Hostname: web-1", "text/plain"));

        var model = Model("GET", new HttpExpect(200, null, "Hostname: {hostname}"));
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal) { ["hostname"] = "web-2" };

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, seed: seed);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        var first = BodyOf(outcome).GetProperty("first");
        Assert.Equal("notFound", first.GetProperty("reason").GetString());
        Assert.Equal(1, responder.RequestCount);
    }

    /// <summary>
    /// The unset-placeholder check reads only the equality form's template: an
    /// <c>exists</c> entry has none, so it can never be the reason a step is withheld.
    /// </summary>
    [Fact]
    public async Task Execute_ExistsEntry_HasNoTemplateToCheck()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":1}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.id", null, true) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal(1, responder.RequestCount);
    }

    /// <summary>
    /// An unresolvable <c>${secret:…}</c> in an expected value is resolved BEFORE the request:
    /// the step is today's reference-only EnvironmentError and nothing is sent.
    /// </summary>
    [Fact]
    public async Task Execute_MissingSecretInExpectedValue_IsEnvironmentError_AndNothingIsSent()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"token\":\"t\"}"));

        var envName = "VOUCHFX_BODY_ASSERT_MISSING_" + Guid.NewGuid().ToString("N");
        var model = Model("GET", new HttpExpect(
            200, new[] { new HttpJsonAssertion("$.token", "${secret:env/" + envName + "}", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl, secrets: EnvSecrets());

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.Contains("\"secretError\"", outcome.Observation, StringComparison.Ordinal);
        Assert.Equal(0, responder.RequestCount);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — decoding
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The body is decoded as <c>capture</c> decodes it: the Content-Type charset is honoured
    /// and a UTF-8 byte-order mark is stripped before the JSON parse.
    /// </summary>
    [Theory]
    [InlineData("latin1")]
    [InlineData("bom")]
    public async Task Execute_Charset_IsHonoured(string variant)
    {
        const string json = "{\"name\":\"café\"}";
        var response = variant == "latin1"
            ? new CannedResponse(200, "application/json; charset=iso-8859-1", Encoding.Latin1.GetBytes(json))
            : new CannedResponse(200, "application/json", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
        using var responder = ScriptedResponder.Start(response);

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.name", "café", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An unknown charset label stays today's EnvironmentError from the shared body read — a
    /// reclassification is a separate change that also covers <c>capture</c>.
    /// </summary>
    [Fact]
    public async Task Execute_UnknownCharset_StaysEnvironmentError()
    {
        using var responder = ScriptedResponder.Start(new CannedResponse(
            200, "application/json; charset=bogus-charset", Encoding.UTF8.GetBytes("{\"a\":1}")));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.a", "1", null) }));

        var (outcome, _) = await RunAsync(model, responder.BaseUrl);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // 4. Execute — RETRY
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Under RETRY every attempt re-sends and re-evaluates: a service that answers PENDING twice
    /// and then SHIPPED passes on the third attempt, and each failing attempt recorded its own
    /// Fail with the <c>body</c> observation.
    /// </summary>
    [Fact]
    public async Task Execute_Retry_PassesOnceTheBodyHolds()
    {
        using var responder = ScriptedResponder.Start(n => CannedResponse.Json(
            n < 3 ? "{\"status\":\"PENDING\"}" : "{\"status\":\"SHIPPED\"}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.status", "SHIPPED", null) }));

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, retry: (TimeoutMs: 20_000, PollMs: 5));

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Equal(3, responder.RequestCount);
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[VarKeys.Attempts(SafeId)]);
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts.Take(2), a =>
        {
            Assert.Equal(Verdict.Fail, a.Verdict);
            Assert.Contains("\"body\":", a.Observation, StringComparison.Ordinal);
        });
        Assert.Equal(Verdict.Pass, attempts[2].Verdict);
    }

    /// <summary>
    /// A body that never holds exhausts the window as Inconclusive — never Fail (§7.2, §12.1) —
    /// with every recorded attempt's Fail and body observation on record, each one a request.
    /// </summary>
    [Fact]
    public async Task Execute_Retry_NeverSatisfied_IsInconclusive()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"status\":\"PENDING\"}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.status", "SHIPPED", null) }));

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, retry: (TimeoutMs: 700, PollMs: 20));

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[VarKeys.Attempts(SafeId)]);
        Assert.True(attempts.Count >= 2, "Expected the poll to have run more than one attempt.");
        Assert.All(attempts, a =>
        {
            Assert.Equal(Verdict.Fail, a.Verdict);
            Assert.Contains("\"body\":", a.Observation, StringComparison.Ordinal);
        });

        // One request per recorded attempt — plus at most one more, because a window that
        // closes while a request is in flight cancels that attempt before it is recorded,
        // after the responder has already counted it.
        Assert.InRange(responder.RequestCount, attempts.Count, attempts.Count + 1);
    }

    /// <summary>
    /// The accepted cost of an unset placeholder under RETRY: the runner has no terminal
    /// Inconclusive, so the poll runs out its window — but it sends NOTHING while doing so.
    /// </summary>
    [Fact]
    public async Task Execute_Retry_UnsetPlaceholder_PollsOutTheWindow_SendingNothing()
    {
        using var responder = ScriptedResponder.Start(CannedResponse.Json("{\"id\":1}"));

        var model = Model("GET", new HttpExpect(200, new[] { new HttpJsonAssertion("$.id", "{orderId}", null) }));

        var (outcome, vars) = await RunAsync(model, responder.BaseUrl, retry: (TimeoutMs: 300, PollMs: 20));

        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[VarKeys.Attempts(SafeId)]);
        Assert.All(attempts, a => Assert.Equal("{\"placeholderUnmet\":\"orderId\"}", a.Observation));
        Assert.Equal(0, responder.RequestCount);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static HttpRestModel Model(string method, HttpExpect expect) =>
        new(Target: "svc", Method: method, Path: "/orders/1", Headers: null, Body: null, Expect: expect);

    private static HttpRestModel BindYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return new HttpRestProvider().Bind(root, new StubBindingContext());
    }

    private static bool IsBodyAssertionsHelper(string helper) =>
        helper.Contains("static class HttpRest_BodyAssertions", StringComparison.Ordinal);

    private static string Squash(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The <c>body</c> member of a response-body Fail observation.</summary>
    private static JsonElement BodyOf(StepOutcome outcome)
    {
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        using var doc = JsonDocument.Parse(outcome.Observation ?? "{}");
        return doc.RootElement.GetProperty("body").Clone();
    }

    private static SecretAccessor EnvSecrets() =>
        new SecretAccessor(new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

    /// <summary>
    /// Emits, assembles, compiles and runs one http.rest step — IMMEDIATE, or RETRY when
    /// <paramref name="retry"/> is given — and returns its outcome and the Vars it ran against.
    /// </summary>
    private static async Task<(StepOutcome Outcome, Dictionary<string, object?> Vars)> RunAsync(
        HttpRestModel model,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? seed = null,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null,
        ISecretAccessor? secrets = null,
        (long TimeoutMs, long PollMs)? retry = null)
    {
        var fragment = new HttpRestProvider().Emit(model, new StubCompileContext(StepId, captures));
        var plan = new StepCompilePlan(
            StepId, fragment, Retry: retry.HasValue, TimeoutMs: retry?.TimeoutMs, PollIntervalMs: retry?.PollMs);
        var assembled = CsxAssembler.Assemble(new[] { plan });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(model.Target)] = baseUrl,
        };
        if (seed is not null)
        {
            foreach (var (key, value) in seed)
                vars[key] = value;
        }

        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            secrets ?? NullSecretAccessor.Instance);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(SafeId)]);
        return (outcome, vars);
    }

    private sealed class StubBindingContext : IBindingContext { }

    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId, IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
            Captures = CaptureExprs.ToDictionary(kv => kv.Key, kv => kv.Value.Expression, StringComparer.Ordinal);
        }

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public string StepId { get; }

        public string SuiteNamespace => "Generated";

        public IReadOnlyDictionary<string, string> Captures { get; }

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    private sealed class StubProjectContext : IProjectContext
    {
        internal StubProjectContext(IReadOnlyDictionary<string, DeclaredServiceInfo> services)
        {
            DeclaredServices = services;
        }

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }
    }

    /// <summary>One canned HTTP response: status, Content-Type header (or none) and raw body bytes.</summary>
    private sealed record CannedResponse(int Status, string? ContentType, byte[] Body)
    {
        public static CannedResponse Json(string text, int status = 200) =>
            new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(text));

        public static CannedResponse Text(string text, string contentType) =>
            new(200, contentType, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// A raw-socket HTTP/1.1 responder. Each request is answered with what the script returns
    /// for its 1-based ordinal, and every request is COUNTED — which is what lets a row prove
    /// that nothing was sent. Raw bytes, so a row controls the Content-Type header and the body
    /// encoding exactly (charset label, byte-order mark).
    /// </summary>
    private sealed class ScriptedResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, CannedResponse> _script;
        private int _requests;

        private ScriptedResponder(Func<int, CannedResponse> script)
        {
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount => Volatile.Read(ref _requests);

        public static ScriptedResponder Start(CannedResponse response) => new(_ => response);

        public static ScriptedResponder Start(Func<int, CannedResponse> script) => new(script);

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (SocketException) { /* already stopped */ }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();

                // Read the request head byte by byte up to the blank line, then any declared
                // body, so the client never sees its request cut off.
                var head = new List<byte>();
                var one = new byte[1];
                while (!EndsWithBlankLine(head))
                {
                    if (await stream.ReadAsync(one, CancellationToken.None).ConfigureAwait(false) == 0)
                        return;
                    head.Add(one[0]);
                }

                var length = ContentLength(Encoding.ASCII.GetString(head.ToArray()));
                var remaining = length;
                var buffer = new byte[4096];
                while (remaining > 0)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (n == 0)
                        break;
                    remaining -= n;
                }

                var response = _script(Interlocked.Increment(ref _requests));
                var headText = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture))
                    .Append(response.Status switch
                    {
                        200 => " OK",
                        201 => " Created",
                        404 => " Not Found",
                        _ => " Status",
                    })
                    .Append("\r\n");
                if (response.ContentType is not null)
                    headText.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
                headText
                    .Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                    .Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(headText.ToString()), CancellationToken.None).ConfigureAwait(false);
                await stream.WriteAsync(response.Body, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away, or teardown raced this response — nothing to report.
            }
            finally
            {
                client.Dispose();
            }
        }

        private static bool EndsWithBlankLine(List<byte> head) =>
            head.Count >= 4
            && head[^4] == (byte)'\r' && head[^3] == (byte)'\n'
            && head[^2] == (byte)'\r' && head[^1] == (byte)'\n';

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                const string prefix = "Content-Length:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.AsSpan(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return n;
                }
            }

            return 0;
        }
    }
}
