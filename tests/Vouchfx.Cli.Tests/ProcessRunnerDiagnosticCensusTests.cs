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
//       AND `null` IS NOT ABSENCE. `environment: null`, or a positional `null`/`default`, is the
//       SAME request to inherit, written out loud. A census that checked the parameter's mere
//       presence was therefore satisfiable by the exact shape it exists to forbid — which is the
//       failure mode a guard has to be read for, not the one it announces.
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
    /// <strong>THE RECEIVER IS RESOLVED AGAINST ITS DECLARED TYPE, NOT ITS SPELLING.</strong> This
    /// used to match a receiver whose text contained <c>rocessRunner</c> and to STATE, as a
    /// limitation, that <c>runner.Run(...)</c> was invisible to it. Stating a limit does not close
    /// it: a renamed local is a two-character edit, and the count assertion stays green at 1 while
    /// the census silently stops seeing the call it exists to police. So the first pass collects
    /// every name DECLARED with a process-runner type anywhere under <c>src/</c> — locals, fields,
    /// parameters, properties — and the second matches a <c>Run</c> whose receiver is one of them,
    /// or is a <c>new</c> of such a type, or still carries the old spelling.
    /// </para>
    /// <para>
    /// <strong>The residue, and it is the opposite direction from the old one.</strong> A type name
    /// is still matched by TEXT (<c>ProcessRunner</c>), so a runner interface renamed wholesale
    /// goes unseen — which the vacuity assertion below catches, because the production site would
    /// stop matching too. The name set is global rather than per-file, so a <c>runner</c> declared
    /// as a runner in one file makes every <c>runner.Run(...)</c> in <c>src/</c> a candidate. That
    /// over-matches, and over-matching is the safe direction: the worst case is this row demanding
    /// an <c>environment</c> argument of a call that did not need one, which reddens and is read,
    /// rather than a leak nobody is told about. A receiver declared in no source file at all — an
    /// inherited member from a referenced assembly — remains outside it; closing that needs a
    /// semantic model, and a semantic model needs the CLI compiled inside the test.
    /// </para>
    /// <para>
    /// <strong>A NULL ARGUMENT IS NOT AN ARGUMENT.</strong> The check used to be the mere
    /// PRESENCE of the parameter, which <c>environment: null</c> — and a positional <c>null</c> or
    /// <c>default</c> — satisfies while asking for exactly the inheriting behaviour this row
    /// forbids: the census was satisfiable by the shape it exists to refuse. The first fix for
    /// that left the same hole one character wide: <c>environment: null!</c> is a
    /// <c>SuppressNullableWarningExpression</c> wrapping the same literal, and it read as
    /// compliant. <see cref="IsNullOnSomePath"/> is syntactic and therefore bounded — a
    /// <c>null</c> arriving through a variable or a method call is still invisible, and its own
    /// remarks enumerate the rest — but the spellings it does see are the ones a caller writes.
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

        var roots = sources
            .Select(file => (File: file, Root: CSharpSyntaxTree
                .ParseText(File.ReadAllText(file), path: file)
                .GetCompilationUnitRoot()))
            .ToList();

        var runnerNames = RunnerTypedNames(roots.Select(r => r.Root));

        Assert.True(
            runnerNames.Count > 0,
            "This census found no declaration of a process-runner type under src/. The receiver "
            + "resolution is built from those declarations, so with none it degrades to the "
            + "spelling match it replaced and guards less than it claims.");

        var callSites = new List<(string File, InvocationExpressionSyntax Call)>();
        foreach (var (file, root) in roots)
        {
            callSites.AddRange(root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Run",
                } access
                    && IsProcessRunnerReceiver(access.Expression, runnerNames))
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
            $"{offenders.Count} IProcessRunner.Run call site(s) supply no environment block, or "
            + "supply one that is null on at least one path. The parameter is optional and null "
            + "means INHERIT — so the child git receives every variable this process holds, "
            + "including whatever `${secret:env/NAME}` loaded (#500).\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// <see cref="IsNullOnSomePath"/> sees every spelling of a null a caller writes at the site,
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows rather than a second census, because the predicate is where the census's whole
    /// strength lives and it is reachable directly. The census above can only ever exercise the
    /// ONE spelling the production call site happens to use today; every other spelling is
    /// guarded by this table or by nothing.
    /// </para>
    /// <para>
    /// The null-forgiving rows are the reason this table exists: <c>null!</c> and <c>default!</c>
    /// parse to a <c>PostfixUnaryExpressionSyntax</c> the first version of the predicate did not
    /// recurse through, so both read as compliant while handing the runner a null. The negative
    /// rows are equally load-bearing — a predicate that answered <see langword="true"/> for
    /// everything would satisfy every positive row and turn the census into a refusal of all
    /// call sites.
    /// </para>
    /// </remarks>
    [Theory]
    // Spelled null, and the same null behind every wrapper a caller writes at the site.
    [InlineData("null", true)]
    [InlineData("null!", true)]
    [InlineData("default", true)]
    [InlineData("default!", true)]
    [InlineData("default(IReadOnlyDictionary<string, string>)", true)]
    [InlineData("(IReadOnlyDictionary<string, string>)null", true)]
    [InlineData("(IReadOnlyDictionary<string, string>)null!", true)]
    [InlineData("((null))", true)]
    [InlineData("null as IReadOnlyDictionary<string, string>", true)]
    // Null on ONE path is still a run of git with this process's whole block.
    [InlineData("flag ? BuildBlock() : null", true)]
    [InlineData("flag ? null! : BuildBlock()", true)]
    [InlineData("cached ?? null", true)]
    // A real block, however it is obtained, is not an offender; nor is an EMPTY one.
    [InlineData("environment", false)]
    [InlineData("BuildBlock()", false)]
    [InlineData("new Dictionary<string, string>()", false)]
    [InlineData("EmptyBlock", false)]
    [InlineData("flag ? BuildBlock() : EmptyBlock", false)]
    [InlineData("cached ?? EmptyBlock", false)]
    public void IsNullOnSomePath_SeesEverySpellingOfNull(string expression, bool expected)
    {
        var parsed = SyntaxFactory.ParseExpression(expression);

        Assert.False(
            parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            $"'{expression}' did not parse, so this row proves nothing about the predicate.");

        Assert.Equal(expected, IsNullOnSomePath(parsed));
    }

    /// <summary>
    /// A type whose name marks its holder as a process runner, matched as a substring.
    /// </summary>
    /// <remarks>
    /// Covers <c>IProcessRunner</c> and <c>SystemProcessRunner</c> alike, and any nullable or
    /// qualified spelling of either, without this file having to enumerate them.
    /// </remarks>
    private const string RunnerTypeMarker = "ProcessRunner";

    /// <summary>
    /// Every identifier DECLARED with a process-runner type anywhere in the parsed sources.
    /// </summary>
    /// <param name="roots">The parsed compilation units.</param>
    /// <returns>The declared names, which become the receivers this census recognises.</returns>
    /// <remarks>
    /// Four declaration shapes, which between them are how a receiver comes to exist: a local or
    /// field (both <see cref="VariableDeclarationSyntax"/>), a parameter, and a property. A
    /// <c>var</c> local initialised from a runner is caught by the <c>new</c> clause in
    /// <see cref="IsProcessRunnerReceiver"/> only when the call is on the <c>new</c> itself; a
    /// <c>var</c> local holding a runner from a factory is the residue named in the row's remarks.
    /// </remarks>
    private static HashSet<string> RunnerTypedNames(IEnumerable<CompilationUnitSyntax> roots)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var root in roots)
        {
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclarationSyntax declaration
                        when NamesRunnerType(declaration.Type):
                        foreach (var variable in declaration.Variables)
                        {
                            names.Add(variable.Identifier.ValueText);
                        }

                        break;

                    case ParameterSyntax parameter when NamesRunnerType(parameter.Type):
                        names.Add(parameter.Identifier.ValueText);
                        break;

                    case PropertyDeclarationSyntax property when NamesRunnerType(property.Type):
                        names.Add(property.Identifier.ValueText);
                        break;

                    default:
                        break;
                }
            }
        }

        return names;
    }

    /// <summary>Whether a declared type is a process-runner type.</summary>
    private static bool NamesRunnerType(TypeSyntax? type) =>
        type is not null
        && type.ToString().Contains(RunnerTypeMarker, System.StringComparison.Ordinal);

    /// <summary>
    /// Whether the receiver of a <c>Run</c> invocation is a process runner.
    /// </summary>
    /// <param name="receiver">The expression the <c>Run</c> was invoked on.</param>
    /// <param name="runnerNames">Names declared with a runner type, from
    /// <see cref="RunnerTypedNames"/>.</param>
    /// <returns><see langword="true"/> when the call is one this census must police.</returns>
    /// <remarks>
    /// The trailing identifier is what is resolved, so <c>_runner</c>, <c>this._runner</c> and
    /// <c>Selection.Runner</c> all reduce to the same question. The spelling match is kept as a
    /// third clause rather than replaced: it covers a static entry point such as
    /// <c>SystemProcessRunner.Instance</c>, whose trailing identifier is <c>Instance</c> and whose
    /// declaring type may not be under <c>src/</c> at all.
    /// </remarks>
    private static bool IsProcessRunnerReceiver(
        ExpressionSyntax receiver, HashSet<string> runnerNames)
    {
        if (receiver.ToString().Contains(RunnerTypeMarker, System.StringComparison.Ordinal))
        {
            return true;
        }

        if (receiver is ObjectCreationExpressionSyntax creation)
        {
            return NamesRunnerType(creation.Type);
        }

        var name = receiver switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };

        return name is not null && runnerNames.Contains(name);
    }

    /// <summary>
    /// Whether an expression is <see langword="null"/> on at least one path, by inspection alone.
    /// </summary>
    /// <param name="expression">The argument expression.</param>
    /// <returns><see langword="true"/> for a spelling that can hand the runner a null.</returns>
    /// <remarks>
    /// <para>
    /// <strong>SOME path, not EVERY path, and the weaker question is the right one.</strong> The
    /// exposure is a child git that inherits this process's block, and
    /// <c>flag ? BuildBlock() : null</c> delivers exactly that on the branch it takes. A predicate
    /// that answered only for an unconditional null would report such a site as compliant, which
    /// is the same "satisfiable by the shape it forbids" failure the presence check had.
    /// </para>
    /// <para>
    /// The spellings: <c>null</c>, <c>default</c>, <c>default(T)</c>, any of them behind
    /// parentheses, a cast, or the null-forgiving <c>!</c> — <c>environment: null!</c> is a
    /// ONE-CHARACTER edit that used to walk past this guard, which is what
    /// <c>IsNullOnSomePath_SeesEverySpellingOfNull</c> pins — plus <c>null as T</c>, either arm of
    /// a conditional, and the right operand of <c>??</c>.
    /// </para>
    /// <para>
    /// <strong>What remains uncovered, stated because the class summary above claims this guard
    /// forbids the inheriting shape.</strong> Every null that is not spelled AT THE CALL SITE is
    /// invisible: a local, field, property or method call that evaluates to null
    /// (<c>environment: _cachedBlock</c>, <c>environment: BuildBlock()</c>), a <c>const</c> or
    /// <c>static readonly</c> field initialised to null, and a null arriving through any other
    /// indirection. Closing those needs a semantic model and a data-flow analysis, which needs the
    /// CLI compiled inside the test — the same boundary the receiver resolution above stops at.
    /// An EMPTY but non-null block is deliberately NOT an offender: it grants the child nothing,
    /// which is the opposite of the exposure.
    /// </para>
    /// </remarks>
    private static bool IsNullOnSomePath(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesised => IsNullOnSomePath(parenthesised.Expression),
        CastExpressionSyntax cast => IsNullOnSomePath(cast.Expression),
        PostfixUnaryExpressionSyntax suppression
            when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
            IsNullOnSomePath(suppression.Operand),
        ConditionalExpressionSyntax conditional =>
            IsNullOnSomePath(conditional.WhenTrue) || IsNullOnSomePath(conditional.WhenFalse),
        BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression) =>
            IsNullOnSomePath(coalesce.Right),
        BinaryExpressionSyntax cast when cast.IsKind(SyntaxKind.AsExpression) =>
            IsNullOnSomePath(cast.Left),
        DefaultExpressionSyntax => true,
        LiteralExpressionSyntax literal =>
            literal.IsKind(SyntaxKind.NullLiteralExpression)
            || literal.IsKind(SyntaxKind.DefaultLiteralExpression),
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="call"/> passes an <c>environment</c> that is not null.
    /// </summary>
    /// <param name="call">The <c>Run</c> invocation.</param>
    /// <returns><see langword="true"/> when a non-null block is passed by name or fourth.</returns>
    /// <remarks>
    /// <para>
    /// Either spelling counts: the production call passes it positionally, and a named argument is
    /// what a caller who skips <c>workingDirectory</c>'s neighbours would write.
    /// </para>
    /// <para>
    /// The positional count is over POSITIONAL arguments only. A plain <c>Count &gt;= 4</c> is
    /// satisfied by three positional arguments plus <c>cancellationToken:</c> by name — which is
    /// exactly the inheriting shape this row exists to refuse, passing it for free.
    /// </para>
    /// <para>
    /// <strong>PRESENCE IS NOT ENOUGH, and that was a real hole rather than a theoretical
    /// one.</strong> <c>null</c> is not "no environment", it is the request to INHERIT the whole
    /// process block — so <c>environment: null</c>, and a positional <c>null</c> or
    /// <c>default</c>, satisfied the old presence test while asking for precisely the exposure
    /// #500 closed. <see cref="IsNullOnSomePath"/> refuses those spellings, and the ones that
    /// reach the same null through <c>!</c>, a conditional arm or <c>??</c>.
    /// </para>
    /// </remarks>
    private static bool SuppliesEnvironment(InvocationExpressionSyntax call)
    {
        var argument = EnvironmentArgument(call);

        return argument is not null && !IsNullOnSomePath(argument.Expression);
    }

    /// <summary>
    /// The argument bound to the <c>environment</c> parameter, or <see langword="null"/> when the
    /// call passes none.
    /// </summary>
    /// <param name="call">The <c>Run</c> invocation.</param>
    /// <returns>The argument node, or <see langword="null"/>.</returns>
    private static ArgumentSyntax? EnvironmentArgument(InvocationExpressionSyntax call)
    {
        var arguments = call.ArgumentList.Arguments;

        var named = arguments.FirstOrDefault(
            a => a.NameColon?.Name.Identifier.ValueText == "environment");
        if (named is not null)
        {
            return named;
        }

        var positional = arguments.TakeWhile(a => a.NameColon is null).ToList();

        return positional.Count >= 4 ? positional[3] : null;
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
