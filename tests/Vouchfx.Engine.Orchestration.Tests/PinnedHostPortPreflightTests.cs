// REQ-025 / EDGE-012 — the pinned-host-port pre-flight, in the lane that blocks a merge.
//
// WHY THIS FILE EXISTS, in the words of the review that demanded it: a green suite is not evidence
// the new safety code works; it is evidence the suite cannot tell. Before these rows, the entire
// loopback remediation could have been reverted and the unit table would not have moved by one —
// no test under tests/ referenced IPv6Any, IPv6Loopback or OSSupportsIPv6, and the only row that
// reached the pre-flight at all was docker-gated, in a job this repository marks non-blocking.
//
// Worse than untested: the one row that DID reach it stayed green for the wrong reason. Under the
// Linux defect described below, the pre-flight threw at its own loopback probe rather than at the
// injected squatter — and every assertion in that row still held, because it asserted only that
// something threw with the port in the message.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE PROPERTY EACH ROW IS DESIGNED FOR: it must go RED if the address set regresses in EITHER
// direction. That is not decoration; both directions have already happened in one round.
//
//   • TOO NARROW (wildcard only) → a squatter bound to a specific address is missed, the container
//     publishes anyway, and a client reaching `localhost:P` transacts with the squatter. On a
//     service with no `security` block a step can do that and PASS. Caught by the squatter rows.
//   • TOO WIDE (all four addresses everywhere) → on Linux/BSD a held wildcard bind conflicts with
//     its own specific-address sibling, so the probe refuses a port nothing holds and every pinned
//     suite fails, blaming a process that does not exist. Caught by the free-port row.
//
// MEASURED on both platforms, one probe per row, before the OS-conditional set was written:
//
//   Debian 12 / .NET 8      free port: 0.0.0.0=OK 127.0.0.1=AddressAlreadyInUse   ← the defect
//   Windows 10.0.26200      free port: all four OK
//   Debian, {Any, IPv6Any}  127.0.0.1 squatter → 0.0.0.0 fails; [::1] squatter → [::] fails
//   Windows, {Any, IPv6Any} 127.0.0.1 squatter → MISSED; [::1] squatter → MISSED
//
// So the rows below pass on both platforms only for the OS-conditional set, and that is the whole
// design: they do not encode WHICH addresses are probed, they encode what the probe must conclude.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// The pre-flight's own behaviour, containerless: what it accepts, what it refuses, and how.
/// </summary>
public sealed class PinnedHostPortPreflightTests
{
    private const string ServiceName = "pinned-service";

    /// <summary>
    /// A FREE pinned port is accepted. This is the row the Linux defect fails.
    /// </summary>
    /// <remarks>
    /// It looks like a test of nothing — the assertion is that no exception is thrown — and it is
    /// the most valuable row in the file. Every other row here proves the check catches something;
    /// only this one proves it does not catch everything, which is the failure mode that would
    /// have taken every pinned suite on every Linux runner down with a message naming a process
    /// that does not exist.
    /// </remarks>
    [Fact]
    public void FreePort_IsAccepted()
    {
        var port = FindAFreePort();

        var exception = Record.Exception(
            () => SuiteTopology.EnsurePinnedHostPortsAreFree(EnvironmentWithPin(port, 9093)));

        Assert.Null(exception);
    }

    /// <summary>
    /// A squatter on <c>127.0.0.1</c> is detected — the false-pass path.
    /// </summary>
    /// <remarks>
    /// It is caught by a different probe on each platform (the specific-address bind on Windows,
    /// the wildcard bind on Linux, where the two conflict), which is exactly why the row asserts
    /// the CONCLUSION rather than the mechanism. A wildcard-only probe passes this on Windows and
    /// the row goes red there.
    /// </remarks>
    [Fact]
    public void SquatterOnIPv4Loopback_IsDetected()
    {
        AssertSquatterIsDetected(IPAddress.Loopback);
    }

    /// <summary>
    /// A squatter on <c>[::1]</c> is detected.
    /// </summary>
    /// <remarks>
    /// Not a completeness exercise: measured on this host, <c>Dns.GetHostAddresses("localhost")</c>
    /// returns <c>::1</c> FIRST, so this is the address a client actually reaches — the squatter
    /// most able to impersonate the container, and the one an IPv4-only probe set cannot see.
    /// </remarks>
    [Fact]
    public void SquatterOnIPv6Loopback_IsDetected()
    {
        if (!Socket.OSSupportsIPv6)
        {
            // Skipping rather than failing: a stack with no IPv6 cannot host the collision, and
            // the pre-flight deliberately omits the IPv6 probes there.
            return;
        }

        AssertSquatterIsDetected(IPAddress.IPv6Loopback);
    }

