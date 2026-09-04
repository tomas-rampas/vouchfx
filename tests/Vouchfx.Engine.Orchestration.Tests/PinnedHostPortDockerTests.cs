// REQ-025 / EDGE-012 — a service's pinned host port, measured against running containers.
//
// The parser's half of this requirement is unit-tested (Vouchfx.Engine.Authoring.Tests.
// ServicePinnedHostPortTests). This file measures the half no parse can establish: that the
// pinned port is the one a client on the ENGINE HOST actually reaches, and that pinning a port
// somebody else holds fails legibly rather than hanging or blaming the image.
//
// WHY THE MEASUREMENT IS A CONNECT AND NOT A DECLARATION. `WithEndpoint`'s `port` parameter is
// documented in the pinned Aspire 13.4.2 only as "An optional port. This is the port that will be
// given to other resource to communicate with this resource" — which does not say host port, and
// this repository's invariants exist because an assumed Aspire API cost a spike. Reading the
// staged endpoint back would confirm what the ENGINE believes; opening a socket to the pinned
// port confirms what is TRUE. Both are asserted, and the container's own published-port table is
// captured beside them, so a future divergence between the three is legible rather than silent.
//
// traefik/whoami rather than the secured broker: this file is about port publishing and nothing
// else, and whoami answers on its port within a second or two where a broker takes ~15. The
// broker is where REQ-025's payoff is measured (the produce/consume drills), because that is the
// only place the advertised address matters.
//
// Run with: dotnet test --filter "requires=docker".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// REQ-025's publishing behaviour and EDGE-012's collision behaviour, against live containers.
/// </summary>
public sealed class PinnedHostPortDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private const string ServiceName = "pinned-port-service";

    /// <summary>The image's own listening port — what a pinned entry names on the right of the colon.</summary>
    private const int ContainerPort = 80;

    private readonly ITestOutputHelper _output;

    public PinnedHostPortDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// REQ-025 acceptance (a): a pinned entry publishes the container port on the host port the
    /// author named, and a client on the engine host reaches it there.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task PinnedEntry_PublishesTheContainerPortOnTheDeclaredHostPort()
    {
        // Probed for freedom before use: a pinned port is by definition one the orchestrator may
        // not move, so a row that pins a busy port measures EDGE-012 while claiming to measure
        // this. NOTE what does NOT protect it — this row starts its topology through
        // HeadlessTopology, which does not run the engine's own pinned-port pre-flight (that check
        // has exactly one caller, on the SuiteTopology path used by the EDGE-012 row below). So the
        // picker is the only guard here, and it is a hint rather than a reservation.
        var hostPort = ReserveAFreePort();
        _output.WriteLine($"pinning host port {hostPort} -> container {ContainerPort}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var topology = await StartAsync(
            new[] { $"{hostPort}:{ContainerPort}" }, cts.Token);

        var staged = await topology.ResolveStagedEndpointAsync(cts.Token);
        _output.WriteLine($"staged endpoint: {staged}");

        var published = await DockerAsync($"port {topology.ContainerName}", cts.Token);
        _output.WriteLine($"docker port:\n{published}");

        // 1. What the ENGINE believes: the endpoint it staged for consumers names the pinned port.
        Assert.Equal(hostPort, PortOf(staged));

        // 2. What is TRUE: something answers on that port, on this host. This is the assertion the
        //    requirement exists for — a broker's advertised address is only worth pinning if a
        //    client can reach what it names.
        Assert.True(
            await CanConnectAsync(hostPort, cts.Token),
            $"nothing accepted a TCP connection on the pinned host port {hostPort}, so the pin "
            + "published nothing a client could use. Staged endpoint was: " + staged);

        // 3. And the container's own published-port table, captured rather than asserted on.
        //    MEASURED, and it is why this is not an assertion: endpoints are proxied by DCP by
        //    default, so the process listening on the pinned host port is the ORCHESTRATOR'S
        //    proxy rather than the container runtime, and the container's own table then shows a
        //    different host port entirely. Either arrangement satisfies the requirement — the
        //    pinned port is reachable — so pinning the table's shape here
        //    would pin an orchestrator implementation detail. The capture is in the output for a
        //    reader who needs to know which arrangement a given Aspire version chose — and it is
        //    NOT asserted on, including its own presence: `DockerAsync` swallows a failure to an
        //    empty string, so asserting the container name were non-empty would let a `docker ps`
        //    hiccup fail this row with a message about port publishing.
    }

    /// <summary>
    /// REQ-025 acceptance (b): a bare entry still gets an orchestrator-allocated host port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This row's ASSERTION lives elsewhere, and saying so is the point.</strong> Its
    /// first form asserted only that the staged port was in 1..65535 and reachable — both of which
    /// a PINNED port satisfies too, so nothing here would have failed had the mapper regressed to
    /// publishing host == container. The requirement's actual claim, "no host port was requested",
    /// is a fact about the application model, and
    /// <c>EnvironmentMapperTests.Map_ServiceWithExplicitPorts_UsesGenericTcpEndpoint</c> asserts it
    /// deterministically and without a container (<c>EndpointAnnotation.Port</c> is null there,
    /// and 19093 in its pinned sibling).
    /// </para>
    /// <para>
    /// What is left for a live container is the half the model cannot show: that the allocated
    /// port is really published and really reachable. That is what this row measures, and it is
    /// deliberately NOT dressed up as a proof that the port was allocated rather than pinned.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task BareEntry_PublishesAReachableOrchestratorAllocatedHostPort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var topology = await StartAsync(
            new[] { ContainerPort.ToString(CultureInfo.InvariantCulture) }, cts.Token);

        var staged = await topology.ResolveStagedEndpointAsync(cts.Token);
        _output.WriteLine($"staged endpoint (unpinned): {staged}");

        var allocated = PortOf(staged);
        Assert.InRange(allocated, 1, 65535);
        Assert.True(
            await CanConnectAsync(allocated, cts.Token),
            $"the orchestrator-allocated host port {allocated} accepted no connection.");
    }

    /// <summary>
    /// EDGE-012: pinning a host port another process already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this test asserts was decided by measuring the pre-existing behaviour first,
    /// not by writing code to a hoped-for message.</strong> The requirement is that the failure be
    /// legible — naming the port and the declaring service, classified as an environment error,
    /// with no step executed — and NOT a hang or a diagnostic blaming the container image.
    /// </para>
    /// <para>
    /// The listener is held for the whole run and released in a finally, so a failure here cannot
    /// leave a bound port behind to poison the next row.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task PinningAHostPortAlreadyInUse_FailsLegiblyRatherThanHanging()
    {
        var hostPort = ReserveAFreePort();

        // Hold it for real, from this process, for the duration of the run — on the ANY address.
        //
        // MEASURED, and the first draft of this row got it wrong in a way worth recording: holding
        // only 127.0.0.1 does NOT collide with publishing a container port. A listener on
        // 127.0.0.1:P and one on 0.0.0.0:P are different bindings, and with a loopback-only blocker
        // the topology started cleanly and this row failed asserting that it had not. Binding the
        // any-address is what a conflicting service on this machine actually does, and it is the
        // binding the container runtime's publish contends with.
        var blocker = new TcpListener(IPAddress.Any, hostPort);
        blocker.Start();
        _output.WriteLine($"test process is holding host port {hostPort} on 0.0.0.0");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            // Driven through SuiteTopology, not HeadlessTopology, because that is the layer that
            // owns environment probing — the same layer the secured-endpoint probe runs in — and
            // therefore the layer where a pinned port that cannot be bound is discovered.
            var sw = Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(async () =>
            {
                var document = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse(
                    SuiteYaml($"{hostPort}:{ContainerPort}"));

                await using var suite = await SuiteTopology.StartAsync(
                    document.Environment,
                    AppHostAssemblyName,
                    startupTimeout: TimeSpan.FromSeconds(60),
                    cancellationToken: cts.Token);
            });
            sw.Stop();

            _output.WriteLine($"elapsed: {sw.Elapsed}");
            _output.WriteLine($"exception: {failure?.GetType().FullName}: {failure?.Message}");

            // The TYPE and the KIND, not merely the text: the requirement is that this is an
            // ENVIRONMENT error, and a refactor throwing an InvalidOperationException carrying the
            // same sentence would keep a text-only assertion green while breaking it.
            var orchestration = Assert.IsType<OrchestrationException>(failure);
            Assert.Equal(OrchestrationErrorKind.Provision, orchestration.Info.Kind);
            Assert.Equal(ServiceName, orchestration.Info.ResourceName);

            // Bounded rather than instant: the requirement is that it does not HANG, and the
            // per-row token above is what would have expired had it done so.
            Assert.False(
                cts.IsCancellationRequested,
                "the run consumed its whole budget rather than failing — EDGE-012's 'never a "
                + "timeout' is exactly what that would violate.");

            var message = Flatten(failure!);
            _output.WriteLine($"flattened message: {message}");

            Assert.Contains(
                hostPort.ToString(CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
            Assert.Contains(ServiceName, message, StringComparison.Ordinal);
        }
        finally
        {
            blocker.Stop();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixture
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The one suite shape every row here uses, with its ports list spliced in.</summary>
    private static string SuiteYaml(params string[] portEntries) =>
        $$"""
        environment:
          services:
            {{ServiceName}}:
              image: traefik/whoami:latest
              ports: [{{string.Join(", ", portEntries.Select(e => $"\"{e}\""))}}]
              healthCheck: { type: tcp, port: {{ContainerPort}} }
        steps:
          - id: noop
            type: script.csharp
            code: "return;"
        """;

    private static async Task<StartedTopology> StartAsync(
        IReadOnlyList<string> portEntries, CancellationToken cancellationToken)
    {
        var document = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse(
            SuiteYaml(portEntries.ToArray()));
        var mapped = EnvironmentMapper.Map(document.Environment, Directory.GetCurrentDirectory());

        var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: mapped.Configure,
            cancellationToken: cancellationToken);

        return new StartedTopology(topology, mapped);
    }

    private sealed class StartedTopology : IAsyncDisposable
    {
        private readonly HeadlessTopology _topology;
        private readonly MappedTopology _mapped;

        internal StartedTopology(HeadlessTopology topology, MappedTopology mapped)
        {
            _topology = topology;
            _mapped = mapped;
        }

        internal string ContainerName { get; private set; } = string.Empty;

        internal async Task<string> ResolveStagedEndpointAsync(CancellationToken cancellationToken)
        {
            await _topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
                ServiceName, WaitBehavior.StopOnResourceUnavailable, cancellationToken);

            ContainerName = (await DockerAsync(
                    $"ps --filter \"name=^{ServiceName}-\" --format \"{{{{.Names}}}}\"",
                    cancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            var resolved = await _mapped.ResolveServices(_topology.Application, cancellationToken);
            return resolved[ServiceName].ToString() ?? string.Empty;
        }

        public ValueTask DisposeAsync() => _topology.DisposeAsync();
    }

    /// <summary>
    /// Finds a host port nothing is currently listening on, by binding port 0 and releasing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inherently a hint rather than a reservation — the port is free when released and could in
    /// principle be taken before the topology binds it. It is used because the alternative, a
    /// hard-coded port, is worse: a hard-coded port that some other process on a developer's
    /// machine happens to hold turns a REQ-025 row into a confusing EDGE-012 failure.
    /// </para>
    /// <para>
    /// <strong>Bound on the ANY address, not loopback.</strong> Measured: the two are independent
    /// in both directions — a port free on <c>127.0.0.1</c> can be held on <c>0.0.0.0</c> and vice
    /// versa — so a loopback-only picker can hand back a port the engine's own pre-flight then
    /// refuses, producing an EDGE-012 collision error inside a row that exists to measure
    /// REQ-025. That is precisely the confusion this helper's own docstring says it avoids.
    /// </para>
    /// </remarks>
    private static int ReserveAFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        // Retried briefly: the health gate proves the CONTAINER is listening, and on a proxied
        // endpoint the host-side listener is a separate thing that can settle a moment later.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        return false;
    }

    private static int PortOf(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        Assert.True(colon > 0, $"staged endpoint '{endpoint}' carries no port");

        var digits = new string(endpoint[(colon + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    /// <summary>Flattens an exception chain into one searchable string.</summary>
    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" <- ", parts);
    }

    /// <remarks>
    /// <para>
    /// The kill is the issue #475 fix. Disposing a <see cref="Process"/> releases a handle and stops
    /// nothing, so the cancellation path below used to swallow the cancellation and RETURN while the
    /// child ran on. This class's two callers pass <c>port</c> and <c>ps</c>, both of which finish in
    /// milliseconds, but the guard is unconditional rather than argument-dependent: the next caller
    /// to pass a long-running argument list would otherwise re-open the hole silently.
    /// </para>
    /// <para>
    /// The SHAPE is the house one — the start in its own <c>try</c>, then <c>using (process)</c>
    /// around a <c>try/finally</c> that only kills. See
    /// <see cref="Vouchfx.TestSupport.ChildProcess.KillTreeQuietly(Process)"/> for why the kill is
    /// never written next to a <c>Dispose()</c>.
    /// </para>
    /// </remarks>
    private static async Task<string> DockerAsync(string arguments, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            // `?? throw` rather than `!`: a null return is a start that produced no process, and
            // dereferencing it would raise a NullReferenceException that the filter below does not
            // admit - turning "docker could not be started" into an opaque crash. Routed instead
            // into the same empty-string result as every other start failure, which is what this
            // helper's callers are documented to expect. The message is therefore UNREACHABLE here
            // - the catch below converts it - and is kept only so all four sites spell the start
            // the same way. The sibling sites' equivalents DO propagate, so theirs are live.
            process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException(
                $"Starting `docker {arguments}` returned no process object.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            return string.Empty;
        }

        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(stdout, stderr);
                await process.WaitForExitAsync(cancellationToken);

                return process.ExitCode == 0 ? await stdout : string.Empty;
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                           or System.ComponentModel.Win32Exception
                                           or InvalidOperationException)
            {
                return string.Empty;
            }
            finally
            {
                ChildProcess.KillTreeQuietly(process);
            }
        }
    }
}
