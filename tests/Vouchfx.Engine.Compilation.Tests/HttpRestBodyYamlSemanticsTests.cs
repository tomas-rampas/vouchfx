// ZEB-67 — http.rest structured request bodies follow YAML's meaning (non-docker, Bind level).
//
// A structured `body:` (a mapping or a sequence) is converted to JSON at bind time. Before
// 1.4.0 that conversion ignored four parts of YAML, and each sent bytes the author did not
// write:
//
//   1. Merge keys: `<<: *base` was sent as a member named "<<" holding the aliased map.
//   2. Block scalars: a `|` or `>` scalar holding 123, true or 1.5 became a JSON number or
//      boolean, and lost the trailing newline `|` keeps.
//   3. Empty values: a plain empty value (`note:`) was sent as "" instead of null.
//   4. Explicit tags: `!!str 123` was the number 123 and `!!int "5"` the string "5".
//
// These rows pin the new rule for each, every error case (a typed authoring refusal: Bind
// never throws, it binds no Body and carries the message in HttpRestModel.BodyRefusal, which
// Validate reports), and a regression row for every behaviour that deliberately did NOT
// change. A final group checks that the schema-validation layer and the binder agree on what
// they accept. The engine-path classification of these refusals (an authoring refusal, never
// a provider fault) is pinned in Vouchfx.Engine.Runtime.Tests, SecretTemplateReferenceTests.
using System.Text;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// ZEB-67: YAML merge keys, block scalars, empty values and explicit tags in an
/// <c>http.rest</c> structured request body.
/// </summary>
public sealed class HttpRestBodyYamlSemanticsTests
{
    private sealed class StubBindingContext : IBindingContext { }

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId) => StepId = stepId;

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    // ── 1. Merge keys ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>&lt;&lt;: *base</c> merges the aliased mapping's members; an explicit key wins over
    /// the merged one, and the <c>&lt;&lt;</c> key itself is not sent. Merged members sit at the
    /// position of the merge key; an overridden member keeps its explicit position.
    /// </summary>
    [Fact]
    public void Merge_AliasToMapping_MergesMembers_ExplicitKeyWins()
    {
        var body = BindBody("""
            anchors:
              base: &base
                currency: EUR
                amount: 10
                channel: web
            steps:
              - id: pay
                type: http.rest
                target: svc
                method: POST
                path: /payments
                body:
                  <<: *base
                  amount: 20
            """);

        // Before ZEB-67: {"<<":{"currency":"EUR","amount":10,"channel":"web"},"amount":20}
        Assert.Equal("{\"currency\":\"EUR\",\"channel\":\"web\",\"amount\":20}", body);
    }

    /// <summary>An explicit key written BEFORE the merge key keeps its place, and still wins.</summary>
    [Fact]
    public void Merge_ExplicitKeysBeforeMerge_KeepDocumentOrder()
    {
        var body = BindBody("""
            anchors:
              base: &base { currency: EUR, amount: 10, channel: web }
            steps:
              - id: pay
                type: http.rest
                target: svc
                method: POST
                path: /payments
                body:
                  id: 7
                  amount: 20
                  <<: *base
                  note: last
            """);

        Assert.Equal(
            "{\"id\":7,\"amount\":20,\"currency\":\"EUR\",\"channel\":\"web\",\"note\":\"last\"}",
            body);
    }

    /// <summary>
    /// <c>&lt;&lt;: [*a, *b]</c> merges both; for a key both supply the EARLIER mapping wins,
    /// and an explicit key wins over either.
    /// </summary>
    [Fact]
    public void Merge_SequenceOfAliases_EarlierWins_ExplicitWinsOverAll()
    {
        var body = BindBody("""
            anchors:
              a: &a { x: a, y: a }
              b: &b { y: b, z: b, w: b }
            steps:
              - id: merge-seq
                type: http.rest
                target: svc
                method: POST
                path: /m
                body:
                  <<: [*a, *b]
                  w: explicit
            """);

        // Before ZEB-67: {"<<":[{"x":"a","y":"a"},{"y":"b","z":"b","w":"b"}],"w":"explicit"}
        Assert.Equal("{\"x\":\"a\",\"y\":\"a\",\"z\":\"b\",\"w\":\"explicit\"}", body);
    }

    /// <summary>
    /// A nested mapping gets the same treatment, a merged mapping's own merge key is applied
    /// first (a chain), and an inline mapping is a valid merge source as well as an alias.
    /// </summary>
    [Fact]
    public void Merge_NestedChainedAndInline_AllApplied()
    {
        var body = BindBody("""
            anchors:
              address: &address { city: Prague, country: CZ }
              billing: &billing
                <<: *address
                kind: billing
            steps:
              - id: nested
                type: http.rest
                target: svc
                method: POST
                path: /customers
                body:
                  customer:
                    name: Ada
                    address:
                      <<: *billing
                      city: Brno
                  flags:
                    <<: { express: true }
                    gift: false
            """);

        Assert.Equal(
            "{\"customer\":{\"name\":\"Ada\",\"address\":{\"country\":\"CZ\",\"kind\":\"billing\",\"city\":\"Brno\"}},"
            + "\"flags\":{\"express\":true,\"gift\":false}}",
            body);
    }

    /// <summary>A merge inside a mapping inside a top-level sequence body is applied too.</summary>
    [Fact]
    public void Merge_InsideTopLevelSequenceBody_Applied()
    {
        var body = BindBody("""
            anchors:
              line: &line { sku: ABC, quantity: 1 }
            steps:
              - id: lines
                type: http.rest
                target: svc
                method: POST
                path: /lines
                body:
                  - <<: *line
                  - <<: *line
                    quantity: 3
            """);

        Assert.Equal(
            "[{\"sku\":\"ABC\",\"quantity\":1},{\"sku\":\"ABC\",\"quantity\":3}]",
            body);
    }

    /// <summary>A QUOTED <c>"&lt;&lt;"</c> is an ordinary key, as YAML defines it, and is sent.</summary>
    [Fact]
    public void Merge_QuotedKey_IsALiteralMember()
    {
        var body = BindBody("""
            steps:
              - id: literal
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  "<<": shift
                  n: 1
            """);

        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        Assert.Equal("shift", (string?)node["<<"]);
        Assert.Equal(1, (int)node["n"]!);
    }

    /// <summary>A merge key whose value is a scalar is a bind error naming the step and the key.</summary>
    [Theory]
    [InlineData("<<: oops", "its value is a scalar")]
    [InlineData("<<:", "its value is a scalar")]
    [InlineData("<<: [*a, 5]", "its sequence holds a scalar at line")]
    [InlineData("<<: [*a, [1, 2]]", "its sequence holds a sequence at line")]
    public void Merge_ValueNotAMapping_IsABindError(string mergeLine, string expected)
    {
        var yaml = """
            anchors:
              a: &a { x: 1 }
            steps:
              - id: bad-merge
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  y: 2
                  MERGE
            """.Replace("MERGE", mergeLine, StringComparison.Ordinal);

        var refusal = RefuseBody(yaml);

        Assert.Contains("step 'bad-merge'", refusal, StringComparison.Ordinal);
        Assert.Contains("merge key '<<' at line 11, column 7", refusal, StringComparison.Ordinal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("authoring fault", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    /// <summary>A mapping that merges itself is refused rather than recursing.</summary>
    [Fact]
    public void Merge_Cycle_IsABindError()
    {
        var yaml = """
            steps:
              - id: cyclic
                type: http.rest
                target: svc
                method: POST
                path: /x
                body: &self
                  x: 1
                  <<: *self
            """;

        var refusal = RefuseBody(yaml);

        Assert.Contains("step 'cyclic'", refusal, StringComparison.Ordinal);
        Assert.Contains("into itself", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    /// <summary>
    /// Merged members draw on the same node budget as aliases: thirty sites each merging a
    /// 1,000-member mapping produce about 30,000 JSON nodes, inside the 50,000 budget on
    /// their own, but every merged member examined is charged as well, so the body is refused.
    /// Without that charge a merge chain could amplify without ever reaching the budget.
    /// </summary>
    [Fact]
    public void Merge_AmplifyingMerges_RunTheNodeBudgetOut()
    {
        var sb = new StringBuilder();
        sb.Append("anchors:\n  big: &big\n");
        for (var i = 0; i < 1000; i++)
            sb.Append("    k").Append(i).Append(": ").Append(i).Append('\n');
        sb.Append("steps:\n  - id: merge-laughs\n    type: http.rest\n    target: svc\n");
        sb.Append("    method: POST\n    path: /x\n    body:\n");
        for (var i = 0; i < 30; i++)
            sb.Append("      s").Append(i).Append(": { <<: *big }\n");

        var refusal = RefuseBody(sb.ToString());

        Assert.Contains("step 'merge-laughs'", refusal, StringComparison.Ordinal);
        Assert.Contains("expands to more than 50000 JSON nodes", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every merge source is charged to the node budget, including one with no members. A
    /// sequence of 300 aliases of an empty mapping, merged into 200 distinct mappings, is 60,000
    /// sources and no merged members, so only the per-source charge can refuse it.
    /// </summary>
    [Fact]
    public void Merge_EmptySourcesRepeated_RunTheNodeBudgetOut()
    {
        var sb = new StringBuilder();
        sb.Append("anchors:\n  e: &e {}\n  sq: &s [");
        for (var i = 0; i < 300; i++)
            sb.Append(i == 0 ? "*e" : ", *e");
        sb.Append("]\nsteps:\n  - id: empty-sources\n    type: http.rest\n    target: svc\n");
        sb.Append("    method: POST\n    path: /x\n    body:\n");
        for (var i = 0; i < 200; i++)
            sb.Append("      m").Append(i).Append(": { <<: *s }\n");

        var refusal = RefuseBody(sb.ToString());

        Assert.Contains("step 'empty-sources'", refusal, StringComparison.Ordinal);
        Assert.Contains("expands to more than 50000 JSON nodes", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    /// <summary>An ordinary merge of an empty mapping still works and adds no members.</summary>
    [Fact]
    public void Merge_EmptySource_OrdinaryMerge_StillBinds()
    {
        var body = BindBody("""
            anchors:
              e: &e {}
            steps:
              - id: pay
                type: http.rest
                target: svc
                method: POST
                path: /payments
                body:
                  <<: [*e, *e]
                  amount: 20
            """);

        Assert.Equal("{\"amount\":20}", body);
    }

    /// <summary>A chain of merged mappings deeper than 64 is refused, bounding the recursion.</summary>
    [Fact]
    public void Merge_ChainDeeperThanTheBound_IsABindError()
    {
        var sb = new StringBuilder();
        sb.Append("anchors:\n  m0: &m0 { k0: 0 }\n");
        for (var i = 1; i <= 70; i++)
            sb.Append("  m").Append(i).Append(": &m").Append(i).Append(" { <<: *m").Append(i - 1)
              .Append(", k").Append(i).Append(": ").Append(i).Append(" }\n");
        sb.Append("steps:\n  - id: deep-merge\n    type: http.rest\n    target: svc\n");
        sb.Append("    method: POST\n    path: /x\n    body:\n      <<: *m70\n");

        var refusal = RefuseBody(sb.ToString());

        Assert.Contains("step 'deep-merge'", refusal, StringComparison.Ordinal);
        Assert.Contains("more than 64 mappings deep", refusal, StringComparison.Ordinal);
    }

    // ── 2. Block scalars ───────────────────────────────────────────────────────

    /// <summary>
    /// Literal and folded block scalars are JSON strings in every chomping form, with exactly
    /// the text YAML defines: clip keeps one trailing newline, strip none, keep all.
    /// </summary>
    [Fact]
    public void BlockScalars_EveryChompingForm_AreStringsWithYamlText()
    {
        var body = BindBody("""
            steps:
              - id: blocks
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  literalClip: |
                    123
                  literalStrip: |-
                    true
                  literalKeep: |+
                    1.5

                  foldedClip: >
                    folded
                    line
                  foldedStrip: >-
                    7
                  foldedKeep: >+
                    null

                  lines: |
                    first
                    second
                  last: 1
            """);

        // Before ZEB-67: "literalClip":123,"literalStrip":true,"literalKeep":1.5,
        // "foldedStrip":7,"foldedKeep":null.
        Assert.Equal(
            "{\"literalClip\":\"123\\n\",\"literalStrip\":\"true\",\"literalKeep\":\"1.5\\n\\n\","
            + "\"foldedClip\":\"folded line\\n\",\"foldedStrip\":\"7\",\"foldedKeep\":\"null\\n\\n\","
            + "\"lines\":\"first\\nsecond\\n\",\"last\":1}",
            body);
    }

    /// <summary>An empty block scalar is the empty string, not null.</summary>
    [Fact]
    public void BlockScalars_Empty_IsEmptyString()
    {
        var body = BindBody("""
            steps:
              - id: empty-block
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  a: |
                  b: >-
                  c: 1
            """);

        Assert.Equal("{\"a\":\"\",\"b\":\"\",\"c\":1}", body);
    }

    // ── 3. Empty values ────────────────────────────────────────────────────────

    /// <summary>
    /// A plain empty value is JSON null, the same as <c>null</c> and <c>~</c>; an explicitly
    /// quoted empty string stays <c>""</c>.
    /// </summary>
    [Fact]
    public void EmptyValues_PlainIsNull_QuotedIsEmptyString()
    {
        var body = BindBody("""
            steps:
              - id: empties
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  note:
                  nullWord: null
                  tilde: ~
                  doubleQuoted: ""
                  singleQuoted: ''
                  list:
                    -
                    - ""
                  flow: { a: , b: "" }
            """);

        // Before ZEB-67: "note":"", the empty sequence item "" and "a":"".
        Assert.Equal(
            "{\"note\":null,\"nullWord\":null,\"tilde\":null,\"doubleQuoted\":\"\",\"singleQuoted\":\"\","
            + "\"list\":[null,\"\"],\"flow\":{\"a\":null,\"b\":\"\"}}",
            body);
    }

    // ── 4. Explicit tags ───────────────────────────────────────────────────────

    /// <summary>
    /// Core-schema tags decide the JSON type whatever the style: <c>!!str</c> forces a string,
    /// <c>!!int</c>/<c>!!float</c>/<c>!!bool</c>/<c>!!null</c> force their type, including on a
    /// quoted value.
    /// </summary>
    [Theory]
    [InlineData("!!str 123", "\"123\"")]
    [InlineData("!!str true", "\"true\"")]
    [InlineData("!!str null", "\"null\"")]
    [InlineData("!!str", "\"\"")]
    [InlineData("!!str 1.50", "\"1.50\"")]
    [InlineData("! 12", "\"12\"")]
    [InlineData("!<tag:yaml.org,2002:str> 5", "\"5\"")]
    [InlineData("!!int \"5\"", "5")]
    [InlineData("!!int '-7'", "-7")]
    [InlineData("!!int +5", "5")]
    [InlineData("!!int 010", "10")]
    [InlineData("!!int 0x1F", "31")]
    [InlineData("!!int 0o17", "15")]
    [InlineData("!!int 123456789012345678901234567890", "123456789012345678901234567890")]
    [InlineData("!!int -00098765432109876543210", "-98765432109876543210")]
    [InlineData("!!float \"1.5\"", "1.5")]
    [InlineData("!!float 1e3", "1000")]
    [InlineData("!!float 7", "7")]
    [InlineData("!!float -.5", "-0.5")]
    [InlineData("!!bool \"true\"", "true")]
    [InlineData("!!bool False", "false")]
    [InlineData("!!bool TRUE", "true")]
    [InlineData("!!null ~", "null")]
    [InlineData("!!null \"\"", "null")]
    [InlineData("!!null", "null")]
    [InlineData("!!null NULL", "null")]
    [InlineData("!!timestamp 2001-12-14", "\"2001-12-14\"")]
    public void Tags_CoreSchema_DecideTheType(string value, string expectedJson)
    {
        var body = BindBody(StepWithValue("tagged", value));

        Assert.Equal("{\"v\":" + expectedJson + "}", body);
    }

    /// <summary><c>!!str</c> on a block scalar is the same string the block scalar is.</summary>
    [Fact]
    public void Tags_StrOnBlockScalar_KeepsTheBlockText()
    {
        var body = BindBody("""
            steps:
              - id: tagged-block
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  v: !!str |
                    42
                  w: 1
            """);

        Assert.Equal("{\"v\":\"42\\n\",\"w\":1}", body);
    }

    /// <summary><c>!!map</c> on a mapping and <c>!!seq</c> on a sequence are accepted.</summary>
    [Fact]
    public void Tags_CollectionTagsOnTheirOwnKind_Accepted()
    {
        var body = BindBody(StepWithValue("collections", "!!map { a: !!seq [1, !!str 2] }"));

        Assert.Equal("{\"v\":{\"a\":[1,\"2\"]}}", body);
    }

    /// <summary>
    /// A value that does not fit its core-schema tag is a bind error naming the step, the tag
    /// and the position.
    /// </summary>
    [Theory]
    [InlineData("!!int abc", "!!int", "not an integer")]
    [InlineData("!!int 1.5", "!!int", "not an integer")]
    [InlineData("!!int 1_000", "!!int", "not an integer")]
    [InlineData("!!int \"\"", "!!int", "not an integer")]
    [InlineData("!!int 0x", "!!int", "not an integer")]
    [InlineData("!!int 0o19", "!!int", "not an integer")]
    [InlineData("!!int 0x1FFFFFFFFFFFFFFFF", "!!int", "does not fit in 64 bits")]
    [InlineData("!!float abc", "!!float", "not a number")]
    [InlineData("!!float 1.2.3", "!!float", "not a number")]
    [InlineData("!!float .inf", "!!float", "no infinity or NaN")]
    [InlineData("!!float .nan", "!!float", "no infinity or NaN")]
    [InlineData("!!float 1e999", "!!float", "too large")]
    [InlineData("!!bool yes", "!!bool", "not one of true")]
    [InlineData("!!bool \"1\"", "!!bool", "not one of true")]
    [InlineData("!!null y", "!!null", "not one of the null forms")]
    public void Tags_ContentNotValidForTag_IsABindError(string value, string tag, string reason)
    {
        var refusal = RefuseBody(StepWithValue("bad-tag", value));

        Assert.Contains("step 'bad-tag'", refusal, StringComparison.Ordinal);
        Assert.Contains("tags the value at line 8, column 10 as " + tag, refusal, StringComparison.Ordinal);
        Assert.Contains(reason, refusal, StringComparison.Ordinal);
        Assert.Contains("authoring fault", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    /// <summary>
    /// An unknown, application-specific or non-JSON tag is a bind error naming the tag, rather
    /// than being ignored as it was before ZEB-67. So is a core tag on the wrong kind of node.
    /// </summary>
    [Theory]
    [InlineData("!money 5", "!money", "a scalar")]
    [InlineData("!!binary aGk=", "!!binary", "a scalar")]
    [InlineData("!<tag:example.com,2026:thing> 1", "!<tag:example.com,2026:thing>", "a scalar")]
    [InlineData("!local { a: 1 }", "!local", "a mapping")]
    [InlineData("!!str { a: 1 }", "!!str", "a mapping")]
    [InlineData("!!map [1]", "!!map", "a sequence")]
    [InlineData("!!set { a: }", "!!set", "a mapping")]
    public void Tags_Unsupported_IsABindErrorNamingTheTag(string value, string tag, string kind)
    {
        var refusal = RefuseBody(StepWithValue("odd-tag", value));

        Assert.Contains("step 'odd-tag'", refusal, StringComparison.Ordinal);
        Assert.Contains("uses the YAML tag '" + tag + "' on " + kind + " at line 8, column 10", refusal, StringComparison.Ordinal);
        Assert.Contains("authoring fault", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    /// <summary>A tag outside printable ASCII is escaped in the diagnostic, which stays ASCII.</summary>
    [Fact]
    public void Tags_NonAsciiTag_DiagnosticStaysAscii()
    {
        var refusal = RefuseBody(StepWithValue("unicode-tag", "!caf%C3%A9 1"));

        Assert.Contains("step 'unicode-tag'", refusal, StringComparison.Ordinal);
        AssertAscii(refusal);
    }

    // ── 5. Unchanged behaviour (regression) ────────────────────────────────────

    /// <summary>Untagged plain scalars are typed exactly as before ZEB-67.</summary>
    [Theory]
    [InlineData("007", "7")]
    [InlineData("010", "10")]
    [InlineData("1.50", "1.5")]
    [InlineData("1e3", "1000")]
    [InlineData("-12", "-12")]
    [InlineData("0x1F", "\"0x1F\"")]
    [InlineData(".inf", "\".inf\"")]
    [InlineData("yes", "\"yes\"")]
    [InlineData("on", "\"on\"")]
    [InlineData("true", "true")]
    [InlineData("False", "false")]
    [InlineData("null", "null")]
    [InlineData("~", "null")]
    [InlineData("'123'", "\"123\"")]
    [InlineData("\"true\"", "\"true\"")]
    [InlineData("\"null\"", "\"null\"")]
    [InlineData("'1.5'", "\"1.5\"")]
    [InlineData("\"{orderId}\"", "\"{orderId}\"")]
    [InlineData("${secret:env/API}", "\"${secret:env/API}\"")]
    [InlineData("hello world", "\"hello world\"")]
    public void Unchanged_PlainAndQuotedScalars(string value, string expectedJson)
    {
        var body = BindBody(StepWithValue("keep", value));

        Assert.Equal("{\"v\":" + expectedJson + "}", body);
    }

    /// <summary>
    /// Plain text that .NET parses as a NON-FINITE double is a string, as DSL §5.1's
    /// "everything else is a string" says: JSON has no NaN or infinity. Before this rule .NET
    /// parsed each of these (in any letter case) to NaN or an infinity and serialising the body
    /// then threw ArgumentException out of Bind, which the engine reported as a provider defect.
    /// No body that used to be sent changes. A finite exponent form is still a number.
    /// </summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("infinity")]
    [InlineData("+Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void PlainNonFiniteNumbers_AreStrings(string value)
    {
        var body = BindBody(StepWithValue("non-finite", value));

        // Compared as parsed JSON: the serialiser's default encoder writes '+' as +.
        var sent = System.Text.Json.Nodes.JsonNode.Parse(body)!["v"]!.AsValue();
        Assert.Equal(System.Text.Json.JsonValueKind.String, sent.GetValueKind());
        Assert.Equal(value, sent.GetValue<string>());
    }

    /// <summary>The largest finite exponent forms stay numbers: only non-finite text changed.</summary>
    [Theory]
    [InlineData("1e308", "1E+308")]
    [InlineData("-1.5e300", "-1.5E+300")]
    public void PlainLargeFiniteNumbers_StayNumbers(string value, string expectedJson)
    {
        var body = BindBody(StepWithValue("finite", value));

        Assert.Equal("{\"v\":" + expectedJson + "}", body);
    }

    /// <summary>
    /// A caller that skips Validate cannot emit a refused body as a request with no body:
    /// Emit refuses the model with the same message.
    /// </summary>
    [Fact]
    public void Emit_RefusedBodyModel_Throws_NeverEmitsWithoutTheBody()
    {
        var model = BindModel(StepWithValue("emit-refused", "!!bool yes"));
        Assert.NotNull(model.BodyRefusal);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new HttpRestProvider().Emit(model, new StubCtx("emit-refused")));
        Assert.Equal(model.BodyRefusal, ex.Message);
    }

    /// <summary>
    /// A quoted scalar reached through an alias stays a string, and a plain one keeps its type.
    /// </summary>
    [Fact]
    public void Unchanged_AliasedScalarsKeepTheirAnchorStyle()
    {
        var body = BindBody("""
            anchors:
              quoted: &q "123"
              plain: &p 123
            steps:
              - id: aliases
                type: http.rest
                target: svc
                method: POST
                path: /x
                body:
                  a: *q
                  b: *p
            """);

        Assert.Equal("{\"a\":\"123\",\"b\":123}", body);
    }

    /// <summary>
    /// A body written as one scalar — including a block scalar — is still sent as its text,
    /// byte for byte; none of the structured-body rules apply to it.
    /// </summary>
    [Fact]
    public void Unchanged_ScalarBodies_AreSentAsWritten()
    {
        Assert.Equal("123", BindModel(StepWithBody("body: 123")).Body);
        Assert.Equal(string.Empty, BindModel(StepWithBody("body:")).Body);
        Assert.Equal(
            "{\"id\": \"{orderId}\"}\n",
            BindModel(StepWithBody("body: |\n      {\"id\": \"{orderId}\"}")).Body);
    }

    /// <summary>A top-level sequence body still converts, with typed items.</summary>
    [Fact]
    public void Unchanged_TopLevelSequenceBody()
    {
        Assert.Equal(
            "[1,\"two\",true,null,{\"k\":\"v\"}]",
            BindModel(StepWithBody("body: [1, two, true, null, { k: v }]")).Body);
    }

    // ── 6. Placeholders and secrets survive into the emitted template ─────────

    /// <summary>
    /// A <c>{placeholder}</c> and a <c>${secret:...}</c> reference inside a block scalar and
    /// inside a merged value survive verbatim into the bound template and the emitted step,
    /// where the helper resolves them at run time, exactly as for any other body string.
    /// </summary>
    [Fact]
    public void Placeholders_InBlockScalarAndMergedValue_ReachTheEmittedTemplate()
    {
        var model = BindModel("""
            anchors:
              auth: &auth
                token: "${secret:env/API_TOKEN}"
                orderId: "{orderId}"
            steps:
              - id: templated
                type: http.rest
                target: svc
                method: POST
                path: /orders
                body:
                  <<: *auth
                  note: |
                    order {orderId}
                  done: true
            """);

        Assert.Equal(
            "{\"token\":\"${secret:env/API_TOKEN}\",\"orderId\":\"{orderId}\",\"note\":\"order {orderId}\\n\",\"done\":true}",
            model.Body);

        var fragment = new HttpRestProvider().Emit(model, new StubCtx("templated"));

        Assert.Contains("${secret:env/API_TOKEN}", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("order {orderId}", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, bodyTemplate)",
            string.Join("\n", fragment.RequiredHelpers),
            StringComparison.Ordinal);
    }

    // ── 7. Agreement with schema validation (the engine's first door) ──────────

    /// <summary>
    /// A suite using every new rule passes schema validation, and the engine's own parser
    /// hands the binder a step that converts as the rows above say. This is the shape the
    /// engine sees: the step mapping retained by <see cref="YamlDocumentParser"/>, with
    /// anchors defined in an earlier step.
    /// </summary>
    [Fact]
    public void Engine_ValidSuite_ValidatesParsesAndBinds()
    {
        const string yaml = """
            environment:
              services:
                svc:
                  image: example/app:1.0
            steps:
              - id: seed
                type: http.rest
                target: svc
                method: POST
                path: /base
                body: &base
                  currency: EUR
                  amount: 10
              - id: pay
                type: http.rest
                target: svc
                method: POST
                path: /payments
                body:
                  <<: *base
                  amount: !!int "20"
                  reference: !!str 00123
                  memo: |
                    paid
                  note:
            """;

        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));

        var document = YamlDocumentParser.Parse(yaml);
        var model = new HttpRestProvider().Bind(document.Steps[1].RawNode, new StubBindingContext());

        Assert.Equal(
            "{\"currency\":\"EUR\",\"amount\":20,\"reference\":\"00123\",\"memo\":\"paid\\n\",\"note\":null}",
            model.Body);
    }

    /// <summary>
    /// The worked example in the DSL specification (section 5.1, "How a structured body becomes
    /// JSON") validates and sends exactly the bytes the specification prints.
    /// </summary>
    [Fact]
    public void Engine_DslSpecificationExample_SendsTheDocumentedBytes()
    {
        const string yaml = """
            environment:
              services:
                payments-api:
                  image: example/payments:1.0
            steps:
            - id: seed
              type: http.rest
              target: payments-api
              method: POST
              path: /defaults
              body: &payment
                currency: EUR
                amount: 10
                channel: web
            - id: pay
              type: http.rest
              target: payments-api
              method: POST
              path: /payments
              body:
                <<: *payment         # sends currency and channel here; amount is overridden below
                amount: !!int "20"   # the number 20
                reference: "00123"   # the string "00123"
                memo: |
                  Paid by {payer}
                note:                # null
            """;

        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));

        var document = YamlDocumentParser.Parse(yaml);
        var model = new HttpRestProvider().Bind(document.Steps[1].RawNode, new StubBindingContext());

        Assert.Equal(
            "{\"currency\":\"EUR\",\"channel\":\"web\",\"amount\":20,\"reference\":\"00123\",\"memo\":\"Paid by {payer}\\n\",\"note\":null}",
            model.Body);
    }

    /// <summary>
    /// An unknown tag on a value that is not a number or a boolean is refused by BOTH layers:
    /// schema validation already refused it as an unresolved tag before ZEB-67, and the binder
    /// now refuses it too instead of ignoring it.
    /// </summary>
    [Fact]
    public void Engine_UnknownTag_RefusedBySchemaAndBinder()
    {
        var yaml = ValidSuiteWithValue("!money five");

        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Message.Contains("unresolved tag '!money'", StringComparison.Ordinal));

        RefuseBody(yaml);
    }

    /// <summary>
    /// The schema bridge types a PLAIN number or boolean before it looks at the tag, so an
    /// unknown tag on one (<c>!money 5</c>) passes schema validation, where before ZEB-67 the
    /// body silently sent 5. The binder now refuses it, so the author gets an authoring
    /// refusal naming the tag rather than a body that ignores it.
    /// </summary>
    [Fact]
    public void Engine_UnknownTagOnANumber_PassesTheBridge_BinderRefuses()
    {
        var yaml = ValidSuiteWithValue("!money 5");

        Assert.True(DocumentValidator.Validate(yaml, s_registry).IsValid);
        var refusal = RefuseBody(yaml);
        Assert.Contains("uses the YAML tag '!money'", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>!!null</c> is refused by schema validation anywhere in a suite (an existing limit of
    /// the YAML-to-JSON bridge, unchanged here), so on the engine path the binder's
    /// <c>!!null</c> rule is reached only by callers that bind without validating. Pinned so a
    /// change to either layer is a deliberate one.
    /// </summary>
    [Fact]
    public void Engine_NullTag_RefusedBySchemaValidation()
    {
        var yaml = ValidSuiteWithValue("!!null ~");

        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Message.Contains("unresolved tag 'tag:yaml.org,2002:null'", StringComparison.Ordinal));

        Assert.Equal("{\"v\":null}", BindBody(yaml));
    }

    /// <summary>
    /// Where the schema bridge is more lenient than YAML's core schema (it reads
    /// <c>!!bool yes</c> as true), the binder is the stricter layer and refuses: the author gets
    /// an authoring refusal, never a body the two layers read differently.
    /// </summary>
    [Fact]
    public void Engine_BridgeLenientTag_BinderRefuses()
    {
        var yaml = ValidSuiteWithValue("!!bool yes");

        Assert.True(DocumentValidator.Validate(yaml, s_registry).IsValid);
        RefuseBody(yaml);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>A one-step document whose body is <c>{ v: VALUE }</c>; VALUE starts at line 8, column 10.</summary>
    private static string StepWithValue(string id, string value) =>
        "steps:\n"
        + "  - id: " + id + "\n"
        + "    type: http.rest\n"
        + "    target: svc\n"
        + "    method: POST\n"
        + "    path: /x\n"
        + "    body:\n"
        + "      v: " + value + "\n";

    private static string StepWithBody(string bodyLine) =>
        "steps:\n"
        + "  - id: scalar-body\n"
        + "    type: http.rest\n"
        + "    target: svc\n"
        + "    method: POST\n"
        + "    path: /x\n"
        + "    " + bodyLine + "\n";

    private static string ValidSuiteWithValue(string value) =>
        "environment:\n  services:\n    svc:\n      image: example/app:1.0\n" + StepWithValue("agree", value);

    /// <summary>Binds the LAST step of a document and returns its body.</summary>
    private static string BindBody(string yaml)
    {
        var model = BindModel(yaml);
        Assert.Null(model.BodyRefusal);
        return model.Body!;
    }

    /// <summary>
    /// Binds the LAST step of a document, which must be refused, and returns the refusal.
    /// Bind itself does not throw (it returned a model to get here), sends no body, and
    /// Validate reports exactly that refusal, so Emit is never reached on the engine path.
    /// </summary>
    private static string RefuseBody(string yaml)
    {
        var model = BindModel(yaml);
        Assert.Null(model.Body);
        var refusal = Assert.IsType<string>(model.BodyRefusal);

        var validation = new HttpRestProvider().Validate(model, new SvcProjectContext());
        Assert.False(validation.IsValid);
        Assert.Equal(new[] { refusal }, validation.Errors);
        return refusal;
    }

    /// <summary>A project context declaring the one service every row here targets.</summary>
    private sealed class SvcProjectContext : IProjectContext
    {
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
            {
                ["svc"] = new DeclaredServiceInfo(Array.Empty<string>()),
            };
    }

    /// <summary>
    /// Loads a whole document, so anchors may be defined outside the step, and binds its last
    /// step's mapping exactly as the engine hands it to the provider.
    /// </summary>
    private static HttpRestModel BindModel(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var steps = (YamlSequenceNode)root.Children[new YamlScalarNode("steps")];
        var step = (YamlMappingNode)steps.Children[steps.Children.Count - 1];
        return new HttpRestProvider().Bind(step, new StubBindingContext());
    }

    private static void AssertAscii(string message)
    {
        foreach (var ch in message)
            Assert.True(ch is >= ' ' and <= '~', $"non-ASCII or control character U+{(int)ch:X4} in: {message}");
    }
}
