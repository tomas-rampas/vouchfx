// Vouchfx.Cli.Tests — issue #502: what `run` SAYS when `--shutdown-on-stdin-eof`'s graceful stop
// ends a run. No Docker.
//
// THE DEFECT WAS NEVER THE EXIT CODE, AND THAT IS THE WHOLE DIFFICULTY OF TESTING IT. Closing
// stdin under --shutdown-on-stdin-eof cancels a LINKED source; cancelling a linked source never
// cancels the parameter token, so `RunCommand.ExecuteAsync`'s cancellation filter
// (`cancellationToken.IsCancellationRequested`) was false for the EOF path and the throw fell into
// the generic catch, which printed:
//
//     vouchfx run: the engine failed unexpectedly and could not reach a verdict (...).  This is an
//     engine or provider defect, not a suite failure - please report it with the suite that
//     triggered it.  Reported as Inconclusive (section 12.1).
//
// Exit 4 — which is the DOCUMENTED code for a stdin-EOF stop and was correct throughout. So a
// regression row that asserts only the integer passes against the defect, before and after; every
// row below that matters asserts the SENTENCE. That is stated here rather than left implicit
// because the cheap version of this file would have been written with `Assert.Equal(4, exitCode)`
// and nothing else, and it would have been worth nothing.
//
// MEASURED RED, by mutation: with `ExecuteAsync`'s `catch (StdinEofShutdownException)` arm deleted
// and nothing else changed, 2 of the 8 rows here fail (2 failed / 6 passed / 8 total, run exit 1).
// The row that matters fails on the MESSAGE, having already passed the exit-code assertion above
// it:
//
//     Assert.Contains() Failure: Sub-string not found
//     String:    "vouchfx run: the engine failed unexpected"...
//     Not found: "vouchfx run: the run was shut down on std"...
//
// That ordering is the point of the file in one screen: the integer was right on both sides, and
// the sentence was not.
//
// WHAT THIS FILE CANNOT REACH, SAID PLAINLY. The composition — a real EOF on the process's own
// standard input, cancelling the real linked source, during a real `--changed-since` git call — is
// not producible in-process: `RunCommand` opens `Console.OpenStandardInput()` directly (a settled
// decision; see ShutdownOnStdinEofArgParsingTests' hygiene note), and a test host's stdin is a
// terminal on a developer machine and a closed pipe in CI, so a row that depended on which would
// be flaky by construction. The classification is therefore pinned in three pieces: the truth
// table of `IsStdinEofShutdown` (executable), the conversion hop in `ExecuteCoreAsync` (a source
// census, the idiom MixedSuiteEngineFaultHopCensusTests uses for the same reason), and the
// reporting arm itself driven through the real `ExecuteAsync` front door with the marker injected
// by a throwing sink — the same injection the taxonomy-backstop rows use.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class StdinEofShutdownDiagnosticTests : IDisposable
{
    private readonly string _root;

    public StdinEofShutdownDiagnosticTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "vouchfx-stdin-eof-diagnostic-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a locked or read-only file must not fail the test.
        }
    }

    /// <summary>
    /// A run ended by the stdin-EOF graceful stop is reported AS that stop — and is not described
    /// as an engine defect the operator should report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE EXIT CODE IS ASSERTED HERE TOO, BUT IT IS NOT THE EVIDENCE. It was already
    /// <see cref="ExitCodes.Inconclusive"/> before the fix and is Inconclusive after it; the
    /// assertion exists so a future change that fixes the wording by taking the code somewhere
    /// else cannot pass. The three message assertions are what this row is for.
    /// </para>
    /// <para>
    /// The driver is a sink that raises the marker on its FIRST write and captures every write
    /// after it — the injection the taxonomy-backstop rows use, extended so the diagnostic the
    /// catch itself writes can be read back. The marker is what
    /// <c>ExecuteCoreAsync</c> raises once it has classified the cancellation; injecting it here
    /// exercises the REPORTING arm, and the census row below pins that nothing else raises it.
    /// </para>
    /// <para>
    /// <strong>THE POSITIVE ASSERTION IS THE WHOLE NOTICE, AND A PHRASE WOULD NOT HAVE
    /// DONE.</strong> Measured against the mutated tree: an earlier draft asserted the phrase
    /// "shut down on stdin EOF" and PASSED with the arm deleted, because the catch-all composes
    /// its line from the
    /// escaping exception's own <see cref="Exception.Message"/> — and the marker's message says
    /// exactly that. So the grep matched the engine-defect line quoting the marker. What separates
    /// the two answers is the whole notice plus the two sentences that must be gone, and that is
    /// what is asserted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_StdinEofShutdown_ReportsTheRequestedStop_NotAnEngineDefect()
    {
        var sink = new ThrowOnceThenCaptureWriter(
            () => new RunCommand.StdinEofShutdownException(
                new OperationCanceledException("stdin closed mid-run")));

        var exitCode = await ExecuteAsync(sink);

        // FIRST, and deliberately: this assertion passes against the DEFECT too. The stdin-EOF
        // stop exited 4 before the fix and exits 4 after it, so a row that stopped here would be
        // green on a run that told the operator their graceful shutdown was an engine bug.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        var written = sink.Captured;

        Assert.Contains(RunCommand.StdinEofShutdownNotice, written, StringComparison.Ordinal);
        Assert.DoesNotContain("failed unexpectedly", written, StringComparison.Ordinal);
        Assert.DoesNotContain("please report it", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The timeout arm did not move.</strong> A <see cref="TaskCanceledException"/> nobody
    /// asked for still maps to <see cref="ExitCodes.Inconclusive"/> AND still carries the
    /// engine-defect wording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RunCommandTaxonomyBackstopTests</c> already pins the CODE for this case. This row exists
    /// because #502 changed the wording of one arm and the cheapest wrong fix — widening the
    /// cancellation filter — would have re-labelled every timeout as a graceful stdin stop while
    /// leaving that code at 4, invisible to any row asserting integers. The sentence is the only
    /// thing that can see it, so the sentence is asserted on BOTH sides of the split.
    /// </para>
    /// <para>
    /// <see cref="TaskCanceledException"/> deliberately, not the base type: it is what
    /// <c>HttpClient</c>'s default timeout and every <c>CancelAfter</c> budget actually raise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_TimeoutCancellation_KeepsTheEngineDefectWording()
    {
        var sink = new ThrowOnceThenCaptureWriter(
            () => new TaskCanceledException("a timeout, not a stdin stop"));

        var exitCode = await ExecuteAsync(sink);

        var written = sink.Captured;

        Assert.Contains("failed unexpectedly", written, StringComparison.Ordinal);
        Assert.DoesNotContain("stdin EOF", written, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// The classification truth table: an EOF stop is a cancelled SHUTDOWN source with an
    /// uncancelled USER token, and nothing else is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row by row: the flag being off (a <see langword="null"/> source) leaves every cancellation
    /// with the mapping it has today, which is what keeps a timeout — on the overwhelmingly common
    /// path where <c>--shutdown-on-stdin-eof</c> was never passed — untouched by this rule. A
    /// source that exists but has not been cancelled is the same answer for the same reason: the
    /// flag was set, no EOF arrived, and the cancellation came from somewhere else.
    /// </para>
    /// <para>
    /// The last row is the one that is easy to get wrong, and is why this is not simply "is the
    /// run's token cancelled". A real Ctrl-C cancels the PARAMETER token, and linking propagates
    /// that INTO the shutdown source — so both are cancelled, and without the second clause the
    /// user's own stop would be re-labelled a stdin stop and would stop re-throwing to
    /// System.CommandLine's termination handling.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false, false, false, false)] // flag off: never this rule's business.
    [InlineData(true, false, false, false)]  // flag on, no EOF: some other cancellation.
    [InlineData(true, true, false, true)]    // EOF, user quiet: the graceful stop.
    [InlineData(true, true, true, false)]    // Ctrl-C, propagated through the link: not ours.
    public void IsStdinEofShutdown_AnswersTrue_OnlyForAnEofStop(
        bool hasShutdownSource,
        bool shutdownCancelled,
        bool userCancelled,
        bool expected)
    {
        using var shutdownSource = hasShutdownSource ? new CancellationTokenSource() : null;
        using var userSource = new CancellationTokenSource();

        if (userCancelled)
        {
            userSource.Cancel();
        }

        if (shutdownCancelled)
        {
            shutdownSource!.Cancel();
        }

        Assert.Equal(
            expected,
            RunCommand.IsStdinEofShutdown(shutdownSource, userSource.Token));
    }

    /// <summary>
    /// The conversion hop: exactly one site raises the marker, it sits in a <c>catch</c> over
    /// <see cref="OperationCanceledException"/>, and that catch's FILTER is
    /// <c>IsStdinEofShutdown</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source census, and the reason is the one this file opens with: the hop needs a real stdin
    /// EOF, which no in-process row can produce. What it guards is the two ways the hop rots
    /// silently. Drop the FILTER and every cancellation that escapes the pipeline — a timeout
    /// included — becomes "the run was shut down on stdin EOF", with the exit code unchanged at 4
    /// and every integer-asserting row still green. Drop the THROW and #502 is fully back.
    /// </para>
    /// <para>
    /// VACUITY FIRST: the expected count is asserted before anything is concluded from it, so a
    /// rename cannot leave this guard matching nothing and passing for free.
    /// </para>
    /// <para>
    /// THE ENCLOSING METHOD IS ASSERTED TOO, so the row's name matches its query. The scan is over
    /// the whole compilation unit, which means the throw could be moved into any other member of
    /// <c>RunCommand</c> — one without the linked source in scope, or one the pipeline never
    /// reaches — and still satisfy a census that only checked the catch shape around it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ExecuteCoreAsync_RaisesTheMarker_OnlyUnderTheClassificationFilter()
    {
        var throwSites = ParsedRunCommand()
            .DescendantNodes()
            .OfType<ThrowStatementSyntax>()
            .Where(t => t.Expression is ObjectCreationExpressionSyntax
            {
                Type: IdentifierNameSyntax { Identifier.ValueText: "StdinEofShutdownException" },
            })
            .ToList();

        Assert.True(
            throwSites.Count == 1,
            "Expected exactly 1 `throw new StdinEofShutdownException(...)` in RunCommand.cs, found "
            + $"{throwSites.Count}. Zero means the stdin-EOF stop is no longer classified at all "
            + "and falls back into the engine-defect wording (#502). More than one means the "
            + "classification is made in two places and can disagree.");

        var enclosingMethod = throwSites[0].FirstAncestorOrSelf<MethodDeclarationSyntax>();

        Assert.Equal("ExecuteCoreAsync", enclosingMethod?.Identifier.ValueText);

        var enclosingCatch = throwSites[0].FirstAncestorOrSelf<CatchClauseSyntax>();

        Assert.NotNull(enclosingCatch);
        Assert.Equal(
            "OperationCanceledException",
            (enclosingCatch!.Declaration?.Type as IdentifierNameSyntax)?.Identifier.ValueText);

        var filter = enclosingCatch.Filter;

        Assert.NotNull(filter);
        Assert.Contains(
            filter!.FilterExpression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.Expression is IdentifierNameSyntax
            {
                Identifier.ValueText: "IsStdinEofShutdown",
            });
    }

    /// <summary>
    /// The reporting hop: <c>ExecuteAsync</c> answers the marker, and does so BEFORE its
    /// catch-all.
    /// </summary>
    /// <remarks>
    /// Order is behaviour, not style: <c>catch (Exception ex)</c> would claim the marker first and
    /// print the engine-defect line, which is the defect verbatim, with the exit code unchanged.
    /// The executable row above proves the arm answers today; this proves the ordering that lets
    /// it, since a reordering is silent in every other respect.
    /// </remarks>
    [Fact]
    public void ExecuteAsync_AnswersTheMarker_BeforeItsCatchAll()
    {
        var executeAsync = ParsedRunCommand()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ExecuteAsync");

        var catches = executeAsync.DescendantNodes().OfType<CatchClauseSyntax>().ToList();

        var markerArm = catches.FindIndex(
            c => (c.Declaration?.Type as IdentifierNameSyntax)?.Identifier.ValueText
                == "StdinEofShutdownException");
        var catchAll = catches.FindIndex(
            c => (c.Declaration?.Type as IdentifierNameSyntax)?.Identifier.ValueText
                == "Exception");

        Assert.True(
            markerArm >= 0,
            "ExecuteAsync has no `catch (StdinEofShutdownException)` arm, so a classified "
            + "stdin-EOF stop falls into the catch-all and is reported as an engine defect "
            + "(#502).");
        Assert.True(
            catchAll >= 0, "ExecuteAsync's `catch (Exception ex)` backstop is gone (#413).");
        Assert.True(
            markerArm < catchAll,
            $"The marker arm must precede the catch-all; found marker at {markerArm}, catch-all at "
            + $"{catchAll}.");
    }

    private Task<int> ExecuteAsync(TextWriter output)
        => RunCommand.ExecuteAsync(
            path: _root,
            criteria: SelectionCriteria.None,
            parallel: null,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

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

    /// <summary>
    /// A sink that raises a caller-chosen exception on its FIRST write and captures every write
    /// after it.
    /// </summary>
    /// <remarks>
    /// The run's own first diagnostic is the injection point (here: "no scenarios found"), and the
    /// catch's reply is what the assertions read. A sink that threw on every write — the
    /// taxonomy-backstop rows' driver — pins the exit code but can never show the message, which
    /// is the only thing #502 moved.
    /// </remarks>
    private sealed class ThrowOnceThenCaptureWriter : TextWriter
    {
        private readonly Func<Exception> _fault;
        private readonly StringWriter _captured = new();
        private bool _thrown;

        public ThrowOnceThenCaptureWriter(Func<Exception> fault) => _fault = fault;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public string Captured => _captured.ToString();

        public override void Write(char value) => WriteCore(value.ToString());

        public override void Write(string? value) => WriteCore(value);

        public override void WriteLine(string? value) => WriteCore(value);

        public override Task WriteLineAsync(string? value)
        {
            WriteCore(value);
            return Task.CompletedTask;
        }

        private void WriteCore(string? value)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw _fault();
            }

            _captured.WriteLine(value);
        }
    }
}
