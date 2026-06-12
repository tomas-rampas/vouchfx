// Tests for S08-B-01: VaultSecretConfiguration — reads the Vault address + token
// from the run ENVIRONMENT (a reference/config, never a YAML literal, §17).  The
// token is held only to be sent on the X-Vault-Token header at resolution time; it
// is never echoed by ToString() and never embedded in any diagnostic.

using System;
using Platform.Engine.Abstractions.Secrets.Vault;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies <see cref="VaultSecretConfiguration"/>: address + token come from the
/// environment (Vault's own <c>VAULT_ADDR</c> / <c>VAULT_TOKEN</c> conventions), the
/// token is never surfaced by <see cref="object.ToString"/>, and a missing
/// address/token is reported without leaking either value (§17).
/// </summary>
public sealed class VaultSecretConfigurationTests
{
    [Fact]
    public void FromEnvironment_ReadsAddressAndToken()
    {
        var addrName = "VAULT_ADDR";
        var tokenName = "VAULT_TOKEN";
        var prevAddr = Environment.GetEnvironmentVariable(addrName);
        var prevToken = Environment.GetEnvironmentVariable(tokenName);
        Environment.SetEnvironmentVariable(addrName, "http://127.0.0.1:8200");
        Environment.SetEnvironmentVariable(tokenName, "root-token-must-not-leak");
        try
        {
            var ok = VaultSecretConfiguration.TryFromEnvironment(out var config, out var error);

            Assert.True(ok, error);
            Assert.NotNull(config);
            Assert.Equal(new Uri("http://127.0.0.1:8200"), config!.Address);
        }
        finally
        {
            Environment.SetEnvironmentVariable(addrName, prevAddr);
            Environment.SetEnvironmentVariable(tokenName, prevToken);
        }
    }

    [Fact]
    public void ToString_DoesNotRevealToken()
    {
        var config = new VaultSecretConfiguration(
            new Uri("http://127.0.0.1:8200"), "super-secret-token", "secret");

        var text = config.ToString();

        Assert.DoesNotContain("super-secret-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryFromEnvironment_MissingToken_FailsWithoutLeaking()
    {
        var addrName = "VAULT_ADDR";
        var tokenName = "VAULT_TOKEN";
        var prevAddr = Environment.GetEnvironmentVariable(addrName);
        var prevToken = Environment.GetEnvironmentVariable(tokenName);
        Environment.SetEnvironmentVariable(addrName, "http://127.0.0.1:8200");
        Environment.SetEnvironmentVariable(tokenName, null);
        try
        {
            var ok = VaultSecretConfiguration.TryFromEnvironment(out var config, out var error);

            Assert.False(ok);
            Assert.Null(config);
            Assert.NotNull(error);
            // The diagnostic names the variable that must be set, never a value.
            Assert.Contains(tokenName, error!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(addrName, prevAddr);
            Environment.SetEnvironmentVariable(tokenName, prevToken);
        }
    }

    [Fact]
    public void TryFromEnvironment_MissingAddress_Fails()
    {
        var addrName = "VAULT_ADDR";
        var tokenName = "VAULT_TOKEN";
        var prevAddr = Environment.GetEnvironmentVariable(addrName);
        var prevToken = Environment.GetEnvironmentVariable(tokenName);
        Environment.SetEnvironmentVariable(addrName, null);
        Environment.SetEnvironmentVariable(tokenName, "a-token");
        try
        {
            var ok = VaultSecretConfiguration.TryFromEnvironment(out var config, out var error);

            Assert.False(ok);
            Assert.Null(config);
            Assert.Contains(addrName, error!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(addrName, prevAddr);
            Environment.SetEnvironmentVariable(tokenName, prevToken);
        }
    }

    [Fact]
    public void Constructor_NullToken_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new VaultSecretConfiguration(new Uri("http://127.0.0.1:8200"), null!, "secret"));
    }
}
