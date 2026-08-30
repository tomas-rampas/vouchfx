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
