// Tests for S05-B-02: SecretAccessor (the type the emitted CSX names) + NullSecretAccessor.
//
// The accessor parses a reference, routes it to the catalog's resolver, and returns
// a redacted SecretString. It accepts both the raw "${secret:env/X}" token and the
// bare "env/X" form. Unknown source → SecretResolutionException. The null accessor
// (used by legacy ctors) always throws.

using System;
using Platform.Engine.Abstractions.Secrets;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies <see cref="SecretAccessor"/> reference parsing/routing and the
/// fail-loud behaviour of <see cref="NullSecretAccessor"/> (§17).
/// </summary>
public sealed class SecretAccessorTests
{
    private sealed class FakeResolver : ISecretResolver
    {
        private readonly string _value;

        public FakeResolver(string source, string value)
        {
            Source = source;
            _value = value;
        }

        public string Source { get; }

        public string? LastPath { get; private set; }

        public SecretString Resolve(string path)
        {
            LastPath = path;
            return new SecretString(_value);
        }
    }

    private static SecretAccessor BuildAccessor(params ISecretResolver[] resolvers)
        => new SecretAccessor(new SecretSourceCatalog(resolvers));

    [Fact]
    public void Resolve_RawToken_RoutesToSourceAndReturnsValue()
    {
        var fake = new FakeResolver("env", "the-value");
        var accessor = BuildAccessor(fake);

        var secret = accessor.Resolve("${secret:env/API_TOKEN}");

        Assert.Equal("the-value", secret.Reveal());
        Assert.Equal("API_TOKEN", fake.LastPath);
    }

    [Fact]
    public void Resolve_BareSourcePath_IsAccepted()
    {
        var fake = new FakeResolver("env", "the-value");
        var accessor = BuildAccessor(fake);

        var secret = accessor.Resolve("env/API_TOKEN");

        Assert.Equal("the-value", secret.Reveal());
        Assert.Equal("API_TOKEN", fake.LastPath);
    }

    [Fact]
    public void Resolve_RoutesBySource_WhenMultipleResolvers()
    {
        var env = new FakeResolver("env", "env-value");
        var vault = new FakeResolver("vault", "vault-value");
        var accessor = BuildAccessor(env, vault);

        Assert.Equal("vault-value", accessor.Resolve("${secret:vault/kv/data/db}").Reveal());
        Assert.Equal("kv/data/db", vault.LastPath);
        Assert.Null(env.LastPath);
    }

    [Fact]
    public void Resolve_UnknownSource_Throws()
    {
        var accessor = BuildAccessor(new FakeResolver("env", "x"));

        var ex = Assert.Throws<SecretResolutionException>(
            () => accessor.Resolve("${secret:vault/x}"));

        Assert.Contains("vault", ex.Message, StringComparison.Ordinal);
        Assert.Equal("vault", ex.SecretSource);
    }

    [Fact]
    public void Resolve_MalformedReference_Throws()
    {
        var accessor = BuildAccessor(new FakeResolver("env", "x"));

        Assert.Throws<SecretResolutionException>(() => accessor.Resolve("not-a-reference"));
    }

    [Fact]
    public void Resolve_EmptyReference_Throws()
    {
        var accessor = BuildAccessor(new FakeResolver("env", "x"));

        Assert.Throws<SecretResolutionException>(() => accessor.Resolve(string.Empty));
    }

    [Fact]
    public void Constructor_NullCatalog_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretAccessor(null!));
    }

    [Fact]
    public void NullSecretAccessor_Resolve_AlwaysThrows()
    {
        var accessor = new NullSecretAccessor();

        var ex = Assert.Throws<SecretResolutionException>(
            () => accessor.Resolve("${secret:env/API_TOKEN}"));

        Assert.Contains("no secret sources are configured", ex.Message, StringComparison.Ordinal);
    }
}
