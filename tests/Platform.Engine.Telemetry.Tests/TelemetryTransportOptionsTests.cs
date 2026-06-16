// Platform.Engine.Telemetry.Tests — TelemetryTransportOptions (S12-G-01, Phase A).
//
// Proves the transport is OFF by default and configured only when both endpoint + token
// are present and the endpoint is a valid absolute http/https URI; and that the optional
// caps parse with conservative fallbacks.  Driven via FromValues so no real process
// environment variable is mutated.

using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class TelemetryTransportOptionsTests
{
    [Fact]
    public void Unconfigured_WhenEndpointAndTokenAbsent()
    {
        var options = TelemetryTransportOptions.FromValues(endpoint: null, token: null);
        Assert.False(options.IsConfigured);
        Assert.Null(options.Endpoint);
        Assert.Null(options.Token);
    }

    [Fact]
    public void Unconfigured_WhenOnlyEndpointPresent()
    {
        var options = TelemetryTransportOptions.FromValues("https://t.example", token: null);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Unconfigured_WhenOnlyTokenPresent()
    {
        var options = TelemetryTransportOptions.FromValues(endpoint: null, token: "tok");
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/relative/path")]
    [InlineData("ftp://host/path")]
    [InlineData("file:///etc/passwd")]
    public void Unconfigured_WhenEndpointIsNotAbsoluteHttpUri(string endpoint)
    {
        var options = TelemetryTransportOptions.FromValues(endpoint, "tok");
        Assert.False(options.IsConfigured);
        Assert.Null(options.Endpoint);
    }

    [Theory]
    [InlineData("https://telemetry.vouchfx.dev")]
    [InlineData("http://localhost:5000/ingest")]
    public void Configured_WhenEndpointIsAbsoluteHttpUri_AndTokenPresent(string endpoint)
    {
        var options = TelemetryTransportOptions.FromValues(endpoint, "tok");
        Assert.True(options.IsConfigured);
        Assert.Equal(endpoint, options.Endpoint!.ToString().TrimEnd('/'),
            ignoreCase: true);
        Assert.Equal("tok", options.Token);
    }

    [Theory]
    [InlineData("https://host/api?x=1")]
    [InlineData("https://host/api#frag")]
    [InlineData("https://host/api?x=1#frag")]
    [InlineData("https://host?x=1")]
    public void Unconfigured_WhenEndpointHasQueryOrFragment(string endpoint)
    {
        // Fail closed: a base query/fragment would be silently dropped by the downstream
        // relative-path resolution, so it is rejected rather than sent to an unintended URL.
        var options = TelemetryTransportOptions.FromValues(endpoint, "tok");
        Assert.False(options.IsConfigured);
        Assert.Null(options.Endpoint);
    }

    [Fact]
    public void Caps_DefaultWhenUnset()
    {
        var options = TelemetryTransportOptions.FromValues("https://t.example", "tok");
        Assert.Equal(TelemetryTransportOptions.DefaultMaxBytes, options.MaxBytes);
        Assert.Equal(TelemetryTransportOptions.DefaultMaxAgeDays, options.MaxAgeDays);
        Assert.Equal(TelemetryTransportOptions.DefaultMaxLines, options.MaxLines);
    }

    [Fact]
    public void Caps_ParseValidOverrides()
    {
        var options = TelemetryTransportOptions.FromValues(
            "https://t.example", "tok",
            maxBytes: "1048576", maxAgeDays: "7", maxLines: "250");

        Assert.Equal(1_048_576, options.MaxBytes);
        Assert.Equal(7, options.MaxAgeDays);
        Assert.Equal(250, options.MaxLines);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Caps_FallBackToDefaultsOnInvalidOverrides(string raw)
    {
        var options = TelemetryTransportOptions.FromValues(
            "https://t.example", "tok",
            maxBytes: raw, maxAgeDays: raw, maxLines: raw);

        Assert.Equal(TelemetryTransportOptions.DefaultMaxBytes, options.MaxBytes);
        Assert.Equal(TelemetryTransportOptions.DefaultMaxAgeDays, options.MaxAgeDays);
        Assert.Equal(TelemetryTransportOptions.DefaultMaxLines, options.MaxLines);
    }

    [Fact]
    public void WhitespaceTokenIsTreatedAsUnset()
    {
        var options = TelemetryTransportOptions.FromValues("https://t.example", "   ");
        Assert.False(options.IsConfigured);
        Assert.Null(options.Token);
    }
}
