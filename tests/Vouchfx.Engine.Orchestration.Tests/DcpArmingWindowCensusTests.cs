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
/// <strong>Six rules, and each rule's limits are stated in its own remarks rather than in this
/// header.</strong> Rules 1 and 2 close the two exits above; rule 3 orders the flush ahead of the
/// classification that has to read it; rules 4 and 5 pin the two halves of the hand-off at the
/// START of the window — that <c>HeadlessTopology.StartAsync</c> gives the recorder to the
/// topology it returns, and that it drops one only from a failure path; rule 6 pins the CALL SITES
/// themselves — every invocation under <c>src/</c>, counted rather than the files holding them —
/// so rules 1–3's <c>InlineData</c> cannot silently go stale. Read the rule, not this
/// paragraph, before quoting any of them as a guarantee: five of the six — every rule but 5 —
/// name a gap of their own, and saying so is the point.
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
    /// The name by which rules 4 and 5 identify the recorder inside
    /// <c>HeadlessTopology.StartAsync</c>. A
    /// syntax census has no symbol table, so the spelling IS the subject — but a RENAME does not
    /// go quiet: it renames the two failure-path <c>recorder?.Dispose()</c> receivers too, so rule
    /// 4's <c>Assert.NotEmpty</c> over the invocation scan reddens first, and rule 5 reddens on the
    /// hand-off argument as well. Rule 4's second vacuity guard covers the narrower case those two
    /// miss: a DECLARATION hoisted out of <c>StartAsync</c> while the disposals still spell it.
    /// </summary>
    /// <remarks>
    /// Rule 4's disposal MATCHING does not compare against this constant directly. It SEEDS
    /// <see cref="RecorderNamesIn"/> with it and then matches every shape against the resulting
    /// alias set, so a local bound from the recorder is the recorder for that rule's purposes.
    /// Rule 5 and the declarator guard below still read the constant itself, and deliberately:
    /// each is about a site with exactly one spelling — the hand-off argument, and the
    /// declaration. Rule 4's FIRST vacuity guard is not in that company and the summary above
    /// should not be read as putting it there: it counts what the SET-based scan returned. It
    /// still reddens on a rename, but by a different route — the set is then just the seed, and
    /// no disposal receiver is spelled like it any more, so the scan returns nothing.
    /// </remarks>
    private const string RecorderLocal = "recorder";

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
    /// <c>StartAsync</c>'s body, every disposal of the recorder spelled as a <c>Dispose</c> call or
    /// as a <c>using</c> must sit inside a <c>catch</c>.
    /// Both legitimate drops are failure-path drops, so the constraint costs nothing and refuses
    /// the mutation above by construction. The behavioural half is
    /// <c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c>, which the same mutation
    /// reddens with real containers; this is the free half that runs in the default lane.
    /// </para>
    /// <para>
    /// <strong>Three shapes of disposal, alias forms included, because the first version knew only
    /// one (#534).</strong>
    /// It matched <c>Dispose</c> INVOCATIONS whose receiver is the recorder, which leaves
    /// <c>using var recorder = DcpFlightRecorder.CreateUnlessDisabled();</c> — one keyword away
    /// from the declaration the method actually carries — invisible: a using declaration disposes
    /// without an invocation node to match, and the two <c>recorder?.Dispose()</c> calls on the
    /// failure paths keep the vacuity guard below satisfied, so the rule did not even go quiet.
    /// MEASURED before the widening: with that one keyword added, all eleven rows of this census
    /// and of <c>DcpRecorderFactoryCensusTests</c> passed. The rule now refuses, alongside the
    /// invocations, a using DECLARATION over the recorder local or over an alias of it, a using
    /// STATEMENT declaring either, and a using STATEMENT taking it as a resource expression — all
    /// of them dispose the recorder at a scope <c>StartAsync</c> owns, which is the drop mutation
    /// above by another spelling.
    /// </para>
    /// <para>
    /// <strong>"The recorder" means a NAME SET rather than one spelling, and all three shapes plus
    /// the invocation scan read the same set.</strong> <see cref="RecorderNamesIn"/> seeds it with
    /// <see cref="RecorderLocal"/> and closes it over every local initialised from a name already
    /// in it, to a fixed point. So <c>var alias = recorder;</c> bound in one statement and disposed
    /// in a later one — by <c>alias.Dispose()</c>, by <c>using (alias) { }</c>, or through a second
    /// hop <c>var b = alias;</c> — is refused exactly as the recorder itself is. Without that
    /// closure the two-statement alias was the nearest edit to the one-keyword mutation above that
    /// this rule could not see. What the set still cannot follow is stated on
    /// <see cref="RecorderNamesIn"/>.
    /// </para>
    /// <para>
    /// <strong>Why that shape earns a rule rather than a comment.</strong>
    /// <c>DcpFlightRecorder.CreateUnlessDisabled</c> is the only reader of
    /// <c>VOUCHFX_DCP_CAPTURE=0</c>, so a recorder disposed as the start returns is
    /// indistinguishable, from outside, from a run where capture was switched off: same absent
    /// capture file, no error, no log line. The failure surfaces later, as a missing capture at
    /// the moment someone needs one. No analyzer closes the gap either: MEASURED — building the
    /// engine with the keyword added reports <c>0 Warning(s)</c>, so nothing in the toolchain would
    /// have nudged the maintainer who wrote it.
    /// </para>
    /// <para>
    /// <strong>The catch exemption has a limit of its own, and it is worth naming.</strong> A
    /// <c>using</c> over the recorder that wrapped the whole success path — the hand-off
    /// included — inside a <c>catch</c> of an outer throwaway <c>try</c> would be exempt from this
    /// rule while rules 1-3 and 5 still passed. That is a deliberate construction rather than a
    /// plausible refactor, and the Docker-gated behavioural row is what covers it. The
    /// <c>using</c> half's own remaining limit — a resource reached through a field, a property, a
    /// method's return value, a cast or a parenthesised expression rather than through a bare
    /// name — is stated on <see cref="UsingDisposalsOf"/> and on <see cref="RecorderNamesIn"/>.
    /// </para>
    /// <para>
    /// <c>FlushOnFailureAsync</c> disposes the recorder too, in its own <c>finally</c>
    /// (<c>DcpCapture.cs</c>), and is deliberately not matched here: it is a different call, on a
    /// different type, and it is the flush this window is supposed to end with. A SUCCESS-path call
    /// to it placed before the return would be a disposal this rule does not see — it takes
    /// an exception argument the success path has none of, which is why constructing one sits
    /// outside this rule's threat rather than inside its blind spot.
    /// </para>
    /// </remarks>
    [Fact]
    public void StartAsync_DisposesTheRecorderOnlyFromInsideACatch()
    {
        var start = PublicStartAsync("HeadlessTopology.cs");

        // Every name that refers to the recorder inside StartAsync, computed once and used by the
        // invocation scan below as well as by all three `using` shapes. The seed is the
        // declaration's own name; the closure is what makes `var alias = recorder;` two statements
        // above a disposal count as a disposal of the recorder. See RecorderNamesIn for the fixed
        // point and for what it cannot follow.
        var names = RecorderNamesIn(start);

        var disposals = start.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "Dispose"
                && Unwrap(ReceiverOf(i)) is IdentifierNameSyntax receiver
                && names.Contains(receiver.Identifier.Text))
            .ToList();

        // Guard against a vacuous pass: a StartAsync that never drops the recorder on any failure
        // path leaks the buffer instead of leaking the window, which is a different defect but
        // not a green one.
        Assert.NotEmpty(disposals);

        // The second vacuity guard, and it belongs to the `using` half specifically: that half
        // finds the recorder by the SPELLING of its local, so a DECLARATION that has left
        // StartAsync - hoisted to a field, or taken as a parameter - while the two failure-path
        // `recorder?.Dispose()` calls still spell it would leave the using scan matching nothing
        // and this rule green on a method it no longer describes. A RENAME is not that case: it
        // renames those two receivers too, so the Assert.NotEmpty above reddens first, and rule 5
        // reddens on the hand-off argument. DcpRecorderFactoryCensusTests pins the declarator count
        // inside StartAsync as well, so this guard is defence in depth - it keeps this rule's own
        // vacuity stated inside this rule, which is the convention the rest of the file follows.
        //
        // What it does NOT do, stated because the guard above invites the stronger reading: it
        // refuses a subject that has VANISHED, not a check that someone DELETES. MEASURED: with
        // the using scan below removed and `using var` on the declaration, all nine rows of this
        // census pass. No assertion can guard its own deletion; the drill is what catches that.
        //
        // Declarators only: a recorder introduced by a declaration pattern (`is { } recorder`) is
        // a SingleVariableDesignation and would trip this guard - loudly and re-aimably, which is
        // the safe direction for a rule that must never go quiet.
        var declarations = start.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Identifier.Text == RecorderLocal)
            // Rule 2's lambda boundary again: a declarator inside a closure is not StartAsync's.
            .Where(d => !d.Ancestors().TakeWhile(a => a != start).Any(IsLambdaBoundary))
            .ToList();

        Assert.True(
            declarations.Count > 0,
            $"no local named `{RecorderLocal}` is declared in HeadlessTopology.StartAsync, so the "
            + "`using` half of this rule is scanning for a name that no longer exists and would "
            + "pass on any disposal shape. The declaration moved out of StartAsync - re-aim this "
            + "rule rather than deleting it.");

        var offenders = disposals
            .Where(i => !InsideACatch(i, start))
            // A conditional-access disposal is the invocation UNDER the `?.`, whose own text is
            // just `.Dispose()`; describe the enclosing conditional access so the offender line
            // names its receiver.
            .Select(i => Describe(i.Parent as ConditionalAccessExpressionSyntax ?? (SyntaxNode)i))
            .ToList();

        offenders.AddRange(UsingDisposalsOf(start, names)
            .Where(u => !InsideACatch(u.Node, start))
            .Select(u => $"{Describe(u.Node)} - {u.Why}"));

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} disposal(s) of the `{RecorderLocal}` local (or a name bound from "
            + "it) in HeadlessTopology.StartAsync sit outside a catch clause, so the #420 arming "
            + "window "
            + "closes when the start returns rather than when the caller reports the topology "
            + "ready. The recorder is handed to the returned topology and dropped by "
            + "SuiteTopology/StubTopology (or by DisposeAsync); it must only be disposed here on "
            + "a failure path:\n  "
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
                && o.ArgumentList.Arguments.Any(a => a.ToString() == RecorderLocal));

        Assert.True(
            handOff,
            "HeadlessTopology.StartAsync no longer hands the #420 recorder to the topology it "
            + "returns, so the arming window closes when the start returns rather than when the "
            + "topology is ready.");
    }

    /// <summary>
    /// Rule 6 — the CALL SITES themselves, so rules 1–3's <c>InlineData</c> cannot silently go
    /// stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules 1, 2 and 3 pin two files by name. That is a complete census only while those two files
    /// hold every call to <c>HeadlessTopology.StartAsync</c> that owns the far end of the arming
    /// window — and nothing checked it. A third caller could be added tomorrow, never drop or
    /// flush the recorder, and every row above would stay green while describing a set that no
    /// longer matched reality: an enumeration standing in for a property, which is the failure
    /// mode this file's own header warns about.
    /// </para>
    /// <para>
    /// <strong>CALL SITES rather than files, because the first version of this rule could not tell
    /// the two apart and the gap was real.</strong> It read each file as TEXT and recorded one FILE
    /// NAME per file containing <c>HeadlessTopology.StartAsync(</c>, so a SECOND call added to
    /// <c>SuiteTopology.cs</c> or <c>StubTopology.cs</c> left both filename checks green. Rules 1-3
    /// would not have caught it either: they are aimed at the single <c>try</c> that returns the
    /// topology type (<see cref="PostStartTry"/>), so a second call in another member of the same
    /// file sits outside everything this census looks at — and would own an arming window nobody
    /// drops or flushes. A PROSE mention was indistinguishable from a call as well:
    /// <c>SuiteTopology.cs</c> names the method in its own header comment, so that file was being
    /// counted for two reasons of which only one is a call. The rule now enumerates INVOCATION
    /// NODES with Roslyn, and the count is part of the assertion.
    /// </para>
    /// <para>
    /// <strong>It cannot pass vacuously, which is why it carries no separate emptiness
    /// guard.</strong>
    /// The assertion is an ordered sequence equality against exactly two named call sites, so a
    /// census that found nothing — because <c>src/</c> resolved somewhere thin, or because every
    /// call moved behind a spelling this rule cannot see — fails on the same line as one that found
    /// too many.
    /// </para>
    /// <para>
    /// Modelled on <c>SuiteProtocolTargetsTests.EverySuiteTopologyStartCallSite_PassesBothTargetSets</c>,
    /// which pins a call-site set the same way and for the same reason, and on
    /// <see cref="ChildProcessKillCallSiteCensusTests"/> for reading a tree this project does not
    /// reference: the files are parsed as text off disk, so assembly boundaries and project
    /// references are irrelevant. A new call site reddens this row and the fix is to add its
    /// <c>InlineData</c> above — or, if it genuinely owns no window, to say so here.
    /// </para>
    /// <para>
    /// <strong>Its own limits, since every rule in this file states them.</strong> The match is
    /// syntactic and name-based: the receiver is read on its RIGHTMOST identifier, so
    /// <c>Vouchfx.Engine.Orchestration.HeadlessTopology.StartAsync(...)</c> counts, the
    /// <c>global::</c>-prefixed spelling counts (measured, not assumed — it parses to the same
    /// member-access shape), and since the alias-qualified arm was added so does
    /// <c>O::HeadlessTopology.StartAsync(...)</c> under a <c>using O = …</c> NAMESPACE alias.
    /// Four spellings still escape. A call through a <c>using</c> alias that renames the TYPE —
    /// a different construct from the namespace alias above, and not read by the <c>::</c> arm,
    /// since a renamed type appears as an ordinary identifier that is not
    /// <c>HeadlessTopology</c>; a delegate
    /// captured from the method group and invoked later; reflection; and — the one that is not
    /// exotic — the BARE or <c>this.</c>-qualified form, because
    /// <see cref="StartAsyncCallSitesIn"/> requires a
    /// <see cref="MemberAccessExpressionSyntax"/> whose receiver names the type. A bare
    /// <c>StartAsync(...)</c> can only be written from inside <c>HeadlessTopology</c> itself, and a
    /// <c>this.</c>-qualified one not even there — <c>StartAsync</c> is static — so the gap is
    /// confined to a self-call spelled without the type name. None of the four occurs under
    /// <c>src/</c> today. Build output (<c>bin</c>, <c>obj</c>) is skipped so a generated source
    /// cannot join the population.
    /// </para>
    /// <para>
    /// One further way a call site could go missing, and it is bounded by the build rather than by
    /// anything here: <c>CSharpSyntaxTree.ParseText</c>
    /// RECOVERS from malformed input rather than failing, and no diagnostics are read, so a file
    /// that did not parse cleanly could contribute fewer call sites than it spells. A file under
    /// <c>src/</c> that does not parse does not compile, so this census would be running against a
    /// tree that never built.
    /// </para>
    /// <para>
    /// <c>HeadlessTopology.cs</c> is no longer excluded, and the exclusion's disappearance is a
    /// consequence of the change rather than a relaxation. The text scan needed it because the
    /// method's own declaration and remarks spell the name; an invocation census sees neither — a
    /// declaration is not an invocation, and remarks are trivia. Should <c>StartAsync</c> ever call
    /// itself through the TYPE NAME, that call WOULD own a window and this rule should say so; a
    /// self-call spelled bare is the gap named above.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOnlyCallersOfHeadlessTopologyStartAsync_AreTheOnesThisCensusCovers()
    {
        var root = RepositoryRoot();
        var engine = Path.Combine(root, "src");

        // The root's own directory name rather than its absolute path: the diagnostic's value is
        // which directory the vouchfx.sln walk landed on, and the account name above it belongs
        // in nobody's public job log.
        Assert.True(
            Directory.Exists(engine),
            $"this census reads the production tree at '{Path.GetFileName(root)}/src', which "
            + "does not exist. The root is resolved by walking up to the directory holding "
            + "vouchfx.sln, so a move that invalidates that walk would otherwise leave every "
            + "caller uncensused.");

        var callSites = Directory
            .EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderBuildOutput(engine, path))
            .SelectMany(StartAsyncCallSitesIn)
            .OrderBy(site => site.File, StringComparer.Ordinal)
            .ThenBy(site => site.Line)
            .ToList();

        // Ordinal order: 'St' sorts before 'Su'.
        var covered = new[] { "StubTopology.cs", "SuiteTopology.cs" };

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var found = callSites.Count == 0
            ? "none"
            : string.Join(
                ", ",
                callSites.Select(s => s.File + "(" + s.Line.ToString(invariant) + ")"));

        Assert.True(
            callSites.Select(site => site.File).SequenceEqual(covered, StringComparer.Ordinal),
            "the set of HeadlessTopology.StartAsync CALL SITES under src/ has changed, so this "
            + "census's InlineData no longer enumerates every owner of the #420 arming window. "
            + "Expected exactly one call in each of " + string.Join(", ", covered) + "; found: "
            + found
            + ". Add the new call site's file to rules 1-3 (and give it a DropDiagnostics on its "
            + "ready path and a FlushDiagnosticsAsync in its catches), or record here why it owns "
            + "no window. A SECOND call in a file already listed above needs the same treatment: "
            + "rules 1-3 only ever look at the one try block that returns the topology.");
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

    /// <summary>One call to <c>HeadlessTopology.StartAsync</c>, by file and line.</summary>
    /// <remarks>
    /// Rendered at rule 6's single call site rather than by a <c>Display</c> member of its own.
    /// Such a member cannot be <c>private</c> — a nested type's private members are NOT reachable
    /// from the containing type, only the other way round — so it would have to be spelled
    /// <c>internal</c> or <c>public</c>, and neither word describes something one expression in
    /// this file uses. MEASURED: <c>private</c> there is CS0122.
    /// </remarks>
    private sealed record StartAsyncCallSite(string File, int Line);

    /// <summary>
    /// Every <c>HeadlessTopology.StartAsync(...)</c> INVOCATION in one source file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver is matched on its RIGHTMOST identifier, so both the bare and the
    /// namespace-qualified spellings count — the same rule
    /// <see cref="ChildProcessKillCallSiteCensusTests"/> applies to <c>Process.Start</c>, and for
    /// the same reason.
    /// </para>
    /// <para>
    /// <c>descendIntoTrivia: false</c> keeps comments and XML doc blocks out, which is the whole
    /// point of parsing rather than scanning text: <c>SuiteTopology.cs</c> spells this call in its
    /// own header comment. String literals are not trivia and are not excluded by that flag; what
    /// makes them invisible is the <c>OfType&lt;InvocationExpressionSyntax&gt;()</c> filter below,
    /// since a literal — interpolated or not — is not an invocation however it is spelled. The
    /// receiver switch after it narrows invocations; it is not what keeps literals out.
    /// </para>
    /// </remarks>
    private static IEnumerable<StartAsyncCallSite> StartAsyncCallSitesIn(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);

        foreach (var invocation in tree.GetRoot()
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access
                || access.Name.Identifier.ValueText != "StartAsync")
            {
                continue;
            }

            // The RIGHTMOST identifier of the receiver, whatever qualifies it. The
            // MemberAccessExpression arm already covers both the namespace-qualified spelling and
            // the `global::`-prefixed one — MEASURED by drill: `global::Vouchfx.Engine
            // .Orchestration.HeadlessTopology.StartAsync(...)` parses with `HeadlessTopology` as
            // that arm's Name, so it was matched before this comment existed. The alias-qualified
            // arm is the one that was missing: `O::HeadlessTopology` after
            // `using O = Vouchfx.Engine.Orchestration;` is an AliasQualifiedNameSyntax, which
            // neither of the other two arms accepts.
            var receiver = access.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText,
                MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText,
                AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
                _ => null,
            };

            if (receiver != "HeadlessTopology")
            {
                continue;
            }

            yield return new StartAsyncCallSite(
                Path.GetFileName(path),
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    /// <summary>
    /// Whether a source file sits under a build-output directory of the census root.
    /// </summary>
    private static bool IsUnderBuildOutput(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>The single public <c>StartAsync</c> declared in <paramref name="fileName"/>.</summary>
    private static MethodDeclarationSyntax PublicStartAsync(string fileName) =>
        Parse(fileName)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "StartAsync"
                && m.Modifiers.Any(SyntaxKind.PublicKeyword));

    /// <summary>
    /// Every name inside <paramref name="method"/> that refers to the recorder: the declaration's
    /// own name, plus every local initialised — directly or through another such local — from one
    /// already in the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A fixed point rather than one pass, and NOT because a chain needs two
    /// sweeps.</strong>
    /// <c>DescendantNodes</c> yields declarators in document order, so
    /// <c>var a = recorder; var b = a;</c> closes in the FIRST sweep — <c>a</c> is already in the
    /// set by the time <c>b</c> is visited — and under C#'s declare-before-use rule a chain in
    /// code that COMPILES always does. The loop is what makes the result independent of that
    /// order: it is a property of the traversal, not of the rule, and a rule whose correctness
    /// rests on an enumeration order nothing here pins is a rule that breaks quietly. Locals are
    /// finite and each is added once, so the iteration terminates on any method; on today's
    /// StartAsync it costs one sweep that adds nothing.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does not follow, and why the line is drawn here.</strong> Only
    /// an initialiser that reduces, under <see cref="Unwrap"/>, to a bare
    /// <see cref="IdentifierNameSyntax"/> propagates — so parentheses, a null-forgiving <c>!</c>
    /// and a cast are seen through, while an alias that reaches the recorder through a field, a
    /// property, a method's return value or an <c>as</c> conversion is invisible to this set and
    /// therefore to rule 4. That is the
    /// boundary of the threat this rule exists for: the mutation it was written against is ONE
    /// KEYWORD (<c>var</c> to <c>using var</c>) on a declaration that is already there, and the
    /// two-statement alias is the smallest neighbouring edit — both now refused. Routing the
    /// recorder out through a member or a call before disposing it is a deliberate construction
    /// that no maintainer performs by accident, and the Docker-gated
    /// <c>AFailingTopology_WritesACaptureIntoTheRedirectedDirectory</c> is what covers it. A
    /// syntax census with no symbol table could not follow a field or a return value in any case.
    /// </para>
    /// <para>
    /// <strong>It OVER-approximates in the other direction, which is the safe one.</strong> The
    /// set holds NAMES and knows nothing of scope, so a local that merely shares a spelling with
    /// an alias — declared in a sibling block the alias never reaches, say — is treated as the
    /// recorder and its disposal is refused. That is a false red, loud and re-aimable at the line
    /// it names, rather than the silent green a scope-aware version could produce by being wrong
    /// the other way. Nothing in <c>StartAsync</c> is shaped like that today.
    /// </para>
    /// <para>
    /// Declarators inside a lambda or a local function are NOT excluded, and that is the safe
    /// direction: a name bound there is still a name, and the disposal it reaches is judged by
    /// <see cref="InsideACatch"/>, which refuses anything behind a lambda boundary outright.
    /// </para>
    /// </remarks>
    private static HashSet<string> RecorderNamesIn(SyntaxNode method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { RecorderLocal };
        var declarators = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToList();

        bool added;
        do
        {
            added = false;
            foreach (var declarator in declarators)
            {
                // Unwrapped for the same reason Aliases is: `var a = recorder!;` binds the same
                // object, and a set that admitted the bare spelling only would accept
                // `using (a)` while refusing `using (recorder!)` — one rule with two answers.
                if (Unwrap(declarator.Initializer?.Value) is IdentifierNameSyntax id
                    && names.Contains(id.Identifier.Text)
                    && names.Add(declarator.Identifier.Text))
                {
                    added = true;
                }
            }
        }
        while (added);

        return names;
    }

    /// <summary>
    /// Every <c>using</c> construct in <paramref name="method"/> that ends the lifetime of a local
    /// in <paramref name="names"/>, paired with the reason to put in the failure message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three constructs, because C# spells this disposal three ways: the using DECLARATION
    /// (<c>using var x = ...;</c>), the using STATEMENT that declares
    /// (<c>using (var x = ...) { }</c>), and the using STATEMENT that takes an already-declared
    /// local as its resource (<c>using (x) { }</c>). Each is matched against
    /// <paramref name="names"/> — <see cref="RecorderNamesIn"/>'s closure over the recorder local —
    /// so all three refuse an ALIAS exactly as they refuse the recorder itself. None of the three
    /// produces a <c>Dispose</c> invocation node, which is why the invocation scan cannot see them.
    /// </para>
    /// <para>
    /// <strong>The two DECLARING forms are split into an alias arm and a plain arm, alias FIRST,
    /// purely so the message is precise.</strong> Either arm alone would redden the same edits:
    /// a declarator initialised from a name in the set is itself in the set, so the plain arm
    /// would catch it too. Testing the INITIALISER first is what lets
    /// <c>using var alias = recorder;</c> be reported as an alias while
    /// <c>using var recorder = DcpFlightRecorder.CreateUnlessDisabled();</c> — whose initialiser is
    /// a call, not a name — falls through to the plain arm. Neither arm is dead.
    /// </para>
    /// <para>
    /// <strong>The remaining limit, stated rather than implied, and it is neither the
    /// two-statement alias nor a re-SPELLING of the local.</strong> Both of those used to escape
    /// and no longer do. <c>var alias = recorder;</c> followed by <c>using (alias) { }</c> some
    /// statements later is refused, because <see cref="RecorderNamesIn"/> carries the name across
    /// the gap; and <c>using (recorder!)</c>, <c>using ((recorder))</c>,
    /// <c>using ((IDisposable)recorder)</c> and <c>recorder!.Dispose()</c> are refused because
    /// <see cref="Unwrap"/> strips the parentheses, the null-forgiving <c>!</c> and the cast
    /// before the name is read. Those four were each ONE KEYSTROKE from the code that is there,
    /// which is squarely inside the mutation this rule exists to refuse.
    /// </para>
    /// <para>
    /// What still escapes is an alias that reaches the recorder through something that is not a
    /// re-spelling of a local at all, and each is a deliberate indirection rather than a
    /// keystroke:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     A FIELD or a PROPERTY. Reaching one means adding a member to
    ///     <c>HeadlessTopology</c> and writing the recorder into it — new declared state on a type
    ///     whose whole subject is that the recorder is handed on rather than held, and a change
    ///     <c>DcpRecorderFactoryCensusTests</c> is aimed at besides.
    ///   </description></item>
    ///   <item><description>
    ///     A METHOD's return value. Reaching one means writing a method that hands the recorder
    ///     back, which is a second exit for a value this method exists to hand to exactly one
    ///     place; a syntax census with no symbol table could not follow it in any case.
    ///   </description></item>
    ///   <item><description>
    ///     An <c>as</c> conversion, or any conversion that is not a cast.
    ///     <c>recorder as IDisposable</c> is a <see cref="BinaryExpressionSyntax"/> whose value
    ///     may be <see langword="null"/> rather than the operand, so <see cref="Unwrap"/>
    ///     deliberately does not treat it as the same object — stripping it would be a claim
    ///     about types this census cannot make. It is also a strictly longer edit than the cast it
    ///     sits beside, which IS refused.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The behavioural row is what covers that class; see <see cref="RecorderNamesIn"/> for the
    /// same boundary argued from the other side.
    /// </para>
    /// <para>
    /// The declaration form is detected by the KEYWORD rather than by the absence of one: an
    /// omitted <c>using</c> is a default <see cref="SyntaxToken"/> rather than a null, so
    /// <c>UsingKeyword.RawKind</c> is 0 exactly when there is no <c>using</c> keyword.
    /// </para>
    /// </remarks>
    private static IEnumerable<(SyntaxNode Node, string Why)> UsingDisposalsOf(
        SyntaxNode method, HashSet<string> names)
    {
        foreach (var node in method.DescendantNodes())
        {
            switch (node)
            {
                case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } alias
                    when Aliases(alias.Declaration, names):
                    yield return (alias,
                        "aliased into a `using` declaration, so the recorder is disposed as "
                        + "StartAsync returns and the post-start capture records nothing");
                    break;

                case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } declaration
                    when Declares(declaration.Declaration, names):
                    yield return (declaration,
                        "declared with `using`, so the recorder is disposed as StartAsync returns "
                        + "and the post-start capture records nothing");
                    break;

                case UsingStatementSyntax { Declaration: { } aliased } aliasStatement
                    when Aliases(aliased, names):
                    yield return (aliasStatement,
                        "aliased into a `using` statement's declaration, so the recorder is "
                        + "disposed when that block ends and the post-start capture records "
                        + "nothing");
                    break;

                case UsingStatementSyntax { Declaration: { } declared } declaringStatement
                    when Declares(declared, names):
                    yield return (declaringStatement,
                        "declared inside a `using` statement, so the recorder is disposed when "
                        + "that block ends and the post-start capture records nothing");
                    break;

                case UsingStatementSyntax { Expression: { } resourceExpression } resourceStatement
                    when Unwrap(resourceExpression) is IdentifierNameSyntax resource
                        && names.Contains(resource.Identifier.Text):
                    yield return (resourceStatement,
                        "handed to a `using` statement as its resource, so the recorder is "
                        + "disposed when that block ends and the post-start capture records "
                        + "nothing");
                    break;
            }
        }
    }

    /// <summary>
    /// The expression underneath the wrappers that change how a value is SPELLED without changing
    /// WHICH object it is: parentheses, the null-forgiving <c>!</c>, and casts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every one of these is a single keystroke against the recorder local, which puts
    /// them inside this rule's stated threat rather than outside it.</strong>
    /// <c>using (recorder!) { }</c>, <c>using ((recorder)) { }</c>,
    /// <c>using ((IDisposable)recorder) { }</c> and <c>recorder!.Dispose()</c> all end the same
    /// object's lifetime at a scope <c>StartAsync</c> owns; before this helper each of them parsed
    /// to a node rule 4's <see cref="IdentifierNameSyntax"/> test did not accept, and slipped.
    /// </para>
    /// <para>
    /// <strong>The cast is included on purpose, and the reason is worth stating because a cast
    /// LOOKS like an indirection.</strong> <c>(IDisposable)recorder</c> changes the static type
    /// the compiler reasons about; it does not change the object — for a reference conversion,
    /// which is all this sealed type admits. The <c>Dispose</c> that runs is
    /// the recorder's, at the recorder's scope, so the disposal this rule refuses has happened
    /// whatever the expression was annotated as. A <c>using</c> over a cast is the drop mutation
    /// in a costume.
    /// </para>
    /// <para>
    /// Iterative rather than recursive so a stack of them (<c>((IDisposable)(recorder!))</c>) is
    /// reduced in one pass, and it terminates because each step strips exactly one node from a
    /// finite tree. <see langword="null"/> in, <see langword="null"/> out, so callers can hand it
    /// an absent initialiser or an unreachable receiver without a guard of their own.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does NOT strip is the conversion that is not a cast.</strong>
    /// <c>recorder as IDisposable</c> is a <see cref="BinaryExpressionSyntax"/> whose result may
    /// be <see langword="null"/> rather than the operand, so treating it as the same object would
    /// be a claim this census cannot make. It escapes, and is named among the limits on
    /// <see cref="UsingDisposalsOf"/>.
    /// </para>
    /// </remarks>
    private static ExpressionSyntax? Unwrap(ExpressionSyntax? expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesised:
                    expression = parenthesised.Expression;
                    break;

                case PostfixUnaryExpressionSyntax postfix
                    when postfix.OperatorToken.IsKind(SyntaxKind.ExclamationToken):
                    expression = postfix.Operand;
                    break;

                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    break;

                default:
                    return expression;
            }
        }
    }

    private static bool Declares(
        VariableDeclarationSyntax declaration, HashSet<string> names) =>
        declaration.Variables.Any(v => names.Contains(v.Identifier.Text));

    private static bool Aliases(VariableDeclarationSyntax declaration, HashSet<string> names) =>
        declaration.Variables.Any(v =>
            Unwrap(v.Initializer?.Value) is IdentifierNameSyntax id
            && names.Contains(id.Identifier.Text));

    // Whether a disposal sits on one of StartAsync's OWN failure paths, which is the only thing
    // this rule exempts. A closure runs when its caller runs it, not when a catch does, so a
    // disposal inside one is refused either way round: a catch INSIDE the closure - the
    // `AddLogging(lb => ...)` one, say - encloses that closure's body rather than a StartAsync
    // failure path, and a catch OUTSIDE it does not run the closure's body on its own path. Hence
    // both halves: no lambda boundary between the disposal and the method, AND a catch on that
    // same stretch. Nothing in StartAsync's one closure disposes the recorder today, so this
    // cannot false-red; it is the shape a future closure would be judged by.
    private static bool InsideACatch(SyntaxNode node, SyntaxNode start)
    {
        var path = node.Ancestors().TakeWhile(a => a != start).ToList();
        return !path.Any(IsLambdaBoundary) && path.Any(a => a is CatchClauseSyntax);
    }

    private static bool IsLambdaBoundary(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

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
    /// An invocation's RECEIVER expression — <c>x</c> for both <c>x.M()</c> and <c>x?.M()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conditional form is why this is not a one-liner: <c>recorder?.Dispose()</c> parses as a
    /// <see cref="ConditionalAccessExpressionSyntax"/> whose invocation carries only a
    /// <see cref="MemberBindingExpressionSyntax"/>, so the receiver is not reachable from the
    /// invocation's own <c>Expression</c> and has to be read off the nearest conditional-access
    /// ancestor. Getting this wrong makes rule 4 silently match nothing, which is exactly the
    /// vacuous pass its <c>Assert.NotEmpty</c> exists to refuse.
    /// </para>
    /// <para>
    /// <strong>The NODE, not its rendered text, and the change is what lets
    /// <see cref="Unwrap"/> reach it.</strong> This used to return <c>.ToString()</c>, which made
    /// the caller's comparison a string equality — so <c>recorder!.Dispose()</c> produced
    /// <c>"recorder!"</c> and matched nothing, and no amount of unwrapping downstream could help,
    /// because the structure had already been flattened. Handing back the expression keeps the
    /// wrappers strippable and keeps one spelling rule (<see cref="Unwrap"/>) serving every site.
    /// </para>
    /// </remarks>
    private static ExpressionSyntax? ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Expression,
            MemberBindingExpressionSyntax => invocation
                .Ancestors()
                .OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault()
                ?.Expression,
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
