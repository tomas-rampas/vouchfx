using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SecurityConfirmationEventEmissionTests
{
    [Theory]
    [InlineData(SecurityConfirmationLevel.TransportConfirmed, "mtls", true, false)]
    [InlineData(SecurityConfirmationLevel.AuthenticatedRoundTrip, "mtls", true, false)]
    [InlineData(SecurityConfirmationLevel.AuthenticatedRoundTrip, "tls", false, true)]
    public void Producer_PreservesActualProbeLevelAndReplay_WithoutPrivateMaterial(
        SecurityConfirmationLevel level, string profile, bool identityResolved, bool replayed)
    {
        var confirmation = new SecurityConfirmation("api", "service", profile, "https", "localhost:443", "Tls13",
            identityResolved, level, "PRIVATE-DETAIL-CERT-PATH-PASSWORD", new SecuredTargetIdentity("api", "PRIVATE-DIGEST"));
        var timestamp = DateTimeOffset.UtcNow;
        var line = Assert.Single(SecurityConfirmationEvents.ToLines(new[] { confirmation }, "run", timestamp, replayed));
        var record = EventStreamJson.FromLine<SecurityConfirmationEvent>(line);
        Assert.Equal(EventTypes.SecurityConfirmation, record.Type);
        Assert.Equal(level.ToString(), record.Level);
        Assert.Equal(profile, record.DeclaredProfile);
        Assert.Equal(identityResolved, record.ClientIdentityResolved);
        Assert.Equal(replayed, record.Replayed);
        Assert.Equal(timestamp, record.Timestamp);
        Assert.Equal("api", record.TargetName);
        Assert.Equal("service", record.TargetKind);
        Assert.Equal("https", record.DeclaredEndpoint);
        Assert.Equal("localhost:443", record.ObservedAddress);
        Assert.Equal("Tls13", record.ObservedProtocol);
        Assert.DoesNotContain("PRIVATE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConfirmations_ProducesNoSuccessfulEvidence() =>
        Assert.Empty(SecurityConfirmationEvents.ToLines(Array.Empty<SecurityConfirmation>(), "run", DateTimeOffset.UtcNow, false));
}
