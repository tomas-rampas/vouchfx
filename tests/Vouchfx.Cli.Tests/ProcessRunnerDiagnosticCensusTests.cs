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
    /// Every <c>IProcessRunner.Run</c> call site under <c>src/</c> that a syntax walk can resolve
    /// supplies an environment block.
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
    /// every name DECLARED with a process-runner type anywhere under <c>src/</c>
    /// (<see cref="RunnerTypedNames"/> enumerates the shapes) and the second matches a <c>Run</c>
    /// whose receiver is one of them, or is a <c>new</c> of such a type, or still carries the old
    /// spelling — reached through <c>.</c> or through <c>?.</c> alike.
    /// </para>
    /// <para>
    /// <strong>THE DECLARATION SHAPES WERE THE THIRD ITERATION OF THE SAME HOLE.</strong> After
    /// the spelling match and after <c>null!</c>, the collector still read only variable
    /// declarations, parameters and properties, so <c>foreach (IProcessRunner runner in …)</c>, a
    /// declaration pattern and an <c>out</c> variable each declared a receiver this census could
    /// not see — and an invisible receiver leaves the count at 1 and the row green while the new
    /// call inherits the whole block. Each shape it now reads was drilled by declaring the
    /// production receiver that way, dropping the argument and watching this row name the site.
    /// </para>
    /// <para>
    /// <strong>The residue, and it is the opposite direction from the old one.</strong> A type name
    /// is still matched by TEXT (<c>ProcessRunner</c>), so a runner interface renamed wholesale
    /// goes unseen — which the vacuity assertion below catches, because the production site would
    /// stop matching too. The name set is global rather than per-file, so a <c>runner</c> declared
    /// as a runner in one file makes every <c>runner.Run(...)</c> in <c>src/</c> a candidate. That
    /// over-matches, and over-matching is the safe direction: the worst case is this row demanding
    /// an <c>environment</c> argument of a call that did not need one, which reddens and is read,
    /// rather than a leak nobody is told about.
    /// </para>
    /// <para>
    /// <strong>THE SEMANTIC-MODEL RESIDUE IS REAL, AND IT USED TO BE STATED AS THOUGH IT WERE THE
    /// ONLY ONE.</strong> What genuinely needs a semantic model is every receiver whose TYPE is
    /// not written at its declaration — a <c>var</c> local or a <c>var</c>/deconstruction pattern
    /// filled from a factory — and every receiver declared in no source file under <c>src/</c> at
    /// all, such as an inherited member from a referenced assembly. Those two clauses were the
    /// whole stated residue, and they did not cover the five spellings MEASURED missing in the
    /// round that added the paragraph below: each of those receivers had its type written at its
    /// declaration AND was declared under <c>src/</c>, so the residue claimed coverage it did not
    /// have. Three were syntactic wrappers and are now unwrapped
    /// (<see cref="UnwrapReceiver"/>). The other two are named in the next paragraph on their own
    /// terms rather than folded into a limit that does not fit them.
    /// </para>
    /// <para>
    /// <strong>WHAT IS STILL NOT MATCHED, PRECISELY: A CALL WHOSE RECEIVER IS THE ENCLOSING
    /// TYPE.</strong> Inside a class that itself implements the runner interface,
    /// <c>this.Run(…)</c>, <c>base.Run(…)</c> and a bare <c>Run(…)</c> are all invisible —
    /// MEASURED: the first two reach the <c>name</c> switch as <c>this</c>/<c>base</c> and fall
    /// out, and the bare form is not even a candidate, because <see cref="RunReceiver"/> only
    /// answers for a member access and a bare call has no receiver node at all. This needs no
    /// semantic model either; it needs a DIFFERENT question — "is the ENCLOSING type a runner?"
    /// rather than "is the RECEIVER one" — and that is why it is not simply another arm. It is
    /// left out on merit, not cost: <c>Run</c> is an ordinary method name, so asking the enclosing
    /// question would make every bare <c>Run(…)</c> inside such a class a candidate, including
    /// private helpers that merely share the name; and the only shape it would catch is the one
    /// implementation re-entering its own <c>Run</c>, where the block in hand is the one the
    /// implementation was handed rather than this process's. Should a second implementation ever
    /// land under <c>src/</c>, reopen this — the reasoning is about there being exactly one today.
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
                .Where(i => RunReceiver(i) is { } receiver
                    && IsProcessRunnerReceiver(receiver, runnerNames))
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
    /// The receiver resolution sees every spelling of the SAME receiver, and the spellings it
    /// deliberately does not see are rows here rather than absences.
    /// </summary>
    /// <param name="statement">A <c>Run</c> call, spelled as a caller would write it.</param>
    /// <param name="expected">Whether the census must treat it as a call site.</param>
    /// <remarks>
    /// <para>
    /// The sibling of <see cref="IsNullOnSomePath_SeesEverySpellingOfNull"/>, and it exists for
    /// the same reason: a receiver this census cannot resolve leaves the count assertion green at
    /// 1 while the new call inherits the whole process block, so a limit that is merely STATED
    /// closes nothing. Every round of this census so far found its hole by someone rewriting the
    /// production call — a drill that leaves nothing behind. These rows are what a drill cannot
    /// be: permanent, and a moved expectation when the resolution changes.
    /// </para>
    /// <para>
    /// The FALSE rows carry as much weight as the true ones. <c>this</c>, <c>base</c> and the bare
    /// call are the residue the class remarks name, pinned so that closing it later is a decision
    /// rather than a side effect; and a receiver of no relation must stay unmatched, since a
    /// predicate that answered true for everything would satisfy every positive row while turning
    /// the census into a refusal of all call sites.
    /// </para>
    /// </remarks>
    [Theory]
    // The shapes the collector resolves through a declared name.
    [InlineData("_runner.Run(x);", true)]
    [InlineData("_runner?.Run(x);", true)]
    [InlineData("this._runner.Run(x);", true)]
    // The three syntactic wrappers, each MEASURED missing before they were unwrapped.
    [InlineData("_runner!.Run(x);", true)]
    [InlineData("(_runner).Run(x);", true)]
    [InlineData("((_runner!)).Run(x);", true)]
    [InlineData("_runners[0].Run(x);", true)]
    [InlineData("_runners[0]!.Run(x);", true)]
    // The spelling match, for a static entry point whose trailing identifier names no runner.
    [InlineData("SystemProcessRunner.Instance.Run(x);", true)]
    [InlineData("new SystemProcessRunner().Run(x);", true)]
    // The documented residue: the receiver is the enclosing type, which is a different question.
    [InlineData("this.Run(x);", false)]
    [InlineData("base.Run(x);", false)]
    [InlineData("Run(x);", false)]
    // Negative controls. Without these the row is satisfied by a predicate that says yes to all.
    [InlineData("_logger.Run(x);", false)]
    [InlineData("_runner.Start(x);", false)]
    public void ReceiverResolution_SeesEverySpellingOfTheRunner(string statement, bool expected)
    {
        var root = CSharpSyntaxTree
            .ParseText($"class C {{ void M() {{ {statement} }} }}")
            .GetCompilationUnitRoot();

        Assert.False(
            root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            $"'{statement}' did not parse, so this row proves nothing about the resolution.");

        var call = root.DescendantNodes().OfType<InvocationExpressionSyntax>().First();
        var names = new HashSet<string> { "_runner", "_runners" };
        var matched = RunReceiver(call) is { } receiver
            && IsProcessRunnerReceiver(receiver, names);

        Assert.Equal(expected, matched);
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
    /// <para>
    /// Every shape that WRITES the type at the declaration: a local or field (both
    /// <see cref="VariableDeclarationSyntax"/>), a parameter, a property, a <c>foreach</c>
    /// variable, a named tuple element, a declaration or recursive PATTERN
    /// (<c>x is IProcessRunner runner</c>, <c>x is IProcessRunner { } runner</c>) and a
    /// declaration EXPRESSION, which is the <c>out</c> variable and the typed half of a
    /// deconstruction.
    /// </para>
    /// <para>
    /// The first three were all this read, and the rest are the hole that left: a receiver
    /// declared by any of them was invisible, which does not redden the count — it leaves it at
    /// the one production call and passes. What is still outside is the shape that writes NO type:
    /// a <c>var</c> local, a <c>var</c> pattern, an untyped deconstruction. Such a local is caught
    /// by the <c>new</c> clause in <see cref="IsProcessRunnerReceiver"/> only when the call is on
    /// the <c>new</c> itself; filled from a factory it is the residue named in the row's remarks,
    /// and it is a semantic-model question rather than a syntactic one.
    /// </para>
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

                    case ForEachStatementSyntax loop when NamesRunnerType(loop.Type):
                        names.Add(loop.Identifier.ValueText);
                        break;

                    case TupleElementSyntax element when NamesRunnerType(element.Type)
                        && element.Identifier.ValueText.Length > 0:
                        names.Add(element.Identifier.ValueText);
                        break;

                    case DeclarationPatternSyntax pattern when NamesRunnerType(pattern.Type):
                        AddDesignated(names, pattern.Designation);
                        break;

                    case RecursivePatternSyntax recursive when NamesRunnerType(recursive.Type):
                        AddDesignated(names, recursive.Designation);
                        break;

                    case DeclarationExpressionSyntax declared when NamesRunnerType(declared.Type):
                        AddDesignated(names, declared.Designation);
                        break;

                    default:
                        break;
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Adds every name a variable designation introduces.
    /// </summary>
    /// <param name="names">The set being built.</param>
    /// <param name="designation">The designation, which may be absent.</param>
    /// <remarks>
    /// A parenthesised designation is a deconstruction, whose written type covers the whole tuple,
    /// so every nested name is added. That over-matches in the safe direction — see the row's
    /// residue paragraph.
    /// </remarks>
    private static void AddDesignated(HashSet<string> names, VariableDesignationSyntax? designation)
    {
        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                names.Add(single.Identifier.ValueText);
                break;

            case ParenthesizedVariableDesignationSyntax parenthesised:
                foreach (var nested in parenthesised.Variables)
                {
                    AddDesignated(names, nested);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The expression a <c>Run</c> was invoked on, or <see langword="null"/> when the invocation
    /// is not a <c>Run</c> on a receiver.
    /// </summary>
    /// <param name="call">A candidate invocation.</param>
    /// <returns>The receiver expression, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Both member-access forms, because <c>_runner?.Run(…)</c> parses to a
    /// <see cref="MemberBindingExpressionSyntax"/> whose receiver sits on the enclosing
    /// <see cref="ConditionalAccessExpressionSyntax"/> — matching only <c>.</c> left the
    /// null-conditional spelling of the very same call unseen, which is the declaration-shape hole
    /// reached from the call side rather than the declaration side.
    /// </remarks>
    private static ExpressionSyntax? RunReceiver(InvocationExpressionSyntax call) =>
        call.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Run" } access =>
                access.Expression,
            MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Run" } =>
                call.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null,
        };

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
    /// <para>
    /// The trailing identifier is what is resolved, so <c>_runner</c>, <c>this._runner</c> and
    /// <c>Selection.Runner</c> all reduce to the same question. The spelling match is kept as a
    /// third clause rather than replaced: it covers a static entry point such as
    /// <c>SystemProcessRunner.Instance</c>, whose trailing identifier is <c>Instance</c> and whose
    /// declaring type may not be under <c>src/</c> at all.
    /// </para>
    /// <para>
    /// <strong>THE SYNTACTIC WRAPPERS COME OFF FIRST, and <c>!</c> is the reason.</strong> Three
    /// spellings of the SAME receiver used to reach the <c>name</c> switch as node kinds it has no
    /// arm for, and fall out at <c>_ =&gt; null</c>: <c>_runner!.Run(…)</c>,
    /// <c>(_runner).Run(…)</c> and <c>_runners[0].Run(…)</c>. Each was MEASURED missing here —
    /// the census reported "found 0" rather than an offender. The first is the one that
    /// matters — it is the same null-forgiving <c>!</c> that has walked past this file's guard
    /// twice already, and <see cref="IsNullOnSomePath"/> two methods down was already unwrapping
    /// that exact node kind on the ARGUMENT side. The file held the unwrap it needed and did not
    /// apply it here, which is why <see cref="UnwrapReceiver"/> mirrors that predicate's arms
    /// rather than inventing a rule.
    /// </para>
    /// </remarks>
    private static bool IsProcessRunnerReceiver(
        ExpressionSyntax receiver, HashSet<string> runnerNames)
    {
        receiver = UnwrapReceiver(receiver);

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
    /// Strips the wrappers that hide a receiver's trailing identifier without changing which
    /// object <c>Run</c> is called on.
    /// </summary>
    /// <param name="receiver">The expression the <c>Run</c> was invoked on.</param>
    /// <returns>The innermost expression that still names the receiver.</returns>
    /// <remarks>
    /// <para>
    /// Three arms, and all three are pure noise around the same object. <c>!</c> asserts
    /// non-nullness and evaluates to its operand; parentheses group; an indexer reaches an ELEMENT
    /// of a runner-typed collection, whose declaration (<c>IProcessRunner[] _runners</c>) names a
    /// runner type by the same text test everything else here uses, so the trailing identifier is
    /// still the right question. The indexer arm is the sibling of the <c>foreach</c> declaration
    /// shape the collector already reads: a census that contemplates a COLLECTION of runners has
    /// no business missing the call that indexes one.
    /// </para>
    /// <para>
    /// Recursive because the wrappers nest — <c>(_runner!)</c> is both — and because unwrapping
    /// one and stopping is the same half-fix this method has already shipped twice.
    /// </para>
    /// </remarks>
    private static ExpressionSyntax UnwrapReceiver(ExpressionSyntax receiver) => receiver switch
    {
        ParenthesizedExpressionSyntax parenthesised =>
            UnwrapReceiver(parenthesised.Expression),
        PostfixUnaryExpressionSyntax suppression
            when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
            UnwrapReceiver(suppression.Operand),
        ElementAccessExpressionSyntax indexed => UnwrapReceiver(indexed.Expression),
        _ => receiver,
    };

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
