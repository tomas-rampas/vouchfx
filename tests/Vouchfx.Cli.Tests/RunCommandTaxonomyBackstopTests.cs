// Vouchfx.Cli.Tests — issue #413's second half: `RunCommand.ExecuteAsync`'s top-level catch.
// No Docker.
//
// WHAT WAS UNPINNED. `run` had no broad catch at all, so ANY exception escaping the engine reached
// System.CommandLine's default exception handler, which prints it and returns exit code **1** —
// TestFailure, the one code §12.1 reserves for a product defect the suite observed. That is inside
// the taxonomy and saying the wrong thing, which is worse than being outside it: a CI job reads a
// provider crash as "your service is broken". The 1 is MEASURED, on the pinned framework, by
// SystemCommandLineExitCodeTests — this file's rationale rests on it. The specific route #413 was
// raised on (a provider whose `Bind` threw) is closed at its own throw site in `ProviderPipeline`;
// this file pins the BACKSTOP, which exists for the routes nobody has found yet.
//
// THE DRIVER IS A THROWING OUTPUT SINK, and it is chosen because it is the one fault this test
// project can inject through the real front door: `RunCommand.ExecuteAsync` builds its provider
// registry from the sealed Core list, so no stub provider can be reached from here. What the sink
// exercises is exactly the property under test — an exception raised inside the run that no inner
// handler claims — and it additionally pins the half that a naive catch gets wrong: the
// diagnostic the catch itself writes must not be able to re-throw the process down.

