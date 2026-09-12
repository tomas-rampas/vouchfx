// Vouchfx.Cli.Tests — a source census over the change-set relay's sanitisation SITE. No Docker.
//
// WHAT THE BEHAVIOURAL ROW CANNOT SEE. GitChangeSetTests.
// ChangedSinceFailure_SanitisesTheRelayedDiagnostic_BeforeTheSink drives an ESC through
// RunCommand's `catch (ChangeSetException)` arm and asserts it does not reach the sink. That row
// pins the INPUT, not the site: its escape arrives on the REF, and the ref is refused by
// GitChangeSet's argument guard. A change that moved the scrub into that guard and dropped it from
// the arm would leave the row green while GIT'S OWN STDERR — the input the arm exists for — went to
// the terminal raw.
//
// AND GIT'S STDERR IS EXACTLY WHAT NO IN-PROCESS ROW CAN PRODUCE: SelectScenarios builds its
// GitChangeSet with SystemProcessRunner.Instance, so no fake runner reaches it from outside
// RunCommand. The site is therefore asserted directly — the argument the arm hands WriteLineAsync
// IS a DisplaySanitiser.SanitiseForDisplay invocation — which needs no runner seam, no git and no
// work tree.
//
// ROSLYN RATHER THAN A TEXT SCAN, the house idiom (ProcessRunnerDiagnosticCensusTests,
// MixedSuiteEngineFaultHopCensusTests, AsciiRuntimeOutputCensusTests) and for the house reason: the
// arm explains its own sanitisation in a comment sitting directly above the call, so a raw-text
// match would let the EXPLANATION satisfy a census for code that had lost the property. Comments
// are trivia in Roslyn's model and no syntax walk can see them.
//
// VACUITY FIRST: the arm is asserted to exist, and to be the only one, before anything at all is
// concluded about what it writes.

using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ChangeSetRelaySanitisationCensusTests
{
    /// <summary>
    /// The <c>ChangeSetException</c> arm writes a sanitised message, asserted at the call site
    /// rather than through the one input a test can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The argument IS the sanitiser call, not merely a node somewhere inside it. A
    /// <c>Contains</c> over the descendants is satisfied by
    /// <c>$"{prefix} {ex.Message}" + Nothing(DisplaySanitiser.SanitiseForDisplay(other))</c> and by
    /// every other shape that keeps the call while relaying the raw message beside it.
    /// </para>
    /// <para>
    /// <strong>The receiver is asserted too.</strong> <c>SanitiseForDisplay</c> is a name, and a
    /// local helper of that name — the shape a "tidy-up" of a long line produces — would otherwise
    /// pass this census while doing nothing. Naming both halves means a genuine move to another
    /// sanitiser is a deliberate edit here rather than a silent one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ChangeSetExceptionArm_SanitisesWhatItWrites()
    {
        var root = ParsedSource(Path.Combine("src", "Cli", "Vouchfx.Cli", "RunCommand.cs"));

        var arms = root.DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Where(c => (c.Declaration?.Type as IdentifierNameSyntax)?.Identifier.ValueText
                == "ChangeSetException")
            .ToList();

        Assert.True(
            arms.Count == 1,
            $"Expected exactly 1 `catch (ChangeSetException)` arm in RunCommand.cs, found "
            + $"{arms.Count}. Zero means this census matches nothing and passes for free. More "
            + "than one is fine in itself — update this count — but read the new arm first: it is a "
            + "place git's own stderr reaches an operator, so it needs the same scrub.");

        var writes = arms[0].Block.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "WriteLineAsync",
            })
            .ToList();

        Assert.True(
            writes.Count == 1,
            $"Expected exactly 1 WriteLineAsync in the ChangeSetException arm, found "
            + $"{writes.Count}. The census asserts what the arm writes; a second write is a second "
            + "relay of the same message and needs the same treatment.");

        var arguments = writes[0].ArgumentList.Arguments;

        Assert.True(
            arguments.Count == 1,
            $"Expected the arm's WriteLineAsync to take 1 argument, found {arguments.Count}.");

        var relayed = Assert.IsType<InvocationExpressionSyntax>(arguments[0].Expression);
        var sanitiser = relayed.Expression as MemberAccessExpressionSyntax;

        Assert.True(
            sanitiser?.Name.Identifier.ValueText == "SanitiseForDisplay"
            && (sanitiser.Expression as IdentifierNameSyntax)?.Identifier.ValueText
                == "DisplaySanitiser",
            "The ChangeSetException arm writes `" + arguments[0].Expression
            + "` rather than a DisplaySanitiser.SanitiseForDisplay(...) call. That message is the "
            + "one place git's own bytes reach the terminal: a repository-configured "
            + "`filter.*.clean` or `core.fsmonitor` inherits git's stderr, so an OSC title "
            + "rewrite, an ESC[2J or a '\r' overwrite lands verbatim in a terminal or a CI log "
            + "(#266 Item 4). The behavioural row beside this one plants its escape on the REF, "
            + "which another guard also scrubs, so it stays green through this change.");
    }

    /// <summary>The parsed syntax tree of a repository-relative source file.</summary>
    /// <param name="relativePath">The path, relative to the repository root.</param>
    /// <returns>The compilation unit.</returns>
    private static CompilationUnitSyntax ParsedSource(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);

        Assert.True(File.Exists(path), $"'{path}' not found; this census cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetCompilationUnitRoot();
    }

    /// <summary>The repository root, found by walking up to the solution file.</summary>
    /// <returns>The absolute path of the directory holding <c>vouchfx.sln</c>.</returns>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(
            dir is not null,
            "No vouchfx.sln above " + System.AppContext.BaseDirectory + "; this census reads "
            + "source from the tree and cannot run outside it.");

        return dir!.FullName;
    }
}
