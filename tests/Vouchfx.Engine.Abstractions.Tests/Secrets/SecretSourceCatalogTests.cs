// Tests for S05-B-02: SecretSourceCatalog — indexes resolvers by source (§17).

using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies that <see cref="SecretSourceCatalog"/> indexes resolvers by source and
/// reports its known sources (the set Vault joins in Sprint 8).
/// </summary>
public sealed class SecretSourceCatalogTests
{
    private sealed class FakeResolver : ISecretResolver
    {
        public FakeResolver(string source) => Source = source;

        public string Source { get; }

        public SecretString Resolve(string path)
            => throw new NotSupportedException("not exercised in catalog tests");
    }

    [Fact]
    public void TryGet_KnownSource_ReturnsResolver()
    {
        var env = new EnvironmentSecretResolver();
        var catalog = new SecretSourceCatalog(new ISecretResolver[] { env });

        var hit = catalog.TryGet("env", out var resolver);

        Assert.True(hit);
        Assert.Same(env, resolver);
    }

    [Fact]
    public void TryGet_UnknownSource_ReturnsFalseAndNull()
    {
        var catalog = new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() });

        var hit = catalog.TryGet("vault", out var resolver);

        Assert.False(hit);
        Assert.Null(resolver);
    }

    [Fact]
    public void Sources_ListsRegisteredSources()
    {
        var catalog = new SecretSourceCatalog(
            new ISecretResolver[] { new EnvironmentSecretResolver(), new FakeResolver("vault") });

        Assert.Contains("env", catalog.Sources);
        Assert.Contains("vault", catalog.Sources);
        Assert.Equal(2, catalog.Sources.Count);
    }

    [Fact]
    public void Constructor_FirstResolverPerSourceWins()
    {
        var first = new FakeResolver("env");
        var second = new FakeResolver("env");
        var catalog = new SecretSourceCatalog(new ISecretResolver[] { first, second });

        catalog.TryGet("env", out var resolver);

        Assert.Same(first, resolver);
        Assert.Single(catalog.Sources);
    }

    [Fact]
    public void Constructor_NullResolvers_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SecretSourceCatalog(null!));
    }
}
