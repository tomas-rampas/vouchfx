// Vouchfx.Engine.Orchestration — TopologyRequest (#364, fix 1).
//
// THE ONE ARGUMENT LIST FOR SuiteTopology.StartAsync.
//
// #364's first two defects are one defect: `securityConfiguration` and then `kafkaSpeakingTargets`
// were each dropped from ONE of THREE hand-maintained argument lists for the same call. An omitted
// optional argument compiles clean and reads correct, and the two failures differ only in how loud
// they are — the first made every secured suite unrunnable under `--watch` blaming the author's
// certificates for a host defect; the second silently downgraded AuthenticatedRoundTrip to
// TransportConfirmed, which is not an error at all, merely a quieter assurance than the run was
// entitled to. StartAsync's own Step 0 guard catches the first shape and CANNOT catch the second:
// it is per-ARGUMENT, and an argument whose omission degrades rather than fails has nothing to
// guard against.
//
// So the argument list stops being something call sites maintain. Every DOCUMENT-DERIVED input
// lives on this record, the two factories below derive them together from one AST (or one runnable
// set), and StartAsync is called from exactly one place in src/. TWO censuses in TWO ASSEMBLIES
// hold that shape, and the split is forced by what each can see:
//
//   • Vouchfx.Engine.Orchestration.Tests/TopologyRequestCoverageCensusTests — REFLECTION over this
//     record and over StartAsync's signature. Set equality both ways, plus a pinned member count,
//     so a sixth parameter added to StartAsync and not here fails immediately, and a member added
//     here forces a decision about the fingerprint.
//
//   • Vouchfx.Engine.Runtime.Tests/SuiteProtocolTargetsTests
//     .EverySuiteTopologyStartCallSite_PassesBothTargetSets — a SOURCE scan over src/, asserting
//     the total number of production call sites is one and that it passes both target sets. It
//     lives there because that is where it already lived when there were three call sites; it was
//     re-pointed from 3 to 1 rather than duplicated here.
//
// WHAT IS DELIBERATELY *NOT* A MEMBER. `securityConfiguration` owns X509Certificate2 instances with
// a per-build disposal contract — each caller builds it inside its own `try` and disposes it in its
// own `finally`. It is also not derived from the document, so it must not enter the topology
// fingerprint (ScenarioRunner.ComputeTopologyFingerprint) either. It is therefore a parameter of
// StartAsync below, not a property of the request. The census excludes it by name, and that
// exclusion is the one place the decision is recorded as a decision.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// The complete, document-derived argument list for <see cref="SuiteTopology.StartAsync"/> (#364):
/// one value object, built by one of the two factories below, carrying every input the document
/// decides.
/// </summary>
/// <param name="Environment">
/// The parsed <c>environment</c> block the topology is built from, or <see langword="null"/> for an
/// empty topology.
/// </param>
/// <param name="AppHostAssemblyName">
/// The short name of the assembly carrying the DCP <c>AssemblyMetadata</c> (CLAUDE.md §"Aspire").
/// Host-derived rather than document-derived, but it is an input to the built topology and so
/// belongs to the request — and, being an input, to the fingerprint.
/// </param>
/// <param name="StartupTimeout">The per-resource health-gate budget.</param>
/// <param name="SeedBaseDirectory">
/// The SUITE directory: the root relative <c>environment.seed</c> paths resolve against AND the
/// root <c>security.serverArtifacts[].source</c> containment is measured from.
/// </param>
/// <param name="KafkaSpeakingTargets">
/// The declared targets this document's own steps address with the Kafka families. Decides both
/// REQ-005's confirmation level and (REQ-023) the STAGED FORM — a bare <c>host:port</c> authority
/// rather than a scheme-carrying URL.
/// </param>
/// <param name="EndpointConsumingTargets">
/// The superset: every declared target at least one step reads a staged endpoint for. Decides one
/// thing in <c>EnvironmentMapper.Map</c> (#348) — whether an endpoint-less <c>project:</c>-form
/// service is a refused authoring fault or a legitimate untargeted worker.
/// </param>
/// <remarks>
/// <para>
/// <strong>Derive the two target sets from the SAME scenarios, which is why neither factory takes
/// them as arguments.</strong> They are computed from one input inside each factory, so no call
/// site can hand them different scenario sets and let them disagree about what the document
/// addresses.
/// </para>
/// </remarks>
public sealed record TopologyRequest(
    EnvironmentSpec? Environment,
    string? AppHostAssemblyName,
    TimeSpan StartupTimeout,
    string? SeedBaseDirectory,
    IReadOnlySet<string> KafkaSpeakingTargets,
    IReadOnlySet<string> EndpointConsumingTargets)
{
    /// <summary>
    /// The health-gate budget every production caller uses. Named rather than repeated at each
    /// factory so the two cannot drift.
    /// </summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Builds the request for a SINGLE scenario that owns its own topology — the single-scenario
    /// <c>run</c> path, <c>--parallel</c>, and the <c>--watch</c> build seam.
    /// </summary>
    /// <param name="ast">The scenario the topology is being built for.</param>
    /// <param name="appHostAssemblyName">The DCP-metadata-carrying assembly's short name.</param>
    /// <param name="seedBaseDirectory">The suite directory (seed root + artefact containment root).</param>
    public static TopologyRequest ForScenario(
        ScenarioAst ast, string? appHostAssemblyName, string? seedBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(ast);
        return new TopologyRequest(
            ast.Environment,
            appHostAssemblyName,
            DefaultStartupTimeout,
            seedBaseDirectory,
            SuiteProtocolTargets.KafkaSpeaking(ast),
            SuiteProtocolTargets.EndpointConsuming(ast));
    }

    /// <summary>
    /// Builds the request for the ONE shared topology of a multi-scenario suite: the union of the
    /// target sets across the RUNNABLE scenarios.
    /// </summary>
    /// <param name="environment">
    /// The baseline scenario's <c>environment</c> — the suite's shared-environment guard has
    /// already established that every runnable scenario agrees with it.
    /// </param>
    /// <param name="runnableScenarios">
    /// The scenarios carrying no early verdict. A scenario that executes nothing stages nothing, so
    /// it must not be able to make a project-form service look targeted (#348) or manufacture a
    /// protocol conflict (REQ-023) for a step that was never going to run.
    /// </param>
    /// <param name="appHostAssemblyName">The DCP-metadata-carrying assembly's short name.</param>
    /// <param name="seedBaseDirectory">The suite-wide seed root.</param>
    public static TopologyRequest ForSuite(
        EnvironmentSpec? environment,
        IReadOnlyList<ScenarioAst?> runnableScenarios,
        string? appHostAssemblyName,
        string? seedBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(runnableScenarios);
        return new TopologyRequest(
            environment,
            appHostAssemblyName,
            DefaultStartupTimeout,
            seedBaseDirectory,
            SuiteProtocolTargets.KafkaSpeaking(runnableScenarios),
            SuiteProtocolTargets.EndpointConsuming(runnableScenarios));
    }

    /// <summary>
    /// A stable, order-independent digest of every input this request carries — the reuse key for a
    /// kept topology.
    /// </summary>
    /// <param name="environmentHash">
    /// The caller-supplied hash of <see cref="Environment"/>. Passed in rather than computed here
    /// because the serialisation that produces it lives in <c>Vouchfx.Engine.Runtime</c>, which
    /// references this assembly and not the other way round.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The ordinal sort is load-bearing.</strong> <see cref="IReadOnlySet{T}"/> carries no
    /// ordering contract, so joining either set in enumeration order would make this digest
    /// nondeterministic — and a nondeterministic reuse key rebuilds the topology on every save,
    /// which is the opposite of what <c>--watch</c> is for.
    /// </para>
    /// <para>
    /// <strong>EVERY variable-length component is LENGTH-FRAMED, and a separator character is not
    /// used at all.</strong> An earlier form joined the members with U+001F and each target set
    /// internally with a comma, and claimed on that basis that "no combination of a target name and
    /// a directory path can forge a different request's digest". That claim was FALSE, and the
    /// counter-example is reachable from ordinary YAML: target names are unconstrained author text
    /// (the schema puts no <c>propertyNames</c> on <c>services</c>/<c>dependencies</c>, and
    /// <see cref="SuiteProtocolTargets"/> reads the raw scalar), so a save whose single target is
    /// <c>svc,zzz</c> and a save targeting <c>svc</c> and <c>zzz</c> separately both join to
    /// <c>svc,zzz</c> — identical fingerprint, no rebuild, and #348's refusal never fires for
    /// <c>svc</c>. That is the recorded residual this digest exists to close, returning through the
    /// encoding. The same collision reaches <see cref="KafkaSpeakingTargets"/>, where it decides the
    /// STAGED FORM a step reads and the confirmation level a run claims.
    /// </para>
    /// <para>
    /// Length framing removes the failure mode rather than making it less likely, which is the
    /// difference between this and picking a rarer separator: <c>&lt;n&gt;:&lt;n characters&gt;</c>
    /// is decodable without knowing anything about the alphabet, so no character an author can write
    /// — U+001F and U+001E included — is a delimiter here. Each set is additionally prefixed with
    /// its own element count, so an element boundary can never be read as a field boundary.
    /// </para>
    /// <para>
    /// <strong>THE INJECTIVITY CLAIM IS ABOUT THIS STRING, AND IT SURVIVES ONLY IF THE CONSUMER
    /// HASHES THE CODE UNITS.</strong> Framing distinguishes values whose CHARACTERS differ; it
    /// cannot distinguish two values a lossy encoder has already collapsed onto the same
    /// characters. <c>Encoding.UTF8.GetBytes</c> does exactly that to an unpaired surrogate — it
    /// substitutes U+FFFD — and a target name is unconstrained author text that can carry one. So
    /// <see cref="ScenarioRunner.ComputeTopologyFingerprint"/> hashes
    /// <c>MemoryMarshal.AsBytes(material.AsSpan())</c>, never a transcode, and this method's claim
    /// is stated as holding of the STRING it returns rather than of whatever a caller does to it.
    /// </para>
    /// <para>
    /// <strong>Two accepted residuals, recorded rather than closed.</strong> First, this record's
    /// generated equality is REFERENCE-based over the two set members — <see cref="IReadOnlySet{T}"/>
    /// does not implement structural equality — so two requests describing the same topology can
    /// compare unequal. Compare <see cref="ComputeFingerprintInput"/> (or the fingerprint derived
    /// from it), never the record, wherever the answer matters; nothing in the engine compares
    /// <c>TopologyRequest</c> instances today. Second, the public constructor can be called
    /// directly with two target sets that no single AST would produce — the factories below are the
    /// only thing that derives them together, and the call-site census counts
    /// <c>SuiteTopology.StartAsync</c> invocations rather than factory usage, so it would not see
    /// such a caller. Both are in-tree risks only: this assembly is not packable and ships to
    /// nobody.
    /// </para>
    /// </remarks>
    public string ComputeFingerprintInput(string environmentHash)
    {
        ArgumentNullException.ThrowIfNull(environmentHash);

        var builder = new StringBuilder();
        AppendFramed(builder, environmentHash);
        AppendFramed(builder, AppHostAssemblyName ?? string.Empty);
        AppendFramed(builder, StartupTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendFramed(builder, SeedBaseDirectory ?? string.Empty);
        AppendFramedSet(builder, KafkaSpeakingTargets);
        AppendFramedSet(builder, EndpointConsumingTargets);
        return builder.ToString();
    }

    /// <summary>
    /// THE ONLY PRODUCTION CALL TO <see cref="SuiteTopology.StartAsync"/>, and the census
    /// <c>Vouchfx.Engine.Runtime.Tests.SuiteProtocolTargetsTests
    /// .EverySuiteTopologyStartCallSite_PassesBothTargetSets</c> keeps it that way — it scans
    /// <c>src/</c> for the call and asserts the total is one.
    /// </summary>
    /// <param name="securityConfiguration">
    /// The resolved client security configuration REQ-005's probe presents. Not a member of this
    /// record: the caller owns the certificates' lifetime and disposes them in its own
    /// <c>finally</c>, and it is not a document input so it must not reach the fingerprint.
    /// </param>
    /// <param name="cancellationToken">Propagated to the whole start sequence.</param>
    public Task<SuiteTopology> StartAsync(
        ISecurityConfigurationAccessor? securityConfiguration, CancellationToken cancellationToken)
        => SuiteTopology.StartAsync(
            Environment,
            AppHostAssemblyName,
            startupTimeout: StartupTimeout,
            seedBaseDirectory: SeedBaseDirectory,
            securityConfiguration: securityConfiguration,
            kafkaSpeakingTargets: KafkaSpeakingTargets,
            endpointConsumingTargets: EndpointConsumingTargets,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Appends one value as <c>&lt;length&gt;:&lt;value&gt;</c> — a self-delimiting frame that needs
    /// no separator character and therefore has no forgeable one.
    /// </summary>
    /// <remarks>
    /// The length is in UTF-16 code units, which is what <see cref="string.Length"/> counts and what
    /// a decoder reading this back would count. Injectivity is all that is required of it; the frame
    /// is never decoded in production, only hashed.
    /// </remarks>
    private static void AppendFramed(StringBuilder builder, string value) =>
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    /// <summary>
    /// Appends a target set as its element count, then every element ordinally sorted and
    /// individually framed.
    /// </summary>
    /// <remarks>
    /// The count prefix is what keeps a set's own extent unambiguous where two sets sit adjacent in
    /// the digest input: without it, the frames of the first set and the frames of the second would
    /// form one undifferentiated run, and moving an element from one set to the other would leave
    /// the input unchanged.
    /// </remarks>
    private static void AppendFramedSet(StringBuilder builder, IReadOnlySet<string> targets)
    {
        builder.Append(targets.Count.ToString(CultureInfo.InvariantCulture)).Append('#');

        var ordered = new List<string>(targets);
        ordered.Sort(StringComparer.Ordinal);
        foreach (var target in ordered)
        {
            AppendFramed(builder, target);
        }
    }
}
