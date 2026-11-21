// http.rest structured request bodies: references are resolved per JSON string (non-docker).
//
// A structured `body:` (a YAML mapping or sequence) is converted to JSON at bind time. The
// references in it used to be resolved over the serialised JSON text, which went wrong in
// three ways:
//
//   1. The serialiser had already JSON-escaped the reference, so a path holding `+`, `<`,
//      `>`, `&`, a quote, a backslash or a non-ASCII character was requested with its
//      six-character escape in place of the character.
//   2. JSON text between two strings could complete a reference that neither string holds:
//      a key and its value, or two sequence items, were read as one reference.
//   3. A resolved value was spliced into the JSON unescaped, so a quote, a backslash or a
//      newline in it broke the body or changed its structure.
//
// Each row goes through the real path without Docker: the YAML is bound by the provider,
// emitted, assembled, compiled once and run against an in-process responder that records the
// body it receives, with the engine's own secret accessor and resolvers behind it.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Secrets.Vault;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// End-to-end rows (bind, emit, compile, run) for references inside an <c>http.rest</c>
/// structured request body.
/// </summary>
public sealed class HttpRestStructuredBodyReferenceTests
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

    /// <summary>A Vault KV transport that records every path read and serves fixed fields.</summary>
    private sealed class RecordingKvClient : IVaultKvClient
    {
        private readonly IReadOnlyDictionary<string, string> _fields;

        public RecordingKvClient(IReadOnlyDictionary<string, string> fields) => _fields = fields;

        public List<string> Paths { get; } = new();

        public IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath)
        {
            lock (Paths) Paths.Add(kvPath);
            return _fields;
        }
    }

    /// <summary>Records every reference the script asks for, and delegates to the real accessor.</summary>
    private sealed class RecordingAccessor : ISecretAccessor
    {
        private readonly ISecretAccessor _inner;

        public RecordingAccessor(ISecretAccessor inner) => _inner = inner;

        public List<string> References { get; } = new();

        public SecretString Resolve(string reference)
        {
            lock (References) References.Add(reference);
            return _inner.Resolve(reference);
        }
    }

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

    private static readonly string[] s_userTokenTags = { "user", "token", "tags" };

    // ── 1. A reference is read as the author wrote it ───────────────────────────

    /// <summary>
    /// A Vault reference whose path holds a character the JSON serialiser escapes requests
    /// that path, unescaped, and the field named after <c>#</c>.
    /// </summary>
    [Theory]
    [InlineData("kv/a+b")]
    [InlineData("kv/a<b>c")]
    [InlineData("kv/a&b")]
    [InlineData("kv/a'b")]
    [InlineData("kv/a\"b")]
    [InlineData("kv/a\\b")]
    [InlineData("kv/caf\u00e9")]
    public async Task VaultReferenceInStructuredBody_RequestsTheUnescapedPath(string kvPath)
    {
        var client = new RecordingKvClient(new Dictionary<string, string> { ["f"] = "vault-value" });
        var yamlPath = kvPath.Replace("'", "''", StringComparison.Ordinal);
        var run = await RunAsync(
            "body:\n  token: '${secret:vault/" + yamlPath + "#f}'\n",
            client: client);

        Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
        Assert.Equal(new[] { kvPath }, client.Paths);
        Assert.Equal(new[] { "${secret:vault/" + kvPath + "#f}" }, run.References);
        Assert.Equal("{\"token\":\"vault-value\"}", run.Received);
    }

    // ── 2. A reference never spans two strings ────────────────────────────────────

    /// <summary>
    /// Text that would read as a reference only once the JSON between two strings is added
    /// (a key and its value, two sequence items, two values) is sent literally: nothing is
    /// resolved, and the step runs.
    /// </summary>
    [Theory]
    [InlineData("body:\n  'a${secret:env/': 'X}'\n", "{\"a${secret:env/\":\"X}\"}")]
    [InlineData("body:\n  - '${secret:env/'\n  - 'X}'\n", "[\"${secret:env/\",\"X}\"]")]
    [InlineData("body:\n  a: '${secret:env/'\n  b: 'X}'\n", "{\"a\":\"${secret:env/\",\"b\":\"X}\"}")]
    public async Task ReferenceJoinedAcrossStrings_IsNotResolved_AndIsSentLiterally(string bodyYaml, string expected)
    {
        var run = await RunAsync(bodyYaml);

        Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
        Assert.Empty(run.References);
        Assert.Equal(expected, run.Received);
    }

    /// <summary>
    /// A folded scalar is ONE YAML string, so a reference inside it is an ordinary reference
    /// in one string and is resolved, and the value is escaped where it is written.
    /// </summary>
    [Fact]
    public async Task ReferenceInsideFoldedScalar_IsResolvedAsOneString()
    {
        var env = NewEnvName();
        Environment.SetEnvironmentVariable(env, "va\"lue");
        try
        {
            var run = await RunAsync("body:\n  note: >-\n    token\n    ${secret:env/" + env + "}\n");

            Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
            Assert.Equal(new[] { "${secret:env/" + env + "}" }, run.References);
            Assert.Equal("{\"note\":\"token va\\u0022lue\"}", run.Received);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    // ── 3. A resolved value is written as a JSON string ────────────────────────────

    /// <summary>
    /// A secret value carrying a quote, a backslash, a newline or text that looks like JSON
    /// structure arrives as one correctly escaped string: the body is valid JSON with the
    /// structure the author wrote, and the value reads back exactly.
    /// </summary>
    [Theory]
    [InlineData("quote\"inside")]
    [InlineData("back\\slash")]
    [InlineData("line\nbreak")]
    [InlineData("\",\"admin\":true,\"x\":\"")]
    [InlineData("tab\tand\u0001control")]
    public async Task SecretValue_IsEscapedAsAJsonString(string value)
    {
        var env = NewEnvName();
        Environment.SetEnvironmentVariable(env, value);
        try
        {
            var run = await RunAsync(
                "body:\n  user: alice\n  token: '${secret:env/" + env + "}'\n  tags: ['a', 'p ${secret:env/" + env + "} q']\n");

            Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
            var sent = JsonNode.Parse(run.Received!)!.AsObject();
            Assert.Equal(s_userTokenTags, sent.Select(p => p.Key));
            Assert.Equal("alice", (string?)sent["user"]);
            Assert.Equal(value, (string?)sent["token"]);
            var tags = sent["tags"]!.AsArray();
            Assert.Equal(2, tags.Count);
            Assert.Equal("a", (string?)tags[0]);
            Assert.Equal("p " + value + " q", (string?)tags[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    /// <summary>
    /// A <c>{placeholder}</c> value is written as a JSON string too, in a value and in a key.
    /// </summary>
    [Theory]
    [InlineData("quote\"inside")]
    [InlineData("back\\slash")]
    [InlineData("line\nbreak")]
    [InlineData("\"}],\"injected\":{\"a\":\"")]
    public async Task PlaceholderValue_IsEscapedAsAJsonString_InKeysAndValues(string value)
    {
        var run = await RunAsync(
            "body:\n  id: '{orderId}'\n  '{orderId}': key\n  n: 1\n",
            vars: new Dictionary<string, object?> { ["orderId"] = value });

        Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
        var sent = JsonNode.Parse(run.Received!)!.AsObject();
        Assert.Equal(new[] { "id", value, "n" }, sent.Select(p => p.Key));
        Assert.Equal(value, (string?)sent["id"]);
        Assert.Equal("key", (string?)sent[value]);
        Assert.Equal(1, (int)sent["n"]!);
    }

    // ── 4. Types are unaffected ─────────────────────────────────────────────────────

    /// <summary>
    /// Numbers, booleans and null keep their JSON types beside a reference, and a string that
    /// is only a reference is still a JSON string even when the value reads as a number.
    /// </summary>
    [Fact]
    public async Task TypedScalarsBesideReferences_KeepTheirTypes_AndAReferenceIsAString()
    {
        var env = NewEnvName();
        Environment.SetEnvironmentVariable(env, "123");
        try
        {
            var run = await RunAsync(
                "body:\n  n: 1.5\n  b: true\n  z: null\n  e:\n  q: '7'\n  t: '${secret:env/" + env + "}'\n  p: '{count}'\n  l: [1, '{count}', false]\n",
                vars: new Dictionary<string, object?> { ["count"] = 42 });

            Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
            Assert.Equal(
                "{\"n\":1.5,\"b\":true,\"z\":null,\"e\":null,\"q\":\"7\",\"t\":\"123\",\"p\":\"42\",\"l\":[1,\"42\",false]}",
                run.Received);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    // ── 5. Bodies with no reference are untouched ──────────────────────────────────

    /// <summary>
    /// A structured body with no reference is sent byte for byte as the bound JSON, including
    /// the characters the serialiser escapes, and its emitted call passes that JSON as before.
    /// </summary>
    [Fact]
    public async Task StructuredBodyWithoutReference_IsSentByteForByte()
    {
        const string bodyYaml = "body:\n  a: 'x+y<z>&\"q\\'\n  b: [1, 2.5, true, null, 'caf\u00e9']\n  c: { d: '} ${ {' }\n";
        var model = Bind(bodyYaml);
        var fragment = new HttpRestProvider().Emit(model, new StubCtx("plain"));
        Assert.Contains(JsonSerializer.Serialize(model.Body) + ",", fragment.StatementBlock, StringComparison.Ordinal);

        var run = await RunAsync(bodyYaml);

        Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
        Assert.Empty(run.References);
        Assert.Equal(model.Body, run.Received);
    }

    /// <summary>
    /// A raw-text body (<c>body: |</c>) keeps today's behaviour: the reference is resolved over
    /// the text the author wrote, and the value is inserted as it is.
    /// </summary>
    [Fact]
    public async Task RawTextBody_KeepsTodaysBehaviour()
    {
        var env = NewEnvName();
        Environment.SetEnvironmentVariable(env, "a\"b");
        try
        {
            var run = await RunAsync("body: |\n  {\"t\":\"${secret:env/" + env + "}\"}\n");

            Assert.Equal(Verdict.Pass, run.Outcome.Verdict);
            Assert.Equal("{\"t\":\"a\"b\"}\n", run.Received);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private sealed record Run(StepOutcome Outcome, string? Received, IReadOnlyList<string> References);

    private static string NewEnvName() => "VOUCHFX_REST_BODY_" + Guid.NewGuid().ToString("N");

    private static HttpRestModel Bind(string bodyYaml)
    {
        var yaml = "id: body-ref\ntarget: svc\nmethod: POST\npath: /orders\n" + bodyYaml;
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var model = new HttpRestProvider().Bind(root, new StubBindingContext());
        Assert.Null(model.BodyRefusal);
        return model with { Expect = new HttpExpect(Status: 200) };
    }

    private static async Task<Run> RunAsync(
        string bodyYaml,
        IReadOnlyDictionary<string, object?>? vars = null,
        IVaultKvClient? client = null)
    {
        var model = Bind(bodyYaml);
        var fragment = new HttpRestProvider().Emit(model, new StubCtx("body-ref"));
        var assembled = CsxAssembler.Assemble(new[] { ("body-ref", fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: s_refs);

        var accessor = new RecordingAccessor(new SecretAccessor(new SecretSourceCatalog(new ISecretResolver[]
        {
            new EnvironmentSecretResolver(),
            new VaultSecretResolver(client ?? new RecordingKvClient(new Dictionary<string, string>())),
        })));

        using var listener = LoopbackHttpListener.Start();
        string? received = null;
        var serve = Task.Run(async () =>
        {
            var context = await listener.Listener.GetContextAsync().ConfigureAwait(false);
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                received = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        });

        var scriptVars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service("svc")] = listener.BaseUrl,
        };
        foreach (var (key, value) in vars ?? new Dictionary<string, object?>())
            scriptVars[key] = value;

        var globals = new ScriptGlobalVariables(
            scriptVars, new Dictionary<string, object>(StringComparer.Ordinal), accessor);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcome = Assert.IsType<StepOutcome>(scriptVars[VarKeys.Outcome(CsxFragment.SanitiseId("body-ref"))]);
        if (outcome.Verdict == Verdict.Pass)
            await serve.WaitAsync(TimeSpan.FromSeconds(10));

        return new Run(outcome, received, accessor.References.ToArray());
    }
}