    /// <summary>
    /// A squatter on the any-address is detected — the case the original single-address probe
    /// already caught, kept so a rewrite cannot lose it.
    /// </summary>
    [Fact]
    public void SquatterOnTheAnyAddress_IsDetected()
    {
        AssertSquatterIsDetected(IPAddress.Any);
    }

    /// <summary>
    /// Two services pinning one host port are refused before anything starts.
    /// </summary>
    /// <remarks>
    /// The parser refuses this for any suite an author writes, which is where the author-facing
    /// diagnostic and the exit code live. This row exercises the backstop underneath it, reached
    /// by constructing the spec directly — which is not a contrivance:
    /// <c>ServiceSpec.PinnedHostPorts</c> is a public init-only property, so the SDK's testing
    /// doubles and any in-tree stand-in can populate it without passing through the parser at all.
    /// Note what a bind-only check could never do here: both pins are for the SAME port, so the
    /// probe binds it once, releases it, and finds it free again.
    /// </remarks>
    [Fact]
    public void TwoServicesPinningOneHostPort_AreRefused()
    {
        var port = FindAFreePort();

        var environment = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["alpha"] = ServiceWithPin(port, 9093),
                ["beta"] = ServiceWithPin(port, 9094),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var exception = Assert.Throws<OrchestrationException>(
            () => SuiteTopology.EnsurePinnedHostPortsAreFree(environment));

        Assert.Equal(OrchestrationErrorKind.Provision, exception.Info.Kind);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
        Assert.Contains("alpha", exception.Message, StringComparison.Ordinal);
        Assert.Contains("beta", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pin whose container port is also the service's <c>httpPort</c> is refused by the mapper's
    /// eager validation, before any builder mutation.
    /// </summary>
    /// <remarks>
    /// Both would declare an endpoint on that port, so the pin has two candidates and no rule
    /// separates them. Refusing is what stops the engine choosing one silently.
    /// </remarks>
    [Fact]
    public void PinnedContainerPortThatIsAlsoTheHttpPort_IsRefused()
    {
        var environment = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                [ServiceName] = new ServiceSpec(
                    Image: "example/image:1",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 9093,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    PinnedHostPorts = new Dictionary<int, int> { [9093] = 19093 },
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var exception = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("httpPort: 9093", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ServiceName, exception.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertSquatterIsDetected(IPAddress squatterAddress)
    {
        var port = FindAFreePort();

        var squatter = new TcpListener(squatterAddress, port);
        squatter.Start();
        try
        {
            var exception = Assert.Throws<OrchestrationException>(
                () => SuiteTopology.EnsurePinnedHostPortsAreFree(EnvironmentWithPin(port, 9093)));

            Assert.Equal(OrchestrationErrorKind.Provision, exception.Info.Kind);
            Assert.Equal(ServiceName, exception.Info.ResourceName);
            Assert.Contains(
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            squatter.Stop();
        }
    }

    private static EnvironmentSpec EnvironmentWithPin(int hostPort, int containerPort)
    {
        var services = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
        {
            [ServiceName] = ServiceWithPin(hostPort, containerPort),
        };

        return new EnvironmentSpec(
            Services: services,
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);
    }

    private static ServiceSpec ServiceWithPin(int hostPort, int containerPort) =>
        new(Image: "example/image:1", Project: null, ImagePullPolicy: null, HttpPort: null, Env: null)
        {
            Ports = new List<int> { containerPort },
            PinnedHostPorts = new Dictionary<int, int> { [containerPort] = hostPort },
        };

    /// <summary>
    /// Finds a port nothing currently holds, by binding the any-address on port 0 and releasing it.
    /// </summary>
    /// <remarks>
    /// The any-address, not loopback: the two are independent on Windows, so a loopback-derived
    /// port can be one the pre-flight then refuses — which would fail these rows for a reason that
    /// has nothing to do with what they measure.
    /// </remarks>
    private static int FindAFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
