// Tests for S03-T0: VarKeys — reserved Vars-key conventions for engine bookkeeping.
// Written RED-first before the implementation exists.

using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests;

/// <summary>
/// Verifies the <see cref="VarKeys"/> constant and method contracts that define the
/// reserved namespace conventions within <c>ScriptGlobalVariables.Vars</c>.
/// </summary>
public sealed class VarKeysTests
{
    // -------------------------------------------------------------------------
    // ServicesPrefix constant
    // -------------------------------------------------------------------------

    [Fact]
    public void VarKeys_ServicesPrefix_Is_svc_colon_colon()
    {
        Assert.Equal("svc::", VarKeys.ServicesPrefix);
    }

    // -------------------------------------------------------------------------
    // Service key helper
    // -------------------------------------------------------------------------

    [Fact]
    public void VarKeys_Service_ReturnsExpectedKey()
    {
        Assert.Equal("svc::whoami", VarKeys.Service("whoami"));
    }

    [Fact]
    public void VarKeys_Service_PreservesServiceName_CaseSensitive()
    {
        // Key lookup against Vars is case-sensitive; the helper must not alter casing.
        Assert.Equal("svc::MyService", VarKeys.Service("MyService"));
    }

    [Fact]
    public void VarKeys_Service_ThrowsOnNullOrEmpty()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null
        // and ArgumentException for empty; both derive from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => VarKeys.Service(null!));
        Assert.Throws<ArgumentException>(() => VarKeys.Service(string.Empty));
    }

    // -------------------------------------------------------------------------
    // Outcome key helper
    // -------------------------------------------------------------------------

    [Fact]
    public void VarKeys_Outcome_ReturnsExpectedKey()
    {
        Assert.Equal("__outcome::ping_whoami", VarKeys.Outcome("ping_whoami"));
    }

    [Fact]
    public void VarKeys_Outcome_ThrowsOnNullOrEmpty()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null
        // and ArgumentException for empty; both derive from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => VarKeys.Outcome(null!));
        Assert.Throws<ArgumentException>(() => VarKeys.Outcome(string.Empty));
    }

    // -------------------------------------------------------------------------
    // Key collision guard — Service and Outcome prefixes must be distinct
    // -------------------------------------------------------------------------

    [Fact]
    public void VarKeys_ServiceAndOutcomePrefixes_DoNotCollide()
    {
        // A service key and an outcome key for the same name must never be equal.
        var serviceKey = VarKeys.Service("foo");
        var outcomeKey = VarKeys.Outcome("foo");

        Assert.NotEqual(serviceKey, outcomeKey);
    }
}
