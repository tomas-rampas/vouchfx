// The latent vacuous-green hazard in TopologyTeardownLeakTests' docker helper, and the two
// properties that keep it closed.
//
// THE HAZARD
// ──────────
// TopologyTeardownLeakTests is a container-LEAK ASSERTION: it asks docker which containers and
// networks carrying this run's DCP creatorProcessId survive teardown, and asserts the answer is
// empty. The helper it asks with never checked ExitCode, and returned an empty list on the bounded
// wait's timeout path. So a `docker ps` that FAILED — non-zero exit with the error on stderr, or a
// wait that expired — produced an empty list, which the assertion read as "no residue survives"
// and PASSED. A broken or slow docker daemon silently turned the leak test green: the exact
// vacuous green that suite exists to detect.
//
// WHY NOT SIMPLY MAKE EVERYTHING THROW
// ────────────────────────────────────
// The callers split, and the split is keyed on the CONSEQUENCE of being wrong, not on taste:
//
//   assertion queries + probe discovery   a false empty is a false PASS   -> must throw
//   self-cleanup safety net (`finally`)   a throw REPLACES the verdict    -> must tolerate
//
// The second is not hypothetical: `docker rm -f` legitimately exits non-zero when the container is
// already gone, a race the self-cleanup is guaranteed to run into because DCP's own teardown is
// removing the same resources concurrently — and a throw out of a `finally` is exactly the
// misattribution issue #378 is about. Hence two named entry points over one shared core: strict is
// what a future caller gets by reaching for `RunDocker` without thinking, and the tolerant path
// must be spelled out.
//
// WHY THESE TESTS NEED NEITHER A DOCKER DAEMON NOR A DOCKER BINARY
// ───────────────────────────────────────────────────────────────
// A test that could only run where docker runs would leave the helper's whole point pinned by
// nothing in the blocking lane — and this file must NOT add a `requires=docker` row to that lane.
// `RunCli` therefore takes the executable as a parameter (every production call passes "docker"),
// and the rows below drive the real code path with a child whose exit code and stderr they choose:
// `cmd.exe` on Windows, `/bin/sh` elsewhere. Both are unconditionally present on their platform.
// That is a REAL child through the REAL helper, not a stand-in for docker — nothing here fakes a
// docker CLI, which would only pin the fake.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Pins <see cref="TopologyTeardownLeakTests.CliFailurePolicy"/>: a failing child never reads as
/// "nothing survives" on the strict path, still reads as nothing to do on the tolerant one, and the
/// tolerant one is reachable from exactly one member.
/// </summary>
public sealed class DockerCliFailurePolicyTests
{
    /// <summary>The exit code the probe child is asked for — non-zero, and not 1.</summary>
    /// <remarks>
    /// Not 1, so an assertion that finds it in the message is evidence the helper READ
    /// <c>ExitCode</c> rather than evidence that some other "1" happens to appear in the text.
    /// </remarks>
    private const int ProbeExitCode = 3;

    /// <summary>What the probe child writes to stderr.</summary>
    private const string ProbeStderr = "vouchfxprobestderr";

    /// <summary>What the probe child writes to stdout on the success row.</summary>
    private const string ProbeStdout = "vouchfxprobestdout";

    /// <summary>The file whose call sites the policy census below reads.</summary>
    private const string CensusedFile = "TopologyTeardownLeakTests.cs";

    /// <summary>The strict entry point — what a caller gets without thinking about it.</summary>
    private const string StrictEntryPoint = "RunDocker";

    /// <summary>The tolerant entry point, which must be spelled out to be selected.</summary>
    private const string TolerantEntryPoint = "RunDockerBestEffort";

    /// <summary>
    /// The suffix the census actually keys the tolerant population on.
    /// </summary>
    /// <remarks>
    /// A suffix rather than the exact name above, so a SECOND tolerant helper added later — a
    /// <c>RunDockerSingleBestEffort</c>, say — arrives inside the fence rather than outside it. The
    /// exact name is still kept, for the vacuity guard and to name the method in the messages.
    /// </remarks>
    private const string TolerantSuffix = "BestEffort";

    /// <summary>The one member permitted to name <see cref="TolerantEntryPoint"/>.</summary>
    private const string TolerantCallerMember = "ForceCleanupForThisRun";

    /// <summary>A representative assertion-query argument list (hoisted per CA1861).</summary>
    private static readonly string[] s_containerQueryArgs = { "ps", "-a" };

    /// <summary>A representative network-query argument list (hoisted per CA1861).</summary>
    private static readonly string[] s_networkQueryArgs = { "network", "ls" };

