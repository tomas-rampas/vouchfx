// Tests for the SHARED property assertion itself — Vouchfx.TestSupport.HostPathDisclosure.
//
// WHY THIS FILE EXISTS AT ALL, which is a finding rather than a tidy-up. #473 lifted
// AssertNoAbsoluteHostPath out of two divergent private copies into one shared method, and folded
// the stronger copy's JSON-escaped check into it. A drill then removed that folded check and the
// whole suite stayed GREEN — 20 of 20 in SecurityPathDisclosureLedgerTests — because not one of the
// seven call sites feeds it a JSON-escaped host directory. The fold was inert: a strengthening that
// strengthened nothing measurable, in an assertion seven call sites across three assemblies now
// depend on and which had no test of its own.
//
// The predicate is a pure string function with no dependency on the engine, so pinning it directly
// costs almost nothing and is the only thing that makes the drill mean anything. An assertion used
// this widely, whose failure mode is a SILENT PASS on a real leak, is the last place to rely on its
// call sites happening to cover it.
//
// WHY HERE. Vouchfx.TestSupport is BCL-only and has no test project of its own;
// Vouchfx.Engine.Orchestration.Tests references it, holds #473's other new tests, and is the
// assembly that could not see the private copy in the first place — the fact that forced the lift.
//
// THE FIXTURE PATHS ARE WINDOWS-SHAPED ON EVERY PLATFORM, deliberately. The predicate is pure
// string work and touches no filesystem, and JSON's backslash escaping is mandated by the format
// rather than by the host OS, so `C:\work\...` escapes to `C:\\work\\...` identically everywhere.
// Using the RUNNING platform's own temp path would make the escaped form a no-op on POSIX (no
// backslashes to escape) and the discriminating arm below would silently stop discriminating.
using System;
using System.Text.Json;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// The shared <see cref="HostPathDisclosure.AssertNoAbsoluteHostPath"/> predicate, pinned directly.
/// </summary>
public sealed class HostPathDisclosureTests
{
    /// <summary>A Windows-shaped host directory — see this file's header for why, on every OS.</summary>
    private const string HostDirectory = @"C:\work\vouchfx-suite";

    /// <summary>The author's own text: relative, and never a disclosure.</summary>
    private const string DeclaredPath = "./certs/client.pem";

    [Fact]
    public void RawHostDirectory_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => HostPathDisclosure.AssertNoAbsoluteHostPath(
                "a channel", $"cannot open {HostDirectory}\\certs\\ca.pem", HostDirectory));

        Assert.Contains("names the host directory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON-ESCAPED host directory is rejected, and the failure says which clause caught it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the arm the folded check exists for, and asserting the MESSAGE is what makes
    /// it discriminating rather than merely green.</strong> "It threw" is not enough: on Windows the
    /// generic rooted-token clause also fires for <c>C:\\work\\…</c>, because
    /// <see cref="System.IO.Path.IsPathRooted"/> needs only a drive and a separator and is
    /// indifferent to the doubling. So an arm that asserted only <c>Throws</c> would pass on
    /// Windows with the escaped check deleted, and the drill would report the fold as covered when
    /// it is not — which is exactly the failure this file was written in response to.
    /// </para>
    /// <para>
    /// Measured, with the escaped check removed: on Windows the message becomes "names an absolute
    /// host path" and this assertion fails; on POSIX <c>IsPathRooted</c> is false for a
    /// drive-letter path and nothing throws at all. Red on both, for different reasons.
    /// </para>
    /// <para>
    /// The raw form is deliberately ABSENT from the text — only the escaped form appears — so the
    /// plain-substring clause cannot be what catches it. That is the real shape: a resolved path
    /// serialised into an event line exists in the artifact only in its escaped form.
    /// </para>
    /// </remarks>
    [Fact]
    public void JsonEscapedHostDirectory_IsRejected_AndTheFailureNamesTheEscapedForm()
    {
        var escaped = JsonEncodedText.Encode(HostDirectory).ToString();

        // The premise, asserted rather than assumed: escaping really does change this string, and
        // the raw form really is absent from what the predicate will be shown.
        Assert.NotEqual(HostDirectory, escaped);
        var serialised = $$"""{"error":"cannot open {{escaped}}\\certs\\ca.pem"}""";
        Assert.DoesNotContain(HostDirectory, serialised, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostPathDisclosure.AssertNoAbsoluteHostPath(
                "an event line", serialised, HostDirectory));

        Assert.Contains("JSON-ESCAPED", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rooted path that is NOT the host directory is still rejected — the clause that generalises
    /// beyond the one leak a given case triggers.
    /// </summary>
    [Fact]
    public void RootedTokenOtherThanTheHostDirectory_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => HostPathDisclosure.AssertNoAbsoluteHostPath(
                "a channel", "cannot open /etc/pki/tls/ca.pem", HostDirectory));

        Assert.Contains("names an absolute host path", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The author's own relative path is ACCEPTED — the measured correction the predicate carries.
    /// </summary>
    /// <remarks>
    /// Trimming <c>.</c> from the FRONT rather than the end would turn <c>./certs/client.pem</c>
    /// into the rooted-looking <c>/certs/client.pem</c> and fail a correct diagnostic. That was
    /// found on the first run of the original test and is the single reason these rules must not
    /// have two copies; it is pinned here so the correction cannot be lost in a later edit.
    /// </remarks>
    [Fact]
    public void DeclaredRelativePath_IsAccepted()
        => HostPathDisclosure.AssertNoAbsoluteHostPath(
            "a channel", $"file '{DeclaredPath}' not found, relative to the suite directory.",
            HostDirectory);

    /// <summary>
    /// A <c>drive:</c>-shaped token with no separator is not a path reference and is ACCEPTED.
    /// </summary>
    /// <remarks>
    /// The separator clause is what keeps ordinary message text out of the net. Without it, a
    /// perfectly innocent <c>note:</c> or <c>C:</c> in prose would fail every diagnostic on Windows,
    /// where <see cref="System.IO.Path.IsPathRooted"/> is true for a bare drive specifier.
    /// </remarks>
    [Fact]
    public void DriveShapedTokenWithNoSeparator_IsAccepted()
        => HostPathDisclosure.AssertNoAbsoluteHostPath(
            "a channel", "seed could not connect; see C: note: retry", HostDirectory);

    /// <summary>
    /// A trailing full stop or colon does not turn a relative path into a rooted one.
    /// </summary>
    [Fact]
    public void RelativePathFollowedByPunctuation_IsAccepted()
        => HostPathDisclosure.AssertNoAbsoluteHostPath(
            "a channel", $"could not read '{DeclaredPath}'.", HostDirectory);
}
