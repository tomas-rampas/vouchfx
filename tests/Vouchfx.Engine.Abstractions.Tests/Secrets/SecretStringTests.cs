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
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Secrets;

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

    // ── S05-G-01: structural redaction safety net for {placeholder} substitution ──

    /// <summary>
    /// If a <see cref="SecretString"/> were ever placed into <c>Vars</c> under a key
    /// and that key were referenced by a <c>{placeholder}</c> token, the substitution
    /// helper must stringify it to the redaction marker, NOT the value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Substitute_Helpers.Resolve</c> resolves a placeholder by calling
    /// <c>System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture)</c>
    /// on the <c>Vars</c> entry (see <c>Vouchfx.Sdk.SubstituteHelper.Source</c>).  For a
    /// <see cref="SecretString"/>, <see cref="Convert.ToString(object, IFormatProvider)"/>
    /// routes through <see cref="object.ToString"/> (the type is deliberately NOT
    /// <see cref="IFormattable"/>), which returns <see cref="SecretString.RedactedMarker"/>.
    /// </para>
    /// <para>
    /// This test pins that exact mechanism at the type level: it reproduces the helper's
    /// <c>Convert.ToString(val, InvariantCulture)</c> call over a <c>Vars</c>-shaped
    /// dictionary, proving the structural safety net holds even if a future provider
    /// regression were to leak a <see cref="SecretString"/> into <c>Vars</c> (§17).
    /// </para>
    /// </remarks>
    [Fact]
    public void SecretStringInVars_StringifiesToMarker()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x"] = new SecretString(TheValue),
        };

        // Reproduce the exact resolution Substitute_Helpers.Resolve performs for "{x}".
        Assert.True(vars.TryGetValue("x", out var val));
        Assert.NotNull(val);
        var resolved = Convert.ToString(val, CultureInfo.InvariantCulture);

        Assert.Equal(SecretString.RedactedMarker, resolved);
        Assert.DoesNotContain(TheValue, resolved!, StringComparison.Ordinal);
    }
}
