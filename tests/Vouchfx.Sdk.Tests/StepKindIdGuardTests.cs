// Tests for StepKindId construction guards — S01-F-01 review fix (Change 6).
// Verifies that null or whitespace Family/Provider values are rejected eagerly
// so that a malformed registry key can never be constructed.
using Xunit;

namespace Vouchfx.Sdk.Tests;

public sealed class StepKindIdGuardTests
{
    // ── Valid construction ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_AcceptsValidFamilyAndProvider()
    {
        var id = new StepKindId("db-assert", "postgres");
        Assert.Equal("db-assert", id.Family);
        Assert.Equal("postgres", id.Provider);
    }

    [Fact]
    public void Constructor_AcceptsSingleCharacterValues()
    {
        var id = new StepKindId("a", "b");
        Assert.Equal("a", id.Family);
        Assert.Equal("b", id.Provider);
    }

    // ── Null guards ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenFamilyIsNull()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId(null!, "postgres"));
        Assert.Equal("family", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenProviderIsNull()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId("db-assert", null!));
        Assert.Equal("provider", ex.ParamName);
    }

    // ── Whitespace guards ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenFamilyIsEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId(string.Empty, "postgres"));
        Assert.Equal("family", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenFamilyIsWhitespace()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId("   ", "postgres"));
        Assert.Equal("family", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenProviderIsEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId("db-assert", string.Empty));
        Assert.Equal("provider", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenProviderIsWhitespace()
    {
        var ex = Assert.Throws<ArgumentException>(() => new StepKindId("db-assert", "   "));
        Assert.Equal("provider", ex.ParamName);
    }
}
