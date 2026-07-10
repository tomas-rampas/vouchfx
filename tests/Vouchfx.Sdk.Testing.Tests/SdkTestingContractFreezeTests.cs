// S08-F-02 / M4 follow-up — Freeze the Vouchfx.Sdk.Testing public-API surface.
//
// Vouchfx.Sdk.Testing is the PUBLISHED provider test-harness consumers compile their
// own test projects against (ProviderTestHarness, StepRunResult, and the three
// Contexts/{TestBindingContext,TestProjectContext,TestCompileContext}).  Its public
// surface must be FROZEN so a v1.x change cannot silently break downstream provider
// test projects — exactly as SdkContractFreezeTests freezes the v1 PROVIDER contract
// (Vouchfx.Sdk) and EventContractFreezeTests freezes the v1 EVENT-WIRE contract.
//
// SCOPE — the harness's OWN public assembly surface (and only that):
//   • SdkPublicApiSignature.Build reflects over the types DECLARED in the
//     Vouchfx.Sdk.Testing assembly only.  It does NOT recurse into the engine
//     assemblies.  It DOES name the engine/SDK types the harness transitively
//     EXPOSES in its member signatures — e.g. StepRunResult.Verdict is
//     `Vouchfx.Engine.Abstractions.Verdict?`, the contexts return
//     `Vouchfx.Sdk.CaptureExpr` — because those names ARE part of the harness's
//     compile-against contract.  We deliberately do NOT freeze the whole engine
//     assemblies here (those evolve at engine cadence); we freeze the slice of them
//     the harness pins by exposing it.
//
// The canonicalisation is the SAME SdkPublicApiSignature shared from
// Vouchfx.TestSupport that SdkContractFreezeTests uses, so this golden and the
// Vouchfx.Sdk golden mean the same thing and are byte-comparable.
//
// Robustness: like SchemaFreezeTests / SdkContractFreezeTests, both sides are
// newline-normalised (CRLF/CR → LF) with any trailing final newline trimmed, so a
// line-ending difference or an editor's insert_final_newline rewrite of the golden
// never produces spurious drift.
// REGENERATION (when the harness's published surface legitimately changes —
// additive only for v1.x):
//   VOUCHFX_REGEN_SDK_TESTING_CONTRACT=1 dotnet test tests/Vouchfx.Sdk.Testing.Tests \
//     --filter "FullyQualifiedName~SdkTestingContractFreezeTests"
//   This rewrites Golden/vouchfx-sdk-testing-public-api.v1.txt from the
//   freshly-reflected surface.  Review the diff (must be additive only), then
//   commit.  Mirror of SchemaFreezeTests.IsRegenRequested / VOUCHFX_REGEN_SCHEMA.
using System;
using System.IO;
using System.Linq;
using Vouchfx.Sdk.Testing;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Sdk.Testing.Tests;

/// <summary>
/// The frozen-<c>Vouchfx.Sdk.Testing</c>-public-API golden-snapshot gate.  Asserts the
/// reflected public surface of the harness assembly is byte-for-byte (newline-normalised)
/// identical to the committed golden, so any change to what consumers compile against is
/// a deliberate, reviewed act.
/// </summary>
public sealed class SdkTestingContractFreezeTests
{
    /// <summary>
    /// Banner-line-1 title for the <c>Vouchfx.Sdk.Testing</c> golden.  This surface is a
    /// VERSIONED test-harness surface — NOT the frozen v1 <c>Vouchfx.Sdk</c> provider
    /// contract — so it carries its own self-describing title (the shared
    /// <c>SdkPublicApiSignature.Build</c> banner-line-1 must not mislabel it as the frozen
    /// provider contract).
    /// </summary>
    private const string BannerTitle =
        "Vouchfx.Sdk.Testing public surface — freeze-gated (a versioned testing surface, NOT the frozen v1 Vouchfx.Sdk provider contract).";

    /// <summary>
    /// The reflected public API of <c>Vouchfx.Sdk.Testing</c> must be byte-for-byte
    /// (newline-normalised) identical to the committed golden.  If this fails, the
    /// published test-harness surface has drifted.
    /// </summary>
    [Fact]
    public void VouchfxSdkTestingPublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(typeof(ProviderTestHarness).Assembly, BannerTitle);

