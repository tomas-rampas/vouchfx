// Vouchfx.Cli.Tests — two source censuses over the process-runner seam. No Docker.
//
// WHY CENSUSES AND NOT BEHAVIOURAL ROWS. Both properties below are true of code that no in-process
// row can reach, and both were introduced with an argument that applies to their own regression
// guard just as much as to the fix.
//
//   (1) #498 rewrote three throw sites in SystemProcessRunner to name `Path.GetFileName(fileName)`
//       rather than the resolved absolute path. Its own justification for touching sites nothing
//       prints today is that "unreachable-today is not a property to build on" — one later caller
//       that prints the message turns it into a live host-path leak. That reasoning does not stop
//       at the fix: a guard that only covered the reachable site would leave the other two free to
//       drift back, which is the same bet the fix refused to make.
//
//       Reaching them behaviourally would mean driving a real child into a faulted pipe read (the
//       capture site), a wedged grandchild (the timeout site) and a null Process handle (which the
//       BCL documents but which no supported launch produces). The first two exist as rows in
//       SystemProcessRunnerTests, but they assert the OUTCOME; the message is discarded by
//       GitChangeSet.RunGit on both paths, so no assertion on it can be made through the seam.
//
//   (2) IProcessRunner.Run's `environment` parameter is OPTIONAL and defaults to null, which the
//       runner reads as "inherit this process's block in full". That default is the exposure #500
//       closed for the one production caller. Nothing gates the next one: a second call site that
//       simply omits the argument compiles, runs, and re-opens the hole silently. There is no
//       behavioural test for a call site that does not exist yet.
//
// VACUITY FIRST in both rows: the match count is asserted before anything is concluded from it. A
// census whose needle stops matching reports no offenders and passes for free, which is the one way
// a file like this stops guarding anything without saying so.
//
// ROSLYN RATHER THAN A TEXT SCAN, the same choice AsciiRuntimeOutputCensusTests and
// MixedSuiteEngineFaultHopCensusTests make and for the same reason: both sources here explain the
// very thing being looked for in prose a few lines above it, so a raw-text match would let the
// EXPLANATION satisfy a census for code that had lost the property. Comments are trivia in Roslyn's
// model and no syntax walk can see them.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ProcessRunnerDiagnosticCensusTests
{
    /// <summary>
    /// The three runner exceptions whose message names the executable.
    /// </summary>
    private static readonly string[] RunnerExceptions =
    {
        "ProcessLaunchException",
        "ProcessCaptureException",
        "ProcessTimeoutException",
    };

    /// <summary>
    /// Every runner exception raised OUTSIDE a <c>catch</c> names the leaf file name, never the
    /// resolved path (#498).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The split is "inside a catch" versus "not", and it is the source's own split.</strong>
    /// One site keeps the absolute path deliberately — the <c>catch (Exception ex) when (ex is not
    /// ProcessLaunchException)</c> arm, whose message
    /// <c>GitChangeSetTests.LaunchFailure_DoesNotDiscloseTheResolvedPath</c> uses as the CONTROL for
    /// the mapped message not naming it. Exempting it by position rather than by an allow-list of
    /// line numbers means the exemption survives the file being edited, and asserting that it still
    /// uses the bare name means the control cannot be quietly "aligned" with its neighbours either.
    /// </para>
    /// <para>
    /// The <see cref="System.ArgumentException"/> raised by the fully-qualified-path guard is out of
    /// scope here: it is not one of the three failures <c>IProcessRunner</c> documents, and it names
    /// the argument the caller passed rather than a path this runner resolved.
    /// </para>
    /// <para>
    /// <strong>WHAT "THE RESOLVED PATH" MEANS HERE, and it is an identifier list rather than a
    /// property.</strong> The scan looks for the runner parameters that hold one —
    /// <c>fileName</c> and <c>workingDirectory</c> (<see cref="HostPathParameters"/>). A message
    /// that composed an absolute path from something else entirely is invisible to it; a syntax
    /// walk cannot evaluate a string. The list is what bounds the claim, and adding to it is how
    /// the claim widens.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRunnerException_NamesTheLeafFileName_ExceptTheDocumentedControl()
    {
        var root = ParsedSource(Path.Combine(
            "src", "Cli", "Vouchfx.Cli", "Selection", "SystemProcessRunner.cs"));

        var raised = root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type is IdentifierNameSyntax name
                && RunnerExceptions.Contains(name.Identifier.ValueText))
            .Where(c => c.Parent is ThrowStatementSyntax or ThrowExpressionSyntax)
            .ToList();

        Assert.True(
            raised.Count == 4,
            $"Expected 4 runner exceptions raised in SystemProcessRunner.cs, found {raised.Count}. "
            + "This census names the types it looks for; a rename leaves it matching nothing and "
            + "passing for free, so the count is asserted before anything is concluded from it.");

        var control = raised
            .Where(c => c.FirstAncestorOrSelf<CatchClauseSyntax>() is not null)
            .ToList();
        var guarded = raised
            .Where(c => c.FirstAncestorOrSelf<CatchClauseSyntax>() is null)
            .ToList();

        Assert.True(
            control.Count == 1,
            $"Expected exactly 1 runner exception raised from inside a catch (the documented "
            + $"resolved-path control), found {control.Count}.");
        Assert.True(
            guarded.Count == 3,
            $"Expected 3 runner exceptions raised outside a catch, found {guarded.Count}.");

        // The control still names the bare path: it is what GitChangeSetTests compares the mapped
        // message against, so a "tidy-up" that narrowed it would take the control with it.
        Assert.Contains(HostPathIdentifiers(control[0]), id => !IsLeafName(id));

        var offenders = guarded
            .SelectMany(HostPathIdentifiers)
            .Where(id => !IsLeafName(id))
            .Select(id => $"  line {Line(id)}: `{id.Parent}`")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} runner exception message(s) interpolate a resolved host path "
            + $"({string.Join("/", HostPathParameters)}) rather than its leaf name (#498). Since "
            + "#499 that value is an "
            + "absolute path, so the message names where git lives on the host — the #357 rule's "
            + "class, widened by #375/#473/#488. Unreachable-to-print today is not a defence: one "
            + "later caller that prints the message turns it into a live leak, which is the fix's "
            + "own argument.\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Every <c>IProcessRunner.Run</c> call site in <c>src/</c> supplies an environment block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>environment</c> is optional and <see langword="null"/> means "inherit this process's block
    /// in full" — the exposure #500 closed, and the shape a second caller falls into by writing
    /// nothing at all. The compiler cannot object; nothing else in this tree can either.
    /// </para>
    /// <para>
    /// <strong>The needle is the receiver's spelling, which is what bounds this census's
    /// claim.</strong>
    /// It matches a receiver naming a process runner (<c>processRunner</c>,
    /// <c>SystemProcessRunner.Instance</c>, a field). A call through a receiver named something else
    /// entirely — <c>runner.Run(...)</c> — is invisible to it. Stated rather than implied: this
    /// gates the shape the one production caller has and the shape a copy of it would have, not
    /// every conceivable spelling. A semantic model would close that, and would mean compiling the
    /// CLI inside the test.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryProcessRunnerCallSite_PassesAnEnvironment()
    {
        var repoRoot = RepositoryRoot();
        var sources = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(
                Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                System.StringComparison.Ordinal))
            .Where(f => !f.Contains(
                Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                System.StringComparison.Ordinal))
            .ToList();

        Assert.True(
            sources.Count > 0,
            $"This census found no .cs files under '{Path.Combine(repoRoot, "src")}'. Most likely "
            + "the tree moved; a census that reads no files reports no offenders.");

        var callSites = new List<(string File, InvocationExpressionSyntax Call)>();
        foreach (var file in sources)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)
                .GetCompilationUnitRoot();

            callSites.AddRange(root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Run",
                } access
                    && access.Expression.ToString().Contains(
                        "rocessRunner", System.StringComparison.Ordinal))
                .Select(i => (Path.GetRelativePath(repoRoot, file), i)));
        }

        Assert.True(
            callSites.Count == 1,
            $"Expected exactly 1 IProcessRunner.Run call site under src/, found {callSites.Count}: "
            + string.Join(", ", callSites.Select(c => c.File))
            + ". Zero means the needle stopped matching and this census guards nothing. More than "
            + "one is fine in itself — update this count — but read the new site first: the "
            + "`environment` argument is optional, so a caller that omits it silently hands git "
            + "this process's whole environment (#500).");

        var offenders = callSites
            .Where(c => !SuppliesEnvironment(c.Call))
            .Select(c => $"  {c.File}: `{c.Call}`")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} IProcessRunner.Run call site(s) supply no environment block. The "
            + "parameter is optional and null means INHERIT — so the child git receives every "
            + "variable this process holds, including whatever `${secret:env/NAME}` loaded "
            + "(#500).\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Whether <paramref name="call"/> passes the <c>environment</c> parameter at all.
    /// </summary>
    /// <param name="call">The <c>Run</c> invocation.</param>
    /// <returns><see langword="true"/> when it is passed by name or in the fourth position.</returns>
    /// <remarks>
    /// Either spelling counts: the production call passes it positionally, and a named argument is
    /// what a caller who skips <c>workingDirectory</c>'s neighbours would write. What is refused is
    /// the argument being ABSENT, which is the only spelling that means "inherit".
    /// <para>
    /// The positional count is over POSITIONAL arguments only. A plain <c>Count &gt;= 4</c> is
    /// satisfied by three positional arguments plus <c>cancellationToken:</c> by name — which is
    /// exactly the inheriting shape this row exists to refuse, passing it for free.
    /// </para>
    /// </remarks>
    private static bool SuppliesEnvironment(InvocationExpressionSyntax call)
    {
        var arguments = call.ArgumentList.Arguments;

        return arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "environment")
            || arguments.TakeWhile(a => a.NameColon is null).Count() >= 4;
    }

    /// <summary>
    /// The runner parameters that hold an absolute host path.
    /// </summary>
    /// <remarks>
    /// <c>workingDirectory</c> is here although no message interpolates it today: it is equally an
    /// absolute path, and it is precisely the value the sibling fix removed from
    /// <c>GitChangeSet</c>'s "git answered with no root" message. Matching only <c>fileName</c>
    /// let a message naming the other one pass silently, which is the same unreachable-today bet
    /// the fix this census guards refused to make.
    /// </remarks>
    private static readonly string[] HostPathParameters = { "fileName", "workingDirectory" };

    /// <summary>
    /// Every absolute-host-path identifier inside <paramref name="creation"/>'s arguments.
    /// </summary>
    private static IEnumerable<IdentifierNameSyntax> HostPathIdentifiers(
        ObjectCreationExpressionSyntax creation) =>
        creation.ArgumentList is null
            ? Enumerable.Empty<IdentifierNameSyntax>()
            : creation.ArgumentList.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(id => HostPathParameters.Contains(id.Identifier.ValueText));

    /// <summary>
    /// Whether <paramref name="identifier"/> is wrapped in a <c>Path.GetFileName(...)</c> call.
    /// </summary>
    private static bool IsLeafName(IdentifierNameSyntax identifier) =>
        identifier.Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "GetFileName",
                Expression: IdentifierNameSyntax { Identifier.ValueText: "Path" },
            });

    private static int Line(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    /// <summary>The parsed syntax tree of a repository-relative source file.</summary>
    private static CompilationUnitSyntax ParsedSource(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);

        Assert.True(File.Exists(path), $"'{path}' not found; this census cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetCompilationUnitRoot();
    }

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
