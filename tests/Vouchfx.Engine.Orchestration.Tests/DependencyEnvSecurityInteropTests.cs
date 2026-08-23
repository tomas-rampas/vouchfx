// dependency-env EDGE-008 — `security:` and `env:` on the SAME dependency.
//
// EDGE-008 has two halves, and they pull in opposite directions:
//
//   1. `env:` must not disturb the mTLS probe a `kafka` dependency's `security:` block drives.
//   2. The collision guard must not accidentally reserve names the security path sets on the
//      author's behalf.
//
// MEASURED, for the second half, against this tree: THE SECURITY PATH SETS NO ENVIRONMENT
// VARIABLE ON ANY RESOURCE. `WithEnvironment` occurs in exactly one file under `src/` —
// `EnvironmentMapper.cs` — and the security path's only container mutation is
// `ServerArtifactInjection.Apply`, which calls `WithContainerFiles` (a copy through the daemon
// API) and nothing else. A secured broker's `KAFKA_SSL_*` settings are AUTHOR-supplied, not
// engine-supplied: `examples/security-mtls.e2e.yaml` writes them in its own `env:` map and in
// the entrypoint script it ships as a `serverArtifacts` entry. So there is no name the security
// path could reserve on the author's behalf, and `s_engineSetEnvKeys` correctly has no `kafka`
// row — the `kafka` registration's only `WithEnvironment` calls land on the schema-registry
// SIDECAR (`<name>-sr`), which no author can name.
//
// WHICH TIER THIS FILE ASSERTS AT, AND WHICH IT DOES NOT. Everything here is the non-Docker
// in-memory tier: `Map` runs its eager passes and `Configure` builds the resource graph, and the
// `EnvironmentCallbackAnnotation` callbacks resolve with no live endpoint (see the test-strategy
// note at the top of EnvironmentMapperTests.cs). The LIVE probe arm of EDGE-008 — a secured
// `kafka` dependency with an unrelated `env:` entry completing its authenticated round trip —
// is `SecuredEndpointProbeTests.Confirm_SecuredKafkaDependencyCarryingAnUnrelatedEnvEntry_
// StillConfirms`, against a loopback TLS listener, also without Docker. What NO tier here covers
// is a real broker container started with both an author `env:` map and engine-injected server
// artefacts: that is the `requires=docker` mTLS suite, and it was NOT run for this file.
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// EDGE-008: a dependency carrying both <c>security:</c> and <c>env:</c>. The two are
/// independent — the author's map reaches the broker container, and the probe configuration the
/// declaration derives is byte-for-byte the one it derives without the map.
/// </summary>
public sealed class DependencyEnvSecurityInteropTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private const string DependencyName = "events";

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    // ── 1. `env:` does not disturb the probe ──────────────────────────────────────────────

    /// <summary>
    /// A secured <c>kafka</c> dependency that also carries an unrelated <c>env:</c> entry derives
    /// the IDENTICAL probe configuration — the same <see cref="SecuredTarget"/> and the same
    /// <see cref="SecuredTargetIdentity"/> — as the same declaration without the map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Compared against a baseline, never against a hand-copied expected value.</b> The two
    /// documents are produced by one template differing in exactly one block, so a difference in
    /// the derived configuration could only be attributable to <c>env:</c>. A hand-written
    /// expected digest would instead pin whatever <c>DigestOf</c> happens to hash today and go red
    /// the next time a field joins the <c>security</c> block — a change that has nothing to do
    /// with this edge.
    /// </para>
    /// <para>
    /// <b>Why this IS the probe's configuration and not a proxy for it.</b>
    /// <c>SecuredEndpointProbe.ConfirmAsync</c> walks <see cref="SecuredTargets.Enumerate"/> over
    /// the <see cref="EnvironmentSpec"/> and derives each confirmation's identity through
    /// <see cref="SecuredTargets.IdentityOf"/> — the same two calls asserted here. Everything else
    /// the probe takes (the staged endpoint, the resolved certificate material, the
    /// Kafka-speaking-target set) is supplied by the caller and is not derived from <c>env:</c> at
    /// all.
    /// </para>
    /// <para>
    /// <b>The vacuity guard is deliberate.</b> Two EMPTY walks are also equal, so the single
    /// target is identified — name, kind and profile — before the comparison, and the two
    /// documents are asserted to differ in the one place they are supposed to.
    /// </para>
    /// </remarks>
    [Fact]
    public void SecuredKafkaDependency_CarryingAnUnrelatedEnvEntry_DerivesTheSameProbeConfiguration()
    {
        var baseline = ParseEnvironment(SecuredKafkaYaml(string.Empty));
        var withEnv = ParseEnvironment(
            SecuredKafkaYaml(EnvBlock("VOUCHFX_DEP_PROBE", "applied")));

        var baselineTargets = SecuredTargets.Enumerate(baseline).ToArray();
        var withEnvTargets = SecuredTargets.Enumerate(withEnv).ToArray();

        // Vacuity guard: name the target the comparison is about before comparing.
        var declared = Assert.Single(baselineTargets);
        Assert.Equal(DependencyName, declared.Name);
        Assert.Equal("kafka", declared.Kind);
        Assert.Equal("mtls", declared.Security.Profile);
        Assert.Equal("9093", declared.Security.Endpoint);

        Assert.Equal(baselineTargets, withEnvTargets);
        Assert.Equal(
            baselineTargets.Select(SecuredTargets.IdentityOf).ToArray(),
            withEnvTargets.Select(SecuredTargets.IdentityOf).ToArray());

        // …and the two documents really do differ, in the one place the template varies.
        Assert.Null(baseline.Dependencies![DependencyName].Env);
        Assert.Equal("applied", withEnv.Dependencies![DependencyName].Env!["VOUCHFX_DEP_PROBE"]);
    }

    // ── 2. the author's entry reaches the broker container ────────────────────────────────

    /// <summary>
    /// The author's unrelated <c>env:</c> entry is applied to the broker container — the resource
    /// named exactly as the dependency was declared — and the no-<c>env:</c> baseline does not
    /// carry it.
    /// </summary>
    /// <remarks>
    /// The negative half is what makes the positive half mean something: without it, a mapper that
    /// set <c>VOUCHFX_DEP_PROBE</c> unconditionally would pass. Between the two, the variable's
    /// presence is attributable to the author's map and to nothing else.
    /// </remarks>
    [Fact]
    public async Task SecuredKafkaDependency_TheAuthorsUnrelatedEnvEntry_ReachesTheBrokerContainer()
    {
        var withEnvVars = await MapAndResolveAsync(
            SecuredKafkaYaml(EnvBlock("VOUCHFX_DEP_PROBE", "applied")));
        Assert.Equal("applied", EnvValueText(withEnvVars["VOUCHFX_DEP_PROBE"]));

        var baselineVars = await MapAndResolveAsync(SecuredKafkaYaml(string.Empty));
        Assert.False(
            baselineVars.ContainsKey("VOUCHFX_DEP_PROBE"),
            "The no-env: baseline carried the probe variable, so its presence in the other arm "
            + "is not attributable to the author's env: map. Resolved keys: "
            + string.Join(", ", baselineVars.Keys));
    }

    // ── 3. no refusal fires: `kafka` reserves no names ────────────────────────────────────

    /// <summary>
    /// <c>kafka</c> has no row in <c>EnvironmentMapper.s_engineSetEnvKeys</c>, so no <c>env:</c>
    /// key is refused on a secured broker — including a <c>KAFKA_SSL_*</c> name, which is the
    /// shape EDGE-008's second clause is about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>kafka</c> reserves nothing, measured rather than assumed.</b> The security path
    /// sets no environment variable on any resource — its only container mutation is
    /// <c>ServerArtifactInjection.Apply</c>'s <c>WithContainerFiles</c> — and the <c>kafka</c>
    /// registration's own <c>WithEnvironment</c> calls all land on the schema-registry sidecar
    /// <c>&lt;name&gt;-sr</c>, a name no author can write. A secured broker's <c>KAFKA_SSL_*</c>
    /// settings are AUTHOR-supplied; see <c>examples/security-mtls.e2e.yaml</c>. So a reserved
    /// <c>kafka</c> row would refuse an author's entry for no reason, which is exactly what the
    /// census test's second direction polices.
    /// </para>
    /// <para>
    /// <b>The reserved table is read by reflection, not respelled.</b> The nine names have ONE
    /// declaration and this test must not become a second one — so the loop below is driven by
    /// whatever the table actually holds. A tenth name added under any type is automatically
    /// asserted not to be refused on <c>kafka</c>.
    /// </para>
    /// <para>
    /// <b>The positive control is not decoration.</b> "Nothing was refused" is also what a
    /// completely disabled guard produces, so one row that MUST be refused is exercised through
    /// the same <c>Map</c> call. Without it this test would stay green if the refusal were deleted
    /// outright.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KafkaReservesNoEngineSetName_SoASecuredBrokersOwnEnvEntriesAreApplied()
    {
        var reserved = ReservedEnvKeys();

        Assert.False(
            reserved.ContainsKey("kafka"),
            "s_engineSetEnvKeys has grown a 'kafka' row. The engine sets no environment variable "
            + "on a kafka dependency's own container — the registration's WithEnvironment calls "
            + "land on the '<name>-sr' schema-registry sidecar, and the security path sets none "
            + "at all — so a reserved name here refuses an author's entry for no reason. That is "
            + "EDGE-008's second clause and the census test's second direction.");

        // Nothing reserved for ANY other type is refused on kafka: the guard is per type, not a
        // global denylist. Driven off the table itself so a tenth name is covered on the day it
        // is added.
        foreach (var reservedElsewhere in reserved.Values.SelectMany(names => names))
        {
            var document = SecuredKafkaYaml(EnvBlock(reservedElsewhere, "applied"));
            Assert.Null(
                Record.Exception(() => EnvironmentMapper.Map(ParseEnvironment(document))));
        }

        // Positive control: the refusal IS live on this same Map path.
        var refused = Assert.Throws<ArgumentException>(
            () => EnvironmentMapper.Map(ParseEnvironment(ReservedKeyOnItsOwnTypeYaml)));
        Assert.Contains("REFUSED", refused.Message, StringComparison.Ordinal);

        // And the name EDGE-008 actually names is not merely un-refused — it is applied.
        var vars = await MapAndResolveAsync(
            SecuredKafkaYaml(
                EnvBlock("KAFKA_SSL_KEYSTORE_LOCATION", "/etc/kafka/secrets/kafka.keystore.pem")));
        Assert.Equal(
            "/etc/kafka/secrets/kafka.keystore.pem",
            EnvValueText(vars["KAFKA_SSL_KEYSTORE_LOCATION"]));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-dependency suite whose <c>kafka</c> dependency carries a <c>security</c> block, with
    /// <paramref name="dependencyEnvBlock"/> the ONLY thing that varies between arms.
    /// </summary>
    /// <remarks>
    /// The certificate paths are declared text and never opened by this tier: <c>Map</c> resolves
    /// and existence-checks <c>serverArtifacts</c> sources only (there are none here), and
    /// <c>caCert</c>/<c>clientCert</c>/<c>clientKey</c> are <c>EnvironmentSecurityValidator</c>'s
    /// to check. <see cref="SecuredTargets.IdentityOf"/> hashes the declared text either way.
    /// </remarks>
    private static string SecuredKafkaYaml(string dependencyEnvBlock) =>
        $"""
        metadata:
          name: dep-env-security-interop
        environment:
          dependencies:
            events:
              type: kafka
              security:
                profile: mtls
                endpoint: "9093"
                caCert: ./certs/ca.pem
                clientCert: ./certs/client.pem
                clientKey: ./certs/client-key.pem
        {dependencyEnvBlock}
        steps:
          - id: noop
            type: script.csharp
            code: "// Filler step."
        """;

    /// <summary>
    /// One <c>env:</c> entry, indented for the dependency level of
    /// <see cref="SecuredKafkaYaml"/>. Written with explicit <c>\n</c> rather than as a raw string
    /// so the indentation is stated here and cannot be re-derived by the outer literal's own
    /// whitespace stripping.
    /// </summary>
    private static string EnvBlock(string key, string value) =>
        $"      env:\n        \"{key}\": \"{value}\"";

    /// <summary>
    /// The positive control for the refusal: a reserved name on the type that DOES reserve it.
    /// </summary>
    private const string ReservedKeyOnItsOwnTypeYaml = """
        metadata:
          name: dep-env-security-interop-control
        environment:
          dependencies:
            search:
              type: elasticsearch
              env:
                ES_JAVA_OPTS: "-Xmx8g"
        steps:
          - id: noop
            type: script.csharp
            code: "// Filler step."
        """;

    private static EnvironmentSpec ParseEnvironment(string yaml)
    {
        var document = YamlDocumentParser.Parse(yaml);
        Assert.NotNull(document.Environment);
        return document.Environment!;
    }

    /// <summary>
    /// Maps <paramref name="yaml"/>, builds the graph in memory, and resolves the environment
    /// variables of the broker container — the resource named exactly as the dependency was
    /// declared, never a sidecar.
    /// </summary>
    private static async Task<Dictionary<string, object>> MapAndResolveAsync(string yaml)
    {
        var mapped = EnvironmentMapper.Map(ParseEnvironment(yaml));
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var target = Assert.Single(
            builder.Resources.OfType<ContainerResource>(), r => r.Name == DependencyName);

        return await ResolveEnvVarsAsync(target);
    }

    /// <summary>
    /// Runs every registered <see cref="EnvironmentCallbackAnnotation"/> on
    /// <paramref name="resource"/> — the same in-memory technique
    /// <c>EnvironmentMapperTests.ResolveEnvVarsAsync</c> and <c>DependencyEnvCensusTests</c> use,
    /// which needs no live endpoint and therefore no Docker.
    /// </summary>
    private static async Task<Dictionary<string, object>> ResolveEnvVarsAsync(IResource resource)
    {
        var envVars = new Dictionary<string, object>();
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

    private static string EnvValueText(object envVarValue) => envVarValue switch
    {
        ReferenceExpression expression => expression.ValueExpression,
        string text => text,
        _ => envVarValue.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Reads <c>EnvironmentMapper.s_engineSetEnvKeys</c> by reflection, so this test reads the
    /// same ONE declaration the refusal and the census read. Respelling the names here would make
    /// this file a third source of truth for them.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ReservedEnvKeys()
    {
        var field = typeof(EnvironmentMapper).GetField(
            "s_engineSetEnvKeys", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        return (IReadOnlyDictionary<string, IReadOnlySet<string>>)field!.GetValue(null)!;
    }
}
