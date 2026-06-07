// Tests for CsxFragment.SanitiseId — BDD red phase for S01-F-01.
using Xunit;

namespace Platform.Sdk.Tests;

public sealed class SanitiseIdTests
{
    [Fact]
    public void SingleHyphen_MapsToUnderscore()
    {
        var result = CsxFragment.SanitiseId("orders-db");
        Assert.Equal("orders_db", result);
    }

    [Fact]
    public void MultipleHyphens_AllMappedToUnderscores()
    {
        var result = CsxFragment.SanitiseId("a-b-c-d");
        Assert.Equal("a_b_c_d", result);
    }

    [Fact]
    public void CleanId_UnchangedByRoundTrip()
    {
        const string clean = "orders_db";
        Assert.Equal(clean, CsxFragment.SanitiseId(clean));
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CsxFragment.SanitiseId(string.Empty));
    }

    [Fact]
    public void NoHyphens_ReturnsInputUnchanged()
    {
        const string input = "plainIdentifier123";
        Assert.Equal(input, CsxFragment.SanitiseId(input));
    }
}
