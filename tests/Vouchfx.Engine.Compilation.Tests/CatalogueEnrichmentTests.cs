// #556 — the four additive StepCatalogueEntry members: tier, supportedVerifyModes, docsUrl and
// example. Docker-free.
//
// What each group proves, and why its proof has the shape it has:
//   • tier: assembly membership in the CALLER's Core set — "core" inside it, "community"
//     outside it, null when no set is passed. Never a namespace or name guess.
//   • supportedVerifyModes: exactly the VerifyMode enum as wire tokens, on every entry of BOTH
//     overloads, because it is an engine fact the caller supplies nothing towards.
//   • additivity: the Core-set overload changes no existing field, byte for byte.
//   • example: it IS SuiteScaffolder.Generate's output for the canonical intent with only the
//     provenance lines removed (so it can never show a value `vouchfx scaffold` would not
//     produce), it is schema-valid, ASCII, LF-only and version-free, its environment declares
//     exactly what its step targets under the documented names, and it is WITHHELD rather than
//     emitted when the engine cannot derive that target. The full schema + bind + provider
//     Validate proof needs ValidateCommand, so it lives in Vouchfx.Cli.Tests.ListCommandTests.
// docsUrl's parity with docs/language-reference.md and mkdocs.yml is LanguageReferenceLinkTests'.

