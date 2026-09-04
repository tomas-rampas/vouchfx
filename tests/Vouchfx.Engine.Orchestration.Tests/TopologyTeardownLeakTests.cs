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
    private void ForceCleanupForThisRun(string labelSelector)
    {
        try
        {
            var leftoverContainers = RunDocker("ps", "-a", "--filter", labelSelector, "--format", "{{.Names}}");
            foreach (var name in leftoverContainers)
            {
                _output.WriteLine($"Self-cleanup: docker rm -f {name}");
                RunDocker("rm", "-f", name);
            }

            var leftoverNetworks = RunDocker("network", "ls", "--filter", labelSelector, "--format", "{{.Name}}");
            foreach (var name in leftoverNetworks)
            {
                _output.WriteLine($"Self-cleanup: docker network rm {name}");
                RunDocker("network", "rm", name);
            }
        }
        catch
        {
            // Cleanup is best-effort; never let it mask the real test outcome.
        }
    }

    /// <summary>
    /// Runs the <c>docker</c> CLI with the supplied arguments via <see cref="Process"/> using an
    /// argument list (NEVER a concatenated shell command line), captures stdout, and returns the
    /// non-empty trimmed lines.
    /// </summary>
    /// <returns>
    /// An empty list when the CLI ran but yielded nothing usable — a null process handle, the
    /// bounded wait expiring, or a non-zero exit. A failure to START <c>docker</c> at all (it is
    /// absent from PATH, say) PROPAGATES as <see cref="System.ComponentModel.Win32Exception"/>, and
    /// deliberately so: this helper's callers ask it which containers and networks survive, and an
    /// empty list is the answer meaning "none". Converting "docker could not be run" into that
    /// answer would turn a broken environment into a silently passing leak assertion.
    /// </returns>
    /// <remarks>
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
    private static List<string> RunDocker(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var proc = Process.Start(psi);
        if (proc is null)
        {
            return new List<string>();
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
                // budget means something is wrong — we kill the tree and return empty rather than hang CI.
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
                    return new List<string>();
                }

                // The bounded wait returned true (process exited). The read tasks below are the real
                // synchronisation point — GetResult() blocks until each fully-drained stream is materialised;
                // the parameterless WaitForExit() additionally flushes any remaining async output handlers.
                proc.WaitForExit();
                var stdout = outTask.GetAwaiter().GetResult();
                _ = errTask.GetAwaiter().GetResult();

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
    /// Convenience for docker commands expected to emit a single value: returns the first
    /// non-empty trimmed stdout line, or <see langword="null"/> if there is none.
    /// </summary>
    private static string? RunDockerSingle(params string[] args)
    {
        var lines = RunDocker(args);
        return lines.Count > 0 ? lines[0] : null;
    }
}
