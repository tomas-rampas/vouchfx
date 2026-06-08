// Tests for S05-B-02: SecretString — redaction enforced at the source (§17).
//
// These tests pin the security-critical invariants of the secret value carrier:
//   • ToString() returns the marker, NEVER the value.
//   • Reveal() returns the value (the single, greppable escape hatch).
//   • System.Text.Json serialisation NEVER emits the value.
//   • The type is NOT IFormattable (cannot be coerced to its value by formatting).
//   • Length exposes safe metadata only.
//
// The internal constructor is reached via [InternalsVisibleTo] on the production
// assembly (no injection sink is needed to mint a value in a unit test).

using System;
using System.Text.Json;
using Platform.Engine.Abstractions.Secrets;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies the redaction-at-source contract of <see cref="SecretString"/> (§17).
/// </summary>
public sealed class SecretStringTests
{
    private const string TheValue = "topsecret-value-123";

    [Fact]
    public void ToString_ReturnsMarker_NotValue()
    {
        var secret = new SecretString(TheValue);

        Assert.Equal(SecretString.RedactedMarker, secret.ToString());
        Assert.DoesNotContain(TheValue, secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_ReturnsValue()
    {
        var secret = new SecretString(TheValue);

        Assert.Equal(TheValue, secret.Reveal());
    }

    [Fact]
    public void Length_ReturnsValueLength_NotCharacters()
    {
        var secret = new SecretString(TheValue);

        Assert.Equal(TheValue.Length, secret.Length);
    }

    [Fact]
    public void JsonSerialize_DoesNotContainValue()
    {
        var secret = new SecretString(TheValue);

        // Serialise the value directly...
        var direct = JsonSerializer.Serialize(secret);
        Assert.DoesNotContain(TheValue, direct, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, direct, StringComparison.Ordinal);

        // ...and nested inside an object graph (the converter must apply both ways).
        var nested = JsonSerializer.Serialize(new { token = secret });
        Assert.DoesNotContain(TheValue, nested, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, nested, StringComparison.Ordinal);
    }

    [Fact]
    public void IsNotIFormattable()
    {
        // If SecretString implemented IFormattable, string interpolation /
        // string.Format could coerce it to its value, defeating redaction.
        // Checked via the type system (a direct `is IFormattable` is a compile-time
        // CS0184 because the type is provably never IFormattable — which is exactly
        // the guarantee; we assert it reflectively so this test pins it permanently).
        Assert.DoesNotContain(
            typeof(IFormattable),
            typeof(SecretString).GetInterfaces());
    }

    [Fact]
    public void StringInterpolation_UsesMarker_NotValue()
    {
        var secret = new SecretString(TheValue);

        var interpolated = $"Authorization: {secret}";

        Assert.DoesNotContain(TheValue, interpolated, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, interpolated, StringComparison.Ordinal);
    }
}