using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation.Scaffold;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class CatalogueEnrichmentTests
{
    private const string VersionMarker = "7.7.7-version-marker";

    private static readonly string[] ExpectedVerifyModeTokens = { "IMMEDIATE", "RETRY" };

    private static readonly Assembly[] HttpRestAssemblyOnly = { typeof(HttpRestProvider).Assembly };

    private static readonly Assembly[] ThisTestAssembly = { typeof(CatalogueEnrichmentTests).Assembly };

    private static StepKindRegistry HttpRestAndCommunityRegistry() =>
        StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new HttpRestProvider(),
            new CommunitySampleProvider(),
        });

    private static StepCatalogueEntry CoreEntry(StepKindRegistry registry, string typeKey) =>
        Assert.Single(
            EngineExport.BuildCatalogue(registry, VersionMarker, SuiteScaffolderTests.CoreProviderAssemblies()).StepTypes,
            e => e.Type == typeKey);

    // ── tier ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCatalogue_WithCoreSet_TiersEachEntryByAssemblyMembership()
    {
        var catalogue = EngineExport.BuildCatalogue(HttpRestAndCommunityRegistry(), "v", HttpRestAssemblyOnly);

        var core = Assert.Single(catalogue.StepTypes, e => e.Type == "http.rest");
        Assert.Equal("core", core.Tier);
        Assert.Equal(EngineExport.CoreTier, core.Tier);
        Assert.NotNull(core.DocsUrl);
        Assert.NotNull(core.Example);

        // Declared in this test assembly, which the caller did not put in its Core set.
        var community = Assert.Single(catalogue.StepTypes, e => e.Type == "sample.community");
        Assert.Equal("community", community.Tier);
        Assert.Equal(EngineExport.CommunityTier, community.Tier);
        Assert.Null(community.DocsUrl);
        Assert.Null(community.Example);
    }

    [Fact]
    public void BuildCatalogue_WithoutCoreSet_LeavesTierDocsUrlAndExampleNull()
    {
        var catalogue = EngineExport.BuildCatalogue(HttpRestAndCommunityRegistry(), "v");

        Assert.Equal(2, catalogue.StepTypes.Count);
        Assert.All(catalogue.StepTypes, e =>
        {
            // "The caller did not say" — even for http.rest, whose assembly IS a Core one.
            Assert.Null(e.Tier);
            Assert.Null(e.DocsUrl);
            Assert.Null(e.Example);
            Assert.NotNull(e.SupportedVerifyModes);
        });
    }

    [Fact]
    public void BuildCatalogue_EmptyCoreSet_IsAStatementThatNothingIsCore()
    {
        var catalogue = EngineExport.BuildCatalogue(HttpRestAndCommunityRegistry(), "v", Array.Empty<Assembly>());

        Assert.All(catalogue.StepTypes, e =>
        {
            Assert.Equal("community", e.Tier);
            Assert.Null(e.DocsUrl);
            Assert.Null(e.Example);
        });
    }

    [Fact]
    public void BuildCatalogue_NullCoreSet_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => EngineExport.BuildCatalogue(HttpRestAndCommunityRegistry(), "v", null!));
    }

    [Fact]
    public void BuildCatalogue_WithCoreSet_StillFailsClosedOnAMissingFragment()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[] { new NoFragmentProvider() });

        var ex = Assert.Throws<CatalogueExportException>(
            () => EngineExport.BuildCatalogue(registry, "v", ThisTestAssembly));

        Assert.Equal("nofragment.enrichment", ex.StepType);
    }

    // ── supportedVerifyModes ──────────────────────────────────────────────────

    [Fact]
    public void SupportedVerifyModes_IsTheVerifyModeEnumAsWireTokens_OnEveryEntryOfBothOverloads()
    {
        // The same projection StepEventBuilder writes into step-started's verifyMode. The literal
        // pin beside it makes a new VerifyMode member a deliberate change to this test rather than
        // a silent widening of the wire.
        var fromEnum = Enum.GetValues<VerifyMode>().Select(m => m.ToString().ToUpperInvariant()).ToArray();
        Assert.Equal(ExpectedVerifyModeTokens, fromEnum);

        var registry = HttpRestAndCommunityRegistry();
        var entries = EngineExport.BuildCatalogue(registry).StepTypes
            .Concat(EngineExport.BuildCatalogue(registry, "v", HttpRestAssemblyOnly).StepTypes)
            .ToList();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(fromEnum, e.SupportedVerifyModes));
    }

    // ── additivity ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCatalogue_CoreSetOverload_ChangesNoOtherField()
    {
        var registry = SuiteScaffolderTests.FullCoreRegistry();
        var withoutSet = EngineExport.BuildCatalogue(registry, "v");
        var withSet = EngineExport.BuildCatalogue(registry, "v", SuiteScaffolderTests.CoreProviderAssemblies());

        // Blank only the three Core-set members; every other member, supportedVerifyModes
        // included, must serialise identically.
        var stripped = withSet with
        {
            StepTypes = withSet.StepTypes
                .Select(e => e with { Tier = null, DocsUrl = null, Example = null })
                .ToList(),
        };

        Assert.Equal(EngineExport.SerializeCatalogue(withoutSet), EngineExport.SerializeCatalogue(stripped));
    }

    [Fact]
    public void BuildCatalogue_WithCoreSet_IsDeterministic()
    {
        var first = EngineExport.BuildCatalogue(
            SuiteScaffolderTests.FullCoreRegistry(), "v", SuiteScaffolderTests.CoreProviderAssemblies());
        var second = EngineExport.BuildCatalogue(
            SuiteScaffolderTests.FullCoreRegistry(), "v", SuiteScaffolderTests.CoreProviderAssemblies());

        Assert.Equal(EngineExport.SerializeCatalogue(first), EngineExport.SerializeCatalogue(second));
    }

    // ── example ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SuiteScaffolderTests.AllCoreProviderTypes), MemberType = typeof(SuiteScaffolderTests))]
    public void Example_IsTheScaffoldersOwnOutput_WithOnlyTheProvenanceLinesRemoved(string typeKey)
    {
        var registry = SuiteScaffolderTests.FullCoreRegistry();
        var entry = CoreEntry(registry, typeKey);
        Assert.NotNull(entry.Example);

        const string provenanceMarker = "9.9.9-provenance-marker";
        var generated = SuiteScaffolder
            .Generate(registry, SuiteScaffolder.CatalogueExampleIntent(entry), provenanceMarker)
            .ReplaceLineEndings("\n");

        // The provenance block: comment lines, the first naming the engine version, then one
        // blank line. Everything after it must be the example, character for character.
        var lines = generated.Split('\n');
        var headerLength = lines.TakeWhile(l => l.StartsWith("# ", StringComparison.Ordinal)).Count();
        Assert.True(headerLength > 0, $"{typeKey}: the scaffold carried no provenance comment lines.");
        Assert.Contains(provenanceMarker, lines[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[headerLength]);

        Assert.Equal(string.Join("\n", lines.Skip(headerLength + 1)), entry.Example);
    }

    [Theory]
    [MemberData(nameof(SuiteScaffolderTests.AllCoreProviderTypes), MemberType = typeof(SuiteScaffolderTests))]
    public void Example_PassesTheComposedSchema(string typeKey)
    {
        var registry = SuiteScaffolderTests.FullCoreRegistry();
        var example = CoreEntry(registry, typeKey).Example!;

        var result = DocumentValidator.Validate(example, registry);

        Assert.True(
            result.IsValid,
            $"{typeKey}: catalogue example failed schema validation. Errors: "
            + string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}")));
    }

    [Theory]
    [MemberData(nameof(SuiteScaffolderTests.AllCoreProviderTypes), MemberType = typeof(SuiteScaffolderTests))]
    public void Example_IsAsciiLfTerminated_AndCarriesNoVersionOrProvenance(string typeKey)
    {
        var example = CoreEntry(SuiteScaffolderTests.FullCoreRegistry(), typeKey).Example!;

        var nonAscii = example.Where(c => c > '\u007F').Select(c => $"U+{(int)c:X4}").Distinct().ToList();
        Assert.True(nonAscii.Count == 0, $"{typeKey}: example carries non-ASCII {string.Join(", ", nonAscii)}.");
        Assert.DoesNotContain('\r', example);
        Assert.EndsWith("\n", example, StringComparison.Ordinal);

        // BuildCatalogue was handed VersionMarker as the engine version; the example must not
        // echo it, nor the scaffold's provenance header that would have carried it.
        Assert.DoesNotContain(VersionMarker, example, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine-drafted by", example, StringComparison.Ordinal);
        Assert.False(example.StartsWith('#'), $"{typeKey}: example starts with a comment line.");
    }

    [Theory]
    [MemberData(nameof(SuiteScaffolderTests.AllCoreProviderTypes), MemberType = typeof(SuiteScaffolderTests))]
    public void Example_DeclaresExactlyTheResourceItsStepTargets_UnderTheCanonicalNames(string typeKey)
    {
        var root = LoadYamlMapping(CoreEntry(SuiteScaffolderTests.FullCoreRegistry(), typeKey).Example!);

        var steps = (YamlSequenceNode)root.Children[new YamlScalarNode("steps")];
        var step = (YamlMappingNode)Assert.Single(steps.Children);
        Assert.Equal(typeKey.Replace('.', '-'), ScalarAt(step, "id"));
        Assert.Equal(typeKey, ScalarAt(step, "type"));

        var target = ScalarAt(step, "target");
        var environment = root.Children.TryGetValue(new YamlScalarNode("environment"), out var envNode)
            ? (YamlMappingNode)envNode
            : null;

        if (target is null)
        {
            // script.csharp, trace-expect.otlp, webhook-listen.http: nothing to declare.
            Assert.Null(environment);
            return;
        }

        Assert.NotNull(environment);
        var services = KeysUnder(environment!, "services");
        var dependencies = KeysUnder(environment!, "dependencies");
        Assert.Equal(1, services.Count + dependencies.Count);

        if (services.Count == 1)
        {
            Assert.Equal(SuiteScaffolder.CatalogueExampleServiceName, Assert.Single(services));
            Assert.Equal(SuiteScaffolder.CatalogueExampleServiceName, target);
            return;
        }

        // A dependency is named after its kind, and the step targets it.
        var dependencyName = Assert.Single(dependencies);
        var dependency = (YamlMappingNode)((YamlMappingNode)environment!.Children[new YamlScalarNode("dependencies")])
            .Children[new YamlScalarNode(dependencyName)];
        Assert.Equal(dependencyName, ScalarAt(dependency, "type"));
        Assert.Equal(dependencyName, target);
    }

    [Fact]
    public void Example_IsWithheld_OnlyWhenTheEngineCannotDeriveWhatTheStepTargets()
    {
        // Both providers sit in a family the scaffolder has no target rule for, and the caller
        // declares both Core. The one that requires `target` would scaffold the undeclared
        // placeholder 'scaffold-target', which a provider's own Validate rejects (measured for
        // 22 of the 25 Core types with no environment), so its example is withheld rather than
        // published broken. The one that targets nothing still gets its example: the withholding
        // keys on the underivable target, not on the unfamiliar family.
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new UnmappedTargetProvider(),
            new UnmappedNoTargetProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry, "v", ThisTestAssembly);

        var withTarget = Assert.Single(catalogue.StepTypes, e => e.Type == "unmapped.target");
        Assert.Equal("core", withTarget.Tier);
        Assert.Null(withTarget.Example);

        var withoutTarget = Assert.Single(catalogue.StepTypes, e => e.Type == "unmapped.notarget");
        Assert.Equal("core", withoutTarget.Tier);
        Assert.NotNull(withoutTarget.Example);
        Assert.DoesNotContain("environment:", withoutTarget.Example!, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static YamlMappingNode LoadYamlMapping(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)Assert.Single(stream.Documents).RootNode;
    }

    private static string? ScalarAt(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value : null;

    private static List<string> KeysUnder(YamlMappingNode environment, string key) =>
        environment.Children.TryGetValue(new YamlScalarNode(key), out var node)
            ? ((YamlMappingNode)node).Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList()
            : new List<string>();

    // ── Test-only providers ───────────────────────────────────────────────────

    /// <summary>A provider in no Core assembly: its entry is Community once a set is passed.</summary>
    [StepProvider]
    private sealed class CommunitySampleProvider : IStepProvider, IStepBinder<EnrichmentModel>
    {
        public StepKindId Kind { get; } = new("sample", "community");

        public ProviderMetadata Metadata { get; } = TestMetadata();

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["message"],
              "properties": { "message": { "type": "string" } }
            }
            """);

        public EnrichmentModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    /// <summary>Requires a <c>target</c> in a family the scaffolder has no target rule for.</summary>
    [StepProvider]
    private sealed class UnmappedTargetProvider : IStepProvider, IStepBinder<EnrichmentModel>
    {
        public StepKindId Kind { get; } = new("unmapped", "target");

        public ProviderMetadata Metadata { get; } = TestMetadata();

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["target"],
              "properties": { "target": { "type": "string" } }
            }
            """);

        public EnrichmentModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    /// <summary>Same unfamiliar family, but the step targets nothing.</summary>
    [StepProvider]
    private sealed class UnmappedNoTargetProvider : IStepProvider, IStepBinder<EnrichmentModel>
    {
        public StepKindId Kind { get; } = new("unmapped", "notarget");

        public ProviderMetadata Metadata { get; } = TestMetadata();

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["message"],
              "properties": { "message": { "type": "string" } }
            }
            """);

        public EnrichmentModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    /// <summary>No binder, so no schema fragment: the fail-closed export target.</summary>
    [StepProvider]
    private sealed class NoFragmentProvider : IStepProvider
    {
        public StepKindId Kind { get; } = new("nofragment", "enrichment");

        public ProviderMetadata Metadata { get; } = TestMetadata();
    }

    private sealed record EnrichmentModel : IStepModel;

    private static readonly string[] TestAuthors = { "test-only" };

    private static ProviderMetadata TestMetadata() => new(
        Version: "0.0.0-test",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: TestAuthors);
}
