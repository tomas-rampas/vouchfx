// Spec A — EngineExport public API: composed schema + shape-level catalogue.
// Docker-free unit tests covering REQ-003/004/005, EDGE-002/003, fail-closed metadata.

using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class CatalogueExportTests
{
    private static readonly string[] CodeFileGroup = { "code", "file" };
    private static readonly string[] AlphaBetaGroup = { "alpha", "beta" };

    // ── T1 (feat/fragment-completeness), B1-corrected: oneOf/anyOf-nested ──────
    // requirements are surfaced as TYPED GROUPS (ExactlyOneOfGroups/
    // AtLeastOneOfGroups), never as prose folded into RequiredFields — the
    // gatekeeper's proven-on-the-built-CLI finding: RequiredFields is consumed
    // by SuiteScaffolder as bare field NAMES emitted verbatim as YAML keys, so
    // a prose string ("exactly one of: code, file") rendered as
    // "exactly one of: code, file: scaffold" — a hard YAML parse error.

    /// <summary>
    /// script.csharp's SchemaFragment declares NO top-level 'required' array —
    /// the "exactly one of code/file" constraint lives entirely inside a
    /// 'oneOf' of single-field 'required' branches. Before the ORIGINAL fix,
    /// <c>EngineExport.ExtractFields</c> only ever read the top-level
    /// 'required' array, so the catalogue silently claimed script.csharp
    /// requires NOTHING — a real lie. This is the concrete case named in the
    /// brief; RequiredFields itself must stay field-names-only (empty here),
    /// with the constraint surfaced via ExactlyOneOfGroups instead.
    /// </summary>
    [Fact]
    public void BuildCatalogue_ScriptCsharp_SurfacesOneOfRequirement_AsTypedGroup_NotProseInRequiredFields()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(ScriptCsharpProvider).Assembly });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var scriptCsharp = Assert.Single(catalogue.StepTypes, e => e.Type == "script.csharp");

        // RequiredFields carries ONLY real field names — never a synthesised
        // sentence (B1: that vehicle broke SuiteScaffolder's YAML emission).
        Assert.Empty(scriptCsharp.RequiredFields);
        Assert.All(scriptCsharp.RequiredFields, name => Assert.DoesNotContain(':', name));

        var group = Assert.Single(scriptCsharp.ExactlyOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.Equal(CodeFileGroup, group);
        Assert.Empty(scriptCsharp.AtLeastOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());

        // Neither 'code' nor 'file' is independently listed as plain-optional —
        // that would contradict the "exactly one" requirement above.
        Assert.DoesNotContain("code", scriptCsharp.OptionalFields);
        Assert.DoesNotContain("file", scriptCsharp.OptionalFields);
    }

    /// <summary>
    /// Generic mechanism, not a script.csharp special case: a synthetic
    /// provider with the identical root-level "oneOf of single-required-name
    /// branches, no top-level required" shape gets the same typed-group
    /// treatment.
    /// </summary>
    [Fact]
    public void BuildCatalogue_RootOneOfOfSingleRequiredBranches_YieldsExactlyOneOfGroup()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new OneOfOnlyProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var entry = Assert.Single(catalogue.StepTypes, e => e.Type == "oneof.only");
        var group = Assert.Single(entry.ExactlyOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.Equal(AlphaBetaGroup, group);
        Assert.Empty(entry.RequiredFields);
        Assert.DoesNotContain("alpha", entry.OptionalFields);
        Assert.DoesNotContain("beta", entry.OptionalFields);
    }

    /// <summary>
    /// The anyOf counterpart (mq-expect.azureservicebus's real shape):
    /// expectPayloadContains/expectProperties, "at least one", surfaced as
    /// AtLeastOneOfGroups. This is also the fix for the corollary M4 finding:
    /// before it, a consumer trusting RequiredFields alone (target only) for
    /// this provider could scaffold a minimal document the composed schema
    /// rejects (neither anyOf branch satisfied).
    /// </summary>
    [Fact]
    public void BuildCatalogue_RootAnyOfOfSingleRequiredBranches_YieldsAtLeastOneOfGroup()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new AnyOfOnlyProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var entry = Assert.Single(catalogue.StepTypes, e => e.Type == "anyof.only");
        var group = Assert.Single(entry.AtLeastOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.Equal(AlphaBetaGroup, group);
        Assert.Empty(entry.ExactlyOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.DoesNotContain("alpha", entry.OptionalFields);
        Assert.DoesNotContain("beta", entry.OptionalFields);
    }

    /// <summary>
    /// Degrade-don't-fabricate (mirrors SchemaErrorCollector's
    /// HasUnattributableBranch guard, M1-r): when a oneOf branch's own
    /// 'required' does NOT contribute exactly one name (here, a two-name
    /// branch — mq-expect.azureservicebus's real shape), the synthesis is
    /// skipped rather than emitting a wrong "exactly one of: a, b, c" group
    /// that misstates the actual constraint (queue OR (topic AND
    /// subscription), not "exactly one of three independent fields"). The
    /// individual names fall back to plain optional, exactly as before this
    /// fix — an honest incompleteness, never a fabricated rule.
    /// </summary>
    [Fact]
    public void BuildCatalogue_OneOfWithMixedBranchCardinality_DoesNotFabricateExactlyOneGroup()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new MixedCardinalityOneOfProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var entry = Assert.Single(catalogue.StepTypes, e => e.Type == "mixed.oneof");
        Assert.Empty(entry.ExactlyOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.Empty(entry.RequiredFields);
        Assert.Contains("alpha", entry.OptionalFields);
        Assert.Contains("beta", entry.OptionalFields);
        Assert.Contains("gamma", entry.OptionalFields);
    }

    /// <summary>
    /// Gatekeeper finding #4: a qualifying branch must be EXACTLY
    /// <c>{"required": ["name"]}</c> — nothing else. A branch carrying an
    /// extra keyword alongside a single-name 'required' (here, a bare
    /// "type": "object" sibling — the shape a careless provider author might
    /// write) must NOT qualify: it disqualifies the WHOLE oneOf from
    /// synthesis, the same degrade-don't-fabricate direction as mismatched
    /// cardinality. Proves the code enforces this, not merely the XML doc.
    /// </summary>
    [Fact]
    public void BuildCatalogue_OneOfBranchWithExtraContentBesidesRequired_DoesNotQualify()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new ExtraContentInBranchProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var entry = Assert.Single(catalogue.StepTypes, e => e.Type == "extracontent.branch");
        Assert.Empty(entry.ExactlyOneOfGroups ?? Array.Empty<IReadOnlyList<string>>());
        Assert.Empty(entry.RequiredFields);
        Assert.Contains("alpha", entry.OptionalFields);
        Assert.Contains("beta", entry.OptionalFields);
    }

    [Fact]
    public void BuildCatalogue_CoreHttpRest_HasNonEmptyFieldsCaptureAndFamilyIntent()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        var catalogue = EngineExport.BuildCatalogue(registry, engineVersion: "test-1.0");

        Assert.Equal(EngineExport.CatalogueSchemaVersion, catalogue.SchemaVersion);
        Assert.Equal("test-1.0", catalogue.EngineVersion);

        var httpRest = Assert.Single(catalogue.StepTypes, e => e.Type == "http.rest");
        Assert.Equal("http", httpRest.Family);
        Assert.Equal("rest", httpRest.Provider);
        Assert.Contains("target", httpRest.RequiredFields);
        Assert.Contains("method", httpRest.RequiredFields);
        Assert.Contains("path", httpRest.RequiredFields);
        Assert.Contains("headers", httpRest.OptionalFields);
        Assert.Contains("body", httpRest.OptionalFields);
        Assert.True(httpRest.CaptureSupported);
        Assert.False(string.IsNullOrWhiteSpace(httpRest.FamilyIntent));
        Assert.Contains("HTTP", httpRest.FamilyIntent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCatalogue_IncludesTestProviderAlongsideCore()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new HttpRestProvider(),
            new SampleExportProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);

        var keys = catalogue.StepTypes.Select(e => e.Type).ToList();
        Assert.Contains("http.rest", keys);
        Assert.Contains("sample.export", keys);

        var sample = Assert.Single(catalogue.StepTypes, e => e.Type == "sample.export");
        Assert.Equal("message", Assert.Single(sample.RequiredFields));
        Assert.Equal("tag", Assert.Single(sample.OptionalFields));
        Assert.True(sample.CaptureSupported);
        // Unknown family still gets a non-empty fallback intent.
        Assert.Equal("Steps in the sample family.", sample.FamilyIntent);
    }

    [Fact]
    public void BuildCatalogue_StepTypesSortedOrdinal()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new SampleExportProvider(),
            new HttpRestProvider(),
        });

        var catalogue = EngineExport.BuildCatalogue(registry);
        var types = catalogue.StepTypes.Select(e => e.Type).ToList();
        var expected = types.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, types);
    }

    [Fact]
    public void BuildCatalogue_MissingSchemaFragment_ThrowsNamingStepType()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new IncompleteNoFragmentProvider(),
        });

        var ex = Assert.Throws<CatalogueExportException>(
            () => EngineExport.BuildCatalogue(registry));

        Assert.Equal("incomplete.none", ex.StepType);
        Assert.Contains("incomplete.none", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Schema fragment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeSchemaJson_MissingSchemaFragment_ThrowsNamingStepType()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new IncompleteNoFragmentProvider(),
        });

        var ex = Assert.Throws<CatalogueExportException>(
            () => EngineExport.ComposeSchemaJson(registry));

        Assert.Equal("incomplete.none", ex.StepType);
        Assert.Contains("incomplete.none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeSchemaJson_WithHttpRest_IncludesTypeConst()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        var json = EngineExport.ComposeSchemaJson(registry);

        Assert.Contains("http.rest", json, StringComparison.Ordinal);
        Assert.Contains("x-vouchfx-schema-version", json, StringComparison.Ordinal);
        // Valid JSON.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void BuildCatalogue_DoesNotEmbedPlantedSecretValue()
    {
        // EDGE-003: export is structural only — a secret present in the process
        // environment must never appear in catalogue JSON.
        const string secretValue = "planted-secret-value-9f3c2a1b";
        Environment.SetEnvironmentVariable("VOUCHFX_CATALOGUE_EXPORT_TEST_SECRET", secretValue);
        try
        {
            var registry = StepKindRegistry.BuildAndFreeze(
                new[] { typeof(HttpRestProvider).Assembly });

            var catalogue = EngineExport.BuildCatalogue(registry, engineVersion: "env-test");
            var json = EngineExport.SerializeCatalogue(catalogue);

            Assert.DoesNotContain(secretValue, json, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "VOUCHFX_CATALOGUE_EXPORT_TEST_SECRET",
                json,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOUCHFX_CATALOGUE_EXPORT_TEST_SECRET", null);
        }
    }

    [Fact]
    public void SerializeCatalogue_IsDeterministicAcrossConsecutiveCalls()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        var a = EngineExport.SerializeCatalogue(EngineExport.BuildCatalogue(registry, "v"));
        var b = EngineExport.SerializeCatalogue(EngineExport.BuildCatalogue(registry, "v"));

        Assert.Equal(a, b);
    }

    // ── Test-only providers ───────────────────────────────────────────────────

    [StepProvider]
    private sealed class SampleExportProvider
        : IStepProvider, IStepBinder<SampleExportModel>
    {
        public SampleExportProvider() { }

        public StepKindId Kind { get; } = new("sample", "export");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["message"],
              "properties": {
                "message": { "type": "string" },
                "tag": { "type": "string" }
              }
            }
            """);

        public SampleExportModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    private sealed record SampleExportModel : IStepModel;

    /// <summary>
    /// A root-level 'oneOf' of two single-name 'required' branches and NO
    /// top-level 'required' array — the generic shape
    /// <c>BuildCatalogue_RootOneOfOfSingleRequiredBranches_SynthesisesExactlyOneOfMessage</c>
    /// exercises (script.csharp's real shape, reproduced synthetically so the
    /// mechanism is proven generic, not hardcoded to one provider).
    /// </summary>
    [StepProvider]
    private sealed class OneOfOnlyProvider : IStepProvider, IStepBinder<OneOfOnlyModel>
    {
        public OneOfOnlyProvider() { }

        public StepKindId Kind { get; } = new("oneof", "only");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "oneOf": [
                { "required": ["alpha"] },
                { "required": ["beta"] }
              ],
              "properties": {
                "alpha": { "type": "string" },
                "beta": { "type": "string" }
              }
            }
            """);

        public OneOfOnlyModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    private sealed record OneOfOnlyModel : IStepModel;

    /// <summary>
    /// A root-level 'oneOf' whose branches have MIXED required-name cardinality
    /// (one single-name branch, one two-name branch) — mirrors
    /// mq-expect.azureservicebus's real shape (queue OR (topic AND
    /// subscription)). Exercises the degrade-don't-fabricate guard in
    /// <c>BuildCatalogue_OneOfWithMixedBranchCardinality_DoesNotFabricateExactlyOneMessage</c>.
    /// </summary>
    [StepProvider]
    private sealed class MixedCardinalityOneOfProvider : IStepProvider, IStepBinder<MixedCardinalityOneOfModel>
    {
        public MixedCardinalityOneOfProvider() { }

        public StepKindId Kind { get; } = new("mixed", "oneof");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "oneOf": [
                { "required": ["alpha"] },
                { "required": ["beta", "gamma"] }
              ],
              "properties": {
                "alpha": { "type": "string" },
                "beta": { "type": "string" },
                "gamma": { "type": "string" }
              }
            }
            """);

        public MixedCardinalityOneOfModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    private sealed record MixedCardinalityOneOfModel : IStepModel;

    /// <summary>
    /// A root-level 'anyOf' of two single-name 'required' branches, no
    /// top-level 'required' — mirrors mq-expect.azureservicebus's real
    /// expectPayloadContains/expectProperties shape, reproduced synthetically
    /// so <c>BuildCatalogue_RootAnyOfOfSingleRequiredBranches_YieldsAtLeastOneOfGroup</c>
    /// proves the mechanism is generic, not hardcoded to one provider.
    /// </summary>
    [StepProvider]
    private sealed class AnyOfOnlyProvider : IStepProvider, IStepBinder<AnyOfOnlyModel>
    {
        public AnyOfOnlyProvider() { }

        public StepKindId Kind { get; } = new("anyof", "only");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "anyOf": [
                { "required": ["alpha"] },
                { "required": ["beta"] }
              ],
              "properties": {
                "alpha": { "type": "string" },
                "beta": { "type": "string" }
              }
            }
            """);

        public AnyOfOnlyModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    private sealed record AnyOfOnlyModel : IStepModel;

    /// <summary>
    /// A root-level 'oneOf' where one branch carries an extra keyword
    /// alongside its single-name 'required' (here, a sibling "type": "object")
    /// — must NOT qualify for ExactlyOneOfGroups synthesis (gatekeeper finding
    /// #4: the code, not merely the doc comment, enforces "nothing else").
    /// </summary>
    [StepProvider]
    private sealed class ExtraContentInBranchProvider : IStepProvider, IStepBinder<ExtraContentInBranchModel>
    {
        public ExtraContentInBranchProvider() { }

        public StepKindId Kind { get; } = new("extracontent", "branch");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "oneOf": [
                { "required": ["alpha"], "type": "object" },
                { "required": ["beta"] }
              ],
              "properties": {
                "alpha": { "type": "string" },
                "beta": { "type": "string" }
              }
            }
            """);

        public ExtraContentInBranchModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    private sealed record ExtraContentInBranchModel : IStepModel;

    /// <summary>
    /// Implements <see cref="IStepProvider"/> only — no binder, so the registry
    /// records a null SchemaFragment (fail-closed export target).
    /// </summary>
    [StepProvider]
    private sealed class IncompleteNoFragmentProvider : IStepProvider
    {
        public IncompleteNoFragmentProvider() { }

        public StepKindId Kind { get; } = new("incomplete", "none");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });
    }
}
