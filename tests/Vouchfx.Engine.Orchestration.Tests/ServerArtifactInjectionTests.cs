// REQ-016 / EDGE-007 — server-side artefact delivery via container-file COPY, never a bind mount
// (authenticated-infrastructure-mtls, slice E).
//
// Non-Docker, like every other EnvironmentMapper test here: building the resource graph is pure
// in-memory work, and the assertions read the annotation the mapper actually produced. That is
// the right level for this requirement, because the acceptance turns on WHICH mechanism was used
// — a copy, whose bytes travel through the daemon API, versus a mount, which silently surfaces an
// empty directory under a remote daemon or Docker-in-Docker. An end-to-end
// `docker exec … test -f` proof against a live broker is slice F's.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// REQ-016 tests: declared <c>security.serverArtifacts</c> reach the container as copied files.
/// </summary>
public sealed class ServerArtifactInjectionTests : IDisposable
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private static readonly int[] s_brokerPorts = { 9093 };
    private static readonly string[] s_keystoreAndTruststore = { "kafka.keystore.jks", "kafka.truststore.jks" };
    private static readonly string[] s_twoDestinations = { "/etc/kafka/secrets", "/etc/ssl" };

    private readonly string _suiteDirectory;

    public ServerArtifactInjectionTests()
    {
        _suiteDirectory = Path.Combine(Path.GetTempPath(), "vouchfx-req016-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(_suiteDirectory, "certs"));

        // Binary content on purpose (EDGE-007): a Java keystore is not text, and the whole reason
        // `serverArtifacts` has no inline `contents:` alternative is that ContainerFile.Contents is
        // `string?`. Writing bytes here means a future implementation that read the file and
        // routed it through Contents would be visibly wrong rather than accidentally passing on
        // ASCII fixtures.
        File.WriteAllBytes(
            Path.Combine(_suiteDirectory, "certs", "kafka.keystore.jks"),
            new byte[] { 0xFE, 0xED, 0xFE, 0xED, 0x00, 0x00, 0x00, 0x02 });
        File.WriteAllBytes(
            Path.Combine(_suiteDirectory, "certs", "kafka.truststore.jks"),
            new byte[] { 0xFE, 0xED, 0xFE, 0xED, 0x00, 0x00, 0x00, 0x02 });
        File.WriteAllText(Path.Combine(_suiteDirectory, "certs", "server.pem"), "-----BEGIN CERTIFICATE-----");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_suiteDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    private static SecuritySpec SecurityWith(params SecurityServerArtifactSpec[] artifacts) =>
        new(
            Profile: "mtls",
            Endpoint: "9093",
            CaCert: null,
            ClientCert: "./certs/server.pem",
            ClientKey: "./certs/server.pem",
            ServerArtifacts: artifacts);

    private static EnvironmentSpec ServiceEnv(SecuritySpec security) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["broker"] = new ServiceSpec(
                    Image: "acme/kafka:7.5.3",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = s_brokerPorts,
                    Security = security,
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static EnvironmentSpec DependencyEnv(SecuritySpec security) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null) { Security = security },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static List<ContainerFileSystemCallbackAnnotation> CopiesOn(
        IDistributedApplicationBuilder builder, string resourceName) =>
        builder.Resources
            .Single(r => r.Name == resourceName)
            .Annotations.OfType<ContainerFileSystemCallbackAnnotation>()
            .ToList();

    private static async Task<List<ContainerFile>> ResolveAsync(
        ContainerFileSystemCallbackAnnotation annotation, IResource model)
    {
        var entries = await annotation.Callback(
            new ContainerFileSystemCallbackContext { Model = model, ServiceProvider = null! },
            CancellationToken.None);
        return entries.OfType<ContainerFile>().ToList();
    }

    // ── The requirement itself ────────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-016's core acceptance, at the mechanism level: a declared artefact becomes a
    /// <see cref="ContainerFile"/> carrying the resolved HOST path in
    /// <see cref="ContainerFile.SourcePath"/>, under a
    /// <see cref="ContainerFileSystemCallbackAnnotation"/> whose
    /// <see cref="ContainerFileSystemCallbackAnnotation.DestinationPath"/> is the declared
    /// target's directory — and the file's <see cref="ContainerFileSystemItem.Name"/> is the
    /// declared target's file name.
    /// </summary>
    [Fact]
    public async Task Map_ServiceServerArtifact_IsCopiedByPathToTheDeclaredContainerPath()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks"))),
            _suiteDirectory);
        mapped.Configure(builder);

        var copies = CopiesOn(builder, "broker");
        var copy = Assert.Single(copies);
        Assert.Equal("/etc/kafka/secrets", copy.DestinationPath);

        var files = await ResolveAsync(copy, builder.Resources.Single(r => r.Name == "broker"));
        var file = Assert.Single(files);
        Assert.Equal("kafka.keystore.jks", file.Name);
        Assert.Equal(
            Path.Combine(_suiteDirectory, "certs", "kafka.keystore.jks"),
            file.SourcePath);

        // EDGE-007: the bytes travel by PATH, never inlined as text. A JKS routed through
        // ContainerFile.Contents (a `string?`) would be silently corrupted.
        Assert.Null(file.Contents);
    }

    /// <summary>
    /// REQ-016's explicit prohibition: no bind mount is used for this field, on any path. Asserted
    /// on the resource's own annotations rather than by reading the mapper's source, so a future
    /// refactor that reached for <c>WithBindMount</c> fails here.
    /// </summary>
    [Fact]
    public void Map_ServerArtifacts_UseNoBindMountAnnotation()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks"))),
            _suiteDirectory);
        mapped.Configure(builder);

        var mounts = builder.Resources
            .Single(r => r.Name == "broker")
            .Annotations.OfType<ContainerMountAnnotation>()
            .ToList();

        Assert.Empty(mounts);
    }

    /// <summary>
    /// A kafka DEPENDENCY — the shape the spec's own Out-of-scope section names as in scope
    /// ("a custom image: override … whose server-side security is already the customer's own") —
    /// gets the same copy, applied to the retained dependency resource. The retained builder is
    /// type-erased to <c>IResourceBuilder&lt;IResource&gt;</c>, so this also pins that the
    /// covariant cast back to the container-typed builder still works.
    /// </summary>
    [Fact]
    public async Task Map_KafkaDependencyServerArtifact_IsCopiedIntoTheBrokerContainer()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            DependencyEnv(SecurityWith(
                new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks"))),
            _suiteDirectory);
        mapped.Configure(builder);

        var copy = Assert.Single(CopiesOn(builder, "events"));
        var files = await ResolveAsync(copy, builder.Resources.Single(r => r.Name == "events"));
        Assert.Equal("kafka.keystore.jks", Assert.Single(files).Name);
    }

    /// <summary>
    /// Two artefacts sharing one container directory produce ONE copy carrying both entries — the
    /// keystore/truststore pair, which is the common shape.
    /// </summary>
    [Fact]
    public async Task Map_TwoArtefactsInOneDirectory_ProduceOneCopyWithBothEntries()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks"),
                new SecurityServerArtifactSpec(
                    "./certs/kafka.truststore.jks", "/etc/kafka/secrets/kafka.truststore.jks"))),
            _suiteDirectory);
        mapped.Configure(builder);

        var copy = Assert.Single(CopiesOn(builder, "broker"));
        var files = await ResolveAsync(copy, builder.Resources.Single(r => r.Name == "broker"));

        Assert.Equal(
            s_keystoreAndTruststore,
            files.Select(f => f.Name).ToArray());
    }

    /// <summary>
    /// Two artefacts in DIFFERENT container directories produce one copy each, in declared order.
    /// </summary>
    [Fact]
    public void Map_ArtefactsInDifferentDirectories_ProduceOneCopyEachInDeclaredOrder()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks"),
                new SecurityServerArtifactSpec("./certs/server.pem", "/etc/ssl/server.pem"))),
            _suiteDirectory);
        mapped.Configure(builder);

        Assert.Equal(
            s_twoDestinations,
            CopiesOn(builder, "broker").Select(c => c.DestinationPath).ToArray());
    }

    /// <summary>
    /// A service declaring no <c>serverArtifacts</c> gets no copy annotation at all — the field is
    /// absent, not defaulted.
    /// </summary>
    [Fact]
    public void Map_NoServerArtifacts_AddsNoCopyAnnotation()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(new SecuritySpec("mtls", "9093", null, "./certs/server.pem", "./certs/server.pem", null)),
            _suiteDirectory);
        mapped.Configure(builder);

        Assert.Empty(CopiesOn(builder, "broker"));
    }

    // ── Eager rejection: REQ-003 containment, REQ-004 existence, target shape ──────────────

    /// <summary>
    /// REQ-003/EDGE-006: a <c>source</c> escaping the suite directory is rejected EAGERLY, by
    /// <c>Map()</c> itself, before any builder mutation — never later, inside Aspire's own file
    /// callback with other resources already added.
    /// </summary>
    [Fact]
    public void Map_ArtefactSourceEscapingTheSuiteDirectory_ThrowsFromMapItself()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec("../outside.jks", "/etc/kafka/secrets/x.jks"))),
            _suiteDirectory));

        Assert.Contains("serverArtifacts[0].source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("outside the suite directory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-004: a declared source that does not exist is named, with the resolved path, rather
    /// than surfacing later as an opaque container-start failure.
    /// </summary>
    [Fact]
    public void Map_ArtefactSourceMissing_ThrowsNamingTheDeclaredAndResolvedPaths()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec("./certs/absent.jks", "/etc/kafka/secrets/x.jks"))),
            _suiteDirectory));

        Assert.Contains("./certs/absent.jks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>target</c> that is not an absolute in-container path is rejected. The schema already
    /// pins <c>^/</c>; this is the belt-and-braces the mapper applies to every other field, and
    /// the only gate for a direct <c>EnvironmentSpec</c> embedding that bypasses the schema.
    /// </summary>
    [Theory]
    [InlineData("etc/kafka/secrets/x.jks")]
    [InlineData("")]
    public void Map_ArtefactTargetNotAbsolute_Throws(string target)
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(new SecurityServerArtifactSpec("./certs/server.pem", target))),
            _suiteDirectory));

        Assert.Contains("must be an absolute path inside the container", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>target</c> naming a directory rather than a file is rejected: there is no file name to
    /// create, and inventing one from the source would silently rename the artefact the broker's
    /// entrypoint is looking for by exact path.
    /// </summary>
    [Fact]
    public void Map_ArtefactTargetNamingADirectory_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec("./certs/server.pem", "/etc/kafka/secrets/"))),
            _suiteDirectory));

        Assert.Contains("names a directory, not a file", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>target</c> whose shape a POSIX container path cannot mean is diagnosed here, with the
    /// field named, rather than reaching the Docker daemon as an opaque failure.
    /// </summary>
    /// <remarks>
    /// No boundary is crossed by any of these — the destination is inside the author's own
    /// container and the author chose it. What they cost is legibility: measured against the
    /// pinned Aspire, <c>/etc/kafka/..</c> produces a <c>ContainerFile { Name = ".." }</c> and
    /// <c>/etc/kafka\secrets\ks.jks</c> a file literally named <c>kafka\secrets\ks.jks</c>, so the
    /// keystore the broker's entrypoint looks for by exact path is simply not where it looks.
    /// </remarks>
    [Theory]
    [InlineData("/etc/kafka/../secrets/x.jks", "'.' or '..' segment")]
    [InlineData("/etc/kafka/..", "'.' or '..' segment")]
    [InlineData("/etc/kafka/./x.jks", "'.' or '..' segment")]
    [InlineData(@"/etc/kafka\secrets\ks.jks", "contains a backslash")]
    [InlineData("/etc//kafka/x.jks", "empty path segment")]
    public void Map_ArtefactTargetWithAnUnrepresentablePosixShape_Throws(string target, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(new SecurityServerArtifactSpec("./certs/server.pem", target))),
            _suiteDirectory));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror that keeps the rule above from over-reaching: a dot INSIDE a segment is an
    /// ordinary file name, not a path segment, and must still be accepted.
    /// </summary>
    [Fact]
    public void Map_ArtefactTargetWithADotInsideAFileName_IsAccepted()
    {
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec("./certs/server.pem", "/etc/kafka/secrets/keystore..jks"))),
            _suiteDirectory);

        Assert.NotNull(mapped);
    }

    /// <summary>
    /// Two artefacts targeting the SAME in-container path are rejected rather than silently
    /// resolved by declaration order: which one the broker ends up reading is not a decision this
    /// engine makes quietly.
    /// </summary>
    [Fact]
    public void Map_TwoArtefactsWithTheSameTarget_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(
                new SecurityServerArtifactSpec("./certs/kafka.keystore.jks", "/etc/kafka/secrets/k.jks"),
                new SecurityServerArtifactSpec("./certs/kafka.truststore.jks", "/etc/kafka/secrets/k.jks"))),
            _suiteDirectory));

        Assert.Contains("declared more than once", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An artefact whose target sits at the container filesystem root still resolves to a
    /// destination directory of <c>"/"</c> — a POSIX split, never <see cref="Path"/>'s, which on
    /// Windows would hand Aspire a backslash-separated destination no Linux container can have.
    /// </summary>
    [Fact]
    public void Map_ArtefactTargetAtContainerRoot_UsesPosixRootAsDestination()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            ServiceEnv(SecurityWith(new SecurityServerArtifactSpec("./certs/server.pem", "/server.pem"))),
            _suiteDirectory);
        mapped.Configure(builder);

        Assert.Equal("/", Assert.Single(CopiesOn(builder, "broker")).DestinationPath);
    }
}
