// Vouchfx.Cli.Tests — issue #480's LAST TWO HOPS, which no executable row in this repository can
// reach. A source census, for the same reason EnvironmentMapperLedgerHopCensusTests is one.
//
// THE HOPS. `SuiteResult.ProviderOrEngineFaultObserved` reaches the exit code through two
// hand-maintained passes of one value inside `RunCommand.ExecuteAsync`, and each is a separate
// chance to drop it:
//
//   1. ProviderPipeline guard      -> ValidationFailure.IsProviderOrEngineFault   set at the fault
//   2. Pass B / slot fold          -> SuiteResult.ProviderOrEngineFaultObserved   MixedSuiteEngine-
//                                                                                FaultTaxonomyTests
//   3. SuiteResult                 -> RunCommand's local                          *** THIS FILE ***
//   4. RunCommand's local          -> ComputeExitCode's named argument            *** THIS FILE ***
//   5. ComputeExitCode's argument  -> the integer                                 MixedSuiteEngine-
//                                                                                FaultExitCodeTests
//
// MEASURED, one mutation at a time, on the tree this file was written against. Delete hop 4 (the
// named argument): `dotnet build vouchfx.sln -warnaserror` still reports 0 errors and 0 WARNINGS,
// because the parameter is optional — and of the WHOLE non-Docker run of this project, exactly the
// two rows below go red: 2 failed / 618 passed / 620 total, and both names are in this file.
// Delete hop 3 (the read off `SuiteResult`): builds equally clean, and exactly one of the two below
// goes red — 1 failed / 619 passed / 620 total, the second row. (Both measured with
// `dotnet test … --no-build -m:1 --logger trx --filter "requires!=docker"`, counters read from the
// TRX.)
//
// THE SCOPE OF THAT MEASUREMENT IS THE PROJECT, NOT "THE ROWS THAT MENTION #480", and the change is
// deliberate. This paragraph used to count the rows mentioning the issue and assert that exactly
// two of them went red. That is a weaker claim measured against a hard number in prose, and the
// number rotted immediately: two `GitChangeSetTests` remarks mention #480 as well. They used to
// attribute an unrelated question to it — whether selection-infrastructure failure deserves an exit
// code of its own — and now name it only to retract that attribution; the string is there either
// way, so the count was wrong while the substantive claim was not. Counting rows is not how this
// guard is checked at all; the property is that no other row in this project can see either
// mutation, and the whole-project failure counts above state it directly. Under BOTH mutations
// every row of `MixedSuiteEngineFaultExitCodeTests` stays green, because those rows hand it to
// `ComputeExitCode` themselves; and the engine-side rows in `Vouchfx.Engine.Runtime.Tests` cannot
// see either mutation at all, since that project has no reference to `Vouchfx.Cli`. So without
// this file, both hops could be deleted with the whole tree green — and #480 would be fully back:
// the seam would answer with `ComputeExitCode`'s own `false` default, which is precisely the
// pre-fix behaviour.
//
// WHY A CENSUS AND NOT AN END-TO-END ROW, AND THE ANSWER IS TWO CSPROJ FACTS RATHER THAN A
// PREFERENCE. Driving `RunCommand.ExecuteAsync` over a real directory — which is exactly how
// `NothingExecutedExitCodeParityTests` closed the SAME gap for #369 — needs both halves of the
// mixed suite to be producible in ONE process, and they are not:
//   • The defect half needs a provider whose reflective SDK surface throws. The CLI's registry is a
//     sealed 25-assembly Core list (`ProviderRegistryFactory.CoreProviderAssemblies`), so no
//     throwing stub can be registered through the front door and no `.e2e.yaml` on disk can name
//     one.
//   • The sibling half must genuinely EXECUTE, which needs a topology, which needs DCP metadata.
//     This project deliberately does not set `IsAspireHost` (its csproj says so, and says why), so
//     no scenario can execute here at all.
// Both are properties of the FIXTURE and not of the fix: #369's parity rows needed neither a stub
// provider nor an executing scenario, which is why a real `ExecuteAsync` drive was available there
// and is not available here. Neither csproj is changed for this: making the lean CLI project an
// Aspire host, or shipping a throwing provider inside a Core assembly, would each buy one row at
// the cost of a boundary the whole test tree is shaped by.
//
// ROSLYN RATHER THAN A COMMENT-STRIPPING REGEX, which is the one place this file departs from
// EnvironmentMapperLedgerHopCensusTests's letter while keeping its whole argument. That file
// strips comments first, and says why: the call site it guards carries a comment naming the very
// argument it looks for, so a raw-text scan would let the EXPLANATION satisfy a census for a call
// that had lost the argument. `RunCommand.cs` has the same hazard several times over — it names
// `SuiteResult.ProviderOrEngineFaultObserved` in prose at the `ComputeExitCode` guard itself — and
// this project already owns the sharper tool for it: `AsciiRuntimeOutputCensusTests` parses these
// same sources with Roslyn, where comments are TRIVIA and no syntax walk can see them. The three
// properties that make the idiom work are kept exactly: the needle's match count is asserted
// BEFORE anything is concluded from it (a census matching nothing cannot fail), the argument must
// be NAMED, and a comment cannot satisfy it.

using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// <c>RunCommand.ExecuteAsync</c> reads issue #480's provenance off the runner's
/// <c>SuiteResult</c> and passes it to <see cref="RunCommand.ComputeExitCode"/> BY NAME.
/// </summary>
public sealed class MixedSuiteEngineFaultHopCensusTests
{
    /// <summary>The member the marker travels on, spelled once.</summary>
    private const string MemberName = "ProviderOrEngineFaultObserved";

