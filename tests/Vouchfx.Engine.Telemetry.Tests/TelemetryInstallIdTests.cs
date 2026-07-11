// Vouchfx.Engine.Telemetry.Tests — TelemetryInstallId parsing.
//
// The VOUCHFX_TELEMETRY_INSTALL_ID parser is tri-state: absent is valid (null id),
// a parseable non-empty GUID is valid, and everything else — including the
// degenerate all-zero Guid.Empty — is invalid.  Pure, so no environment mutation.

using Vouchfx.Engine.Telemetry;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetryInstallIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TryParse_Unset_ReturnsTrueWithNullId(string? raw)
    {
        Assert.True(TelemetryInstallId.TryParse(raw, out var id));
        Assert.Null(id);
    }

    [Theory]
    [InlineData("8f7c2f0a-1b2c-4d3e-9f4a-5b6c7d8e9f0a")]                    // D format
    [InlineData("8F7C2F0A-1B2C-4D3E-9F4A-5B6C7D8E9F0A")]                    // uppercase D
    [InlineData("8f7c2f0a1b2c4d3e9f4a5b6c7d8e9f0a")]                        // N format
    [InlineData("{8f7c2f0a-1b2c-4d3e-9f4a-5b6c7d8e9f0a}")]                  // B format
    [InlineData("  8f7c2f0a-1b2c-4d3e-9f4a-5b6c7d8e9f0a  ")]                // padded
    public void TryParse_ValidGuid_ReturnsTrueWithParsedId(string raw)
    {
        Assert.True(TelemetryInstallId.TryParse(raw, out var id));
        Assert.Equal(Guid.Parse("8f7c2f0a-1b2c-4d3e-9f4a-5b6c7d8e9f0a"), id);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("8f7c2f0a-1b2c-4d3e-9f4a")]   // truncated
    public void TryParse_Garbage_ReturnsFalse(string raw)
    {
        Assert.False(TelemetryInstallId.TryParse(raw, out var id));
        Assert.Null(id);
    }

    [Fact]
    public void TryParse_EmptyGuid_ReturnsFalse()
    {
        // The all-zero GUID is a degenerate value, never a usable identity.
        Assert.False(TelemetryInstallId.TryParse(Guid.Empty.ToString(), out var id));
        Assert.Null(id);
    }

    [Fact]
    public void EnvVar_IsTheDocumentedName()
    {
        Assert.Equal("VOUCHFX_TELEMETRY_INSTALL_ID", TelemetryInstallId.EnvVar);
    }
}
