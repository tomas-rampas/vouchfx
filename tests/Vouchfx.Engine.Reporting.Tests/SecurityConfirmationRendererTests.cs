using System.Xml.Linq;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class SecurityConfirmationRendererTests
{
    [Theory]
    [InlineData("TransportConfirmed", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "tls", false, true)]
    [InlineData("FutureLevel", "tls", false, true)]
    public void HtmlAndJunit_PreserveLevelProfileIdentityReplayAndCorrelation_WithoutCreatingVerdicts(
        string level, string profile, bool identity, bool replayed)
    {
        var record = new SecurityConfirmationEvent
        {
            RunId = "topology-run",
            TargetName = "<script>alert(1)</script>",
            TargetKind = "service",
            DeclaredProfile = profile,
            DeclaredEndpoint = "https",
            ObservedAddress = "localhost:443",
            ObservedProtocol = "Tls13",
            ClientIdentityResolved = identity,
            Level = level,
            Replayed = replayed,
        };
        var line = EventStreamJson.ToLine(record);
        using var junit = new StringWriter();
        JunitXmlRenderer.Render(new[] { line }, junit);
        var document = XDocument.Parse(junit.ToString());
        Assert.Equal("0", document.Root!.Attribute("tests")!.Value);
        var property = Assert.Single(document.Descendants("property"));
        Assert.Equal("vouchfx.securityConfirmation.0", property.Attribute("name")!.Value);
        var parsed = EventStreamJson.FromLine<SecurityConfirmationEvent>(property.Attribute("value")!.Value);
        Assert.Equal(record, parsed);
        using var html = new StringWriter();
        HtmlRenderer.Render(new[] { line }, html);
        Assert.Contains("Security confirmations", html.ToString(), StringComparison.Ordinal);
        Assert.Contains(level, html.ToString(), StringComparison.Ordinal);
        Assert.Contains("topology-run", html.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(record.TargetName, html.ToString(), StringComparison.Ordinal);
        Assert.Contains("A replay is not a fresh probe", html.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedSecurityEvent_IsCountedAsUnreadableRatherThanInventingEvidence()
    {
        const string line = """{"type":"security-confirmation","runId":"run"}""";
        using var junit = new StringWriter();
        JunitXmlRenderer.Render(new[] { line }, junit);
        var document = XDocument.Parse(junit.ToString());
        var property = Assert.Single(document.Descendants("property"));
        Assert.Equal("vouchfx.skippedEventLines", property.Attribute("name")!.Value);
        Assert.Equal("1", property.Attribute("value")!.Value);
    }
}
