// Regression test for the container-teardown leak defect (fix/aspire-teardown-leak).
//
// Root cause (§4.5 teardown discipline): vouchfx teardown called only
// DistributedApplication.DisposeAsync(), which in Aspire 13.4.2 does NOT call StopAsync and
// does NOT wait for DCP to delete containers (DcpPublisher:WaitForResourceCleanup defaults to
// false → DCP's stop fires a "Stopping" PATCH and returns immediately). The process then exits
// before the detached DCP apiserver finishes deletion → orphaned containers + the
// aspire-session-network-* network. The fix mirrors Aspire's own DistributedApplicationFactory:
// set WaitForResourceCleanup=true AND await a bounded app.StopAsync() before DisposeAsync.
//
// Scoping (security/safety): every container/network DCP creates is stamped with
// com.microsoft.developer.usvc-dev.creatorProcessId=<DCP apiserver PID>. That PID is the
// detached DCP apiserver this run spawned — NOT Environment.ProcessId (the test host), which DCP
// records separately as its --monitor target. The test therefore DISCOVERS the actual
// creatorProcessId from the probe container it just created (correlated by a per-run unique
// name), then scopes its assertion and its self-cleanup strictly to that discovered value. This
// makes the query and the rm operations impossible to point at any resource this run did not
// create: unrelated containers (e.g. ollapoc-*) carry no usvc-dev labels at all.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~TopologyTeardownLeak"
// Excluded from non-Docker CI:  dotnet test --filter "requires!=docker"
using System.Diagnostics;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated regression test proving <see cref="HeadlessTopology.DisposeAsync"/> leaves no
/// orphaned containers or networks behind for the current run.
/// </summary>
/// <remarks>
/// All residue assertions and the self-cleanup safety net are scoped to the DCP
/// <c>com.microsoft.developer.usvc-dev.creatorProcessId</c> value discovered at runtime from the
/// probe container, so the test can never enumerate or remove a resource it did not create.
/// </remarks>
public sealed class TopologyTeardownLeakTests
{
    /// <summary>Short name of this test assembly (carries DCP metadata via Aspire.AppHost.Sdk).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>The DCP label key stamped with the creating (DCP apiserver) process id.</summary>
    private const string CreatorProcessIdLabel = "com.microsoft.developer.usvc-dev.creatorProcessId";

    /// <summary>The DCP label key carrying the logical resource name (our base name + DCP suffix).</summary>
    private const string DcpNameLabel = "com.microsoft.developer.usvc-dev.name";

    /// <summary>Line separators for splitting docker CLI stdout (hoisted per CA1861).</summary>
    private static readonly char[] s_lineSeparators = { '\r', '\n' };

    /// <summary>Bound for a single docker CLI call so a wedged process can never hang CI.</summary>
    private const int DockerTimeoutMs = 30_000;

    /// <summary>The executable every production call in this class runs.</summary>
    private const string DockerExecutable = "docker";

    /// <summary>How much of a failing child's stderr a failure message carries.</summary>
    /// <remarks>
    /// Enough for the line that matters ("Cannot connect to the Docker daemon at ...", "No such
    /// object: ...") and bounded so a child that dumps a help screen cannot bury the exit code
    /// under it.
    /// </remarks>
    private const int StderrBudget = 2_000;

    private readonly ITestOutputHelper _output;