    /// <summary>The parameter it must arrive at, spelled once.</summary>
    private const string ParameterName = "providerOrEngineFaultObserved";

    /// <summary>
    /// Hop 4: the one production call to <c>ComputeExitCode</c> passes
    /// <c>providerOrEngineFaultObserved:</c> as a NAMED argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The parameter is OPTIONAL, which is what makes the drop silent.</strong> It defaults
    /// to <see langword="false"/> so the engine's other callers and every pre-existing exit-code
    /// row keep compiling — the same trade `SuiteTopology.StartAsync`'s <c>pathDisclosures</c> makes
    /// against its ~60 Docker call sites — so deleting the argument here compiles, runs, and
    /// reports a clean exit 0 over a provider defect with the whole suite green.
    /// </para>
    /// <para>
    /// <strong>NAMED is a real constraint rather than a style rule</strong>, for the same reason it
    /// is at the ledger hop: this guard looks for the NAME, so a positional spelling would satisfy
    /// the compiler and not this file. It is also the only spelling that survives a future
    /// parameter being inserted ahead of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void RunCommand_PassesTheProvenanceMarkerToComputeExitCodeByName()
    {
        var call = TheProductionComputeExitCodeCall();

        var argument = call.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == ParameterName);

        Assert.True(
            argument is not null,
            $"RunCommand.ExecuteAsync's call to ComputeExitCode does not pass `{ParameterName}:`. "
            + "That parameter is OPTIONAL, so this compiles and runs clean while a provider or "
            + "engine defect beside a scenario that ran exits 0 again — the whole of #480 — and no "
            + "other test sees it: MixedSuiteEngineFaultExitCodeTests hands the argument in "
            + "itself, and MixedSuiteEngineFaultTaxonomyTests stops at the SuiteResult. Restore "
            + "the argument, NAMED (a positional argument satisfies the compiler but not this "
            + "guard, deliberately). Argument list found: "
            + call.ArgumentList.ToString());
    }

    /// <summary>
    /// Hop 3: the value that argument carries is read off the runner's own <c>SuiteResult</c> and
    /// is not re-derived at the seam.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two assertions, because the argument being named proves nothing about WHAT it carries. The
    /// first pins that the argument is a plain local rather than a literal — a hard-coded
    /// <c>false</c> would pass the row above and regress #480 completely — and the second pins that
    /// exactly one assignment anywhere in the file gives that local a
    /// <c>…&#46;ProviderOrEngineFaultObserved</c>.
    /// </para>
    /// <para>
    /// The assignment is matched on the MEMBER rather than on the receiver's spelling, so renaming
    /// the runner result local is not a breakage; and it is required to be UNIQUE, so a second
    /// assignment appearing later in the method (which would make the value depend on statement
    /// order) is a loud failure rather than a silent one.
    /// </para>
    /// </remarks>
    [Fact]
    public void RunCommand_TakesThatValueOffTheSuiteResult()
    {
        var root = ParsedRunCommand();
        var call = TheProductionComputeExitCodeCall();

        var argument = Assert.Single(
            call.ArgumentList.Arguments,
            a => a.NameColon?.Name.Identifier.ValueText == ParameterName);

        var local = Assert.IsType<IdentifierNameSyntax>(argument.Expression);

        var assignments = root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a =>
                a.Left is IdentifierNameSyntax left
                && left.Identifier.ValueText == local.Identifier.ValueText
                && a.Right is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == MemberName)
            .ToList();

        Assert.True(
            assignments.Count == 1,
            $"Expected exactly one `{local.Identifier.ValueText} = <suite result>.{MemberName}` "
            + $"assignment in RunCommand.cs, found {assignments.Count}. Zero means the local "
            + "handed to ComputeExitCode is no longer fed from the runner's SuiteResult, so the "
            + "seam silently answers with its own `false` default and #480 regresses in full. More "
            + "than one means the value depends on statement order, which is the same defect with "
            + "a longer fuse.");
    }

    /// <summary>
    /// The single production call — the declaration is excluded by shape, not by line number.
    /// </summary>
    /// <remarks>
    /// VACUITY FIRST, the rule this idiom turns on: a census whose needle matches nothing cannot
    /// fail, so the expected count is asserted before anything is concluded from it. If a SECOND
    /// call site is added it must pass the argument too and this count must be updated; if the
    /// method is renamed, re-point this guard rather than leaving it matching nothing.
    /// </remarks>
    private static InvocationExpressionSyntax TheProductionComputeExitCodeCall()
    {
        var calls = ParsedRunCommand()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "ComputeExitCode" }
                or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ComputeExitCode" })
            .ToList();

        Assert.True(
            calls.Count == 1,
            $"Expected exactly 1 ComputeExitCode call site in RunCommand.cs, found {calls.Count}.");

        return calls[0];
    }

    /// <summary>The parsed syntax tree of the CLI's <c>RunCommand.cs</c>.</summary>
    private static CompilationUnitSyntax ParsedRunCommand()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "Cli", "Vouchfx.Cli", "RunCommand.cs");

        Assert.True(
            File.Exists(path),
            $"RunCommand.cs not found at '{path}'; this guard cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the solution file — the same shape
    /// <c>AsciiRuntimeOutputCensusTests</c> uses to read CLI source directly.
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
