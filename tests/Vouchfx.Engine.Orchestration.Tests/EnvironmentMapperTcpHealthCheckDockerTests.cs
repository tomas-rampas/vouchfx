// Docker-gated regression coverage for B1 (fix round 2, PR #349 follow-up) — the raw
// `tcp` health check that could never fail.
//
// WHY A SEPARATE DOCKER FILE, NOT A NON-DOCKER UNIT TEST
// ---------------------------------------------------------
// EnvironmentMapperTests.cs's own header explains why the mapper's builder phase is testable
// without Docker: adding resources to an IDistributedApplicationBuilder is pure in-memory graph
// construction, and only DistributedApplication.StartAsync triggers DCP, image pulls, and real
// containers. B1 is exactly the exception that proves that rule: the defect lives entirely in
// what happens AFTER StartAsync — DCP's host-published TCP proxy accepts a connection
// unconditionally before it has even tried the real backend, so `Map_ServiceHealthCheckTcp_
// RegistersResolvableHealthCheck_NeverPerformsHttpRequest` (EnvironmentMapperTests.cs), which
// never starts a container, structurally cannot observe it — the pre-fix bug and the post-fix
// behaviour are IDENTICAL from that test's vantage point (both report Unhealthy pre-StartAsync,
// "endpoint not yet allocated"). A live probe asserting only "reaches healthy" would ALSO pass
// either way (the whole point of B1 is that it used to reach healthy for a dead backend too).
// The only test that actually catches this is the one below: a tcp health check on a port with
// no listener must NOT reach healthy, proven against a REAL DCP-proxied endpoint.
//
// Run with: dotnet test --filter "requires=docker". Excluded from the unit-CI job via
// `dotnet test --filter "requires!=docker"`.
//
// MEASURED (live, on this exact tree, Docker Desktop for Windows):
//   - Pre-fix (bare ConnectAsync, no read discriminator): a `traefik/whoami` service
//     declaring `ports: [9093]` (whoami does not listen on 9093 — nothing is listening
//     inside the container at all on that port) with `healthCheck: { type: tcp, port: 9093 }`
//     reached Healthy — the exact false-positive B1 describes.
//   - Post-fix: the SAME topology never reaches Healthy within the bounded wait below —
//     `Map_ServiceTcpHealthCheck_DeadPort_NeverReachesHealthy` is RED against the pre-fix
//     source and GREEN against the fixed source (verified by temporarily reverting
//     EnvironmentMapper.ApplyTcpHealthCheck to the bare-connect form and re-running this file).
//   - Positive control: a live, silent (does-not-speak-first) TCP listener — whoami's own
//     port 80 — still reaches Healthy within the same bound, proving the bounded
//     zero-byte-read discriminator does not turn a real backend into a false negative.
//
// LINUX CI: UNVERIFIED. This was only run on Docker Desktop for Windows. The same "accept the
// connection unconditionally, only then dial the backend, close on failure" DCP proxy design is
// documented for the Linux DCP binary too, but the exact close-timing this discriminator relies
// on (immediate zero-byte read on a dead backend) has not been independently re-confirmed there.

using System;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// B1 (fix round 2) — negative-control regression test: a <c>tcp</c> health check on a
/// declared port with no listener behind it must NOT reach <c>Healthy</c>, proven against a
/// real, DCP-proxied endpoint (see the file-header remarks for why this cannot be a non-Docker
/// unit test).
/// </summary>
public sealed class EnvironmentMapperTcpHealthCheckDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// The bounded wait every test in this file uses for
    /// <c>WaitForResourceHealthyAsync</c>. Generous enough for a `traefik/whoami` image pull +
    /// container start + at least one health-check poll cycle on a cold CI cache, while still
    /// keeping this Docker-gated file's total runtime bounded.
    /// </summary>
    private static readonly TimeSpan s_boundedWait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// B1 negative control. <c>whoami</c> declares <c>ports: [9093]</c> and
    /// <c>healthCheck: { type: tcp, port: 9093 }</c> — but the <c>traefik/whoami</c> image only
    /// ever listens on port 80; nothing is listening on 9093 inside the container at all. Before
    /// the fix, DCP's proxy accepting the connection unconditionally made this reach Healthy
    /// anyway (measured ~18s in the original report against this exact shape). After the fix,
    /// the bounded wait below must NOT complete successfully.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Map_ServiceTcpHealthCheck_DeadPort_NeverReachesHealthy()
    {
        var env = new EnvironmentSpec(
            Services: new System.Collections.Generic.Dictionary<string, ServiceSpec>
            {
                ["dead-whoami"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new System.Collections.Generic.List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "tcp", Path: null, Port: 9093),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);

        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: mapped.Configure);

        using var boundedCts = new CancellationTokenSource(s_boundedWait);

        // The fixed check must never report Healthy for a dead backend: either the wait times
        // out (cancelled) because the resource never leaves its pre-healthy state, or the
        // resource is observed to move to an explicitly unavailable state — both are acceptable
        // "did not reach healthy" outcomes; only a clean, non-throwing return would mean B1
        // regressed.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
                "dead-whoami", WaitBehavior.StopOnResourceUnavailable, boundedCts.Token));
    }

    /// <summary>
    /// Positive control (the reviewer's "good=CONNECT_OK READ_TIMEOUT(alive-ish)" case): a
    /// REAL, silent listener (whoami's own port 80 — an HTTP server that does not speak first
    /// over a raw TCP connection) must still reach Healthy within the same bound. This proves
    /// the bounded zero-byte-read discriminator does not turn a live backend into a false
    /// negative merely because it stays quiet until spoken to.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Map_ServiceTcpHealthCheck_LiveSilentListener_ReachesHealthy()
    {
        var env = new EnvironmentSpec(
            Services: new System.Collections.Generic.Dictionary<string, ServiceSpec>
            {
                ["live-whoami"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new System.Collections.Generic.List<int> { 80 },
                    HealthCheck = new HealthCheckSpec(Type: "tcp", Path: null, Port: 80),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);

        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: mapped.Configure);

        using var boundedCts = new CancellationTokenSource(s_boundedWait);

        var exception = await Record.ExceptionAsync(() =>
            topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
                "live-whoami", WaitBehavior.StopOnResourceUnavailable, boundedCts.Token));

        Assert.Null(exception);
    }
}