    public TopologyTeardownLeakTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Starts a one-container topology with a per-run-unique probe name, confirms DCP created the
    /// container, discovers the run's DCP <c>creatorProcessId</c>, disposes the topology, and
    /// asserts that no container or network carrying that creatorProcessId survives. On the
    /// unfixed code the probe container and the <c>aspire-session-network-*</c> network outlive
    /// disposal, so the bounded poll never reaches empty and the test fails, naming the leftovers.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Topology_Dispose_LeavesNoOrphanedContainersOrNetworks_ForThisRun()
    {
        // A per-run-unique base name so the DCP resource name is unmistakably ours. DCP appends
        // its own suffix (e.g. "leakprobeNNNNNNNN-cevshvfy"), so the name label STARTS WITH this.
        // Lower-case alphanumeric only — DCP/Docker resource names reject most punctuation.
        var runToken = "leakprobe" + Guid.NewGuid().ToString("N")[..8];
        _output.WriteLine($"Run token (probe base name): {runToken}");

        HeadlessTopology? topology = null;
        string? creatorPid = null;
        try
        {
            // Start a lightweight real topology.
            topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: AppHostAssemblyName,
                configureResources: b => b.AddContainer(runToken, "traefik/whoami"));

            // PROVE DCP actually created the container before we dispose — otherwise the test is
            // vacuous and would pass even on buggy code. Poll up to 60s for a DCP-labelled
            // container whose name label starts with our run token, then read its creatorProcessId.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            do
            {
                creatorPid = TryDiscoverCreatorPidForProbe(runToken);
                if (creatorPid is not null)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            while (DateTime.UtcNow < deadline);

            if (creatorPid is null)
            {
                Assert.Fail(
                    $"PRECONDITION/ENVIRONMENT: DCP did not create a container named '{runToken}*' " +
                    $"carrying a '{CreatorProcessIdLabel}' label within 60s. The test cannot prove " +
                    "teardown behaviour because nothing was created. Check that Docker is creating " +
                    "containers and that the DCP label keys are correct for this Aspire version.");
            }

            var labelSelector = $"label={CreatorProcessIdLabel}={creatorPid}";
            _output.WriteLine($"Discovered DCP creatorProcessId: {creatorPid}");
            _output.WriteLine($"Label selector: {labelSelector}");

            var beforeContainers = RunDocker("ps", "-a", "--filter", labelSelector, "--format", "{{.Names}}");
            var beforeNetworks = RunDocker("network", "ls", "--filter", labelSelector, "--format", "{{.Name}}");
            _output.WriteLine(
                $"Before dispose — containers: [{string.Join(", ", beforeContainers)}], " +
                $"networks: [{string.Join(", ", beforeNetworks)}]");

            // Dispose the topology explicitly and await it. This is the code under test.
            await topology.DisposeAsync();
            topology = null;

            // Assert NO residue for THIS run. Poll both up to ~20s; on the fixed code the bounded
            // StopAsync + WaitForResourceCleanup=true means deletion has already completed (or
            // completes within the post-dispose settle), so both reach empty. On the buggy code
            // they never empty → assertion fails naming the leftovers.
            var remainingContainers = beforeContainers;
            var remainingNetworks = beforeNetworks;

            var settleBudget = TimeSpan.FromSeconds(20);
            var settleInterval = TimeSpan.FromMilliseconds(500);
            var settleDeadline = DateTime.UtcNow + settleBudget;
            do
            {
                remainingContainers = RunDocker("ps", "-a", "--filter", labelSelector, "--format", "{{.Names}}");
                remainingNetworks = RunDocker("network", "ls", "--filter", labelSelector, "--format", "{{.Name}}");
                if (remainingContainers.Count == 0 && remainingNetworks.Count == 0)
                {
                    break;
                }

                await Task.Delay(settleInterval);
            }
            while (DateTime.UtcNow < settleDeadline);

            _output.WriteLine(
                $"After dispose (+settle) — containers: [{string.Join(", ", remainingContainers)}], " +
                $"networks: [{string.Join(", ", remainingNetworks)}]");

            Assert.True(
                remainingContainers.Count == 0,
                "Leaked container(s) carrying this run's creatorProcessId survived DisposeAsync: " +
                $"[{string.Join(", ", remainingContainers)}]");
            Assert.True(
                remainingNetworks.Count == 0,
                "Leaked network(s) carrying this run's creatorProcessId survived DisposeAsync: " +
                $"[{string.Join(", ", remainingNetworks)}]");
        }
        finally
        {
            // Best-effort dispose if an assertion threw before we disposed.
            if (topology is not null)
            {
                try
                {
                    await topology.DisposeAsync();
                }
                catch
                {
                    // Swallow — the self-cleanup below is the authoritative safety net.
                }
            }

            // Self-cleanup safety net, STRICTLY scoped to the discovered creatorProcessId: force-
            // remove anything still carrying THIS run's label so a red test never leaves leftovers
            // and never touches unrelated containers (ollapoc-* carry no usvc-dev labels at all).
            if (creatorPid is not null)
            {
                ForceCleanupForThisRun($"label={CreatorProcessIdLabel}={creatorPid}");
            }
        }
    }

