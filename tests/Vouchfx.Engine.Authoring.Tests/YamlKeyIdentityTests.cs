// Vouchfx.Engine.Authoring.Tests — #417: the parser and the schema validator must agree
// about which keys a document has.

using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// #417: the engine reads every <c>.e2e.yaml</c> through two YAML front-ends — this parser
/// (YamlDotNet's <c>RepresentationModel</c>) and the schema validator (YamlDotNet's
/// deserialiser). A <c>RepresentationModel</c> key lookup used to compare a scalar's TAG as
/// well as its value; the deserialiser never has. These rows pin that they now agree.
/// </summary>
/// <remarks>
/// The exposure was never a live defect — <c>UnbuiltDocument.Assure</c> calls the schema door
/// unconditionally, so a tagged-key document was still caught. It was that the safety depended
/// on NOT applying an optimisation that looks obviously correct: "the parser found no
/// <c>environment</c>, so the schema cannot report an error under <c>/environment</c>". With
/// that skip applied the CLI suite passed 513/513, blind to it. These rows make the reasoning
/// itself true, so the optimisation is no longer a trap.
/// </remarks>
public sealed class YamlKeyIdentityTests
{
    private const string PlainKey = """
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: noop
            type: script.csharp
            code: "// Filler step."
        """;

    /// <summary>
    /// The #417 row. <c>!!str environment:</c> is legal YAML carrying the explicit tag
    /// <c>tag:yaml.org,2002:str</c>, while a plain <c>environment</c> carries the non-specific
    /// tag <c>?</c> — and both resolve to the same string key, so the two documents must bind
    /// identically. Red before the fix: the tagged spelling bound <c>Environment</c> as null.
    /// </summary>
    [Fact]
    public void Parse_ExplicitlyTaggedTopLevelKey_BindsTheSameAsThePlainSpelling()
    {
        var tagged = PlainKey.Replace(
            "environment:", "!!str environment:", StringComparison.Ordinal);

        var plainDocument = YamlDocumentParser.Parse(PlainKey);
        var taggedDocument = YamlDocumentParser.Parse(tagged);

        Assert.NotNull(plainDocument.Environment);
        Assert.NotNull(taggedDocument.Environment);

        // Same key means the same binding, not merely a non-null one.
        Assert.Equal(
            plainDocument.Environment!.Services?.Count,
            taggedDocument.Environment!.Services?.Count);
        Assert.True(taggedDocument.Environment.Services!.ContainsKey("api"));
    }

    /// <summary>
    /// A tagged key NESTED inside the document, not merely at the root — the fix is in the one
    /// shared lookup, so it must hold at every depth that lookup serves.
    /// </summary>
    [Fact]
    public void Parse_ExplicitlyTaggedNestedKey_BindsTheSameAsThePlainSpelling()
    {
        var tagged = PlainKey.Replace(
            "  services:", "  !!str services:", StringComparison.Ordinal);

        var document = YamlDocumentParser.Parse(tagged);

        Assert.NotNull(document.Environment);
        Assert.True(document.Environment!.Services!.ContainsKey("api"));
    }

    /// <summary>
    /// The second shape #417 names, in the OPPOSITE direction: a duplicate <c>environment:</c>
    /// key. Here the parser is STRICTER than the deserialiser — it throws rather than binding
    /// one of the two — and that asymmetry is safe and deliberate, so it is pinned rather than
    /// removed: a throwing parse produces no document at all, so nothing downstream can reason
    /// from a half-read one. Recorded because the issue asks for a row either way.
    /// </summary>
    /// <remarks>
    /// The refusal on THIS spelling is YamlDotNet's own — its loader compares key nodes and both
    /// of these are identical — so the message asserted below is the loader's, reached through
    /// <c>Parse</c>'s wrapping catch. The next row covers the spelling the loader lets past.
    /// </remarks>
    [Fact]
    public void Parse_DuplicateTopLevelKey_IsRefusedOutrightRatherThanBindingOne()
    {
        var duplicate = PlainKey + "\nenvironment:\n  services: {}\n";

        var error = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(duplicate));

        Assert.Contains("Duplicate key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row #417 was missing, and the reason its first round did not close the divergence it
    /// named: two <c>environment</c> keys differing ONLY by tag. YamlDotNet's own duplicate check
    /// is scalar-node equality, and that includes the tag, so it loads this document without a
    /// word — measured on the pinned version, <c>"environment":</c> and <c>'environment':</c>
    /// beside a plain one ARE both refused by the loader, leaving an explicit <c>!!str</c> as the
    /// one spelling that gets through.
    /// </summary>
    /// <remarks>
    /// Measured on the build before <c>RequireUniqueMappingKeys</c>, over exactly this document:
    /// <c>Parse</c> returned a document binding the FIRST occurrence (<c>services: [tagged]</c>),
    /// while <c>DocumentValidator.Validate</c> reported <c>[additionalProperties] Unknown property
    /// 'totallyBogusKey' on service 'plain'</c> — an error inside the SECOND, which the parser
    /// never saw. The parser scans forward and takes the first match; the validator's front-end is
    /// last-wins. Refusing the document is what makes the two agree, and it is why the fix is a
    /// refusal rather than a tie-break: choosing a winner would have moved the disagreement to
    /// which winner, not ended it.
    /// </remarks>
    [Fact]
    public void Parse_TagDistinguishedDuplicateTopLevelKey_IsRefusedRatherThanBindingTheFirst()
    {
        const string TagDistinguishedDuplicate = """
            !!str environment:
              services: { tagged: { image: myorg/tagged:1.0 } }
            environment:
              services: { plain: { image: myorg/plain:1.0, totallyBogusKey: 1 } }
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var error = Assert.Throws<YamlParseException>(
            () => YamlDocumentParser.Parse(TagDistinguishedDuplicate));

        Assert.Contains("Duplicate key 'environment'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same shape NESTED, on a key the parser never looks up by name: two service names under
    /// <c>services</c>, differing only by tag. This is pinned separately because it fixes the
    /// SCOPE of the guard — the walk covers every mapping in the document, not only the keys
    /// <c>TryGetNode</c> is asked for. Narrowing it to the lookup would leave this one binding
    /// silently (measured before the guard: the parser kept the LAST <c>api</c>, dropping the
    /// other without a diagnostic).
    /// </summary>
    [Fact]
    public void Parse_TagDistinguishedDuplicateNestedKey_IsRefusedToo()
    {
        const string DuplicateServiceName = """
            environment:
              services:
                !!str api: { image: myorg/tagged-api:1.0 }
                api: { image: myorg/plain-api:1.0 }
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var error = Assert.Throws<YamlParseException>(
            () => YamlDocumentParser.Parse(DuplicateServiceName));

        Assert.Contains("Duplicate key 'api'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and it is what makes the rows above mean something: the shapes #417 measured
    /// as ALREADY agreeing must keep agreeing. A scalar, sequence or null <c>environment:</c>
    /// binds no <c>EnvironmentSpec</c> — two segments short of a security match — and that is
    /// unchanged by comparing keys on value.
    /// </summary>
    [Theory]
    [InlineData("environment: mtls")]
    [InlineData("environment:\n  - one\n  - two")]
    [InlineData("environment:")]
    public void Parse_NonMappingEnvironment_StillBindsNoEnvironment(string environmentBlock)
    {
        var yaml = environmentBlock + """

            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        Assert.Null(YamlDocumentParser.Parse(yaml).Environment);
    }
}
