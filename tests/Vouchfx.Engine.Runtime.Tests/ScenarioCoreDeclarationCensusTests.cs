// Vouchfx.Engine.Runtime.Tests — the attach property the deleted repair used to guarantee,
// enforced over ScenarioRunner.cs's own source. No Docker, no topology, no registry.
//
// WHY A SOURCE CENSUS AND NOT A BEHAVIOURAL TEST. Until issue #409, `ParallelSuiteRunner`
// re-attached its own `SecuredTargets.Enumerate` walk over whatever the core returned, so a core
// door that forgot to attach a declaration was invisible: the aggregator repaired it. #409 deleted
// that repair — correctly, because a second corrector for one value is how one rule comes to have
// two spellings — and in doing so made every one of the core's six doors individually load-bearing.
//
// Nothing was guarding them. MEASURED during the #409 review, on the fixed tree: deleting
// `.Declaring(declaredTargets)` from the merged pre-topology door reddens an existing test, but
// deleting it from the `catch (ArgumentException)` door — `EnvironmentMapper.Map`'s eager
// validation of `${conn:…}`, inside StartAsync and ahead of DCP — left Runtime 516/516 AND
// Cli 573/573 green. A secured suite whose only fault was `${conn:nosuchdependency}` would have
// silently stopped reporting its declaration unconfirmed under `--parallel`, which is exactly the
// breaking change the changelog records as delivered.
//
// The behavioural half of the fix is in `SecurityAssuranceMatrixTests`, whose secured arms now
// assert the security notice rather than only an exit code — Row 7 is the one that covers this
// door. This file is the OTHER half, and the two are not redundant: the matrix pins the doors that
// have a row, while a census pins the ones nobody has written a row for yet. A door added
// tomorrow gets no matrix row by default; it gets counted here the moment it is written.
//
// BOTH HALVES WERE THEN MEASURED TO FIRE, by re-running that same mutation against the guarded
// tree: with `.Declaring(declaredTargets)` deleted from the `${conn:}` door, this file's census
// FAILS (Runtime 517/518) and `Row07_UnknownConnReference_Secured_ExitsInconclusive(parallel: 1)`
// FAILS (Cli 572/573) — where before both suites were wholly green. Note which arm of Row 7 went
// red: `parallel: 1` and not `parallel: null`. That is the shape confirming the row reaches the
// door it claims to, because the sequential path answers from `RunSuiteAsync` and never enters
// this core at all.
//
// The census is deliberately syntactic. It cannot tell a correct attach from an incorrect one —
// that is the matrix's job — and it does not try. It answers one question: does every return from
// the core carry an attach at all.

using System.Text.RegularExpressions;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Every return from <c>ScenarioRunner.RunScenarioOwningTopologyAsync</c> attaches the caller's
/// declared targets (issue #409), enforced by counting the source rather than by exercising each
/// door.
/// </summary>
public sealed class ScenarioCoreDeclarationCensusTests
{
    /// <summary>
    /// A <c>return</c> of the core's result record, on any line that is not a comment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ScenarioCoreResult</c> is returned by exactly one method in this file — the core — so the
    /// whole-file count and the core-region count are the same number, and the regex needs no
    /// region delimiters to keep in step with. If a second method ever returns one, the equality
    /// this test asserts is the property that method needs too.
    /// </para>
    /// <para>
    /// <strong>The comment exclusion is the load-bearing part, and the anchor deliberately is
    /// not.</strong> Both of these expressions are quoted in prose a few lines from where they are
    /// executed — this fix's own comments do it — so a naive substring count reads those quotations
    /// as call sites and inflates both sides until they happen to balance. Excluding <c>//</c> lines
    /// fixes that. Requiring the expression to START the line would also have worked today, but it
    /// would break on a legal one-line reformat of a chained initialiser, turning a formatting
    /// change into a false failure about security.
    /// </para>
    /// </remarks>
    private const string CoreReturnPattern =
        @"^(?![ \t]*//)[^\r\n]*return new ScenarioCoreResult";

    /// <summary>The attach, counted the same way and for the same reasons.</summary>
    private const string AttachPattern =
        @"^(?![ \t]*//)[^\r\n]*\.Declaring\(declaredTargets\)";

