// http.rest structured request bodies: the secret-reference check, substitution provenance and
// the reproducibility envelope read each JSON string of the body on its own, exactly as the
// run-time resolver does (non-docker).
//
// The census used to read the serialised JSON text of the body. That text carries JSON escapes
// (`+` became a six-character escape), and the JSON between two strings could complete a
// reference that no single string holds. Those rows pin the per-string reading: a reference is
// checked, listed and hashed with the text the author wrote, equal to the same reference in a
// header, and each string is checked exactly as a header value is, so a `${secret:` with no
// closing `}` in any string is refused before the run, even when a later string would have
// completed it. The run-time half of the contract is pinned in Vouchfx.Engine.Compilation.Tests,
// HttpRestStructuredBodyReferenceTests.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;
using static Vouchfx.Engine.Runtime.Tests.SecretTemplateEmissionCensusTests;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class HttpRestStructuredBodyCensusTests
{
    /// <summary>
    /// A reference in a structured body is hashed, listed and checked with the text the author
    /// wrote, so its envelope hash equals the same reference written in a header.
    /// </summary>
    [Theory]
    [InlineData("${secret:vault/kv/a+b#f}")]
    [InlineData("${secret:vault/kv/a<b>&c#f}")]
    [InlineData("${secret:vault/kv/café#f}")]
    [InlineData("${secret:vault/kv/a\"b#f}")]
    [InlineData("${secret:vault/kv/a\\b#f}")]
    public void BodyReference_HashesAndListsLikeTheSameHeaderReference(string reference)
    {
        var quoted = "'" + reference.Replace("'", "''", StringComparison.Ordinal) + "'";
        var inBody = RestNode("body:\n  token: " + quoted + "\n");
        var inHeader = RestNode("headers:\n  Authorization: " + quoted + "\n");

        var bodyHashes = ScenarioRunner.BuildReproducibilityEnvelope(Ast(inBody), null).SecretReferences;
        var headerHashes = ScenarioRunner.BuildReproducibilityEnvelope(Ast(inHeader), null).SecretReferences;
        Assert.Single(bodyHashes);
        Assert.Equal(JsonSerializer.Serialize(headerHashes), JsonSerializer.Serialize(bodyHashes));
        Assert.Contains(ReferenceHash(reference), JsonSerializer.Serialize(bodyHashes), StringComparison.Ordinal);

        var label = reference["${secret:".Length..^1];
        var provenance = ScenarioRunner.DeriveSubstitutionProvenance(inBody, new Dictionary<string, string>())!;
        Assert.Equal(label, Assert.Single(provenance).Placeholder);

        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(inBody), out var diagnostic, out _), diagnostic);
    }

    /// <summary>
    /// A placeholder in a structured body key is listed by name, like one in a value.
    /// </summary>
    [Fact]
    public void BodyPlaceholderInKeyAndValue_IsListedOnce()
    {
        var node = RestNode("body:\n  '{tenant}': '{tenant}'\n");
        var provenance = ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>())!;
        var only = Assert.Single(provenance);
        Assert.Equal("tenant", only.Placeholder);
        Assert.False(only.SecretDerived);
    }

    /// <summary>
    /// A <c>${secret:</c> with no closing <c>}</c> in any string of a structured body (a member
    /// name or a string value, at any depth, wherever it stands) is a malformed reference and
    /// is refused before the run, exactly as in a header. Nothing a later string holds changes
    /// that: text that would read as a reference only once two strings are joined is refused
    /// too, because its first string holds the unclosed reference. The diagnostic quotes that
    /// whole string as the author wrote it.
    /// </summary>
    [Theory]
    [InlineData("body:\n  token: '${secret:env/T'\n  b: y\n", "${secret:env/T")]
    [InlineData("body:\n  token: '${secret:env/T'\n", "${secret:env/T")]
    [InlineData("body:\n  a: x\n  token: '${secret:env/T'\n", "${secret:env/T")]
    [InlineData("body:\n  - x\n  - '${secret:env/T'\n", "${secret:env/T")]
    [InlineData("body:\n  outer:\n    token: '${secret:env/T'\n", "${secret:env/T")]
    [InlineData("body:\n  v: '${secret:env/A} ${secret:env/B'\n", "${secret:env/A} ${secret:env/B")]
    [InlineData("body:\n  v: '${secret:nosuchsource/A} ${secret:'\n", "${secret:nosuchsource/A} ${secret:")]
    // Joined forms: a later string would complete the reference if the two were read together.
    [InlineData("body:\n  'a${secret:env/': 'X}'\n", "a${secret:env/")]
    [InlineData("body:\n  - '${secret:env/'\n  - 'X}'\n", "${secret:env/")]
    [InlineData("body:\n  a: '${secret:env/'\n  b: 'X}'\n", "${secret:env/")]
    [InlineData("body:\n  'a${secret:env/': {}\n  b: 'X}'\n", "a${secret:env/")]
    [InlineData("body:\n  a: 'p${secret:env/'\n  b: { c: 'X}' }\n", "p${secret:env/")]
    [InlineData("body:\n  'a${secret:env/': { 'X}': 1 }\n", "a${secret:env/")]
    [InlineData("body:\n  'a${secret:nosuchsource/': 'X}'\n", "a${secret:nosuchsource/")]
    [InlineData("body:\n  token: '${secret:'\n  b: 'x}'\n", "${secret:")]
    [InlineData("body:\n  token: '${secret:env'\n  b: 'x}'\n", "${secret:env")]
    [InlineData("body:\n  token: '${secret:API_TOKEN'\n  b: '{\"a\":1}'\n", "${secret:API_TOKEN")]
    [InlineData("body:\n  v: '${secret: ${secret:env/A'\n  b: 'x}'\n", "${secret: ${secret:env/A")]
    [InlineData("body:\n  v: '${secret:env/A} ${secret:env/B'\n  w: 'X}'\n", "${secret:env/A} ${secret:env/B")]
    [InlineData("body:\n  '${secret:bad} a${secret:env/': 'X}'\n", "${secret:bad} a${secret:env/")]
    [InlineData("body:\n  '${secret:nosuchsource/K} a${secret:env/': 'X}'\n", "${secret:nosuchsource/K} a${secret:env/")]
    // An unclosed reference beside a well-formed one in a later string.
    [InlineData("body:\n  a: '${secret:env/T'\n  b: '${secret:env/U}'\n", "${secret:env/T")]
    [InlineData("body:\n  user: '${secret:vault/db#user'\n  password: '${secret:vault/db#pass}'\n", "${secret:vault/db#user")]
    [InlineData("body:\n  token: 'Bearer ${secret:env/TOKEN'\n  callback: 'https://x/{id}'\n", "Bearer ${secret:env/TOKEN")]
    [InlineData("body:\n  - '${secret:'\n  - '}'\n", "${secret:")]
    [InlineData("body:\n  - '${secret:nosuch/A'\n  - '}'\n", "${secret:nosuch/A")]
    public void UnclosedReferenceInAnyBodyString_IsRefusedAsMalformed_QuotingTheString(string yaml, string written)
    {
        var node = RestNode(yaml);

        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _));
        Assert.Contains("the field '" + written + "' contains a malformed secret reference", diagnostic, StringComparison.Ordinal);
        Assert.Contains("(field 'body')", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text that would read as a reference only across two strings is never one: besides being
    /// refused, it is neither listed in substitution provenance nor hashed into the envelope.
    /// </summary>
    [Theory]
    [InlineData("body:\n  'a${secret:env/': 'X}'\n")]
    [InlineData("body:\n  - '${secret:env/'\n  - 'X}'\n")]
    [InlineData("body:\n  a: '${secret:env/'\n  b: 'X}'\n")]
    [InlineData("body:\n  'a${secret:nosuchsource/': { 'X}': 1 }\n")]
    public void ReferenceJoinedAcrossStrings_IsNeitherListedNorHashed(string yaml)
    {
        var node = RestNode(yaml);

        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(node), out _, out _));
        Assert.Null(ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>()));
        Assert.Empty(ScenarioRunner.BuildReproducibilityEnvelope(Ast(node), null).SecretReferences);
    }

    /// <summary>
    /// The same unclosed text is refused in a header and in a structured body with the same
    /// diagnostic, apart from the field it names, including when a later body string would
    /// have completed it. The header rule refuses every one of these forms.
    /// </summary>
    [Theory]
    [InlineData("${secret:env/T")]
    [InlineData("a${secret:env/")]
    [InlineData("${secret:")]
    [InlineData("${secret:env")]
    [InlineData("${secret: ${secret:env/A")]
    [InlineData("Bearer ${secret:env/TOKEN")]
    [InlineData("${secret:env/A} ${secret:env/B")]
    [InlineData("${secret:nosuchsource/A} ${secret:")]
    public void UnclosedReference_IsRefusedAlikeInAHeaderAndInABody(string written)
    {
        var inBody = RestNode("body:\n  token: '" + written + "'\n  b: 'X}'\n");
        var inHeader = RestNode("headers:\n  Authorization: '" + written + "'\n");

        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(inBody), out var bodyDiagnostic, out _));
        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(inHeader), out var headerDiagnostic, out _));
        Assert.Contains("the field '" + written + "' contains a malformed secret reference", headerDiagnostic, StringComparison.Ordinal);
        Assert.Equal(
            headerDiagnostic!.Replace("(field 'headers[*]')", "(field 'body')", StringComparison.Ordinal),
            bodyDiagnostic);
    }

    /// <summary>
    /// A closed reference beside a <c>{placeholder}</c>, in a member name, or nested, is
    /// accepted and listed, as in a header.
    /// </summary>
    [Fact]
    public void ClosedReferences_AnywhereInTheBody_AreAcceptedAndListed()
    {
        var node = RestNode("body:\n  '${secret:env/K}': 'x {p} ${secret:env/A}'\n  outer: [{ t: '${secret:vault/db#pass}' }]\n");

        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _), diagnostic);
        var listed = ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>())!
            .Select(p => p.Placeholder)
            .ToArray();
        Assert.Equal(s_closedReferenceLabels, listed);
    }

    private static readonly string[] s_closedReferenceLabels = { "env/K", "p", "env/A", "vault/db#pass" };

    /// <summary>
    /// End to end through <see cref="ScenarioRunner.RunAsync"/>: a body string holding an
    /// unclosed reference refuses the run before any topology starts, so nothing is sent. The
    /// second row is joined text that a later string would have completed.
    /// </summary>
    /// <remarks>
    /// The image is pinned so that the secret-reference refusal is the only one. The runner
    /// prints no event of its own when it starts a topology, so "nothing started" is pinned by
    /// the whole rendered output: the refusal, the scenario's opening line and its summary, and
    /// nothing else. An acquisition would add a line, either the environment fault of a failed
    /// start or what a started topology reports, and a step that ran would be counted.
    /// </remarks>
    [Theory]
    [InlineData("      token: '${secret:env/T'\n      b: y\n", "${secret:env/T")]
    [InlineData("      'a${secret:env/': 'X}'\n", "a${secret:env/")]
    public async Task RunAsync_UnclosedReferenceInBody_IsRefusedBeforeTheRun(string bodyMembers, string written)
    {
        const string head = """
            environment:
              services:
                whoami:
                  image: traefik/whoami:v1.11.0
            steps:
              - id: post-unclosed-secret
                type: http.rest
                target: whoami
                method: POST
                path: /
                body:
            """;
        var yaml = head + "\n" + bodyMembers;
        var output = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "unclosed-secret-in-body",
            providerAssemblies: new[] { typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly },
            appHostAssemblyName: "Vouchfx.Engine.Runtime.Tests",
            output: output);

        Assert.Equal(Verdict.Inconclusive, verdict);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Collection(
            lines,
            refusal =>
            {
                Assert.StartsWith("step 'post-unclosed-secret': the field '" + written + "' contains a malformed secret reference", refusal, StringComparison.Ordinal);
                Assert.EndsWith("(field 'body')", refusal, StringComparison.Ordinal);
            },
            started => Assert.Equal("Scenario 'unclosed-secret-in-body' started", started),
            summary => Assert.StartsWith(
                "Scenario 'unclosed-secret-in-body': INCONCLUSIVE  (pass=0 fail=0 envError=0 inconclusive=1 ",
                summary,
                StringComparison.Ordinal));
        Assert.DoesNotContain("step-completed", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed or unknown-source reference closed inside one body string is still refused,
    /// naming the body field, exactly as it would be in a header.
    /// </summary>
    [Theory]
    [InlineData("body:\n  t: '${secret:nopath}'\n", "malformed")]
    [InlineData("body:\n  t: 'x ${secret:nosuchsource/A} y'\n", "nosuchsource")]
    [InlineData("body:\n  '${secret:nosuchsource/K}': v\n", "nosuchsource")]
    [InlineData("body:\n  - 1\n  - ['${secret:env/}']\n", "malformed")]
    public void ReferenceClosedInsideOneString_IsStillChecked(string yaml, string expected)
    {
        var node = RestNode(yaml);

        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _));
        Assert.Contains(expected, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(field 'body')", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>A raw-text body is still read as one text, joined references included.</summary>
    [Fact]
    public void RawTextBody_IsStillReadAsOneText()
    {
        var node = RestNode("body: '{\"a${secret:env/\":\"X}\"}'\n");
        var provenance = ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>())!;
        Assert.Equal("env/\":\"X", Assert.Single(provenance).Placeholder);
    }

    /// <summary>
    /// TRIPWIRE: the conversion decides which body strings are resolved at run time with a
    /// copy of the resolver's own pattern; a string the copy missed would be sent unresolved.
    /// The pattern is read out of the frozen <see cref="SecretHelper.Source"/>.
    /// </summary>
    [Fact]
    public void ConversionReferencePattern_EqualsSecretHelpersCombinedPattern()
    {
        const string patternStart = "@\"(?<secret>";
        var source = SecretHelper.Source;
        var start = source.IndexOf(patternStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "SecretHelper.Source no longer carries the combined pattern this test extracts.");
        var end = source.IndexOf('"', start + 2);
        Assert.True(end > start, "The combined pattern literal in SecretHelper.Source is not terminated.");

        Assert.Equal(Vouchfx.Steps.HttpRest.HttpRestBodyConversion.ReferencePattern, source.Substring(start + 2, end - start - 2));
    }

    /// <summary>
    /// The census reads every string of a structured body, keys included, and no text the
    /// body's JSON structure supplies.
    /// </summary>
    [Fact]
    public void StructuredBody_IsCollectedOneStringAtATime()
    {
        var node = RestNode("body:\n  a: 'x+y'\n  'k{p}': [1, '', 'z', null, true]\n");
        var texts = StepTemplateCensus.Collect(node).Fields.Where(f => f.Location == "body").Select(f => f.Text);
        Assert.Equal(s_collectedStrings, texts);
    }

    private static readonly string[] s_collectedStrings = { "a", "x+y", "k{p}", "z" };

    /// <summary>An http.rest census node whose raw mapping is <paramref name="yaml"/> plus a fixed target, method and path.</summary>
    private static StepNode RestNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var raw = (YamlMappingNode)stream.Documents[0].RootNode;
        raw.Add("target", "fixture"); raw.Add("method", "POST"); raw.Add("path", "/");
        return CreateFixture("http.rest").Node with { RawNode = raw };
    }

    private static string ReferenceHash(string reference) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant();

    private static ScenarioAst Ast(StepNode node) => new(null, null, new Dictionary<string, string>(), new[] { node });
}
