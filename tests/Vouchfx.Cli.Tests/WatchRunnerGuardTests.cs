// Vouchfx.Cli.Tests — WatchRunner keep-watching policy tests (S08-T10, B1). No Docker.
//
// WatchRunner.ProcessChangeGuardedAsync is the per-change exception policy of the watch loop:
// Ctrl-C (OperationCanceledException) propagates so the loop STOPS, but EVERY other run/build
// error — crucially including a raw ArgumentException (which EnvironmentMapper.Map throws on a
// malformed `environment` block, documented to propagate as-is through SuiteTopology.StartAsync)
// — is reported and SWALLOWED so the watcher KEEPS WATCHING.  These tests pin that policy at the
// shell level with a fake "process one change" action, so no FileSystemWatcher and no container
// is ever touched.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchRunnerGuardTests
{
    [Fact]
    public async Task NonCancellationException_IsReported_AndDoesNotEscape_SoLoopContinues()
    {
        // B1: a build/run throwing a NON-OperationCanceledException (here the exact failure mode —
        // a raw ArgumentException from a malformed `environment` block) must be CAUGHT and
        // reported, not allowed to escape and kill the watcher.  We run the guard twice — first a
        // throwing change, then a succeeding one — and assert BOTH: the throw was swallowed (no
        // exception escapes ProcessChangeGuardedAsync) AND the subsequent change still ran.
        var output = new StringWriter();
        var secondChangeRan = false;

        // 1st change: throws (must NOT escape — the await below would throw if it did).
        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new ArgumentException(
                "Service 'api' has neither 'image' nor 'project' set.", "env"),
            output,
            CancellationToken.None);

        // 2nd change: succeeds — proving the loop CONTINUES after the caught error.
        await WatchRunner.ProcessChangeGuardedAsync(
            _ =>
            {
                secondChangeRan = true;
                return Task.CompletedTask;
            },
            output,
            CancellationToken.None);

        Assert.True(secondChangeRan, "the loop must keep processing changes after a caught run error");

        var reported = output.ToString();
        Assert.Contains("--watch: error during run", reported, StringComparison.Ordinal);
        // The concise message carries the exception TYPE and MESSAGE (no secret/token).
        Assert.Contains("ArgumentException", reported, StringComparison.Ordinal);
        Assert.Contains("neither 'image' nor 'project'", reported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrchestrationException_IsReported_AsEnvironmentError_AndSwallowed()
    {
        // An OrchestrationException (a topology build/reset failure, §12.1) is reported as an
        // environment error and swallowed — the loop keeps watching.
        var output = new StringWriter();
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: "postgres",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "container failed to start");

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new OrchestrationException(info, new InvalidOperationException("boom")),
            output,
            CancellationToken.None);

        Assert.Contains(
            "--watch: environment error during run",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationCanceledException_Propagates_SoCtrlCStopsTheLoop()
    {
        // Ctrl-C MUST stop the loop: an OperationCanceledException is re-thrown, never swallowed
        // (it is caught before the general catch-all — order is load-bearing).
        var output = new StringWriter();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            WatchRunner.ProcessChangeGuardedAsync(
                _ => throw new OperationCanceledException(),
                output,
                CancellationToken.None));

        // Nothing was reported as a run error — cancellation is not a run failure.
        Assert.DoesNotContain("error during run", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #266, Item 4: <c>oex.Message</c> can reflect author <c>environment.*</c> config (an
    /// <see cref="Vouchfx.Engine.Orchestration.EnvironmentMapper"/> diagnostic quoting a declared
    /// resource name, for instance) — reachable on every save-triggered re-run, so it must be
    /// rendered inert before reaching the terminal.
    /// </summary>
    [Fact]
    public async Task OrchestrationException_WithAnsiSequenceInMessage_RendersInert()
    {
        // OrchestrationException.Message is built from OrchestrationErrorInfo's OWN fields (see
        // OrchestrationError.cs's BuildMessage), not the inner exception — so the author-declared
        // value that reaches oex.Message is the resource NAME (environment.services/dependencies),
        // not the inner exception's message.
        var esc = (char)0x1B;
        var output = new StringWriter();
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: "postgres" + esc + "[31mHACKED" + esc + "[0m",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "container failed to start");

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new OrchestrationException(info, new InvalidOperationException("boom")),
            output,
            CancellationToken.None);

        var rendered = output.ToString();
        Assert.Contains("--watch: environment error during run", rendered, StringComparison.Ordinal);
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain(esc, rendered);
    }

    /// <summary>
    /// Issue #266, Item 4: the catch-all path's <c>ex.Message</c> can carry author-declared
    /// config (e.g. the <see cref="ArgumentException"/> from a malformed <c>environment</c>
    /// block, as in <see cref="NonCancellationException_IsReported_AndDoesNotEscape_SoLoopContinues"/>
    /// above) — reachable on every save-triggered re-run, so it must be rendered inert too.
    /// </summary>
    [Fact]
    public async Task NonCancellationException_WithAnsiSequenceInMessage_RendersInert()
    {
        var esc = (char)0x1B;
        var output = new StringWriter();
        var hostileMessage =
            "Service 'api" + esc + "[31mHACKED" + esc + "[0m' has neither 'image' nor 'project' set.";

        await WatchRunner.ProcessChangeGuardedAsync(
            _ => throw new ArgumentException(hostileMessage, "env"),
            output,
            CancellationToken.None);

        var rendered = output.ToString();
        Assert.Contains("--watch: error during run", rendered, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", rendered, StringComparison.Ordinal);
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain(esc, rendered);
    }
}
