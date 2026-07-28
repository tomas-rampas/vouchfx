// Spec A — EngineExport public API: composed schema + shape-level catalogue.
// Docker-free unit tests covering REQ-003/004/005, EDGE-002/003, fail-closed metadata.

using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class CatalogueExportTests
{
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
