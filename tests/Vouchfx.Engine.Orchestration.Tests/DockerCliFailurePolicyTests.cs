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
// HOW TOLERANCE IS FENCED: VISIBILITY FIRST, CENSUS SECOND
// ────────────────────────────────────────────────────────
// `CliFailurePolicy` and the `RunCli` core that takes it are BOTH private, so the assembly-visible
// surface is four members and not one of them accepts a policy: `RunCliStrict`, `RunCliBestEffort`,
// and (private to the leak test) `RunDocker`, `RunDockerBestEffort`. Outside that class the
// compiler makes tolerance cost you the word `BestEffort`. That was not true of the first version
// of this fix, where the core and the policy were `internal` and tolerance was selectable as
// `RunCli(exe, CliFailurePolicy.Tolerate, …)` from any file — while the census below, which then
// filtered on method NAME alone, stayed green. The census now polices what visibility cannot: the
// inside of the class, where everything private is nameable. Both spellings, two rules.
//
// WHY THESE TESTS NEED NEITHER A DOCKER DAEMON NOR A DOCKER BINARY
// ───────────────────────────────────────────────────────────────
// A test that could only run where docker runs would leave the helper's whole point pinned by
// nothing in the blocking lane — and this file must NOT add a `requires=docker` row to that lane.
// `RunCliStrict` / `RunCliBestEffort` therefore take the executable as a parameter (every
// production call passes "docker"), and the rows below drive the real code path with a child whose
// exit code and stderr they choose: `cmd.exe` on Windows, `/bin/sh` elsewhere. Both are
// unconditionally present on their platform. That is a REAL child through the REAL helper, not a
// stand-in for docker — nothing here fakes a docker CLI, which would only pin the fake.
//
// These rows call `RunCliBestEffort`, which rule 2 permits because rule 2 is keyed on the enclosing
// TYPE: they are the gate exercising the policy, not a consumer reading an empty list as an answer.
// Legal by rule, not by a filename exemption.
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
/// tolerant one is reachable only through a member named for the choice — and, inside the
/// leak-assertion type, only from the self-cleanup net. Not "from exactly one member": an earlier
/// draft of this line said that, and it is false four ways over
/// (<c>ForceCleanupForThisRun</c>, the two <c>…BestEffort</c> runners, and this file's own row).
/// The authoritative statement is the two-rule <c>&lt;remarks&gt;</c> on the test below.
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

    /// <summary>The type that owns the leak assertion, and so owns rule 2's population.</summary>
    private const string LeakAssertionType = nameof(TopologyTeardownLeakTests);

    /// <summary>The policy type whose tolerant member rule 1 fences.</summary>
    private const string PolicyType = "CliFailurePolicy";

    /// <summary>The tolerant policy member.</summary>
    private const string TolerantMember = "Tolerate";

    /// <summary>The strict entry point — what a caller gets without thinking about it.</summary>
    private const string StrictEntryPoint = "RunDocker";

    /// <summary>
    /// The suffix that marks a member as tolerance-bearing — the census's one predicate for both
    /// rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A suffix rather than a list of blessed names, because there are already two
    /// (<c>RunDockerBestEffort</c>, <c>RunCliBestEffort</c>) and a list is a second thing to
    /// maintain. It also means a third added later arrives inside the fence rather than outside it.
    /// </para>
    /// <para>
    /// The predicate is NAME-ONLY — it does not check that the callee has anything to do with a CLI
    /// failure policy. No other <c>…BestEffort</c> member exists in this project today, but the
    /// spelling is house idiom one file-move away: <c>Vouchfx.Cli.Tests/PlanCommandTests</c> and
    /// <c>Vouchfx.Engine.Runtime.Tests/SecretObservationLeakPenetrationTests</c> both have a
    /// <c>DeleteBestEffort(string)</c>. If one lands in <c>TopologyTeardownLeakTests</c>, calling it
    /// outside the self-cleanup net reddens rule 2 with an offender message about docker failure
    /// policy and vacuous greens — the wrong hazard named at the worst moment. Fail-closed, so this
    /// costs a confusing red rather than a miss.
    /// </para>
    /// </remarks>
    private const string TolerantSuffix = "BestEffort";

    /// <summary>The one member of the leak-assertion type permitted to CALL a tolerant helper.</summary>
    private const string TolerantCallerMember = "ForceCleanupForThisRun";

    /// <summary>
    /// The fewest <c>.cs</c> files this project can plausibly hold. Below it, the census is assumed
    /// to have failed to find the source tree rather than to have found a small one.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact count, for the reason the sibling census in this project gives:
    /// an exact count is a second thing to maintain and would redden on every unrelated file added.
    /// </remarks>
    private const int MinimumCensusFiles = 20;

    /// <summary>Build output, which holds generated sources this census has no business reading.</summary>
    private static readonly string[] s_excludedDirectories = { "bin", "obj" };

    /// <summary>A representative assertion-query argument list (hoisted per CA1861).</summary>
    private static readonly string[] s_containerQueryArgs = { "ps", "-a" };

    /// <summary>A representative network-query argument list (hoisted per CA1861).</summary>
    private static readonly string[] s_networkQueryArgs = { "network", "ls" };

    /// <summary>A representative label-read argument list (hoisted per CA1861).</summary>
    private static readonly string[] s_inspectArgs = { "inspect", "--format", "{{.Id}}", "0123456789ab" };

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
            TopologyTeardownLeakTests.RunCliStrict(ProbeExecutable, FailingProbeArgs));

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
        var lines = TopologyTeardownLeakTests.RunCliBestEffort(ProbeExecutable, FailingProbeArgs);

        Assert.Empty(lines);
    }

    /// <summary>
    /// A child that exits 0 still yields its stdout lines — the strictness added nothing that
    /// reddens the path every assertion query actually takes.
    /// </summary>
    [Fact]
    public void StrictPolicy_ZeroExit_ReturnsTheStdoutLines()
    {
        var lines = TopologyTeardownLeakTests.RunCliStrict(ProbeExecutable, SucceedingProbeArgs);

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
    /// A vanished container is still recognisable from the failure text the helper builds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this pins, and what it cannot.</strong> Probe discovery enumerates every
    /// DCP-labelled container on the host and then inspects each one, so an unrelated run removing
    /// its container in that window makes <c>docker inspect</c> exit non-zero. Under the strict
    /// policy that would throw past <c>creatorPid</c>'s assignment and skip THIS run's own cleanup,
    /// so <c>TryReadLabel</c> catches exactly that one case by matching
    /// <see cref="TopologyTeardownLeakTests.VanishedContainerMarker"/> in the exception message.
    /// </para>
    /// <para>
    /// The race itself cannot be produced without a live daemon — stated plainly rather than
    /// approximated with a stub, because a stub would pin the stub. What IS pinnable here, and is
    /// the part that would actually rot, is the JOINT: the filter matches a message that
    /// <c>DescribeCliFailure</c> assembles, so a future change that reworded, dropped or truncated
    /// stderr out of that message would silently stop the filter matching and bring the skipped
    /// cleanup back with nothing red. This row fails the moment that coupling breaks.
    /// </para>
    /// </remarks>
    [Fact]
    public void VanishedContainerFailure_IsStillRecognisableFromTheAssembledMessage()
    {
        var message = TopologyTeardownLeakTests.DescribeCliFailure(
            "docker", s_inspectArgs, exitCode: 1, stderr: "Error: No such object: 0123456789ab", "exited non-zero");

        Assert.Contains(
            TopologyTeardownLeakTests.VanishedContainerMarker,
            message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every reference to the tolerant policy sits in a <c>…BestEffort</c> member, and no member of
    /// the leak-assertion class other than the self-cleanup net invokes one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>TWO rules, because tolerance has two spellings and the first version of this gate
    /// policed only one.</strong> That version filtered the population to invocations named
    /// <c>RunDocker</c> or ending <c>BestEffort</c>, and claimed a syntactic pin on "no other member
    /// selects tolerance" — which the file it shipped alongside falsified on the same commit, by
    /// reaching <c>CliFailurePolicy.Tolerate</c> through a policy parameter without naming any
    /// <c>BestEffort</c> member. Recorded here rather than quietly fixed because it is the same
    /// gate-versus-claim divergence the sibling kill-site census corrected one commit earlier: a
    /// census whose prose asserts more than its predicate is worse than no census, since it also
    /// buys off the reviewer.
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <strong>The token rule.</strong> Every <c>CliFailurePolicy.Tolerate</c> reference that
    ///     SELECTS the policy — an argument, an assignment, an initialiser — sits in a member whose
    ///     name ends <c>BestEffort</c>. References that merely TEST it (an equality operand, a
    ///     constant pattern) are exempt by shape; see <see cref="IsPolicyTest"/> for why that is
    ///     not a hole. This is the rule the earlier version lacked entirely.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>The invocation rule.</strong> Inside <c>TopologyTeardownLeakTests</c> — the type
    ///     that owns the leak assertion — every call to a <c>…BestEffort</c> member sits either in
    ///     <c>ForceCleanupForThisRun</c> or in a member that is itself <c>…BestEffort</c>. The
    ///     second clause is what lets <c>RunDockerBestEffort</c> delegate to
    ///     <c>RunCliBestEffort</c>, and it opens nothing: a chain of tolerance-bearing members has
    ///     to be entered from somewhere, and that entry point is by definition not
    ///     <c>…BestEffort</c>, so it is still caught. Keyed on the enclosing TYPE, not on a
    ///     filename, so this test file's own tolerant calls are legal by rule rather than by
    ///     exemption: they are the gate exercising the policy, not a consumer reading an empty list
    ///     as an answer.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>What the compiler does that this cannot.</strong> <c>CliFailurePolicy</c> and
    /// <c>RunCli</c> are <c>private</c>, so outside <c>TopologyTeardownLeakTests</c> tolerance is
    /// reachable only through <c>RunCliBestEffort</c> — an author in another file cannot select it
    /// without typing the word, whatever this census does. Rule 1 therefore polices the residue the
    /// compiler cannot: references from INSIDE that class, where everything private is nameable. It
    /// is run project-wide anyway, at no cost, so that a future widening of that visibility is
    /// caught rather than silently unfenced.
    /// </para>
    /// <para>
    /// Roslyn rather than a regex, and node kind rather than text, for the house reason (see
    /// <c>ChildProcessKillCallSiteCensusTests</c>): this file and the censused one both name every
    /// one of these members many times in prose. <c>descendIntoTrivia: false</c> drops the comments
    /// and XML docs, and switching on <see cref="InvocationExpressionSyntax"/> and
    /// <see cref="MemberAccessExpressionSyntax"/> drops the string literals, which are ordinary
    /// syntax and not trivia.
    /// </para>
    /// <para>
    /// <strong>Spellings it cannot see</strong> — named, not implied, because the earlier version's
    /// defect was an unnamed one. A policy value reached through a local alias
    /// (<c>const CliFailurePolicy P = CliFailurePolicy.Tolerate;</c> in a permitted member, used
    /// elsewhere) or through a cast from its underlying integer escapes rule 1; a tolerant call made
    /// through a delegate or a further wrapper not named <c>…BestEffort</c> escapes rule 2. None
    /// occurs today. Both are aimed at the accident and at the author taking a convenient shortcut,
    /// not at an adversary.
    /// </para>
    /// <para>
    /// Vacuity-guarded four ways, per root: the directory must exist, it must hold more than a
    /// floor of files, and each of the three populations (strict calls, tolerant calls, tolerant
    /// token references) must be non-empty. "No offender" is what a census resolved to nowhere
    /// reports.
    /// </para>
    /// </remarks>
    [Fact]
    public void ToleranceIsReachableOnlyThroughABestEffortMember_AndInTheLeakTypeOnlyFromTheCleanupNet()
    {
        var root = ProjectDirectory();

        Assert.True(
            Directory.Exists(root),
            $"This census is configured to read '{root}', which does not exist. It resolves the "
            + "project directory from the compiled assembly's location, so a change to the output "
            + "layout moves the source tree out from under it and would otherwise leave the whole "
            + "policy split ungated.");

        var files = CensusFiles(root);

        Assert.True(
            files.Count > MinimumCensusFiles,
            $"This census found only {files.Count} .cs file(s) under '{root}', which is below the "
            + $"floor of {MinimumCensusFiles}. It has almost certainly resolved the wrong directory "
            + "rather than found a small project - and a census over no files passes for free.");

        var sites = files.SelectMany(file => FindPolicySites(root, file)).ToList();

        Assert.True(
            sites.Any(site => site.Token == StrictEntryPoint),
            $"This census found no `{StrictEntryPoint}` call anywhere under '{root}'. Either the "
            + "helper was renamed - in which case this gate is watching nothing and must move with "
            + "it - or the calls are reached through a spelling the census does not recognise, "
            + "which is worse, because it reports itself green either way.");

        var tolerantCalls = sites.Where(site => site.Kind == SiteKind.TolerantCall).ToList();
        var tolerantTokens = sites.Where(site => site.Kind == SiteKind.TolerantToken).ToList();

        Assert.True(
            tolerantCalls.Count > 0,
            $"This census found no `{TolerantSuffix}` call anywhere under '{root}'. The tolerant "
            + $"path exists for exactly one production member ({TolerantCallerMember}); if nothing "
            + "calls it, either that member stopped tolerating - which turns a docker race in a "
            + "`finally` into a teardown failure that replaces the real verdict (issue #378) - or "
            + "this gate has lost sight of the call sites it is meant to fence.");

        Assert.True(
            tolerantTokens.Count > 0,
            $"This census found no `{PolicyType}.{TolerantMember}` reference anywhere under "
            + $"'{root}' that SELECTS the policy. That is the spelling the FIRST version of this "
            + "gate could not see at all, so a "
            + "census that can no longer find any is back where it started. Either the policy type "
            + "was renamed, or tolerance is now selected some other way - and either way rule 1 is "
            + "watching nothing.");

        var offendingTokens = tolerantTokens
            .Where(site => !site.Member.EndsWith(TolerantSuffix, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offendingTokens.Count == 0,
            $"`{PolicyType}.{TolerantMember}` makes a failing docker call report an EMPTY LIST, and "
            + "the leak assertion reads an empty list as 'no container or network survived "
            + "teardown'. Selecting it anywhere other than a member NAMED for the choice is how the "
            + "policy stops being visible at the call site - which is the whole mechanism keeping a "
            + "broken or slow docker from turning that assertion green. Put the selection in a "
            + $"`{TolerantSuffix}`-suffixed member and call THAT. Offending reference(s):\n"
            + string.Join("\n", offendingTokens.Select(site => site.Display)));

        var offendingCalls = tolerantCalls
            .Where(site => site.Type == LeakAssertionType
                           && site.Member != TolerantCallerMember
                           && !site.Member.EndsWith(TolerantSuffix, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offendingCalls.Count == 0,
            $"A `{TolerantSuffix}` helper reports a failing docker call as an EMPTY LIST, and every "
            + $"other member of `{LeakAssertionType}` reads an empty list as 'no container or "
            + "network survived teardown'. Used outside the self-cleanup safety net it therefore "
            + "converts a broken or slow docker into a passing leak assertion - the vacuous green "
            + $"that class exists to detect. It is legitimate only in `{TolerantCallerMember}`, "
            + "which runs from a `finally`, asserts on nothing, and races DCP's own teardown for the "
            + $"same resources - or in a member itself named `…{TolerantSuffix}`, which is where "
            + $"tolerance is declared to live. Use `{StrictEntryPoint}` instead. Offending call "
            + "site(s):\n"
            + string.Join("\n", offendingCalls.Select(site => site.Display)));
    }

    /// <summary>Which of the two rules a site belongs to.</summary>
    private enum SiteKind
    {
        /// <summary>A call to the strict entry point. Population evidence only.</summary>
        StrictCall,

        /// <summary>A call to a <c>…BestEffort</c> member — rule 2.</summary>
        TolerantCall,

        /// <summary>A <c>CliFailurePolicy.Tolerate</c> reference — rule 1.</summary>
        TolerantToken,
    }

    /// <summary>One policy-bearing site: what it is, where it is, and what encloses it.</summary>
    private sealed record PolicySite(SiteKind Kind, string Token, string Type, string Member, string File, int Line)
    {
        /// <summary>The site as an offender message names it: file, line, token, member.</summary>
        internal string Display => $"  {File}({Line}): {Token} in {Type}.{Member}";
    }

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

    /// <summary>Every <c>.cs</c> file under one root, excluding build output.</summary>
    private static List<string> CensusFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderExcludedDirectory(root, path))
            .ToList();

    private static bool IsUnderExcludedDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => s_excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every policy-bearing site in one file: calls to the two runners, and references to the
    /// tolerant policy member.
    /// </summary>
    /// <remarks>
    /// One walk, two node kinds. A tolerant CALL is an invocation whose method name ends
    /// <c>BestEffort</c>; a tolerant TOKEN is a member access <c>…CliFailurePolicy.Tolerate</c>,
    /// matched on the rightmost identifier of the receiver so both <c>CliFailurePolicy.Tolerate</c>
    /// and <c>TopologyTeardownLeakTests.CliFailurePolicy.Tolerate</c> are seen.
    /// </remarks>
    private static IEnumerable<PolicySite> FindPolicySites(string root, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        var file = Path.GetRelativePath(root, path);

        foreach (var node in tree.GetRoot().DescendantNodes(descendIntoTrivia: false))
        {
            var (kind, token) = Classify(node);
            if (kind is null)
            {
                continue;
            }

            yield return new PolicySite(
                kind.Value,
                token!,
                EnclosingTypeName(node),
                EnclosingMemberName(node),
                file,
                node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    /// <summary>What kind of policy site this node is, if any, and how it is spelled.</summary>
    private static (SiteKind? Kind, string? Token) Classify(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax invocation)
        {
            var method = invocation.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                _ => null,
            };

            if (method is null)
            {
                return (null, null);
            }

            if (method.EndsWith(TolerantSuffix, StringComparison.Ordinal))
            {
                return (SiteKind.TolerantCall, method);
            }

            return method == StrictEntryPoint ? (SiteKind.StrictCall, method) : (null, null);
        }

        // `…CliFailurePolicy.Tolerate`, however the type is qualified. Deliberately NOT keyed on the
        // invocation it sits inside: SELECTING the policy must be visible wherever it is written -
        // an argument, a local, a field initialiser - not only where it is passed to the runner.
        if (node is MemberAccessExpressionSyntax policyAccess
            && policyAccess.Name.Identifier.ValueText == TolerantMember
            && RightmostIdentifier(policyAccess.Expression) == PolicyType
            && !IsPolicyTest(policyAccess))
        {
            return (SiteKind.TolerantToken, $"{PolicyType}.{TolerantMember}");
        }

        return (null, null);
    }

    /// <summary>
    /// Whether this reference merely TESTS the policy rather than selecting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runner and its failure handler each compare their <c>policy</c> parameter against
    /// <c>Tolerate</c>. Those are reads of a value chosen elsewhere: they cannot make a caller
    /// tolerant, so fencing them would be fencing the implementation of the fence. The first draft
    /// of rule 1 did exactly that and reddened on two legitimate sites — recorded because the fix
    /// is a CARVE-OUT, and a carve-out is the shape that quietly turns a gate back off.
    /// </para>
    /// <para>
    /// It is safe because it is keyed on SHAPE, not on a member name or a file: an equality operand
    /// or a constant pattern. Neither can put the value anywhere. Everything that can — an
    /// argument, an assignment, a local's initialiser, a field's — is still an offender wherever it
    /// is written. The kind check is what makes that safe rather than lucky: a bare
    /// <c>BinaryExpressionSyntax</c> exemption would have admitted
    /// <c>maybePolicy ?? CliFailurePolicy.Tolerate</c> and <c>Fail | Tolerate</c>, both of which
    /// SELECT. An enum admits no user-defined operator, so <c>==</c>/<c>!=</c> cannot be subverted.
    /// </para>
    /// <para>
    /// Deliberately NARROWER than "all reads", and the residue is a false positive rather than a
    /// false negative: <c>policy == (CliFailurePolicy.Tolerate)</c> (parent is the parenthesis) and
    /// a <c>case CliFailurePolicy.Tolerate:</c> label in a switch STATEMENT (Roslyn emits
    /// <c>CaseSwitchLabelSyntax</c> holding the expression directly, not a constant pattern) both
    /// redden. Fail-closed, so the cost is a nuisance red, never a miss.
    /// </para>
    /// </remarks>
    private static bool IsPolicyTest(MemberAccessExpressionSyntax access) =>
        access.Parent is ConstantPatternSyntax
        || (access.Parent is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)));

    /// <summary>The rightmost identifier of a (possibly qualified) receiver expression.</summary>
    private static string? RightmostIdentifier(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The innermost enclosing type, or a marker when there is none.</summary>
    private static string EnclosingTypeName(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? "<no enclosing type>";

    /// <summary>The named member a site sits in, or a marker when there is none.</summary>
    /// <remarks>
    /// The nearest member-like ancestor wins, so a site inside a local function is reported as
    /// being in that local function rather than in the method declaring it. That is the correct
    /// label for rule 1 (a local function is a member and can be named <c>…BestEffort</c>) and it
    /// is FAIL-CLOSED for rule 2: a tolerant call inside a local function declared within
    /// <c>ForceCleanupForThisRun</c> would be reported as an offender rather than waved through.
    /// Noted because this same helper builds the offender text, so the message would name the local
    /// function, not the cleanup member a reader is expecting. No such local function exists today;
    /// the day one does, widen the rule rather than the label.
    /// </remarks>
    private static string EnclosingMemberName(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
            }
        }

        return "<no enclosing member>";
    }
}
