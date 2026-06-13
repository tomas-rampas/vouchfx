// S08-F-02 / M4 follow-up — Freeze the Platform.Sdk.Testing public-API surface.
//
// Platform.Sdk.Testing is the PUBLISHED provider test-harness consumers compile their
// own test projects against (ProviderTestHarness, StepRunResult, and the three
// Contexts/{TestBindingContext,TestProjectContext,TestCompileContext}).  Its public
// surface must be FROZEN so a v1.x change cannot silently break downstream provider
// test projects — exactly as SdkContractFreezeTests freezes the v1 PROVIDER contract
// (Platform.Sdk) and EventContractFreezeTests freezes the v1 EVENT-WIRE contract.
//
// SCOPE — the harness's OWN public assembly surface (and only that):
//   • SdkPublicApiSignature.Build reflects over the types DECLARED in the
//     Platform.Sdk.Testing assembly only.  It does NOT recurse into the engine
//     assemblies.  It DOES name the engine/SDK types the harness transitively
//     EXPOSES in its member signatures — e.g. StepRunResult.Verdict is
//     `Platform.Engine.Abstractions.Verdict?`, the contexts return
//     `Platform.Sdk.CaptureExpr` — because those names ARE part of the harness's
//     compile-against contract.  We deliberately do NOT freeze the whole engine
//     assemblies here (those evolve at engine cadence); we freeze the slice of them
//     the harness pins by exposing it.
//
// The canonicalisation is the SAME SdkPublicApiSignature shared from
// Platform.TestSupport that SdkContractFreezeTests uses, so this golden and the
// Platform.Sdk golden mean the same thing and are byte-comparable.
//
// Robustness: like SchemaFreezeTests / SdkContractFreezeTests, both sides are
// newline-normalised (CRLF/CR → LF) with any trailing final newline trimmed, so a
// line-ending difference or an editor's insert_final_newline rewrite of the golden
// never produces spurious drift.
using System;
using System.IO;
using System.Linq;
using Platform.Sdk.Testing;
using Platform.Sdk.Testing.Contexts;
using Platform.TestSupport;
using Xunit;

namespace Platform.Sdk.Testing.Tests;

/// <summary>
/// The frozen-<c>Platform.Sdk.Testing</c>-public-API golden-snapshot gate.  Asserts the
/// reflected public surface of the harness assembly is byte-for-byte (newline-normalised)
/// identical to the committed golden, so any change to what consumers compile against is
/// a deliberate, reviewed act.
/// </summary>
public sealed class SdkTestingContractFreezeTests
{
    /// <summary>
    /// The reflected public API of <c>Platform.Sdk.Testing</c> must be byte-for-byte
    /// (newline-normalised) identical to the committed golden.  If this fails, the
    /// published test-harness surface has drifted.
    /// </summary>
    [Fact]
    public void PlatformSdkTestingPublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(typeof(ProviderTestHarness).Assembly);
        var golden = ReadGolden();

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The Platform.Sdk.Testing public API surface has DRIFTED. This is the published "
            + "provider test-harness that downstream test projects compile against — its "
            + "surface is FROZEN for the v1.x engine series and evolves ADDITIVELY only. If "
            + "this change is intentional, regenerate "
            + "Golden/platform-sdk-testing-public-api.v1.txt (capture the SdkPublicApiSignature.Build "
            + "output verbatim, do not hand-author it) and get it reviewed; otherwise revert."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    /// <summary>
    /// The harness's key published types must remain present in the frozen surface.  This
    /// coarse completeness guard complements the byte-for-byte golden: it catches an
    /// accidental surface SHRINK (a removed/renamed public type) with a harness-specific
    /// message even before the golden diff is read.  A removal is a BREAKING change for
    /// every downstream provider test project and must never happen silently in v1.x.
    /// </summary>
    [Fact]
    public void KeyPublishedTypes_RemainPresentInGolden()
    {
        var golden = Normalise(ReadGolden());

        // The published harness contract: the entry point, its result record, and the
        // three reusable engine-context stand-ins a provider author drives a provider with.
        string[] requiredHeaders =
        {
            "class Platform.Sdk.Testing.ProviderTestHarness",
            "record Platform.Sdk.Testing.StepRunResult",
            "class Platform.Sdk.Testing.Contexts.TestBindingContext",
            "class Platform.Sdk.Testing.Contexts.TestProjectContext",
            "class Platform.Sdk.Testing.Contexts.TestCompileContext",
        };

        var goldenLines = golden.Split('\n');

        foreach (var header in requiredHeaders)
        {
            // Each type header is a line that STARTS with the kind+name (a record/class
            // line may carry a ": <bases>" suffix), so match on a prefix.
            Assert.True(
                goldenLines.Any(line =>
                    line.Equals(header, StringComparison.Ordinal)
                    || line.StartsWith(header + " :", StringComparison.Ordinal)),
                $"Frozen Platform.Sdk.Testing public type '{header}' is missing from the golden. "
                + "The published harness surface is FROZEN — a public type may never be removed "
                + "or renamed in the v1.x engine series (that is a breaking change for every "
                + "downstream provider test project).");
        }
    }

    // ── Helpers (mirror SdkContractFreezeTests) ──────────────────────────────────

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the freeze
    // contract compares signature CONTENT, immune to line-ending style and to an
    // editor's insert_final_newline rewrite of the golden file.
    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    /// <summary>
    /// Reads the committed golden artifact from the test assembly's output directory
    /// (shipped as a copied <c>Content</c> item under <c>Golden/</c>).
    /// </summary>
    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "platform-sdk-testing-public-api.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden Platform.Sdk.Testing public-API signature not found at '{path}'. The "
            + "freeze gate requires Golden/platform-sdk-testing-public-api.v1.txt to be "
            + "committed and copied to output.");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Produces a short description of the first differing line between golden and actual,
    /// so a drift failure is diagnosable from the test output alone.
    /// </summary>
    private static string FirstDifference(string golden, string actual)
    {
        var goldenLines = golden.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(goldenLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var g = i < goldenLines.Length ? goldenLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (!string.Equals(g, a, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  golden: {g}"
                    + $"{Environment.NewLine}  actual: {a}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
