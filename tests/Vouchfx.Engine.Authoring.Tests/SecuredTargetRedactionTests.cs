// Vouchfx.Engine.Authoring.Tests — SecuredTarget disclosure guard (#408).

using System.Reflection;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// #408: <see cref="SecuredTarget.ToString()"/> must never expand the
/// <see cref="SecuritySpec"/> it carries, because that spec's own
/// <c>ToString()</c> prints <see cref="SecuritySpec.ClientKeyPassword"/>.
/// </summary>
public sealed class SecuredTargetRedactionTests
{
    private const string Canary = "P@ssw0rd-LEAK-CANARY";

    /// <summary>CA1861 is an error in this project, so the expected set is a field.</summary>
    private static readonly string[] s_expectedMembers = { "Kind", "Name", "Security" };

    private static SecuredTarget Target() => new(
        "api",
        "service",
        new SecuritySpec(
            Profile: "mtls",
            Endpoint: "kafka://localhost:9092",
            CaCert: "./certs/ca.pem",
            ClientCert: "./certs/client.pem",
            ClientKey: "./certs/client.key",
            ServerArtifacts: null)
        {
            ClientKeyPassword = Canary,
        });

    /// <summary>
    /// The defect exactly as reported: red before the guard, where the compiler-generated
    /// <c>ToString()</c> emitted <c>... ClientKeyPassword = P@ssw0rd-LEAK-CANARY }</c>.
    /// </summary>
    [Fact]
    public void ToString_DoesNotDiscloseTheClientKeyPassword()
    {
        var rendered = Target().ToString();

        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientKeyPassword", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Interpolation is the shape the hazard actually arrives in — a diagnostic, event or log
    /// line that names the target — so it is pinned separately from the direct call.
    /// </summary>
    [Fact]
    public void Interpolation_DoesNotDiscloseTheClientKeyPassword()
    {
        var target = Target();

        Assert.DoesNotContain(Canary, $"probing {target}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Redaction must not read as absence: a target that HAS a security block and one that
    /// merely defaulted must not render identically, or a reader concludes the wrong thing.
    /// </summary>
    [Fact]
    public void ToString_StillIdentifiesTheTargetAndMarksTheRedaction()
    {
        var rendered = Target().ToString();

        Assert.Contains("Name = api", rendered, StringComparison.Ordinal);
        Assert.Contains("Kind = service", rendered, StringComparison.Ordinal);
        Assert.Contains("<redacted>", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closes the drift objection that made <see cref="SecuritySpec"/> refuse a hand-written
    /// <c>PrintMembers</c>: an explicit override cannot enumerate a member that does not exist
    /// yet, so a fourth member would be silently unprinted. Pinning the count turns that silence
    /// into a failing test, forcing a conscious decision about whether the new member belongs in
    /// the redacted rendering.
    /// </summary>
    [Fact]
    public void SecuredTarget_HasExactlyTheThreeMembersPrintMembersEnumerates()
    {
        var members = typeof(SecuredTarget)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(s_expectedMembers, members);
    }
}
