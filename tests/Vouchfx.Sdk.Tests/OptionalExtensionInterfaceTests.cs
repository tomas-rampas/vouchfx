// S08-F-02 (T3 — Freeze the v1 provider contract): extension-mechanism demo.
//
// The v1 provider contract is FROZEN (see SdkContractFreezeTests).  The agreed
// evolution mechanism is: extend via a NEW optional interface, NEVER mutate a
// frozen v1 interface — exactly how S7 added IStepDiffRenderer and
// IHostResourceContributor without touching IStepProvider et al.
//
// This test PROVES that mechanism end-to-end:
//   • It defines a brand-new optional marker interface (ISampleV1_1Capability)
//     ENTIRELY in the test assembly — the frozen Vouchfx.Sdk surface is not
//     touched.
//   • A fake provider implements the frozen CORE interfaces PLUS this new optional
//     interface.
//   • StepKindRegistry.BuildAndFreeze discovers the provider unchanged, and the new
//     optional capability is detectable on the discovered instance via a runtime
//     `is`-cast — i.e. a consumer can light up new behaviour for providers that opt
//     in, while providers that do not implement it are unaffected.
//
// This is the live counterpart to the static golden guard: discovery and capability
// detection both work WITHOUT any change to the frozen v1 interfaces.
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Sdk.Tests;

// ── A brand-new OPTIONAL extension interface, declared here, not in Vouchfx.Sdk ──

/// <summary>
/// A hypothetical v1.1 optional capability interface declared entirely in the test
/// assembly.  Its existence demonstrates that the engine can be extended with new
/// optional surfaces WITHOUT mutating any frozen v1 interface in Vouchfx.Sdk.
/// </summary>
public interface ISampleV1_1Capability
{
    /// <summary>A capability tag the consumer can read when the provider opts in.</summary>
    string Capability { get; }
}

/// <summary>
/// Strongly-typed model for the extension-demo provider.
/// </summary>
/// <param name="Note">A free-text note (kept non-empty for validity).</param>
public sealed record SampleExtensionModel(string Note) : IStepModel;

/// <summary>
/// A fake provider that implements the FROZEN core provider interfaces
/// (<see cref="IStepProvider"/>, <see cref="IStepBinder{TModel}"/>,
/// <see cref="IStepValidator{TModel}"/>, <see cref="IStepCompiler{TModel}"/>,
/// <see cref="IResourceContributor{TModel}"/>) PLUS the brand-new optional
/// <see cref="ISampleV1_1Capability"/>.  It is decorated with
/// <see cref="StepProviderAttribute"/> so the registry discovers it, under the
/// distinct key <c>sample-ext.demo</c> (no collision with other test providers).
/// </summary>
[StepProvider]
public sealed class SampleExtensionProvider
    : IStepProvider,
      IStepBinder<SampleExtensionModel>,
      IStepValidator<SampleExtensionModel>,
      IStepCompiler<SampleExtensionModel>,
      IResourceContributor<SampleExtensionModel>,
      ISampleV1_1Capability
{
    private static readonly string[] s_authors = { "vouchfx-contributors" };

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("sample-ext", "demo");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.1.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: s_authors);

    /// <summary>The new optional capability tag exposed by this provider.</summary>
    public string Capability => "sample-extension";

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """{ "type": "object", "properties": { "note": { "type": "string" } } }""");

    /// <inheritdoc />
    public SampleExtensionModel Bind(YamlNode node, IBindingContext ctx) =>
        new SampleExtensionModel("noop");

    /// <inheritdoc />
    public ValidationResult Validate(SampleExtensionModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    /// <inheritdoc />
    public CsxFragment Emit(SampleExtensionModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: "{ }");

    /// <inheritdoc />
    public System.Collections.Generic.IEnumerable<ResourceRequirement> Resources(
        SampleExtensionModel model) =>
        System.Array.Empty<ResourceRequirement>();
}

/// <summary>
/// Proves the frozen-contract extension mechanism: a provider implementing a NEW
/// optional interface is discovered and its capability detected, with no change to
/// any frozen v1 interface.
/// </summary>
public sealed class OptionalExtensionInterfaceTests
{
    /// <summary>
    /// The registry discovers <see cref="SampleExtensionProvider"/> via the normal
    /// <see cref="StepProviderAttribute"/> scan — proving a provider that ALSO
    /// implements a new optional interface remains fully discoverable.
    /// </summary>
    [Fact]
    public void Registry_DiscoversProvider_ImplementingNewOptionalInterface()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(SampleExtensionProvider).Assembly });

        var found = registry.TryGet("sample-ext.demo", out var provider);

        Assert.True(found, "Expected sample-ext.demo to be discovered by the frozen registry.");
        Assert.NotNull(provider);
        Assert.IsType<SampleExtensionProvider>(provider!.Instance);
    }

    /// <summary>
    /// The new optional capability is detectable on the discovered instance via an
    /// <c>is</c>-cast — the consumer-side half of "extend via a new optional
    /// interface".  No frozen v1 interface was touched to make this work.
    /// </summary>
    [Fact]
    public void DiscoveredProvider_ExposesNewOptionalCapability_WithoutMutatingFrozenContract()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(SampleExtensionProvider).Assembly });

        Assert.True(registry.TryGet("sample-ext.demo", out var provider));

        // The optional capability lights up only for providers that opt in.
        Assert.True(
            provider!.Instance is ISampleV1_1Capability,
            "Discovered provider must be detectable as ISampleV1_1Capability.");

        var capability = (ISampleV1_1Capability)provider.Instance;
        Assert.Equal("sample-extension", capability.Capability);

        // And it remains, first and foremost, a frozen-core IStepProvider — the new
        // optional interface is additive, not a replacement.
        Assert.IsAssignableFrom<IStepProvider>(provider.Instance);
    }

    /// <summary>
    /// The new optional interface lives in the TEST assembly, not in Vouchfx.Sdk:
    /// concrete proof that the extension required no change to the frozen v1 surface.
    /// </summary>
    [Fact]
    public void NewOptionalInterface_IsNotPartOfFrozenSdkSurface()
    {
        Assert.NotEqual(
            typeof(IStepProvider).Assembly,
            typeof(ISampleV1_1Capability).Assembly);
    }
}
