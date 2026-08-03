// Tests for StorageAssertS3Provider — bind/validate/schema/registry/resources.
//
// Covers:
//   1.  Bind: full YAML step (target/bucket/key/expect) → correct model.
//   2.  Bind: non-mapping node → safe empty model.
//   3.  Bind: expect absent → all-null expectation (exists defaults to true downstream).
//   4.  Validate: valid model → IsValid.
//   5.  Validate: empty target/bucket/key → invalid.
//   6.  Validate: exists:false + a content expectation → invalid (coherence rule 1).
//   7.  Validate: size + minSize together → invalid (coherence rule 2, mutually exclusive).
//   8.  Validate: target not declared / wrong dependency type → invalid.
//   9.  Registry: provider discoverable via StepKindRegistry with key "storage-assert.s3".
//   10. Registry: SchemaFragment contains "bucket".
//   11. Resources: yields a minio ResourceRequirement whose Name equals model.Target.
//   12. CompileReferenceAssemblies: contains AWSSDK.S3 + JsonPath.Net assemblies.
//
// All tests are non-docker.
using Vouchfx.Sdk;
using Vouchfx.Steps.StorageAssert.S3;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.StorageAssert.S3.Tests;

file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(IReadOnlyDictionary<string, string>? deps = null)
    {
        DeclaredDependencies = deps ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

internal sealed class StubBindingContext : IBindingContext { }

public sealed class StorageAssertS3ProviderTests
{
    private readonly StorageAssertS3Provider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML mapping ─────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("uploads") },
            { "bucket", new YamlScalarNode("reports") },
            { "key", new YamlScalarNode("exports/{orderId}.csv") },
            {
                "expect", new YamlMappingNode
                {
                    { "exists", new YamlScalarNode("true") },
                    { "size", new YamlScalarNode("128") },
                    { "contentType", new YamlScalarNode("text/csv") },
                    {
                        "metadata", new YamlMappingNode
                        {
                            { "source", new YamlScalarNode("orders-api") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("uploads", model.Target);
        Assert.Equal("reports", model.Bucket);
        Assert.Equal("exports/{orderId}.csv", model.Key);
        Assert.True(model.Expect.Exists);
        Assert.Equal("128", model.Expect.Size);
        Assert.Equal("text/csv", model.Expect.ContentType);
        Assert.NotNull(model.Expect.Metadata);
        Assert.Equal("orders-api", model.Expect.Metadata!["source"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Bucket);
        Assert.Equal(string.Empty, model.Key);
        Assert.Null(model.Expect.Exists);
        Assert.Null(model.Expect.Size);
        Assert.Null(model.Expect.Metadata);
    }

    // ── 3. Bind: expect absent ─────────────────────────────────────────────────

    [Fact]
    public void Bind_ExpectAbsent_AllNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("uploads") },
            { "bucket", new YamlScalarNode("reports") },
            { "key", new YamlScalarNode("exports/report.csv") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Expect.Exists);
        Assert.Null(model.Expect.Size);
        Assert.Null(model.Expect.MinSize);
        Assert.Null(model.Expect.Sha256);
        Assert.Null(model.Expect.ContentContains);
        Assert.Null(model.Expect.ContentType);
        Assert.Null(model.Expect.Metadata);
    }

    // ── 4. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, "128", null, null, null, "text/csv", null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "minio" }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 5. Validate: empty target/bucket/key ──────────────────────────────────

    [Fact]
    public void Validate_EmptyFields_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "", "", "", new StorageExpectation(null, null, null, null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
        Assert.Contains(result.Errors, e => e.Contains("bucket"));
        Assert.Contains(result.Errors, e => e.Contains("key"));
    }

    // ── 6. Validate: exists:false + content expectation → invalid ────────────

    [Fact]
    public void Validate_ExistsFalseWithSizeExpectation_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(false, "128", null, null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "minio" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("exists: false"));
    }

    [Fact]
    public void Validate_ExistsFalseWithMetadataExpectation_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(
                false, null, null, null, null, null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "orders-api" }));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "minio" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("exists: false"));
    }

    // ── 7. Validate: size + minSize mutually exclusive ────────────────────────

    [Fact]
    public void Validate_SizeAndMinSizeTogether_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, "128", "64", null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "minio" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mutually exclusive"));
    }

    // ── 8. Validate: target dependency reconciliation ──────────────────────────

    [Fact]
    public void Validate_TargetNotDeclared_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, null, null, null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    [Fact]
    public void Validate_TargetWrongDependencyType_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, null, null, null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "postgres" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    /// <summary>
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Minio" does not match the canonical "minio" — treated
    /// identically to a genuinely mismatched type.
    /// </summary>
    [Fact]
    public void Validate_TargetWrongCaseDependencyType_IsInvalid()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, null, null, null, null, null, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "Minio" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 9. Registry: provider discoverable ─────────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(StorageAssertS3Provider).Assembly });

        Assert.True(
            registry.TryGet("storage-assert.s3", out var provider) && provider is not null,
            "Provider for 'storage-assert.s3' must be discoverable via StepKindRegistry.");
    }

    // ── 10. Registry: SchemaFragment contains 'bucket' ─────────────────────────

    [Fact]
    public void SchemaFragment_ContainsBucket()
    {
        Assert.Contains("\"bucket\"", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 11. Resources: yields minio ResourceRequirement ────────────────────────

    [Fact]
    public void Resources_YieldsMinioRequirementWithCorrectName()
    {
        var model = new StorageAssertS3Model(
            "uploads", "reports", "exports/report.csv",
            new StorageExpectation(true, null, null, null, null, null, null));
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("minio", resources[0].Family);
        Assert.Equal("uploads", resources[0].Name);
    }

    // ── 12. CompileReferenceAssemblies: contains AWSSDK.S3 + JsonPath.Net ──────

    [Fact]
    public void CompileReferenceAssemblies_ContainsS3AndJsonPathNetAssemblies()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "AWSSDK.S3");
        Assert.Contains(refs, a => a.GetName().Name == "JsonPath.Net");
    }
}
