// Tests for IProviderModule — S01-F-01 review fix (Change 2).
// Verifies that an IProviderModule implementation is implementable and returns
// its declared providers correctly.
using Platform.Sdk;
using Xunit;

namespace Platform.Sdk.Tests;

/// <summary>
/// Minimal <see cref="IProviderModule"/> that bundles stub providers,
/// used solely to exercise the interface contract.
/// </summary>
file sealed class StubProviderModule : IProviderModule
{
    private readonly IStepProvider[] _providers;

    public StubProviderModule(params IStepProvider[] providers)
    {
        _providers = providers;
    }

    /// <inheritdoc />
    public IEnumerable<IStepProvider> Providers() => _providers;
}

/// <summary>
/// Minimal <see cref="IStepProvider"/> stub for module tests.
/// </summary>
file sealed class StubProvider : IStepProvider
{
    private static readonly IReadOnlyList<string> s_authors = new[] { "test" };

    public StubProvider(string family, string technology)
    {
        Kind = new StepKindId(family, technology);
        Metadata = new ProviderMetadata(
            Version: "0.0.1",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: s_authors);
    }

    /// <inheritdoc />
    public StepKindId Kind { get; }

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; }
}

public sealed class ProviderModuleTests
{
    // ── Interface is implementable ────────────────────────────────────────────

    [Fact]
    public void IProviderModule_IsImplementable()
    {
        // StubProviderModule implements IProviderModule; the cast proves assignability.
        IProviderModule module = new StubProviderModule();
        Assert.NotNull(module);
    }

    // ── Providers() returns declared providers ────────────────────────────────

    [Fact]
    public void Providers_ReturnsEmptySequence_WhenNoProvidersRegistered()
    {
        var module = new StubProviderModule();
        Assert.Empty(module.Providers());
    }

    [Fact]
    public void Providers_ReturnsSingleProvider()
    {
        var provider = new StubProvider("mq-publish", "kafka");
        var module = new StubProviderModule(provider);

        var results = module.Providers().ToList();

        Assert.Single(results);
        Assert.Same(provider, results[0]);
    }

    [Fact]
    public void Providers_ReturnsMultipleProviders_InOrder()
    {
        var first = new StubProvider("db-assert", "postgres");
        var second = new StubProvider("mq-expect", "kafka");
        var module = new StubProviderModule(first, second);

        var results = module.Providers().ToList();

        Assert.Equal(2, results.Count);
        Assert.Same(first, results[0]);
        Assert.Same(second, results[1]);
    }

    [Fact]
    public void Providers_ReturnedProviders_HaveCorrectKindIds()
    {
        var provider = new StubProvider("http", "rest");
        var module = new StubProviderModule(provider);

        var result = module.Providers().Single();

        Assert.Equal("http", result.Kind.Family);
        Assert.Equal("rest", result.Kind.Provider);
    }
}