        // Regeneration mode: rewrite the committed golden from the freshly-reflected
        // surface and pass.  Mirror of SdkContractFreezeTests / SchemaFreezeTests.
        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Sdk.Testing.Tests",
                "Golden", "vouchfx-sdk-testing-public-api.v1.txt");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden();

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The Vouchfx.Sdk.Testing public API surface has DRIFTED. This is the published "
            + "provider test-harness that downstream test projects compile against — its "
            + "surface is FROZEN for the v1.x engine series and evolves ADDITIVELY only. If "
            + "this change is intentional, regenerate "
            + "Golden/vouchfx-sdk-testing-public-api.v1.txt with VOUCHFX_REGEN_SDK_TESTING_CONTRACT=1 "
            + "(capture the SdkPublicApiSignature.Build output verbatim, do not hand-author it) "
            + "and get it reviewed; otherwise revert."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    /// <summary>
    /// The harness's key published types must remain present in BOTH the committed golden
    /// AND the LIVE reflected surface.  This coarse completeness guard complements the
    /// byte-for-byte golden: it catches an accidental surface SHRINK (a removed/renamed
    /// public type) with a harness-specific message even before the golden diff is read.
    /// A removal is a BREAKING change for every downstream provider test project and must
    /// never happen silently in v1.x.  Asserting against the LIVE
    /// <see cref="SdkPublicApiSignature.Build"/> output (not only the committed golden)
    /// makes the guard's promise literally true against the live assembly — so it cannot
    /// pass on a stale golden after a type has actually been removed from the harness.
    /// </summary>
    [Fact]
    public void KeyPublishedTypes_RemainPresentInGoldenAndLiveSurface()
    {
        // The published harness contract: the entry point, its result record, and the
        // three reusable engine-context stand-ins a provider author drives a provider with.
        string[] requiredHeaders =
        {
            "class Vouchfx.Sdk.Testing.ProviderTestHarness",
            "record Vouchfx.Sdk.Testing.StepRunResult",
            "class Vouchfx.Sdk.Testing.Contexts.TestBindingContext",
            "class Vouchfx.Sdk.Testing.Contexts.TestProjectContext",
            "class Vouchfx.Sdk.Testing.Contexts.TestCompileContext",
        };

        // (1) Defend the committed golden artifact.
        AssertHeadersPresent(Normalise(ReadGolden()), requiredHeaders, "golden");

        // (2) Independently defend the LIVE reflected surface, so the guard cannot pass on
        //     a stale golden after a type was actually removed/renamed in the assembly.
        var live = Normalise(
            SdkPublicApiSignature.Build(typeof(ProviderTestHarness).Assembly, BannerTitle));
        AssertHeadersPresent(live, requiredHeaders, "live reflected surface");
    }

    // Asserts every required type header is present in the supplied signature text.
    private static void AssertHeadersPresent(string signature, string[] requiredHeaders, string source)
    {
        var lines = signature.Split('\n');

        foreach (var header in requiredHeaders)
        {
            // Each type header is a line that STARTS with the kind+name (a record/class
            // line may carry a ": <bases>" suffix), so match on a prefix.
            Assert.True(
                lines.Any(line =>
                    line.Equals(header, StringComparison.Ordinal)
                    || line.StartsWith(header + " :", StringComparison.Ordinal)),
                $"Frozen Vouchfx.Sdk.Testing public type '{header}' is missing from the {source}. "
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
    /// Set <c>VOUCHFX_REGEN_SDK_TESTING_CONTRACT=1</c> to make the gate REWRITE the
    /// committed golden <c>Golden/vouchfx-sdk-testing-public-api.v1.txt</c> from the
    /// freshly-reflected surface instead of asserting against it.  Mirror of
    /// <c>SdkContractFreezeTests.RegenEnvVar</c> / <c>VOUCHFX_REGEN_SCHEMA</c>.
    /// </summary>
    private const string RegenEnvVar = "VOUCHFX_REGEN_SDK_TESTING_CONTRACT";

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the directory
    /// containing <c>vouchfx.sln</c> — the repo root.  Mirrors
    /// <c>SdkContractFreezeTests.FindRepoRoot</c>.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').  This gate must locate "
            + "the source-tree golden to rewrite it.");
    }

    /// <summary>
    /// Reads the committed golden artifact from the test assembly's output directory
    /// (shipped as a copied <c>Content</c> item under <c>Golden/</c>).
    /// </summary>
    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "vouchfx-sdk-testing-public-api.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden Vouchfx.Sdk.Testing public-API signature not found at '{path}'. The "
            + "freeze gate requires Golden/vouchfx-sdk-testing-public-api.v1.txt to be "
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
