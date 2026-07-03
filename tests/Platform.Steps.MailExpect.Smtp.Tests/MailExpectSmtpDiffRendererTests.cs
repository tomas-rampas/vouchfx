// Tests for MailExpectSmtpProvider — IStepDiffRenderer implementation.
//
// Covers:
//   1. CanRender: returns true for a Fail count-mismatch observation
//      {"matched":false,"expected":E,"actual":A}.
//   2. CanRender: returns false for a Pass observation {"matched":true,"count":N}.
//   3. CanRender: returns false for an EnvironmentError observation {"error":…}.
//   4. RenderDiff: produces a table with "expected" and "actual" rows.
using System;
using System.Text.Json;
using Platform.Steps.MailExpect.Smtp;
using Xunit;

namespace Platform.Steps.MailExpect.Smtp.Tests;

/// <summary>
/// Non-docker unit tests for <see cref="MailExpectSmtpProvider"/>'s
/// <see cref="IStepDiffRenderer"/> implementation.
/// </summary>
public sealed class MailExpectSmtpDiffRendererTests
{
    private readonly MailExpectSmtpProvider _provider = new();

    // ── 1. CanRender: Fail count-mismatch ────────────────────────────────────────

    [Fact]
    public void CanRender_FailCountMismatch_ReturnsTrue()
    {
        var json = JsonDocument.Parse("""{"matched":false,"expected":2,"actual":0}""");

        Assert.True(_provider.CanRender(json.RootElement));
    }

    // ── 2. CanRender: Pass observation ────────────────────────────────────────────

    [Fact]
    public void CanRender_PassObservation_ReturnsFalse()
    {
        var json = JsonDocument.Parse("""{"matched":true,"count":1}""");

        Assert.False(_provider.CanRender(json.RootElement));
    }

    // ── 3. CanRender: EnvironmentError observation ────────────────────────────────

    [Fact]
    public void CanRender_EnvironmentErrorObservation_ReturnsFalse()
    {
        var json = JsonDocument.Parse("""{"error":"HttpRequestException"}""");

        Assert.False(_provider.CanRender(json.RootElement));
    }

    // ── 4. RenderDiff: produces expected/actual table ─────────────────────────────

    [Fact]
    public void RenderDiff_FailObservation_ProducesTable()
    {
        var json = JsonDocument.Parse("""{"matched":false,"expected":3,"actual":1}""");

        var diff = _provider.RenderDiff(json.RootElement);

        Assert.NotNull(diff);
        Assert.Contains("expected", diff!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual", diff!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", diff!, StringComparison.Ordinal);
        Assert.Contains("1", diff!, StringComparison.Ordinal);
    }
}
