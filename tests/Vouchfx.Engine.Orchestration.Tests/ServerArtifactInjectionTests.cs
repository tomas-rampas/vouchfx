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
using YamlDotNet.RepresentationModel;

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

    /// <summary>
    /// One managed dependency of the given declared name and type, carrying
    /// <paramref name="security"/> and optionally an <c>env:</c> map and an <c>Extra</c> block.
    /// </summary>
    /// <remarks>
    /// Parameterised over the TYPE not because every type may carry <c>security</c> — REQ-021's
    /// schema clause and <c>SecurityProfileWiringValidator</c> confine it to <c>kafka</c> — but
    /// because the resource graph differs per type and the mapper must target the dependency's own
    /// container in every shape it may later be asked to handle: the four database-backed types
    /// (postgres, sqlserver, mysql, mongodb) register a server container PLUS an
    /// <c>AddDatabase</c> child, and it is the container the artefacts must reach.
    /// </remarks>
    private static EnvironmentSpec DependencyEnv(
        string name,
        string type,
        SecuritySpec security,
        IReadOnlyDictionary<string, string>? env = null,
        YamlMappingNode? extra = null) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
            {
                [name] = new DependencySpec(Type: type, Version: null, Extra: extra)
                {
                    Security = security,
                    Env = env,
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>The <c>Extra</c> block that turns on kafka's schema-registry sidecar.</summary>
    private static YamlMappingNode SchemaRegistryExtra() =>
        new() { { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") } };

    /// <summary>
    /// Resolves a resource's environment callbacks in memory — no Docker, no DCP — mirroring
    /// <c>DependencyEnvCensusTests.ResolveEnvVarsAsync</c>.
    /// </summary>
    private static async Task<Dictionary<string, object>> ResolveEnvVarsAsync(IResource resource)
    {
        var envVars = new Dictionary<string, object>(StringComparer.Ordinal);
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource,
            envVars);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(callbackContext);
        }

        return envVars;
    }

    private static string EnvValueTextOf(object envVarValue) => envVarValue switch
    {
        ReferenceExpression re => re.ValueExpression,
        string s => s,
        _ => envVarValue.ToString() ?? string.Empty,
    };

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
    /// A dependency's declared artefacts are copied into the dependency's OWN container — the
    /// resource named exactly the declared dependency name — and into nothing else (#426).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is actually reachable, measured.</b>  <c>security</c> on a dependency is confined
    /// to <c>type: kafka</c> by two independent gates: REQ-021's schema clause
    /// (<c>$defs/dependency</c>'s final <c>allOf</c> sets <c>security: false</c> for any type that
    /// is <c>not</c> kafka) and <c>SecurityProfileWiringValidator</c> (REQ-022) on the compile
    /// path.  The kafka rows are therefore the shipped, author-reachable shape, and they behaved
    /// correctly before this fix: the retained kafka builder was already container-typed.
    /// </para>
    /// <para>
    /// <b>Why the postgres row exists anyway.</b>  Not as a repair of a reachable authoring break
    /// — issue #426's own body claims one only because its probe called <c>Map</c> +
    /// <c>Configure</c> directly and so bypassed both gates.  It pins the two paths that DO reach
    /// this code with a database-backed dependency: the 1.1 widening the <c>$defs/security</c>
    /// description already commits to ("a release position rather than a permanent one:
    /// transport security for the remaining dependency kinds is a 1.1 capability"), and callers
    /// that embed an <see cref="EnvironmentSpec"/> directly, as this test does and as the shipped
    /// <c>Vouchfx.Sdk.Testing</c> surface allows.  For those four types the RETAINED builder is
    /// the <c>AddDatabase</c> child, which is not a container at all.
    /// </para>
    /// <para>
    /// The third row is the keystore shape from the original kafka-dependency <c>Fact</c> this
    /// theory absorbed: a different destination directory and file name, so folding it in loses
    /// no assertion.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("events", "kafka", "server.pem", "/etc/vouchfx/server.pem", "/etc/vouchfx", "server.pem")]
    [InlineData(
        "events", "kafka", "kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks",
        "/etc/kafka/secrets", "kafka.keystore.jks")]
    [InlineData("orders", "postgres", "server.pem", "/etc/vouchfx/server.pem", "/etc/vouchfx", "server.pem")]
    public async Task Map_DependencyServerArtifact_IsCopiedIntoTheContainerNamedForTheDependency(
        string name,
        string type,
        string sourceFile,
        string target,
        string expectedDirectory,
        string expectedFileName)
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            DependencyEnv(name, type, SecurityWith(
                new SecurityServerArtifactSpec("./certs/" + sourceFile, target))),
            _suiteDirectory);
        mapped.Configure(builder);

        // The resource named EXACTLY the dependency name is a container. This RE-ASSERTS, for two
        // types, an invariant already gated across all thirteen by
        // DependencyEnvCensusTests.EveryDependencyType_AppliesAuthorEnvToItsOwnContainer, whose
        // Assert.Single over every ContainerResource is the stronger form; it does not pin it.
        var target_ = Assert.Single(builder.Resources, r => r.Name == name);
        Assert.IsAssignableFrom<ContainerResource>(target_);

        // What this DOES pin: the copy landed there and nowhere else. For a database-backed type
        // the AddDatabase child must carry none — asserted positively below so the negative half
        // cannot go vacuous if that child were ever dropped from the registration.
        var annotated = builder.Resources
            .Where(r => r.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Any())
            .Select(r => r.Name)
            .ToList();
        Assert.Equal(name, Assert.Single(annotated));

        if (type == "postgres")
        {
            Assert.Contains(builder.Resources, r => r.Name == name + "db");
        }

        var copy = Assert.Single(CopiesOn(builder, name));
        Assert.Equal(expectedDirectory, copy.DestinationPath);

        var files = await ResolveAsync(copy, target_);
        var file = Assert.Single(files);
        Assert.Equal(expectedFileName, file.Name);
        Assert.Equal(Path.Combine(_suiteDirectory, "certs", sourceFile), file.SourcePath);

        // EDGE-007 on the dependency path too: the bytes travel by PATH. Row 2's source is the
        // binary .jks fixture, so a future implementation routing it through the `string?`
        // Contents would be caught here and not only on the service path.
        Assert.Null(file.Contents);
    }

    /// <summary>
    /// The sidecar shape: <c>kafka</c> + <c>schemaRegistry: true</c> is the ONLY dependency shape
    /// that is both schema-legal for <c>security</c> and multi-container, so it is the only
    /// reachable shape where resolving the copy target by name has a wrong answer available.
    /// </summary>
    /// <remarks>
    /// The single-container rows above cannot fail this way — their graph has nothing else to hit.
    /// A refactor of the by-name lookup to <c>FirstOrDefault</c>, a <c>StartsWith</c> match or a
    /// positional index would copy the broker's server certificate and private key into
    /// <c>confluentinc/cp-schema-registry</c>, a separate network-exposed process, and stay green
    /// on every other test in this file. Hence the explicit ZERO-copies assertion on
    /// <c>&lt;name&gt;-sr</c> rather than only a positive one on the broker.
    /// </remarks>
    [Fact]
    public async Task Map_KafkaWithSchemaRegistry_CopiesToTheBrokerAndNeverTheSidecar()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            DependencyEnv(
                "events",
                "kafka",
                SecurityWith(new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks")),
                extra: SchemaRegistryExtra()),
            _suiteDirectory);
        mapped.Configure(builder);

        // The sidecar is genuinely present — without this the zero-copies assertion below would
        // pass vacuously if `schemaRegistry: true` ever stopped registering it.
        Assert.Contains(builder.Resources, r => r.Name == "events-sr");
        Assert.Empty(CopiesOn(builder, "events-sr"));

        var copy = Assert.Single(CopiesOn(builder, "events"));
        Assert.Equal("/etc/kafka/secrets", copy.DestinationPath);
        var files = await ResolveAsync(copy, builder.Resources.Single(r => r.Name == "events"));
        Assert.Equal("kafka.keystore.jks", Assert.Single(files).Name);
    }

    /// <summary>
    /// A dependency carrying BOTH <c>security.serverArtifacts</c> and <c>env:</c> — the only path
    /// on which the mapper's lazily-resolved container local is consumed twice in one loop
    /// iteration, which is the entire reason it exists. Both land on the SAME resource.
    /// </summary>
    /// <remarks>
    /// <c>kafka</c> is used because it is the one dependency type on which both fields are
    /// simultaneously schema-legal, so this is the shipped shape rather than a synthetic one.
    /// </remarks>
    [Fact]
    public async Task Map_DependencyWithBothArtefactsAndEnv_AppliesBothToTheSameContainer()
    {
        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(
            DependencyEnv(
                "events",
                "kafka",
                SecurityWith(new SecurityServerArtifactSpec(
                    "./certs/kafka.keystore.jks", "/etc/kafka/secrets/kafka.keystore.jks")),
                env: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["KAFKA_SSL_KEYSTORE_LOCATION"] = "/etc/kafka/secrets/kafka.keystore.jks",
                }),
            _suiteDirectory);
        mapped.Configure(builder);

        var brokerName = Assert.Single(
            builder.Resources
                .Where(r => r.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Any())
                .Select(r => r.Name)
                .ToList());
        Assert.Equal("events", brokerName);

        var broker = builder.Resources.Single(r => r.Name == "events");
        var vars = await ResolveEnvVarsAsync(broker);
        Assert.True(
            vars.ContainsKey("KAFKA_SSL_KEYSTORE_LOCATION"),
            "The author's env: variable did not land on the same resource as the artefact copy. "
            + $"Resolved keys: {string.Join(", ", vars.Keys)}");
        Assert.Equal(
            "/etc/kafka/secrets/kafka.keystore.jks",
            EnvValueTextOf(vars["KAFKA_SSL_KEYSTORE_LOCATION"]));
    }

    /// <summary>
    /// Two dependencies, each with its own artefacts: each copy lands on its own container.
    /// </summary>
    /// <remarks>
    /// Every other test in this file declares exactly ONE dependency, so nothing crosses a loop
    /// iteration and the mapper's per-iteration container local is never actually exercised as
    /// per-iteration. Its freshness is a C# guarantee today (a <c>foreach</c> body's locals are
    /// fresh per iteration); this converts that guarantee into a gate, so hoisting the local out
    /// of the loop body — which would copy the first dependency's material into every subsequent
    /// dependency's container — fails here rather than in production.
    /// </remarks>
    [Fact]
    public async Task Map_TwoDependenciesWithArtefacts_EachCopyLandsOnItsOwnContainer()
    {
        var builder = CreateBuilder();
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Security = SecurityWith(new SecurityServerArtifactSpec(
                        "./certs/kafka.keystore.jks", "/etc/kafka/secrets/first.jks")),
                },
                ["audit"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Security = SecurityWith(new SecurityServerArtifactSpec(
                        "./certs/kafka.truststore.jks", "/etc/kafka/secrets/second.jks")),
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env, _suiteDirectory);
        mapped.Configure(builder);

        var firstFiles = await ResolveAsync(
            Assert.Single(CopiesOn(builder, "events")),
            builder.Resources.Single(r => r.Name == "events"));
        Assert.Equal("first.jks", Assert.Single(firstFiles).Name);
        Assert.Equal(
            Path.Combine(_suiteDirectory, "certs", "kafka.keystore.jks"),
            Assert.Single(firstFiles).SourcePath);

        var secondFiles = await ResolveAsync(
            Assert.Single(CopiesOn(builder, "audit")),
            builder.Resources.Single(r => r.Name == "audit"));
        Assert.Equal("second.jks", Assert.Single(secondFiles).Name);
        Assert.Equal(
            Path.Combine(_suiteDirectory, "certs", "kafka.truststore.jks"),
            Assert.Single(secondFiles).SourcePath);
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
