// Spec B — SuiteScaffolder public library (mcp-generator-scaffold-and-run).
// Docker-free unit tests covering REQ-001..006 and EDGE-001/002/004/005 for the library.

using Vouchfx.Engine.Compilation.Scaffold;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class SuiteScaffolderTests
{
    private static StepKindRegistry BuildHttpAndPostgresRegistry() =>
        StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new HttpRestProvider(),
            new DbAssertPostgresProvider(),
        });

    private static ScaffoldIntent MultiTypeIntent() => new(
        Steps: new[]
        {
            new ScaffoldStepIntent("get-api", "http.rest", Label: "GET api"),
            new ScaffoldStepIntent("check-db", "db-assert.postgres"),
        },
        Services: new[] { new ScaffoldServiceIntent("api", "traefik/whoami") },
        Dependencies: new[] { new ScaffoldDependencyIntent("db", "postgres") });

    [Fact]
    public void Generate_MultiType_EmitsTypesIdsEnvAndOrder()
    {
        // REQ-001 / REQ-004
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            MultiTypeIntent(),
            engineVersion: "test-1.0");

        Assert.Contains("type: http.rest", yaml, StringComparison.Ordinal);
        Assert.Contains("type: db-assert.postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("id: get-api", yaml, StringComparison.Ordinal);
        Assert.Contains("id: check-db", yaml, StringComparison.Ordinal);
        Assert.Contains("api:", yaml, StringComparison.Ordinal);
        Assert.Contains("db:", yaml, StringComparison.Ordinal);
        Assert.Contains("type: postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("# label: GET api", yaml, StringComparison.Ordinal);

        var getIdx = yaml.IndexOf("id: get-api", StringComparison.Ordinal);
        var checkIdx = yaml.IndexOf("id: check-db", StringComparison.Ordinal);
        Assert.True(getIdx >= 0 && checkIdx > getIdx, "Step order must match intent order.");
    }

    [Fact]
    public void Generate_UnknownType_ThrowsNamingType()
    {
        // REQ-002
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("x", "nope.fake") })));

        Assert.Contains("nope.fake", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_MultiType_PassesDocumentValidator()
    {
        // REQ-003
        var registry = BuildHttpAndPostgresRegistry();
        var yaml = SuiteScaffolder.Generate(registry, MultiTypeIntent(), engineVersion: "test");

        var result = DocumentValidator.Validate(yaml, registry);
        Assert.True(
            result.IsValid,
            "Scaffold output must be schema-valid. Errors: "
            + string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Generate_ProvenanceComments_MarkMachineDraftedAndReview()
    {
        // REQ-005
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            MultiTypeIntent(),
            engineVersion: "1.2.3");

        var lines = yaml.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Take(5)
            .ToList();

        Assert.All(lines.Take(3), line => Assert.StartsWith("#", line, StringComparison.Ordinal));
        var header = string.Join('\n', lines);
        Assert.Contains("Machine-drafted", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vouchfx scaffold", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", header, StringComparison.Ordinal);
        Assert.Contains("review", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_NeverEmitsPlantedSecretLiteral_UsesSecretReferenceForSecretLikeFields()
    {
        // REQ-006
        const string planted = "super-secret-token-VALUE-9f3a";
        Environment.SetEnvironmentVariable("VOUCHFX_SCAFFOLD_TEST_TOKEN", planted);
        try
        {
            var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
            {
                new HttpRestProvider(),
                new SampleSecretFieldProvider(),
            });

            var yaml = SuiteScaffolder.Generate(
                registry,
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("s", "sample.secretstep") }));

            Assert.DoesNotContain(planted, yaml, StringComparison.Ordinal);
            Assert.Contains("${secret:env/SCAFFOLD_PLACEHOLDER}", yaml, StringComparison.Ordinal);
            Assert.Contains("apiToken:", yaml, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOUCHFX_SCAFFOLD_TEST_TOKEN", null);
        }
    }

    [Fact]
    public void Generate_DuplicateIds_ThrowsNamingId()
    {
        // EDGE-001
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[]
                    {
                        new ScaffoldStepIntent("same", "http.rest"),
                        new ScaffoldStepIntent("same", "db-assert.postgres"),
                    })));

        Assert.Contains("same", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("1starts-digit")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public void Generate_InvalidStepIdFormat_Throws(string badId)
    {
        // Schema pattern ^[A-Za-z_][A-Za-z0-9_-]*$ — fail closed before emit.
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent(badId, "http.rest") })));

        Assert.Contains(badId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_LabelWithNewlines_StaysSingleLineComment()
    {
        // Labels must not inject multi-line content into the YAML comment header.
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            new ScaffoldIntent(
                Steps: new[]
                {
                    new ScaffoldStepIntent(
                        "get-api",
                        "http.rest",
                        Label: "safe\nid: injected\ntype: evil"),
                }));

        Assert.DoesNotContain("\n  id: injected", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\ntype: evil", yaml, StringComparison.Ordinal);
        Assert.Contains("# label: safe id: injected type: evil", yaml, StringComparison.Ordinal);
        Assert.Contains("id: get-api", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EmptySteps_Throws()
    {
        // EDGE-002
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(Steps: Array.Empty<ScaffoldStepIntent>())));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_UnknownDependencyKind_ThrowsNamingKind()
    {
        // EDGE-004
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("a", "http.rest") },
                    Dependencies: new[] { new ScaffoldDependencyIntent("x", "not-a-real-dep") })));

        Assert.Contains("not-a-real-dep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TwoRunsIdentical_NoTimestamps()
    {
        // EDGE-005
        var registry = BuildHttpAndPostgresRegistry();
        var intent = MultiTypeIntent();

        var a = SuiteScaffolder.Generate(registry, intent, engineVersion: "stable");
        var b = SuiteScaffolder.Generate(registry, intent, engineVersion: "stable");

        Assert.Equal(a, b);
        Assert.DoesNotContain("2026-", a, StringComparison.Ordinal);
        Assert.DoesNotContain("T00:", a, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownDependencyKinds_ContainsCoreMapperKinds()
    {
        Assert.True(KnownDependencyKinds.Contains("postgres"));
        Assert.True(KnownDependencyKinds.Contains("minio"));
        Assert.True(KnownDependencyKinds.Contains("mailpit"));
        Assert.False(KnownDependencyKinds.Contains("not-a-real-dep"));
    }

    /// <summary>
    /// Minimal provider whose required field name is secret-shaped (for REQ-006).
    /// </summary>
    private sealed class SampleSecretFieldProvider : IStepProvider, IStepBinder<SampleSecretModel>
    {
        public StepKindId Kind { get; } = new("sample", "secretstep");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["apiToken"],
              "properties": {
                "apiToken": { "type": "string" }
              }
            }
            """);

        public SampleSecretModel Bind(
            YamlDotNet.RepresentationModel.YamlNode node,
            IBindingContext ctx) => new();
    }

    private sealed record SampleSecretModel : IStepModel;
}
