// Tests for S03-T0: VerifyMode enum — wire-token convention and member inventory.
// Written RED-first before the implementation exists.

using Platform.Engine.Abstractions;
using Xunit;

namespace Platform.Engine.Abstractions.Tests;

/// <summary>
/// Verifies the <see cref="VerifyMode"/> enum member inventory and the canonical
/// upper-case wire-token convention documented on the type.
/// </summary>
public sealed class VerifyModeTests
{
    // -------------------------------------------------------------------------
    // Member inventory
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyMode_HasExactlyTwoMembers()
    {
        // The enum must have exactly Immediate and Retry — no undocumented variants.
        var members = Enum.GetValues<VerifyMode>();

        Assert.Equal(2, members.Length);
        Assert.Contains(VerifyMode.Immediate, members);
        Assert.Contains(VerifyMode.Retry, members);
    }

    // -------------------------------------------------------------------------
    // Wire-token convention
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyMode_Immediate_WireToken_Is_IMMEDIATE()
    {
        // The DSL wire token for Immediate is "IMMEDIATE" — the member name upper-cased.
        Assert.Equal("IMMEDIATE", nameof(VerifyMode.Immediate).ToUpperInvariant());
    }

    [Fact]
    public void VerifyMode_Retry_WireToken_Is_RETRY()
    {
        // The DSL wire token for Retry is "RETRY" — the member name upper-cased.
        Assert.Equal("RETRY", nameof(VerifyMode.Retry).ToUpperInvariant());
    }
}
