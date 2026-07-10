// Tests for S05-B-02: EnvironmentSecretResolver — reads env vars at resolution
// time only (§17). A present var resolves; a missing one throws
// SecretResolutionException (an environment/configuration problem, §12.1).

using System;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies the <c>env</c> resolver: present variable → revealed value; absent
/// variable → <see cref="SecretResolutionException"/> naming the path, never a value.
/// </summary>
public sealed class EnvironmentSecretResolverTests
{
    [Fact]
    public void Source_IsEnv()
    {
        var resolver = new EnvironmentSecretResolver();
        Assert.Equal("env", resolver.Source);
    }

    [Fact]
    public void Resolve_PresentVariable_RevealsValue()
    {
        // Unique name so concurrent test runs cannot collide.
        var name = "VOUCHFX_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        const string value = "resolved-secret-value";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var resolver = new EnvironmentSecretResolver();

            var secret = resolver.Resolve(name);

            Assert.Equal(value, secret.Reveal());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Resolve_MissingVariable_ThrowsSecretResolutionException()
    {
        var name = "VOUCHFX_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        var resolver = new EnvironmentSecretResolver();

        var ex = Assert.Throws<SecretResolutionException>(() => resolver.Resolve(name));

        // The message names the variable (so the author can act) but no value exists.
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        Assert.Equal("env", ex.SecretSource);
        Assert.Equal(name, ex.SecretPath);
    }

    [Fact]
    public void Resolve_EmptyPath_ThrowsSecretResolutionException()
    {
        var resolver = new EnvironmentSecretResolver();

        Assert.Throws<SecretResolutionException>(() => resolver.Resolve(string.Empty));
    }
}