    /// <summary>
    /// A non-zero exit throws rather than yielding an empty list — the hazard itself.
    /// </summary>
    /// <remarks>
    /// The two assertions on the message are not decoration. A leak test that dies naming the exit
    /// code and the child's stderr is diagnosable; one that dies saying "assertion failed" sends
    /// its reader to the wrong code. Both halves are asserted because both are reachable only if
    /// the helper actually waited for the child and materialised the stderr drain.
    /// </remarks>
    [Fact]
    public void StrictPolicy_NonZeroExit_Throws_RatherThanReportingAnEmptyList()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            TopologyTeardownLeakTests.RunCli(
                ProbeExecutable,
                TopologyTeardownLeakTests.CliFailurePolicy.Fail,
                FailingProbeArgs));

        Assert.Contains($"exit code {ProbeExitCode}", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ProbeStderr, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tolerant path still absorbs a failing child — the <c>docker rm -f</c>-lost-the-race
    /// case, from a <c>finally</c> where a throw would cost the real verdict.
    /// </summary>
    [Fact]
    public void TolerantPolicy_NonZeroExit_ReturnsEmpty_WithoutThrowing()
    {
        var lines = TopologyTeardownLeakTests.RunCli(
            ProbeExecutable,
            TopologyTeardownLeakTests.CliFailurePolicy.Tolerate,
            FailingProbeArgs);

        Assert.Empty(lines);
    }

    /// <summary>
    /// A child that exits 0 still yields its stdout lines — the strictness added nothing that
    /// reddens the path every assertion query actually takes.
    /// </summary>
    [Fact]
    public void StrictPolicy_ZeroExit_ReturnsTheStdoutLines()
    {
        var lines = TopologyTeardownLeakTests.RunCli(
            ProbeExecutable,
            TopologyTeardownLeakTests.CliFailurePolicy.Fail,
            SucceedingProbeArgs);

        Assert.Equal(new[] { ProbeStdout }, lines);
    }

    /// <summary>
    /// The failure text carries the command, what went wrong, the exit code and stderr.
    /// </summary>
    /// <remarks>
    /// Asserted directly rather than only through a live child because the timeout branch has no
    /// exit code and no stderr to hand — this row is what pins that such a failure still names the
    /// command and says so explicitly instead of printing an empty tail.
    /// </remarks>
    [Fact]
    public void DescribeCliFailure_NamesTheCommand_TheExitCode_AndStderr()
    {
        var withCode = TopologyTeardownLeakTests.DescribeCliFailure(
            "docker", s_containerQueryArgs, exitCode: 1, stderr: "Cannot connect to the Docker daemon", "exited non-zero");

        Assert.Contains("docker ps -a", withCode, StringComparison.Ordinal);
        Assert.Contains("exit code 1", withCode, StringComparison.Ordinal);
        Assert.Contains("Cannot connect to the Docker daemon", withCode, StringComparison.Ordinal);

        var withoutCode = TopologyTeardownLeakTests.DescribeCliFailure(
            "docker", s_networkQueryArgs, exitCode: null, stderr: null, "did not exit in time");

        Assert.Contains("docker network ls", withoutCode, StringComparison.Ordinal);
        Assert.Contains("no exit code", withoutCode, StringComparison.Ordinal);
        Assert.Contains("(no stderr)", withoutCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the self-cleanup safety net may name the tolerant entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Naming is already most of the guard — a caller has to type <c>BestEffort</c> to get
    /// tolerance, so it cannot be selected by a slip. What naming cannot stop is a future author
    /// reaching for the tolerant one DELIBERATELY, in a member where an empty list is read as an
    /// answer, because a strict call there was inconveniently loud. That reintroduces the exact
    /// defect, and the assertion it would silently satisfy is the leak assertion. So the population
    /// is pinned syntactically: every invocation of a <c>…BestEffort</c> helper sits in
    /// <c>ForceCleanupForThisRun</c>.
    /// </para>
    /// <para>
    /// Roslyn rather than a regex, and node kind rather than text, for the house reason (see
    /// <c>ChildProcessKillCallSiteCensusTests</c>): this file and the censused one both name both
    /// methods many times in prose. <c>descendIntoTrivia: false</c> drops the comments and XML
    /// docs, and switching on <see cref="InvocationExpressionSyntax"/> drops the string literals,
    /// which are ordinary syntax and not trivia.
    /// </para>
    /// <para>
    /// Vacuity-guarded in both directions: a census that found no tolerant call and one that found
    /// no strict call are each what a census pointed at the wrong file reports, and "no offender"
    /// is what both would say.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheSelfCleanupSafetyNet_NamesTheTolerantEntryPoint()
    {
        var path = Path.Combine(ProjectDirectory(), CensusedFile);

        Assert.True(
            File.Exists(path),
            $"This census reads '{path}' by path, so renaming or moving {CensusedFile} takes the "
            + "population out from under it and would otherwise leave the policy split ungated.");

        var calls = FindCalls(path);

        Assert.True(
            calls.Any(call => call.Method == StrictEntryPoint),
            $"This census found no `{StrictEntryPoint}` call in {CensusedFile}. Either the helper "
            + "was renamed - in which case this gate is watching nothing and must move with it - or "
            + "the calls are reached through a spelling the census does not recognise, which is "
            + "worse, because it reports itself green either way.");

        var tolerant = calls.Where(call => call.Method.EndsWith(TolerantSuffix, StringComparison.Ordinal)).ToList();

        Assert.True(
            tolerant.Count > 0,
            $"This census found no `{TolerantEntryPoint}` call in {CensusedFile}. The tolerant path "
            + $"exists for exactly one member ({TolerantCallerMember}); if nothing calls it, either "
            + "that member stopped tolerating - which turns a docker race in a `finally` into a "
            + "teardown failure that replaces the real verdict (issue #378) - or this gate has lost "
            + "sight of the call sites it is meant to fence.");

        var offenders = tolerant.Where(call => call.Member != TolerantCallerMember).ToList();

        Assert.True(
            offenders.Count == 0,
            $"A `{TolerantSuffix}` helper reports a failing docker call as an EMPTY LIST, and every "
            + "other member of this class reads an empty list as 'no container or network survived "
            + "teardown'. Used outside the self-cleanup safety net it therefore converts a broken or "
            + "slow docker into a passing leak assertion - the vacuous green the whole class exists "
            + $"to detect. It is legitimate only in `{TolerantCallerMember}`, which runs from a "
            + "`finally`, asserts on nothing, and races DCP's own teardown for the same resources. "
            + $"Use `{StrictEntryPoint}` instead. Offending call site(s):\n"
            + string.Join(
                "\n",
                offenders.Select(call => $"  {CensusedFile}({call.Line}): {call.Method} in {call.Member}")));
    }

    /// <summary>One call to a docker entry point: which one, in which member, on which line.</summary>
    private sealed record HelperCall(string Method, string Member, int Line);

    /// <summary>The executable the probe child runs — unconditionally present on its platform.</summary>
    private static string ProbeExecutable => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /// <summary>
    /// Arguments producing a child that writes <see cref="ProbeStderr"/> to stderr and exits
    /// <see cref="ProbeExitCode"/>.
    /// </summary>
    /// <remarks>
    /// One argument carrying the whole script, which is what keeps the two platforms comparable.
    /// On Unix <c>ArgumentList</c> entries become argv verbatim, so <c>/bin/sh -c</c> sees the
    /// script exactly. On Windows .NET quotes the entry because it contains spaces, and
    /// <c>cmd.exe /c</c> strips that outer pair before executing — the documented "otherwise"
    /// branch of its quoting rule, which applies here because the script contains the special
    /// characters <c>&amp;</c> and <c>&gt;</c>.
    /// </remarks>
    private static string[] FailingProbeArgs => OperatingSystem.IsWindows()
        ? new[] { "/c", $"echo {ProbeStderr} 1>&2 & exit {ProbeExitCode}" }
        : new[] { "-c", $"echo {ProbeStderr} >&2; exit {ProbeExitCode}" };

    /// <summary>Arguments producing a child that prints one stdout line and exits 0.</summary>
    private static string[] SucceedingProbeArgs => OperatingSystem.IsWindows()
        ? new[] { "/c", "echo", ProbeStdout }
        : new[] { "-c", $"echo {ProbeStdout}" };

    /// <summary>
    /// This test project's directory, resolved from the compiled assembly rather than from the
    /// working directory, which <c>dotnet test</c> does not guarantee.
    /// </summary>
    private static string ProjectDirectory()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(DockerCliFailurePolicyTests).Assembly.Location)!;

        // bin/<cfg>/net8.0 -> the project directory.
        return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", ".."));
    }

    /// <summary>Every call to either docker entry point in one file, with its enclosing member.</summary>
    private static List<HelperCall> FindCalls(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        var calls = new List<HelperCall>();

        foreach (var invocation in tree.GetRoot()
                     .DescendantNodes(descendIntoTrivia: false)
                     .OfType<InvocationExpressionSyntax>())
        {
            var method = invocation.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                _ => null,
            };

            if (method is null
                || (method != StrictEntryPoint && !method.EndsWith(TolerantSuffix, StringComparison.Ordinal)))
            {
                continue;
            }

            calls.Add(new HelperCall(
                method,
                EnclosingMemberName(invocation),
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        return calls;
    }

    /// <summary>The named member a call sits in, or a marker when there is none.</summary>
    private static string EnclosingMemberName(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
            }
        }

        return "<no enclosing member>";
    }
}
