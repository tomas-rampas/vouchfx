// Vouchfx.Engine.Runtime.Tests - issue #480's NORMAL-COMPLETION carry, which no untraited row can
// reach. That return needs a topology that starts, so its only executable cover is
// MixedSuiteEngineFaultTaxonomyTests.RunSuiteAsync_ProviderDefectBesideAPassingSibling_..., and
// that row is [Trait("requires","docker")]. Delete the initialiser and the property takes its
// `false` default: blocking lane green, a plain `vouchfx run` over a mixed suite exits 0 again.
//
// The without-topology return IS covered untraited, twice over, so this file's value is the
// normal-completion one; it pins both because one rule with two spellings is how the second one
// rots.
//
// MEASURED, one mutation at a time, `dotnet build vouchfx.sln -warnaserror` clean (0 warnings,
// 0 errors) on both, and the two rows of this file run as a pair: setting that initialiser to
// `false` reddens ScenarioRunner_TakesThatMarkerFromTheAccumulator and leaves the other green;
// deleting it outright reddens ScenarioRunner_InitialisesTheFaultMarkerOnBothPostBindReturns and
// leaves the other green. Each mutation therefore has exactly one accuser.
//
// ROSLYN RATHER THAN A REGEX, for the reason DrillHostSweepCallSiteCensusTests gives: comments are
// trivia that no syntax walk can see, and ScenarioRunner.cs names this member in prose repeatedly.

using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Every <c>SuiteResult</c> <c>ScenarioRunner</c> can return from a run that bound a step carries
/// issue #480's provenance marker, taken from the accumulator rather than from a literal.
/// </summary>
public sealed class SuiteResultFaultMarkerCensusTests
{
    /// <summary>The property the marker travels on, spelled once.</summary>
    private const string MemberName = "ProviderOrEngineFaultObserved";

    /// <summary>The Pass-B accumulator it must be read from, spelled once.</summary>
    private const string LocalName = "providerOrEngineFaultObserved";

    /// <summary>
    /// <c>new SuiteResult(...)</c> constructions in <c>ScenarioRunner.cs</c>. MEASURED, not
    /// assumed: three.
    /// </summary>
    /// <remarks>
    /// The third is the no-scenario arm of <c>RunSuiteAsync</c>, which returns ABOVE the point
    /// where the accumulator is declared - deliberately, per that declaration's own comment, so
    /// that "no step was bound, therefore `false`" is a scoping property rather than a promise.
    /// It is the one construction that must NOT carry the marker.
    /// </remarks>
    private const int ExpectedConstructions = 3;

    /// <summary>The two that return from a run which reached Pass B.</summary>
    private const int ExpectedCarriers = 2;

    /// <summary>
    /// The normal-completion tail and the without-topology tail both initialise the marker.
    /// </summary>
    /// <remarks>
    /// MEASURED: deleting the normal-completion initialiser compiles clean - the property has a
    /// <c>false</c> default - and reddens this row.
    /// </remarks>
    [Fact]
    public void ScenarioRunner_InitialisesTheFaultMarkerOnBothPostBindReturns()
    {
        var carriers = MarkerInitialisers();

        Assert.True(
            carriers.Count == ExpectedCarriers,
            $"Expected exactly {ExpectedCarriers} `{MemberName} = ...` initialisers across "
            + $"ScenarioRunner.cs's SuiteResult constructions, found {carriers.Count}. Fewer means "
            + "a return that bound a step now answers with the property's `false` default, so a "
            + "provider defect beside a sibling exits 0 again - the whole of #480. More means a "
            + "construction that returns before any step was bound has started claiming a "
            + "provenance it cannot have.");
    }

    /// <summary>
    /// Each of those initialisers reads the Pass-B accumulator, never a literal.
    /// </summary>
    /// <remarks>
    /// The row above proves an initialiser exists; it says nothing about what it carries. A
    /// hard-coded <c>false</c> satisfies it and regresses #480 in full.
    /// </remarks>
    [Fact]
    public void ScenarioRunner_TakesThatMarkerFromTheAccumulator()
    {
        foreach (var initialiser in MarkerInitialisers())
        {
            var identifier = initialiser.Right as IdentifierNameSyntax;

            Assert.True(
                identifier?.Identifier.ValueText == LocalName,
                $"A `{MemberName} = ...` initialiser in ScenarioRunner.cs is fed by "
                + $"`{initialiser.Right}` rather than by `{LocalName}`. A literal there compiles, "
                + "runs, and reports a clean exit 0 over a provider defect - and the only other "
                + "cover for the normal-completion return is traited requires=docker, so the "
                + "blocking lane would stay green.");
        }
    }

    /// <summary>
    /// Every <c>MemberName = ...</c> initialiser inside a <c>new SuiteResult(...)</c>.
    /// </summary>
    /// <remarks>
    /// VACUITY FIRST: the construction count is asserted before anything is concluded from it, so
    /// a census whose needle has stopped matching fails loudly. The needle is the EXPLICIT
    /// spelling, so a rewrite to target-typed <c>new(...)</c> reddens this count rather than
    /// silently shrinking the population.
    /// </remarks>
    private static List<AssignmentExpressionSyntax> MarkerInitialisers()
    {
        var constructions = ParsedScenarioRunner()
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type is IdentifierNameSyntax { Identifier.ValueText: "SuiteResult" })
            .ToList();

        Assert.True(
            constructions.Count == ExpectedConstructions,
            $"Expected exactly {ExpectedConstructions} `new SuiteResult(...)` constructions in "
            + $"ScenarioRunner.cs, found {constructions.Count}. If you ADDED one it must decide "
            + $"`{MemberName}` too, and this count must be updated; if the shape moved, re-point "
            + "this guard rather than leaving it matching nothing.");

        return constructions
            .Where(c => c.Initializer is not null)
            .SelectMany(c => c.Initializer!.Expressions)
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax left
                && left.Identifier.ValueText == MemberName)
            .ToList();
    }

    /// <summary>The parsed syntax tree of the engine's <c>ScenarioRunner.cs</c>.</summary>
    private static CompilationUnitSyntax ParsedScenarioRunner()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Runtime", "ScenarioRunner.cs");

        Assert.True(
            File.Exists(path),
            $"ScenarioRunner.cs not found at '{path}'; this guard cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the solution file - the same shape
    /// <c>Vouchfx.Cli.Tests.MixedSuiteEngineFaultHopCensusTests</c> uses.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
