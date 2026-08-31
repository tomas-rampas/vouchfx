using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Structural census over the #420 flight recorder's ARMING WINDOW: the property that the
/// window has no exit which neither flushes nor drops.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why exhaustiveness rather than a list of known sites.</strong> The first version of
/// this census asserted three named strings, which would have gone green forever if a FOURTH
/// exit were added beside them — and an enumeration in a test is a claim of completeness that
/// nothing checks, the same failure mode this repository's ASCII census remarks warn about. The
/// version below asserts a RULE instead, over the syntax tree: within the region between
/// <c>HeadlessTopology.StartAsync</c> returning and the topology being handed to its caller,
/// <em>every</em> exit complies. A new catch clause or a new return statement is covered the
/// moment it is written, because the census enumerates them rather than naming them.
/// </para>
/// <para>
/// <strong>The exhaustiveness argument, stated so it can be challenged.</strong> The post-start
/// region is one <c>try</c> block. Control can leave it in exactly three ways: it RETURNS the
/// topology (rule 2 below), it THROWS (reaching one of that try's catch clauses, rule 1
/// below — including anything thrown from a nested block, which is why nested catches need no
/// rule of their own), or the process dies. Nested returns are covered because rule 2
/// enumerates return statements at any depth in the try. What is deliberately NOT covered is an
/// exit BEFORE the try opens — but such a path leaks the started topology itself, which the
/// Docker-gated teardown-leak tests already fail on, so it cannot be added silently either.
/// </para>
/// <para>
/// <strong>Five rules, and each states its own limit in its own remarks rather than in this
/// header.</strong> Rules 1 and 2 close the two exits above; rule 3 orders the flush ahead of the
/// classification that has to read it; rules 4 and 5 pin the two halves of the hand-off at the
/// START of the window — that <c>HeadlessTopology.StartAsync</c> gives the recorder to the
/// topology it returns, and that it drops one only from a failure path. Read the rule, not this
/// paragraph, before quoting any of them as a guarantee: two of the five are approximations with
/// a named gap, and saying so is the point.
/// </para>
/// <para>
/// <strong>Why a census at all, when a behavioural drill exists.</strong> Both are here.
/// <c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c> proves the gate-spanning
/// property end to end with real containers - reverting the widening turns it RED - but it is
/// Docker-gated, slow, and covers exactly ONE path. The census is fast, runs in the default
/// lane, and covers the paths a single behavioural drill cannot reach: the discovery failure,
/// the probe failure, the seed failure, and whatever is added next.
/// </para>
/// </remarks>
public sealed class DcpArmingWindowCensusTests
{
    /// <summary>
    /// Rule 1 — every catch of the post-start region flushes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This checks that a flush is PRESENT, not that it is EXECUTED, and the gap is real
    /// rather than theoretical.</strong> Both real flush sites sit under
    /// <c>if (!cancellationToken.IsCancellationRequested)</c> — deliberately, because a run the
    /// caller stopped with Ctrl-C is not a fault worth spending a retention slot on. A syntax
    /// census cannot tell that guard apart from <c>if (false)</c>: it sees an invocation somewhere
    /// inside the catch and says so. Anyone who narrows one of those guards until the flush stops
    /// firing in practice will leave this rule green.
    /// </para>
    /// <para>
    /// That is accepted rather than fixed here. Deciding execution needs a control-flow analysis
    /// this census has no compilation for, and the condition it would have to reason about is a
    /// runtime value. The behavioural half of the pair —
    /// <c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c> — is what proves a flush
    /// actually fires, on one path, with real containers. The census's own job is narrower and is
    /// worth stating exactly: it catches the omission (a new catch clause with no flush in it at
    /// all), which is the failure mode a person adding a catch clause actually has.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("SuiteTopology.cs", "SuiteTopology")]
    [InlineData("StubTopology.cs", "StubTopology")]
    public void EveryCatchOfThePostStartRegion_FlushesTheRecorder(string fileName, string typeName)
    {
        var postStart = PostStartTry(fileName, typeName);

        var offenders = postStart.Catches
            .Where(c => !InvokesAnywhere(c, "FlushDiagnosticsAsync"))
            .Select(c => Describe(c))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} catch clause(s) in {fileName}'s post-start region do not flush the "
            + "#420 flight recorder, so a failure reaching them discards the DCP traffic buffered "
            + "since the start. Add `await topology.FlushDiagnosticsAsync(ex)` before the "
            + "dispose:\n  " + string.Join("\n  ", offenders));

        // Guard against a vacuous pass: a region with no catches would satisfy the loop above
        // for free, and would also mean the topology leaks on failure.
        Assert.NotEmpty(postStart.Catches);
    }

    /// <summary>
    /// Rule 2 — every return from the post-start region is DOMINATED by a drop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Dominance, not position, and the difference is a defect this rule used to
    /// admit.</strong> The first version compared <c>SpanStart</c>: any <c>DropDiagnostics()</c>
    /// appearing earlier in the file than a return satisfied it. So
    /// <c>if (x) { topology.DropDiagnostics(); } return new SuiteTopology(...);</c> passed while
    /// leaving the recorder armed on every path where <c>x</c> is false — the exact regression the
    /// rule names in its own failure message. Lexical order is not execution order, and a census
    /// whose stated rule is stronger than its check is worse than no census, because it is quoted
    /// as if it held.
    /// </para>
    /// <para>
    /// What is checked instead: the drop must be a bare expression STATEMENT whose enclosing block
    /// also contains the return, at a lower statement index. Control entering a block runs its
    /// statements in order, so reaching a statement at index <em>j</em> means having executed the
    /// one at index <em>i &lt; j</em> — the drop is unconditional with respect to that return. A
    /// drop nested inside an <c>if</c>, a loop or a nested try no longer counts, because its
    /// enclosing block is not the return's.
    /// </para>
    /// <para>
    /// The one construct that would defeat this is a <c>goto</c> jumping into the middle of the
    /// block past the drop. There is none in either file, C# forbids jumping into a block from
    /// outside it, and a label added between the two would be conspicuous — so the approximation
    /// is stated rather than defended as exact.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("SuiteTopology.cs", "SuiteTopology")]
    [InlineData("StubTopology.cs", "StubTopology")]
    public void EveryReturnFromThePostStartRegion_DropsTheRecorder(string fileName, string typeName)
    {
        var postStart = PostStartTry(fileName, typeName);

        var drops = postStart.Try.Block
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "DropDiagnostics")
            .ToList();

        var returns = postStart.Try.Block
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .ToList();

        Assert.NotEmpty(returns);

        var offenders = returns
            // A return inside a block-bodied lambda is not a return FROM the post-start region -
            // it leaves the lambda. None exists in either file today; excluding them keeps a
            // future one from reddening this rule for a reason that has nothing to do with the
            // arming window. (Rule 3's catch enumeration has no equivalent hazard: a catch inside
            // a lambda would still have to flush.)
            .Where(r => !r.Ancestors()
                .TakeWhile(a => a != postStart.Try)
                .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Where(r => !drops.Any(d => DropDominates(d, r)))
            .Select(r => Describe(r))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} return(s) from {fileName}'s post-start region are not preceded, "
            + "unconditionally and in the same block, by a `topology.DropDiagnostics()`, so the "
            + "#420 arming window would stay open on at least one path to the return and the "
            + "buffer would live until teardown. A drop inside an `if` does not count - put it "
            + "beside the return:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Rule 3 — every catch in the post-start region that CLASSIFIES flushes first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order, not presence, and it is about the DETAIL rather than about the file. The classifier
    /// reads the capture summary off the exception's <c>Data</c>, so flushing after
    /// <c>Classify</c> still writes the capture but leaves the Environment-error detail without
    /// the pointer to it and without the tail. On a CI runner, whose filesystem is discarded with
    /// the job, that detail is the only copy anyone ever reads.
    /// </para>
    /// <para>
    /// <strong>Every such catch, rather than the health gate alone — the narrower rule missed a
    /// real one.</strong> This rule previously named the health-gate catch by the shape of its own
    /// try block, and the DISCOVERY catch three statements below it classified without flushing
    /// for the whole life of the feature: the outer safety net wrote the file, and the detail
    /// never named it. The rule now enumerates instead of naming, so the next catch that
    /// classifies is covered the moment it is written.
    /// </para>
    /// <para>
    /// <strong>Scoped to the POST-START region deliberately.</strong> The catches around
    /// <c>HeadlessTopology.StartAsync</c> also classify and must NOT flush: no topology exists to
    /// flush yet, and <c>StartAsync</c> has already flushed and dropped its own recorder on the
    /// way out.
    /// </para>
    /// <para>
    /// <strong>What it does not reach.</strong> A failure that never calls <c>Classify</c> —
    /// the secured-endpoint probe and the seed, both of which build their own
    /// <c>OrchestrationErrorInfo</c> — has no site for this rule to order, and its detail names no
    /// capture. The file is still written by the outer net. That limit is recorded at the outer
    /// catch in <c>SuiteTopology</c> and in <c>docs/troubleshooting.md</c>; it is a property of
    /// where the detail is built, not something this census could assert away.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("SuiteTopology.cs", "SuiteTopology")]
    [InlineData("StubTopology.cs", "StubTopology")]
    public void EveryCatchThatClassifies_FlushesFirst(string fileName, string typeName)
    {
        var postStart = PostStartTry(fileName, typeName);

        var classifying = postStart.Try
            .DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Where(c => InvokesAnywhere(c, "Classify"))
            .ToList();

        // Guard against a vacuous pass: a region that classifies nowhere would satisfy the loop
        // below for free, and would also mean the failures here reach the caller unclassified.
        Assert.NotEmpty(classifying);

        var offenders = new List<string>();
        foreach (var clause in classifying)
        {
            var flush = FirstInvocationPosition(clause, "FlushDiagnosticsAsync");
            var classify = FirstInvocationPosition(clause, "Classify");

            if (!flush.HasValue || flush.Value > classify!.Value)
            {
                offenders.Add(Describe(clause));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} catch clause(s) in {fileName}'s post-start region call "
            + "OrchestrationErrorClassifier.Classify without first calling "
            + "`await topology.FlushDiagnosticsAsync(ex)`, so the #420 capture summary cannot "
            + "reach the Environment-error detail they build. Flush first:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Rule 4 — <c>HeadlessTopology.StartAsync</c> never drops the recorder on its way OUT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The far end of the arming window belongs to the CALLER, not to <c>StartAsync</c>: dropping
    /// here means a fault surfacing at a health gate has its evidence buffered and then discarded,
    /// which is the shape this feature originally shipped. MEASURED: inserting
    /// <c>recorder?.Dispose();</c> immediately before the <c>return new HeadlessTopology(app,
    /// recorder)</c> passed the entire suite.
    /// </para>
    /// <para>
    /// The rule is therefore stated as a location rather than as a presence: within
    /// <c>StartAsync</c>'s body, every <c>Dispose</c> call whose receiver is the recorder must sit
    /// inside a <c>catch</c>. Both legitimate drops are failure-path drops, so the constraint
    /// costs nothing and refuses the mutation above by construction. The behavioural half is
    /// <c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c>, which the same mutation
    /// reddens with real containers; this is the free half that runs in the default lane.
    /// </para>
    /// <para>
    /// <c>FlushOnFailureAsync</c> disposes the recorder too, in its own <c>finally</c>, and is
    /// deliberately not matched here: it is a different call, on a different type, and it is the
    /// flush this window is supposed to end with.
    /// </para>
    /// </remarks>
    [Fact]
    public void StartAsync_DisposesTheRecorderOnlyFromInsideACatch()
    {
        var start = PublicStartAsync("HeadlessTopology.cs");

        var disposals = start.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "Dispose" && ReceiverOf(i) == "recorder")
            .ToList();

        // Guard against a vacuous pass: a StartAsync that never drops the recorder on any failure
        // path leaks the buffer instead of leaking the window, which is a different defect but
        // not a green one.
        Assert.NotEmpty(disposals);

        var offenders = disposals
            .Where(i => !i.Ancestors().OfType<CatchClauseSyntax>().Any())
            .Select(i => Describe(i))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} `recorder.Dispose()` call(s) in HeadlessTopology.StartAsync sit "
            + "outside a catch clause, so the #420 arming window closes when the start returns "
            + "rather than when the caller reports the topology ready. The recorder is handed to "
            + "the returned topology and dropped by SuiteTopology/StubTopology (or by "
            + "DisposeAsync); it must only be disposed here on a failure path:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Rule 5 — the recorder is CONSTRUCTED INTO the returned topology.
    /// </summary>
    /// <remarks>
    /// The complement of rule 4, and it is named for exactly what it checks: rule 4 refuses a drop
    /// on the way out, this one refuses a hand-back that never carried the recorder in the first
    /// place. An earlier version of this row was named for the drop and checked only the argument,
    /// which the drop mutation preserves — a name claiming more than its assertion, in a file whose
    /// whole subject is that failure mode.
    /// </remarks>
    [Fact]
    public void StartAsync_ConstructsTheReturnedTopologyWithTheRecorder()
    {
        var start = PublicStartAsync("HeadlessTopology.cs");

        var handOff = start.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is ObjectCreationExpressionSyntax o
                && o.Type.ToString() == "HeadlessTopology"
                && o.ArgumentList is not null
                && o.ArgumentList.Arguments.Any(a => a.ToString() == "recorder"));

        Assert.True(
            handOff,
            "HeadlessTopology.StartAsync no longer hands the #420 recorder to the topology it "
            + "returns, so the arming window closes when the start returns rather than when the "
            + "topology is ready.");
    }

    /// <summary>
    /// Rule 6 — the CALLER SET itself, so rules 1–3's <c>InlineData</c> cannot silently go stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules 1, 2 and 3 pin two files by name. That is a complete census only while those two are
    /// the only callers of <c>HeadlessTopology.StartAsync</c> that own the far end of the arming
    /// window — and nothing checked it. A third caller could be added tomorrow, never drop or
    /// flush the recorder, and every row above would stay green while describing a set that no
    /// longer matched reality: an enumeration standing in for a property, which is the failure
    /// mode this file's own header warns about.
    /// </para>
    /// <para>
    /// Modelled on <c>SuiteProtocolTargetsTests.EverySuiteTopologyStartCallSite_PassesBothTargetSets</c>,
    /// which pins a call-site set the same way and for the same reason. A new caller reddens this
    /// row and the fix is to add its <c>InlineData</c> above — or, if it genuinely owns no window,
    /// to say so here.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOnlyCallersOfHeadlessTopologyStartAsync_AreTheOnesThisCensusCovers()
    {
        var engine = Path.Combine(RepositoryRoot(), "src");

        var callers = Directory
            .EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p)
                .Contains("HeadlessTopology.StartAsync(", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // HeadlessTopology.cs itself is excluded: the string occurs there in its own remarks and
        // its own declaration, not as a call.
        var external = callers
            .Where(n => !string.Equals(n, "HeadlessTopology.cs", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            external.Count == 2
                && external.Contains("SuiteTopology.cs")
                && external.Contains("StubTopology.cs"),
            "the set of HeadlessTopology.StartAsync callers under src/ has changed, so this "
            + "census's InlineData no longer enumerates every owner of the #420 arming window. "
            + "Found: " + string.Join(", ", external) + ". Add the new caller to rules 1-3 (and "
            + "give it a DropDiagnostics on its ready path and a FlushDiagnosticsAsync in its "
            + "catches), or record here why it owns no window.");
    }

    // -----------------------------------------------------------------------
    // Syntax helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// The <c>try</c> whose block ends by handing back a <paramref name="typeName"/> instance:
    /// the post-start region, identified by what it produces rather than by its position, so
    /// inserting another try above or below it does not silently re-aim this census.
    /// </summary>
    private static (TryStatementSyntax Try, IReadOnlyList<CatchClauseSyntax> Catches) PostStartTry(
        string fileName, string typeName)
    {
        var root = Parse(fileName);

        var candidates = root.DescendantNodes()
            .OfType<TryStatementSyntax>()
            .Where(t => t.Block.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression is ObjectCreationExpressionSyntax o
                    && o.Type.ToString() == typeName))
            .ToList();

        Assert.True(
            candidates.Count == 1,
            $"expected exactly one try block in {fileName} returning a {typeName}; found "
            + candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ". The post-start region moved - re-aim this census rather than deleting it.");

        return (candidates[0], candidates[0].Catches.ToArray());
    }

    /// <summary>The single public <c>StartAsync</c> declared in <paramref name="fileName"/>.</summary>
    private static MethodDeclarationSyntax PublicStartAsync(string fileName) =>
        Parse(fileName)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "StartAsync"
                && m.Modifiers.Any(SyntaxKind.PublicKeyword));

    /// <summary>
    /// Whether <paramref name="drop"/> is guaranteed to have executed before
    /// <paramref name="ret"/> is reached — see rule 2's remarks for the argument and its one
    /// stated limit.
    /// </summary>
    private static bool DropDominates(InvocationExpressionSyntax drop, ReturnStatementSyntax ret)
    {
        // A drop that is not a statement of its own - a drop inside a condition, an argument, a
        // ternary - is not something this rule is willing to reason about, so it does not count.
        if (drop.Parent is not ExpressionStatementSyntax dropStatement ||
            dropStatement.Parent is not BlockSyntax block)
        {
            return false;
        }

        // The return's own statement AT THIS BLOCK's level: the return itself when it is a direct
        // statement of the block, otherwise the enclosing statement (an if, a loop, a try) that
        // is. Null when the return is not inside this block at all.
        var returnAnchor = ret.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault(s => ReferenceEquals(s.Parent, block));

        if (returnAnchor is null)
        {
            return false;
        }

        return block.Statements.IndexOf(dropStatement) < block.Statements.IndexOf(returnAnchor);
    }

    /// <summary>
    /// The text of an invocation's RECEIVER — <c>x</c> for both <c>x.M()</c> and <c>x?.M()</c>.
    /// </summary>
    /// <remarks>
    /// The conditional form is why this is not a one-liner: <c>recorder?.Dispose()</c> parses as a
    /// <see cref="ConditionalAccessExpressionSyntax"/> whose invocation carries only a
    /// <see cref="MemberBindingExpressionSyntax"/>, so the receiver is not reachable from the
    /// invocation's own <c>Expression</c> and has to be read off the nearest conditional-access
    /// ancestor. Getting this wrong makes rule 4 silently match nothing, which is exactly the
    /// vacuous pass its <c>Assert.NotEmpty</c> exists to refuse.
    /// </remarks>
    private static string? ReceiverOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Expression.ToString(),
        MemberBindingExpressionSyntax => invocation
            .Ancestors()
            .OfType<ConditionalAccessExpressionSyntax>()
            .FirstOrDefault()
            ?.Expression.ToString(),
        _ => null,
    };

    private static bool InvokesAnywhere(SyntaxNode node, string methodName) =>
        FirstInvocationPosition(node, methodName).HasValue;

    private static int? FirstInvocationPosition(SyntaxNode node, string methodName)
    {
        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (NameOf(invocation) == methodName)
            {
                return invocation.SpanStart;
            }
        }

        return null;
    }

    private static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => null,
    };

    private static string Describe(SyntaxNode node)
    {
        var line = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        var text = node.ToString().Split('\n')[0].Trim();
        return $"line {line.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {text}";
    }

    private static CompilationUnitSyntax Parse(string fileName)
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Orchestration", fileName);

        Assert.True(File.Exists(path), $"census target moved or was renamed: {path}");

        return (CompilationUnitSyntax)CSharpSyntaxTree
            .ParseText(File.ReadAllText(path), path: path)
            .GetRoot();
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