using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class RunCommandTaxonomyBackstopTests : IDisposable
{
    private readonly string _root;

    public RunCommandTaxonomyBackstopTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "vouchfx-taxonomy-backstop-" + Guid.NewGuid().ToString("n"));
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
    /// An exception that would otherwise escape the run maps onto
    /// <see cref="ExitCodes.Inconclusive"/> — never a non-taxonomy exit, and never
    /// <see cref="ExitCodes.Success"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED RED FIRST: without the catch this test failed with the injected
    /// <see cref="InvalidOperationException"/> propagating out of <c>ExecuteAsync</c> rather than
    /// with a wrong integer — which is the defect stated exactly.
    /// </para>
    /// <para>
    /// The sink throws on EVERY write, including the one the catch makes to report the fault, so
    /// this row also pins that the diagnostic is best-effort while the exit code is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_UnexpectedThrow_IsMappedToTheInconclusiveExitCode()
    {
        var exitCode = await ExecuteAsync(new ThrowingWriter(_ => new InvalidOperationException("boom")));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// A cancellation the USER asked for — the token System.CommandLine cancels on Ctrl-C /
    /// SIGTERM — is re-thrown rather than mapped, so that path keeps the behaviour it has.
    /// </summary>
    /// <remarks>
    /// Not decoration: this is the assertion that stops the backstop widening into "the run never
    /// throws", which would swallow a Ctrl-C and take the exit away from the framework's own
    /// termination handling. The token is CANCELLED here, which is the whole point — the same
    /// exception with an uncancelled token is the row below, and it must answer differently.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_UserCancellation_IsRethrownRatherThanMapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ExecuteAsync(
                new ThrowingWriter(_ => new OperationCanceledException(cts.Token)),
                cts.Token));
    }

    /// <summary>
    /// <strong>A <see cref="TaskCanceledException"/> nobody asked for — a TIMEOUT — maps to
    /// <see cref="ExitCodes.Inconclusive"/>, and does NOT ride the cancellation re-throw out to
    /// exit 1.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED RED FIRST against the unfiltered <c>catch (OperationCanceledException) { throw; }</c>
    /// this replaced: the exception propagated straight out of <c>ExecuteAsync</c>, and in the real
    /// process that is System.CommandLine's default handler and exit <strong>1</strong>
    /// (<c>SystemCommandLineExitCodeTests</c>) — a transport hiccup reported to CI as a product
    /// defect, by the very frame written to stop that happening.
    /// </para>
    /// <para>
    /// <see cref="TaskCanceledException"/> is the type deliberately, not
    /// <see cref="OperationCanceledException"/>: it derives from it, and it is what
    /// <c>HttpClient</c>'s default 100-second timeout and every <c>CancelAfter</c> budget actually
    /// raise. A row using the base type would pass against a filter that special-cased only the
    /// derived one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_TimeoutCancellationNobodyRequested_IsMappedToInconclusive()
    {
        var exitCode = await ExecuteAsync(
            new ThrowingWriter(_ => new TaskCanceledException("a timeout, not a user stop")));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// <strong>An escaped exception whose <see cref="Exception.Message"/> ITSELF throws still
    /// returns the taxonomy code.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catch-all's diagnostic interpolates <c>ex.Message</c>. While that interpolation was
    /// evaluated as a CALL-SITE ARGUMENT it sat outside the best-effort guard, so a <c>Message</c>
    /// override that throws escaped <c>ExecuteAsync</c> entirely and handed the run to
    /// System.CommandLine's exit <strong>1</strong> — with the taxonomy code already decided and one
    /// <c>return</c> away. That is the same defect this whole file exists to pin, reached through
    /// the reporting of the fault rather than the fault itself, and it is open issue #518's class.
    /// </para>
    /// <para>
    /// <strong>Not a hypothetical exception type.</strong> A provider, or a <c>script.csharp</c>
    /// author, can define one; <c>Message</c> is virtual and nothing constrains an override. The row
    /// asserts the INTEGER because that is what the defect changed — the diagnostic is
    /// unproducible here by construction, and saying so is the point of the best-effort contract.
    /// </para>
    /// <para>
    /// MEASURED RED by reverting the composition to a call-site argument: this row then fails with
    /// the injected <see cref="InvalidOperationException"/> propagating out of <c>ExecuteAsync</c>,
    /// and it is the only row in the project that moves.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_ExceptionWhoseMessageThrows_StillReturnsTheTaxonomyCode()
    {
        var exitCode = await ExecuteAsync(new ThrowingWriter(_ => new HostileMessageException()));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// <strong>A <see cref="Exception.Message"/> getter that throws an
    /// <see cref="OperationCanceledException"/> WHILE THE TOKEN IS CANCELLED still returns the
    /// taxonomy code.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row above pins a hostile <c>Message</c> that throws
    /// <see cref="InvalidOperationException"/>, and it cannot see this: the guard's write filter
    /// deliberately declines an <see cref="OperationCanceledException"/> raised while the token is
    /// cancelled, so that a genuine Ctrl-C during the WRITE propagates. Composition used to sit
    /// inside that same <c>try</c> and therefore under that same filter — so a <c>Message</c>
    /// getter that raised THAT type rather than any other walked straight out of
    /// <c>ExecuteAsync</c> and past the taxonomy again. Nothing constrains which type an override
    /// throws, so #518's fix was undone by the choice of exception type alone.
    /// </para>
    /// <para>
    /// <strong>The row is not vacuous, and it asserts so rather than claiming it.</strong> A
    /// cancelled token can also make the pipeline itself throw before the injected fault is ever
    /// raised, which would leave this passing on the wrong mechanism;
    /// <c>MessageWasRead</c> fails the row unless the hostile getter was actually reached.
    /// </para>
    /// <para>
    /// MEASURED RED by reverting composition back inside the write's <c>try</c>: this row then
    /// fails with the injected <see cref="OperationCanceledException"/> propagating out of
    /// <c>ExecuteAsync</c>, while every other row in this class — the
    /// <see cref="InvalidOperationException"/> one above included — stays green, which is the
    /// whole reason this row had to be added rather than the existing one extended.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_MessageThrowsCancellationWhileCancelled_StillReturnsTheCode()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var hostile = new CancellingMessageException(cts.Token);

        var exitCode = await ExecuteAsync(new ThrowingWriter(_ => hostile), cts.Token);

        Assert.True(
            hostile.MessageWasRead,
            "The hostile Message getter was never reached, so this row proves nothing: the run "
            + "returned before the catch-all composed its diagnostic.");
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    private Task<int> ExecuteAsync(TextWriter output, CancellationToken cancellationToken = default)
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
            cancellationToken: cancellationToken);

    /// <summary>
    /// An exception whose <see cref="Exception.Message"/> throws rather than returning text.
    /// </summary>
    /// <remarks>
    /// <c>Message</c> is virtual, so this is a shape any provider-defined or author-defined
    /// exception type can have — deliberately or by accident, since a lazily-composed message can
    /// dereference state the failure already invalidated.
    /// </remarks>
    private sealed class HostileMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("this exception's Message throws");
    }

    /// <summary>
    /// An exception whose <see cref="Exception.Message"/> throws an
    /// <see cref="OperationCanceledException"/> — the one type the write guard's filter lets
    /// through while the token is cancelled.
    /// </summary>
    /// <remarks>
    /// Nothing stops an override raising that type in particular, which is exactly why the
    /// composition may not share a filter written to let a real Ctrl-C out of the WRITE.
    /// <see cref="MessageWasRead"/> exists so the row asserting this cannot pass vacuously.
    /// </remarks>
    private sealed class CancellingMessageException : Exception
    {
        private readonly CancellationToken _token;

        public CancellingMessageException(CancellationToken token) => _token = token;

        public bool MessageWasRead { get; private set; }

        public override string Message
        {
            get
            {
                MessageWasRead = true;
                throw new OperationCanceledException("this exception's Message cancels", _token);
            }
        }
    }

    /// <summary>A sink that raises a caller-chosen exception on every write.</summary>
    private sealed class ThrowingWriter : TextWriter
    {
        private readonly Func<string?, Exception> _fault;

        public ThrowingWriter(Func<string?, Exception> fault) => _fault = fault;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw _fault(value.ToString());

        public override void Write(string? value) => throw _fault(value);

        public override void WriteLine(string? value) => throw _fault(value);

        public override Task WriteLineAsync(string? value) => throw _fault(value);
    }
}
