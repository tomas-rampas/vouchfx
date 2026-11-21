// http.rest structured request bodies: the secret-reference check, substitution provenance and
// the reproducibility envelope read each JSON string of the body on its own, exactly as the
// run-time resolver does (non-docker).
//
// The census used to read the serialised JSON text of the body. That text carries JSON escapes
// (`+` became a six-character escape), and the JSON between two strings could complete a
// reference that no single string holds. Those rows pin the per-string reading: a reference is
// checked, listed and hashed with the text the author wrote, equal to the same reference in a
// header, and text that is a reference only across two strings is neither refused, listed nor
// hashed. The run-time half of the contract is pinned in Vouchfx.Engine.Compilation.Tests,
// HttpRestStructuredBodyReferenceTests.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Text that is a reference only once a key and its value, two sequence items or two
    /// values are read together is not refused by the check (even naming an unknown source)
    /// and is neither listed in provenance nor hashed into the envelope: it is never resolved.
    /// </summary>
    [Theory]
    [InlineData("body:\n  'a${secret:nosuchsource/': 'X}'\n")]
    [InlineData("body:\n  - '${secret:nosuchsource/'\n  - 'X}'\n")]
    [InlineData("body:\n  a: '${secret:env/'\n  b: 'X}'\n")]
    [InlineData("body:\n  a: 'p${secret:env/'\n  b: { c: 'X}' }\n")]
    public void ReferenceJoinedAcrossStrings_IsNotRefusedListedOrHashed(string yaml)
    {
        var node = RestNode(yaml);

        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _), diagnostic);
        Assert.Null(ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>()));
        Assert.Empty(ScenarioRunner.BuildReproducibilityEnvelope(Ast(node), null).SecretReferences);
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