    /// <summary>
    /// The implicit-conversion hazard: <c>ScenarioCoreResult</c> converts from
    /// <c>(Verdict, List&lt;string&gt;)</c> and that conversion defaults the assurance to
    /// <see cref="SecurityAssurance.None"/> — an EMPTY declaration, with nothing downstream to
    /// repair it any more.
    /// </summary>
    private const string BareTupleReturnPattern =
        @"^(?![ \t]*//)[^\r\n]*return \([^)]*buffer[^)]*\)[ \t]*;";

    /// <summary>
    /// The count of returns and the count of attaches must be equal: a door that returns without
    /// attaching reports an empty declaration, and <c>SecurityAssurance.Unconfirmed</c>'s
    /// <c>AuthoringFault</c> disjunct then reads <see langword="false"/> for a secured document the
    /// engine refused — a green pipeline on an unverified security assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Equality, not "at least one each".</strong> The weaker form passes on a core where
    /// five doors attach and one does not, which is the exact shape that shipped between the repair
    /// being deleted and this test being written.
    /// </para>
    /// <para>
    /// <strong>The return count is asserted non-zero BEFORE the equality</strong>, because
    /// <c>0 == 0</c> satisfies an equality perfectly — the failure mode every source-census test
    /// has, reached here by any rename that made both regexes match nothing. The attach count needs
    /// no separate non-zero check: it is compared against a count already proved positive.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCoreReturn_AttachesTheCallersDeclaredTargets()
    {
        var runner = ScenarioRunnerSource();

        var returns = Regex.Count(runner, CoreReturnPattern, RegexOptions.Multiline);
        var attaches = Regex.Count(runner, AttachPattern, RegexOptions.Multiline);

        Assert.True(
            returns > 0,
            "No `return new ScenarioCoreResult` was found in ScenarioRunner.cs at all — most "
            + "likely the core's result record was renamed. Update this census to the new name "
            + "rather than leaving it matching nothing: a census that matches nothing satisfies its "
            + "own equality and passes for free, which is the one way this file can stop guarding "
            + "anything without saying so.");

        Assert.True(
            attaches == returns,
            $"ScenarioRunner.cs returns a ScenarioCoreResult from {returns} site(s) but attaches "
            + $"the caller's declaration at {attaches} of them. Every door must call "
            + "`.Declaring(declaredTargets)`: since issue #409 deleted ParallelSuiteRunner's "
            + "re-attach, a door that omits it hands back an EMPTY `Declared`, and a secured "
            + "document refused at that door then reads `Unconfirmed == false` — the run reports a "
            + "clean exit on a `security` block it never confirmed. Add the attach; do not relax "
            + "this count.");
    }

    /// <summary>
    /// The core must never return the pre-slice-E tuple shape: the implicit conversion silently
    /// defaults the assurance, which is the one way to reintroduce #409 without touching any of the
    /// attaches the census above counts.
    /// </summary>
    /// <remarks>
    /// The hazard is named on <c>ScenarioCoreResult.Assurance</c>'s own remarks. It is a hazard
    /// rather than a bug because the conversion is wanted — the test doubles and the callers that
    /// never asked about security both use it — so it cannot simply be removed, and a check is what
    /// is left. Scoped to the tuple built from the core's <c>buffer</c> local, which is the shape
    /// every one of its doors would use.
    /// </remarks>
    [Fact]
    public void TheCore_NeverReturnsTheBareTupleShape()
    {
        var runner = ScenarioRunnerSource();

        var bareReturns = Regex.Matches(runner, BareTupleReturnPattern, RegexOptions.Multiline);

        Assert.True(
            bareReturns.Count == 0,
            "ScenarioRunner.cs returns a bare `(verdict, buffer)` tuple, which converts implicitly "
            + "to ScenarioCoreResult and defaults its Assurance to SecurityAssurance.None — an "
            + "empty declaration that nothing repairs since issue #409. Return the record "
            + "explicitly with `.Declaring(declaredTargets)`. Found: "
            + string.Join(" | ", bareReturns.Select(m => m.Value.Trim())));
    }

    /// <summary>
    /// Reads <c>ScenarioRunner.cs</c> from the repository, walking up from the test assembly's
    /// output directory to the solution file — the same shape
    /// <c>TransportNoticeEventEmissionTests</c> uses for its own source census.
    /// </summary>
    private static string ScenarioRunnerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName, "src", "Engine", "Vouchfx.Engine.Runtime", "ScenarioRunner.cs");

        Assert.True(
            File.Exists(path),
            $"ScenarioRunner.cs was not found at '{path}'. This census reads the engine's source "
            + "directly; a moved file must move this path with it, not silently stop checking.");

        return File.ReadAllText(path);
    }
}
