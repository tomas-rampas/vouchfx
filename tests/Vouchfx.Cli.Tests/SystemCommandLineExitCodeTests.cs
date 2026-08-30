// Vouchfx.Cli.Tests — what the PINNED System.CommandLine does with an exception that escapes a
// command action. No Docker.
//
// WHY THIS EXISTS AS A TEST RATHER THAN A SENTENCE. `RunCommand.ExecuteAsync`'s taxonomy backstop
// (issue #413) is justified entirely by this number: if an escaping exception already produced a
// sensible exit code there would be nothing to fix, and if it produced a NON-taxonomy code the fix
// would be a different one. The first draft of that rationale asserted "the runtime's own crash
// code" from reasoning, and it was wrong — the framework catches it and returns 1, TestFailure,
// which is the one answer §12.1 reserves for a product defect the suite observed. A claim that
// decides a design belongs in a test, not in a comment.
//
// IT ALSO GUARDS A DEPENDENCY BUMP. `EnableDefaultExceptionHandler` defaulting to true is a
// property of the pinned version, not a law; a future System.CommandLine that rethrows instead
// would change what the backstop's re-throw arm costs, and this row goes red rather than the
// rationale going quietly stale.

using System.CommandLine;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class SystemCommandLineExitCodeTests
{
    /// <summary>
    /// An exception escaping a command action, under the same bare
    /// <see cref="InvocationConfiguration"/> <c>Program.cs</c> uses, exits <strong>1</strong>.
    /// </summary>
    [Fact]
    public async Task EscapingException_UnderTheDefaultHandler_ExitsOne()
    {
        var command = new RootCommand("probe");
        command.SetAction((_, _) => throw new InvalidOperationException("escapes the action"));

        // The SAME construction Program.cs performs for a non-run invocation: a bare
        // InvocationConfiguration, nothing set on it, so EnableDefaultExceptionHandler is whatever
        // the pinned framework defaults it to.
        var configuration = new InvocationConfiguration();
        Assert.True(
            configuration.EnableDefaultExceptionHandler,
            "the backstop's rationale rests on the default handler being ON for a bare config.");

        var exitCode = await command.Parse(Array.Empty<string>()).InvokeAsync(configuration);

        Assert.Equal(ExitCodes.TestFailure, exitCode);
    }

    /// <summary>
    /// The same for an <see cref="OperationCanceledException"/> — so a re-thrown cancellation costs
    /// exit 1 too, which is why <c>RunCommand</c>'s backstop re-throws only a genuine USER
    /// cancellation and maps everything else (timeouts included) to
    /// <see cref="ExitCodes.Inconclusive"/>.
    /// </summary>
    /// <remarks>
    /// Asserted with <see cref="TaskCanceledException"/> specifically, because that is the type a
    /// timeout raises and the type an unfiltered <c>catch (OperationCanceledException)</c> would
    /// have sent here.
    /// </remarks>
    [Fact]
    public async Task EscapingTaskCanceledException_UnderTheDefaultHandler_AlsoExitsOne()
    {
        var command = new RootCommand("probe");
        command.SetAction((_, _) => throw new TaskCanceledException("a timeout, not a user stop"));

        var exitCode = await command.Parse(Array.Empty<string>())
            .InvokeAsync(new InvocationConfiguration());

        Assert.Equal(ExitCodes.TestFailure, exitCode);
    }
}
