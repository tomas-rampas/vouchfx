// S05-B-03 — TerminalRenderer forward-compatibility for the new
// reproducibility-envelope event type.
//
// The terminal renderer (v0) has no case for reproducibility-envelope and must
// therefore silently ignore it via its default branch (§14 forward-compat): the
// envelope is for the JSON Lines consumers (reproducibility diffing, the Healer),
// not the terminal.  These tests prove the renderer does not throw and produces no
// spurious / garbled output for the new type — including when it carries a secret
// reference hash and fixture hashes.

using System.IO;
using System.Linq;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

/// <summary>
/// Forward-compatibility tests for rendering a <c>reproducibility-envelope</c>
/// event line through <see cref="TerminalRenderer"/> (S05-B-03).
/// </summary>
public sealed class ReproducibilityEnvelopeRenderTests
{
    private static string EnvelopeLine() =>
        EventStreamJson.ToLine(new ReproducibilityEnvelopeEvent
        {
            RunId = "run-render-test",
            ScenarioId = "order-flow",
            EnvSchemaVersion = ReproducibilityEnvelope.CurrentSchemaVersion,
            SecretReferences = new[]
            {
                new SecretReferenceDigest(
                    "env",
                    "a1b2c3d4e5f6071829304152637485960718293041526374859607182930a1b2"),
            },
            Fixtures = new[]
            {
                new FixtureDigest("fixtures/orders.sql", "deadbeefcafef00d"),
            },
        });

    [Fact]
    public void Render_ReproducibilityEnvelope_DoesNotThrowAndProducesNothing()
    {
        using var writer = new StringWriter();

        // Must not throw.
        TerminalRenderer.Render(new[] { EnvelopeLine() }, writer);

        // The renderer has no case for this type → no output at all.
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void Render_EnvelopeAmongKnownEvents_OnlyKnownEventsRendered()
    {
        var lines = new[]
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = "run-render-test",
                ScenarioId = "order-flow",
            }),
            EnvelopeLine(),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = "run-render-test",
                ScenarioId = "order-flow",
                Verdict = Vouchfx.Engine.Abstractions.Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The known events still render around the silently-ignored envelope line.
        Assert.Contains("order-flow", output);
        Assert.Contains("PASS", output);

        // No fragment of the envelope leaks into the rendered output.
        Assert.DoesNotContain("reproducibility-envelope", output, System.StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", output, System.StringComparison.Ordinal);
        Assert.DoesNotContain("a1b2c3d4", output, System.StringComparison.Ordinal);

        // Exactly two rendered lines (scenario started + scenario completed); the
        // envelope contributes none.
        var rendered = output
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        Assert.Equal(2, rendered.Length);
    }
}