    // ── Helpers (self-contained to this test file) ─────────────────────────────

    /// <summary>
    /// Looks for a DCP-managed container whose <see cref="DcpNameLabel"/> begins with
    /// <paramref name="runToken"/> and, if found, returns its <see cref="CreatorProcessIdLabel"/>
    /// value (the run's DCP apiserver PID). Returns <see langword="null"/> if not yet present.
    /// </summary>
    private static string? TryDiscoverCreatorPidForProbe(string runToken)
    {
        // Enumerate only DCP-labelled containers (label-key-only filter); ollapoc-* never match.
        var candidates = RunDocker(
            "ps", "-a",
            "--filter", $"label={DcpNameLabel}",
            "--format", "{{.ID}}");

        foreach (var id in candidates)
        {
            var name = RunDockerSingle("inspect", "--format", $"{{{{index .Config.Labels \"{DcpNameLabel}\"}}}}", id);
            if (name is null || !name.StartsWith(runToken, StringComparison.Ordinal))
            {
                continue;
            }

            var pid = RunDockerSingle(
                "inspect", "--format", $"{{{{index .Config.Labels \"{CreatorProcessIdLabel}\"}}}}", id);
            if (!string.IsNullOrWhiteSpace(pid))
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>
    /// Force-removes every container and network carrying the supplied label selector. Strictly
    /// scoped — it only ever names resources DCP stamped with this run's creatorProcessId.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the ONE member allowed to run docker under
    /// <see cref="CliFailurePolicy.Tolerate"/>, and the reason is the consequence of being wrong,
    /// not convenience.</strong> Everywhere else in this class a docker call that fails and reports
    /// an empty list is read as "no residue survives" — a false PASS on the very leak the class
    /// exists to catch. Here the polarity inverts: this member runs from the test's
    /// <c>finally</c>, so a throw out of it REPLACES the real verdict with a teardown failure,
    /// which is the misattribution issue #378 is about, and it would do so over failures that are
    /// not defects at all. <c>docker rm -f</c> legitimately exits non-zero when the container is
    /// already gone — a race this method is guaranteed to run into, because DCP's own teardown is
    /// removing the same resources concurrently.
    /// </para>
    /// <para>
    /// Nothing here is asserted on, which is what makes tolerating safe: the return of every call
    /// below either drives a removal or is discarded. The outer <c>catch</c> remains as the
    /// backstop for anything the policy does not cover (an <c>_output</c> write after the test
    /// completes, say).
    /// </para>
    /// </remarks>
    private void ForceCleanupForThisRun(string labelSelector)
    {
        try
        {
            var leftoverContainers = RunDockerBestEffort("ps", "-a", "--filter", labelSelector, "--format", "{{.Names}}");
            foreach (var name in leftoverContainers)
            {
                _output.WriteLine($"Self-cleanup: docker rm -f {name}");
                RunDockerBestEffort("rm", "-f", name);
            }

            var leftoverNetworks = RunDockerBestEffort("network", "ls", "--filter", labelSelector, "--format", "{{.Name}}");
            foreach (var name in leftoverNetworks)
            {
                _output.WriteLine($"Self-cleanup: docker network rm {name}");
                RunDockerBestEffort("network", "rm", name);
            }
        }
        catch
        {
            // Cleanup is best-effort; never let it mask the real test outcome.
        }
    }

    /// <summary>
    /// How a child that RAN but did not succeed is reported back to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no correct blanket answer, because the two ways of being wrong have opposite
    /// costs. Every caller of this helper asks docker WHICH containers and networks survive
    /// teardown, and an empty list is the answer meaning "none". So a failure reported as an empty
    /// list is a false PASS on the one defect this class exists to catch — a broken or slow docker
    /// daemon silently turns the leak assertion green. That is why <see cref="Fail"/> is the
    /// default and the one a caller gets by reaching for the obviously-named
    /// <c>RunDocker</c>.
    /// </para>
    /// <para>
    /// <see cref="Tolerate"/> exists for the self-cleanup safety net alone (see
    /// <see cref="ForceCleanupForThisRun"/>), where the polarity inverts: that code runs from a
    /// <c>finally</c>, so a throw REPLACES the real verdict with a teardown failure — issue #378's
    /// misattribution — over failures that are not defects (<c>docker rm -f</c> racing DCP's own
    /// teardown for a container that has already gone). It is reachable only by naming
    /// <c>RunDockerBestEffort</c>, so it cannot be selected by accident, and
    /// <c>DockerCliFailurePolicyTests</c> pins that no other member names it.
    /// </para>
    /// </remarks>
    internal enum CliFailurePolicy
    {
        /// <summary>Throw, naming the command, the exit code and stderr. The default.</summary>
        Fail = 0,

        /// <summary>Return an empty list. Legitimate only where nothing is asserted on it.</summary>
        Tolerate = 1,
    }

    /// <summary>
    /// Runs the <c>docker</c> CLI with the supplied arguments and returns the non-empty trimmed
    /// stdout lines. Any failure — a failure to start, a non-zero exit, or the bounded wait
    /// expiring — throws.
    /// </summary>
    private static List<string> RunDocker(params string[] args) =>
        RunCli(DockerExecutable, CliFailurePolicy.Fail, args);

    /// <summary>
    /// As <see cref="RunDocker(string[])"/>, but a child that fails yields an empty list instead
    /// of throwing. Legitimate ONLY in <see cref="ForceCleanupForThisRun(string)"/> — see the
    /// remarks on <see cref="CliFailurePolicy"/> for why, and why nothing else may use it.
    /// </summary>
    private static List<string> RunDockerBestEffort(params string[] args) =>
        RunCli(DockerExecutable, CliFailurePolicy.Tolerate, args);

    /// <summary>
    /// Runs <paramref name="fileName"/> with the supplied arguments via <see cref="Process"/> using
    /// an argument list (NEVER a concatenated shell command line), captures both pipes, and returns
    /// the non-empty trimmed stdout lines of a child that exited 0.
    /// </summary>
    /// <param name="fileName">
    /// The executable. Every production call passes <see cref="DockerExecutable"/>; the parameter
    /// exists so <c>DockerCliFailurePolicyTests</c> can drive the real code path with a child whose
    /// exit code and stderr it chooses, in the blocking non-Docker lane and on every platform. A
    /// test that could only run where a docker daemon does would leave this helper's whole point —
    /// that it never reports a failure as "nothing survives" — pinned by nothing.
    /// </param>
    /// <param name="policy">See <see cref="CliFailurePolicy"/>. Not defaulted: the two entry points
    /// above are how a caller chooses, and they are named for the choice.</param>
    /// <param name="args">Arguments, passed as a list rather than a command line.</param>
    /// <returns>
    /// Under <see cref="CliFailurePolicy.Tolerate"/>, an empty list for every failure. Under
    /// <see cref="CliFailurePolicy.Fail"/> an empty list means one thing only — the child exited 0
    /// and printed nothing — because every other outcome throws.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>An empty list is never a failure signal under <see cref="CliFailurePolicy.Fail"/>,
    /// and that is the whole point.</strong> The callers ask which containers and networks survive
    /// teardown; "none" is what an empty list means to them. Converting "docker could not be run",
    /// "docker exited 1" or "docker never came back" into that answer would turn a broken
    /// environment into a silently passing leak assertion — the exact vacuous green this class
    /// exists to detect. So all three propagate: a failure to START the child as whatever
    /// <see cref="Process.Start(ProcessStartInfo)"/> raises (a
    /// <see cref="System.ComponentModel.Win32Exception"/> when the executable is absent from PATH),
    /// and the other two as an <see cref="InvalidOperationException"/> naming the command, the exit
    /// code and stderr — because "docker ps exited 1: Cannot connect to the Docker daemon" is
    /// diagnosable and "assertion failed" is not.
    /// </para>
    /// <para>
    /// Issue #475 widened the kill here from the timeout path to EVERY path. The bounded
    /// <c>WaitForExit(int)</c> was already guarded; nothing else was. BEFORE it sit the two
    /// <c>ReadToEndAsync()</c> calls that open the drains; AFTER it sit the parameterless
    /// <c>WaitForExit()</c> and the two <c>GetResult()</c> calls that materialise those reads. A
    /// throw from any of them left the child running, because disposing a <see cref="Process"/>
    /// releases a handle and stops nothing.
    /// </para>
    /// <para>
    /// The SHAPE is the house one — <c>using (proc)</c> around a <c>try/finally</c> that only kills,
    /// so the compiler emits the <c>Dispose</c> in a <c>finally</c> enclosing the explicit one. The
    /// kill inside the timeout branch is the SEMANTIC one; the <c>finally</c> is the backstop.
    /// </para>
    /// </remarks>
    internal static List<string> RunCli(string fileName, CliFailurePolicy policy, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (policy == CliFailurePolicy.Tolerate
                                       && ex is System.ComponentModel.Win32Exception
                                           or InvalidOperationException
                                           or PlatformNotSupportedException
                                           or ObjectDisposedException
                                           or System.IO.FileNotFoundException)
        {
            // Tolerant path ONLY. Under Fail this propagates untouched — see the remarks. Here
            // there is nothing to clean up if docker cannot be run at all, and a throw from the
            // test's finally would cost the real verdict. The filter is the set
            // Process.Start(ProcessStartInfo) documents, read off the .NET 8 reference XML rather
            // than assumed, for the same reason ChildProcess.KillTreeQuietly's is.
            return new List<string>();
        }

        if (proc is null)
        {
            return OnCliFailure(policy, fileName, args, exitCode: null, stderr: null, "started no process");
        }

        using (proc)
        {
            try
            {
                // Drain BOTH redirected pipes concurrently, started BEFORE the wait. Reading stdout to
                // completion and only then reading stderr can deadlock: if docker fills the stderr pipe
                // buffer while the parent is still blocked draining stdout, the child blocks on the full
                // stderr pipe and the parent blocks on stdout (the deadlock the Copilot review flagged).
                // Kicking off both reads up front lets either pipe drain freely. The wait is BOUNDED with
                // kill-on-timeout: these are quick `docker ps/inspect/network/rm` calls, so exceeding the
                // budget means something is wrong — we kill the tree rather than hang CI.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(DockerTimeoutMs))
                {
                    // Kill HERE rather than leaving it to the finally, so the abandoned-read observation
                    // below runs against a child that is already going down. The finally is the
                    // backstop, not the primary; a second call on an exited process is a no-op.
                    //
                    // ONE DELIBERATE NARROWING, recorded because it is a narrowing: this line replaced a
                    // bare `catch { }` around the kill, so an exception outside the four types
                    // Process.Kill(bool) documents would now propagate rather than be swallowed.
                    // Accepted - the filter is read off the .NET 8 reference XML, and a
                    // swallow-everything copy is exactly the drift the shared helper exists to prevent.
                    ChildProcess.KillTreeQuietly(proc);

                    // Observe the abandoned reads.
                    //
                    // MEASURED: these two reads take NO cancellation token, so killing the child
                    // simply drives them to EOF - both tasks end RanToCompletion, not Faulted. There is
                    // no unobserved fault here to suppress. Kept for the same reason as its sibling in
                    // SutEnvConfigDockerTests.InitializeAsync (whose reads DO take a token, and end
                    // Canceled - also not Faulted): it costs nothing, and it is the guard that would
                    // matter if either read ever did fault.
                    _ = Task.WhenAll(outTask, errTask).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

                    // Reporting comes AFTER the kill and after that observation, deliberately: the
                    // ordering above was settled over four review rounds and is unchanged. The only
                    // thing that moved is the verdict this branch returns — killing was always
                    // right, reporting an empty list was the hazard, because a wedged docker then
                    // reads as "no residue survives".
                    return OnCliFailure(
                        policy, fileName, args, exitCode: null, stderr: null,
                        $"did not exit within {DockerTimeoutMs} ms (its process tree was killed)");
                }

                // The bounded wait returned true (process exited). The read tasks below are the real
                // synchronisation point — GetResult() blocks until each fully-drained stream is materialised;
                // the parameterless WaitForExit() additionally flushes any remaining async output handlers.
                proc.WaitForExit();
                var stdout = outTask.GetAwaiter().GetResult();
                var stderr = errTask.GetAwaiter().GetResult();

                if (proc.ExitCode != 0)
                {
                    return OnCliFailure(policy, fileName, args, proc.ExitCode, stderr, "exited non-zero");
                }

                return stdout
                    .Split(s_lineSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();
            }
            finally
            {
                ChildProcess.KillTreeQuietly(proc);
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="policy"/> to a child that ran and did not succeed: an empty list
    /// under <see cref="CliFailurePolicy.Tolerate"/>, otherwise a throw carrying enough to
    /// diagnose it.
    /// </summary>
    /// <returns>An empty list, under <see cref="CliFailurePolicy.Tolerate"/> only.</returns>
    /// <exception cref="InvalidOperationException">Under <see cref="CliFailurePolicy.Fail"/>.</exception>
    private static List<string> OnCliFailure(
        CliFailurePolicy policy, string fileName, string[] args, int? exitCode, string? stderr, string what)
    {
        if (policy == CliFailurePolicy.Tolerate)
        {
            return new List<string>();
        }

        throw new InvalidOperationException(DescribeCliFailure(fileName, args, exitCode, stderr, what));
    }

    /// <summary>
    /// The failure text: the command as it was run, what went wrong, the exit code, and stderr.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="OnCliFailure"/> so it can be asserted on directly. A leak test that
    /// dies saying <c>docker ps exited non-zero (exit code 1): Cannot connect to the Docker daemon</c>
    /// is diagnosable; one that dies saying "assertion failed" is not, and the difference is the
    /// whole reason this path throws rather than returning empty.
    /// </remarks>
    internal static string DescribeCliFailure(
        string fileName, string[] args, int? exitCode, string? stderr, string what)
    {
        var command = string.Join(" ", new[] { fileName }.Concat(args));
        var code = exitCode is null ? "no exit code" : $"exit code {exitCode.Value}";

        var trimmed = stderr?.Trim();
        var detail = string.IsNullOrEmpty(trimmed)
            ? "(no stderr)"
            : trimmed.Length <= StderrBudget ? trimmed : trimmed[..StderrBudget] + " …(stderr truncated)";

        return $"`{command}` {what} ({code}): {detail}"
            + " — reported rather than swallowed because this helper's callers ask which containers"
            + " and networks survive teardown, and an empty list is the answer meaning 'none'."
            + " Returning empty for a failed docker call would turn a broken environment into a"
            + " silently passing leak assertion.";
    }

    /// <summary>
    /// Convenience for docker commands expected to emit a single value: returns the first
    /// non-empty trimmed stdout line, or <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>
    /// Strict, because it inherits <see cref="RunDocker(string[])"/> and its only caller is the
    /// probe-discovery poll, where loud is right for the same reason: if docker is genuinely
    /// broken, failing with the real reason beats polling for 60 s and then reporting a
    /// PRECONDITION message that blames DCP for creating no container. There is deliberately no
    /// best-effort sibling. The one case that argues for one — <c>docker inspect</c> losing a race
    /// against a concurrent removal of some OTHER run's DCP container, which the poll would
    /// previously have skipped over — now surfaces as a loud failure naming "No such object". That
    /// is a true statement of what happened, and if it ever proves noisy the fix is a second
    /// explicitly-named entry point, never a silent empty here.
    /// </remarks>
    private static string? RunDockerSingle(params string[] args)
    {
        var lines = RunDocker(args);
        return lines.Count > 0 ? lines[0] : null;
    }
}
