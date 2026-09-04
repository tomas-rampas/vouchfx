// REQ-016 / EDGE-005 — live-container evidence for `security.serverArtifacts`
// (authenticated-infrastructure-mtls, slice E; peer review fix round three, MAJOR-3).
//
// WHY A DOCKER TEST, WHEN THE UNIT TESTS ALREADY PASS. ServerArtifactInjectionTests proves what
// this engine ASKS FOR — one ContainerFileSystemCallbackAnnotation per destination directory, no
// ContainerMountAnnotation — which is an assertion about the application model, not about the
// container. REQ-016's own acceptance names `docker exec <container> test -f …`, and the CHANGELOG
// states as settled that "the bytes are streamed in through the container runtime's own API rather
// than bind-mounted". Both were inferred from the annotation TYPE. This file measures the outcome.
//
// THE SPECIFIC RISK, and it is not academic: whether the destination directory's PRE-EXISTING
// IMAGE CONTENT SURVIVES. If Aspire's implementation replaced the directory rather than adding to
// it, declaring a keystore into a directory the image already populates would delete that image's
// own files — and for a broker whose entrypoint reads its configuration from there, the result is
// EDGE-005 exactly: a container that comes up healthy with no SSL listener and no error anywhere.
// That is the precise failure WithContainerFiles was chosen to avoid, so it is the precise thing
// this file has to observe.
//
// MEASURED FIRST, and it corrected the assumption this test was written from: on
// confluentinc/cp-kafka:7.6.1, `/etc/kafka/secrets` — the directory a Confluent keystore is
// conventionally declared into, and the one named in the risk — is EMPTY in the image. The
// populated directory is its PARENT, `/etc/kafka` (server.properties, producer.properties, a
// kraft/ subdirectory, and thirteen more). Declaring into an empty destination could not have
// detected shadowing at all, so this test declares into BOTH: the realistic empty destination, and
// the populated parent whose survival is the actual question.
//
// Run with: dotnet test --filter "requires=docker". Excluded from the unit-CI job via
// `dotnet test --filter "requires!=docker"`.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// REQ-016 against a live container: a declared artefact arrives, and the image's own content in
/// the same directory survives.
/// </summary>
public sealed class ServerArtifactInjectionDockerTests : IDisposable
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>The service name, and therefore the container-name prefix DCP allocates from.</summary>
    private const string BrokerResource = "artefact-broker";

    private readonly ITestOutputHelper _output;

    private readonly string _suiteDirectory = Path.Combine(
        Path.GetTempPath(), "vouchfx-artefact-" + Guid.NewGuid().ToString("n"));

    public ServerArtifactInjectionDockerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_suiteDirectory, "certs"));
        File.WriteAllText(
            Path.Combine(_suiteDirectory, "certs", "keystore.pem"), "vouchfx-injected-keystore\n");
        File.WriteAllText(
            Path.Combine(_suiteDirectory, "certs", "extra.properties"), "vouchfx-injected-sibling\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_suiteDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives one test run is noise, never a failure.
        }
    }

    /// <summary>
    /// Declares one artefact into a directory the image leaves EMPTY (<c>/etc/kafka/secrets</c>,
    /// the conventional keystore destination) and one into a directory the image POPULATES
    /// (<c>/etc/kafka</c>), then asserts through <c>docker exec</c> that both injected files exist
    /// AND that the image's own <c>server.properties</c> and <c>kraft/</c> in the populated
    /// directory are still there.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Apply_ArtefactsIntoAPopulatedDirectory_LandWithoutShadowingTheImagesOwnFiles()
    {
        var environment = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                [BrokerResource] = new ServiceSpec(
                    Image: "confluentinc/cp-kafka:7.6.1",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: KafkaBrokerEnvironment())
                {
                    Ports = new List<int> { 9092 },
                    Security = new SecuritySpec(
                        Profile: "tls",
                        Endpoint: "9092",
                        CaCert: null,
                        ClientCert: null,
                        ClientKey: null,
                        ServerArtifacts: new List<SecurityServerArtifactSpec>
                        {
                            new("certs/keystore.pem", "/etc/kafka/secrets/vouchfx.keystore.pem"),
                            new("certs/extra.properties", "/etc/kafka/vouchfx.extra.properties"),
                        }),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // EnvironmentMapper + HeadlessTopology directly, NOT SuiteTopology.StartAsync: REQ-016 is
        // a topology-construction requirement, and going through the suite entry point would also
        // run REQ-005's confirmation probe against a broker this test deliberately leaves
        // plaintext — failing the run for a reason unrelated to what is being measured.
        var mapped = EnvironmentMapper.Map(environment, _suiteDirectory);

        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: mapped.Configure);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
            BrokerResource, WaitBehavior.StopOnResourceUnavailable, cts.Token);

        var container = await ResolveContainerNameAsync(BrokerResource, cts.Token);
        _output.WriteLine($"container: {container}");

        var secrets = await DockerExecAsync(container, "ls -1 /etc/kafka/secrets", cts.Token);
        var etcKafka = await DockerExecAsync(container, "ls -1 /etc/kafka", cts.Token);
        var injected = await DockerExecAsync(
            container, "cat /etc/kafka/secrets/vouchfx.keystore.pem", cts.Token);

        _output.WriteLine("ls -1 /etc/kafka/secrets:\n" + secrets);
        _output.WriteLine("ls -1 /etc/kafka:\n" + etcKafka);
        _output.WriteLine("cat injected keystore:\n" + injected);

        // 1. The declared artefacts arrived, with their content intact.
        Assert.Contains("vouchfx.keystore.pem", secrets, StringComparison.Ordinal);
        Assert.Contains("vouchfx-injected-keystore", injected, StringComparison.Ordinal);
        Assert.Contains("vouchfx.extra.properties", etcKafka, StringComparison.Ordinal);

        // 2. THE POINT OF THIS TEST: the image's own content in the SAME directory survived.
        //    Had WithContainerFiles replaced the destination directory, these would be gone and
        //    the broker would be running on a configuration it never received.
        Assert.Contains("server.properties", etcKafka, StringComparison.Ordinal);
        Assert.Contains("kraft", etcKafka, StringComparison.Ordinal);
        Assert.Contains("producer.properties", etcKafka, StringComparison.Ordinal);

        // 3. And the mechanism is a copy, not a bind mount — asserted on the mount TABLE rather
        //    than on the annotation type the unit tests already cover.
        //
        //    MEASURED, and it corrected a first draft of this assertion that read the mount list
        //    without reading the image: the container DOES carry two mounts, and NEITHER is
        //    Aspire's. `/etc/kafka/secrets` and `/var/lib/kafka/data` are declared as VOLUME in
        //    confluentinc/cp-kafka's own Dockerfile (verified: `docker inspect
        //    confluentinc/cp-kafka:7.6.1 --format "{{json .Config.Volumes}}"` reports exactly
        //    those two), so Docker creates an anonymous volume for each at run time whatever the
        //    engine does. What matters is the TYPE: every mount here is a `volume`, none is a
        //    `bind`, so no host directory is projected into the container and the host/daemon
        //    filesystem co-location assumption REQ-016 rejects is genuinely absent.
        //
        //    Note the stronger fact this run establishes in passing: the artefact declared into
        //    `/etc/kafka/secrets` is readable INSIDE the image's own anonymous volume, so the copy
        //    happens after that volume is in place rather than being buried under it.
        var mounts = await DockerInspectMountsAsync(container, cts.Token);
        _output.WriteLine("mounts (type:destination): " + mounts);

        Assert.DoesNotContain("bind:", mounts, StringComparison.Ordinal);
        Assert.Contains("volume:/etc/kafka/secrets", mounts, StringComparison.Ordinal);

        // The populated destination is not mounted at all — which is why the image's own files and
        // the injected one coexist there.
        Assert.DoesNotContain(":/etc/kafka ", mounts + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// The KRaft single-node configuration that makes <c>cp-kafka</c> come up and listen on 9092,
    /// so the health gate this test waits on is satisfied by a real broker rather than by a
    /// container that merely started.
    /// </summary>
    private static Dictionary<string, string> KafkaBrokerEnvironment() =>
        new(StringComparer.Ordinal)
        {
            ["KAFKA_NODE_ID"] = "1",
            ["KAFKA_PROCESS_ROLES"] = "broker,controller",
            ["KAFKA_LISTENERS"] = "PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9094",
            ["KAFKA_ADVERTISED_LISTENERS"] = "PLAINTEXT://localhost:9092",
            ["KAFKA_CONTROLLER_LISTENER_NAMES"] = "CONTROLLER",
            ["KAFKA_CONTROLLER_QUORUM_VOTERS"] = "1@localhost:9094",
            ["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT",
            ["KAFKA_INTER_BROKER_LISTENER_NAME"] = "PLAINTEXT",
            ["KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR"] = "1",
            ["KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR"] = "1",
            ["KAFKA_TRANSACTION_STATE_LOG_MIN_ISR"] = "1",
            ["KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS"] = "0",
            ["CLUSTER_ID"] = "MkU3OEVBNTcwNTJENDM2Qk",
        };

    /// <summary>
    /// Finds the container DCP started for <paramref name="resourceName"/>.
    /// </summary>
    /// <remarks>
    /// MEASURED: DCP names a container <c>&lt;resource&gt;-&lt;eight lower-case letters&gt;</c>
    /// (observed: <c>plain-vyydfpwx</c>), so a name-prefix filter identifies it without this test
    /// having to reach into Aspire's application model for a container id it does not publish.
    /// </remarks>
    private static async Task<string> ResolveContainerNameAsync(
        string resourceName, CancellationToken cancellationToken)
    {
        var listed = await RunAsync(
                "docker",
                $"ps --filter \"name=^{resourceName}-\" --format \"{{{{.Names}}}}\"",
                cancellationToken)
            .ConfigureAwait(false);

        var names = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(
            names.Length == 1,
            $"Expected exactly one running container named '{resourceName}-*'; docker reported: "
            + string.Join(", ", names));

        return names[0];
    }

    private static Task<string> DockerExecAsync(
        string container, string command, CancellationToken cancellationToken) =>
        RunAsync("docker", $"exec {container} sh -c \"{command}\"", cancellationToken);

    /// <summary>
    /// Returns the container's mount table as space-separated <c>&lt;type&gt;:&lt;destination&gt;</c>
    /// pairs — the TYPE is what separates "the image declared a VOLUME here" from "something
    /// bind-mounted a host directory here", and only the second would contradict REQ-016.
    /// </summary>
    private static Task<string> DockerInspectMountsAsync(
        string container, CancellationToken cancellationToken) =>
        RunAsync(
            "docker",
            $"inspect --format \"{{{{range .Mounts}}}}{{{{.Type}}}}:{{{{.Destination}}}} {{{{end}}}}\" "
            + container,
            cancellationToken);

    /// <summary>
    /// Runs a process to completion and returns its standard output, failing the test on a
    /// non-zero exit code so a mistyped command reads as a fault rather than as an empty result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kill is the issue #475 fix. Disposing a <see cref="Process"/> releases a handle and stops
    /// nothing, so the two cancellation paths here (a cancelled read, a cancelled wait) used to
    /// return while the child ran on. NOT the <see cref="Assert"/> below: that runs after
    /// <c>WaitForExitAsync</c> has returned, so the child has already exited by the time it can
    /// throw. The kill is unconditional rather than restricted to the two paths that need it,
    /// because "this argument list always finishes in milliseconds" is a property of today's
    /// callers, not of the helper.
    /// </para>
    /// <para>
    /// <strong>Both pipes are drained concurrently, and the reason is not stylistic.</strong>
    /// Awaiting stdout to completion and only then reading stderr deadlocks whenever the child
    /// fills the stderr buffer while the parent is still blocked on stdout — the failure written up
    /// at <c>TopologyTeardownLeakTests.RunDocker</c>. That it cannot happen for a short
    /// <c>docker ps</c> is the same "property of today's callers" this method's own kill refuses to
    /// rely on, so the drain does not rely on it either.
    /// </para>
    /// </remarks>
    private static async Task<string> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException(
            $"Starting `{fileName} {arguments}` returned no process object.");

        using (process)
        {
            try
            {
                // Both reads STARTED before either is awaited — see the remarks.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                Assert.True(
                    process.ExitCode == 0,
                    $"`{fileName} {arguments}` exited {process.ExitCode}. stderr: {stderr}");

                return stdout;
            }
            finally
            {
                ChildProcess.KillTreeQuietly(process);
            }
        }
    }
}
