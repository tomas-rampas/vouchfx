// Vouchfx.Engine.Orchestration — SuiteProtocolTargets (authenticated-infrastructure-mtls,
// slice E — REQ-005, REQ-011).
//
// Answers two questions about a suite's declared targets, both by classifying its own STEPS.
//
//   1. For REQ-005's probe: which target names is this suite about to speak KAFKA to?
//   2. For EnvironmentMapper's #348 refusal: which target names will ANY step read a staged
//      endpoint for? (The union of the Kafka and HTTP families — see EndpointConsuming.)
//
// The first is the original question and everything below argues for it; the second reuses the
// same predicates rather than restating the families, so the two cannot drift.
//
// WHY THIS EXISTS AT ALL. REQ-005's strong confirmation level — the ApiVersions round trip that
// separates "the endpoint speaks TLS" from "the endpoint accepted this identity" — is only
// available where the probe knows the target's application protocol. The obvious source is the
// dependency's declared `type: kafka`. That source is also, for this feature's own deployment, the
// wrong one: REQ-011 states outright that the customer's mTLS broker "runs its own entrypoint/
// config and is authored as a SERVICE, not the engine-provisioned `kafka` dependency type". Keying
// the strong level on the declaration kind therefore hands it to the shape the customer does not
// use, and hands the weak, transport-only level — the one REQ-005 itself calls a ceiling — to the
// shape they do.
//
// The suite already states the protocol, in the only place that is authoritative about what will
// actually happen: its own steps. A `mq-publish.kafka` or `mq-expect.kafka` step naming a target is
// a statement that Kafka framing is about to travel to that target. Confirming exactly that is what
// the probe is for.
//
// REJECTED — a new `security.protocol` schema field. It is freeze-critical surface (REQ-019/020's
// whole argument) bought for a fact the document already carries, and an author who wrote it
// inconsistently with their own steps would get a confirmation about a protocol nothing speaks.
//
// REJECTED — attempt-and-degrade (send ApiVersions to every secured target and fall back to
// transport-only when the framing does not come back Kafka-shaped). It sends Kafka bytes into
// arbitrary services, and, worse, it cannot tell "this is not a Kafka broker" from "this Kafka
// broker refused my certificate" — those two produce the same non-Kafka-shaped silence. Collapsing
// them re-creates the false assurance the round trip exists to destroy.
using Vouchfx.Engine.Authoring.Ast;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Derives, from a suite's own steps, which declared targets speak which application protocol.
/// </summary>
public static class SuiteProtocolTargets
{
    /// <summary>The step family of a Kafka publish step.</summary>
    private const string MqPublishFamily = "mq-publish";

    /// <summary>The step family of a Kafka expectation step.</summary>
    private const string MqExpectFamily = "mq-expect";

    /// <summary>The provider segment naming Kafka in a dotted step type.</summary>
    private const string KafkaProvider = "kafka";

    /// <summary>The step field naming the dependency or service a step addresses.</summary>
    private const string TargetField = "target";

    /// <summary>
    /// The three step types that resolve <c>target</c> through <c>VarKeys.Service</c> and consume
    /// the staged value as an HTTP base URL.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than derived, and pinned by
    /// <c>SuiteProtocolTargetsTests.ProtocolFamilyLists_CoverEverySvcKeyConsumingStepType</c>,
    /// which scans the provider sources: these three plus the two Kafka step types above are
    /// exactly the step types whose emitted CSX contains <c>VarKeys.Service(model.Target)</c>.
    /// These three read it UNCONDITIONALLY (a dependency target is rejected outright at
    /// validation, REQ-012 as narrowed); the two Kafka ones read it only for a service target.
    /// Every other <c>svc::</c> read in the tree is a HOST RESOURCE's own key (a
    /// <c>webhook-listen.http</c> listener, a <c>trace-expect.otlp</c> receiver, a kafka
    /// dependency's <c>&lt;name&gt;-sr</c> schema-registry sidecar), which the engine stages
    /// itself under a name no <c>environment.services</c> entry may collide with.
    /// </remarks>
    private static readonly (string Family, string Provider)[] HttpSpeakingStepTypes =
    {
        ("http", "rest"),
        ("http", "soap"),
        ("metrics-assert", "prometheus"),
    };

    /// <summary>
    /// The set of declared target names addressed by at least one Kafka-protocol step across every
    /// scenario supplied.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios whose staging this set is about to drive — NOT necessarily every scenario the
    /// suite declares. A multi-scenario suite shares ONE topology, so a target any of these will
    /// speak Kafka to is a target the one shared probe must confirm as a broker; but a scenario
    /// carrying an early verdict stages nothing, because it executes nothing. At the suite seam the
    /// right input is therefore the RUNNABLE scenarios (<c>EarlyVerdict is null</c>), and passing
    /// every scenario instead lets one that cannot run veto one that can — the defect m7 (fix round
    /// eight) closed. The rule that survives both spellings: pass exactly the set whose staging you
    /// are protecting.
    /// </param>
    /// <returns>
    /// An ordinal set of target names, empty when no scenario carries a Kafka step. Never
    /// <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Reads the raw step node rather than a bound provider model on purpose: this runs BEFORE the
    /// topology is built and outside any provider's own binder, and a target name is a plain scalar
    /// that needs no binding. A step whose <c>target</c> is absent, non-scalar, or blank contributes
    /// nothing — that is the provider validator's error to raise, not this helper's, and inventing a
    /// name here would be worse than contributing none.
    /// </remarks>
    public static IReadOnlySet<string> KafkaSpeaking(IEnumerable<ScenarioAst?>? scenarios) =>
        TargetsOf(scenarios, IsKafkaStep);

    /// <summary>
    /// The set for a single scenario — the single-scenario call sites' convenience over
    /// <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/>.
    /// </summary>
    /// <param name="scenario">The scenario, or <see langword="null"/>.</param>
    /// <returns>An ordinal set of target names; never <see langword="null"/>.</returns>
    public static IReadOnlySet<string> KafkaSpeaking(ScenarioAst? scenario) =>
        KafkaSpeaking(new[] { scenario });

    /// <summary>
    /// The set of declared target names addressed by at least one HTTP-family step
    /// (<c>http.rest</c>, <c>http.soap</c>, <c>metrics-assert.prometheus</c>) across every
    /// scenario supplied.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios whose staging this set is about to drive; a multi-scenario suite shares ONE
    /// topology. Same scoping rule as <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/> — pass
    /// exactly the set whose staging you are protecting.
    /// </param>
    /// <returns>An ordinal set of target names; never <see langword="null"/>.</returns>
    /// <remarks>
    /// <strong>Internal where its <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/> sibling is
    /// public, and the asymmetry is measured rather than stylistic</strong> (the critic's residual,
    /// closed in the slice F hygiene pass). <c>KafkaSpeaking</c> has a production caller outside
    /// this assembly's <c>InternalsVisibleTo</c> grants — <c>Vouchfx.Cli</c>'s
    /// <c>WatchRunner</c> — so it cannot narrow. This one has no consumer outside the class at all
    /// beyond <c>SuiteProtocolTargetsTests</c>, which sits in <c>Vouchfx.Engine.Runtime.Tests</c>
    /// and is already granted internals by this project's own csproj; the only production read of
    /// this METHOD is <see cref="BothHttpAndKafkaSpeaking"/>, in this same class.
    /// (<see cref="EndpointConsuming(IEnumerable{ScenarioAst})"/> does not call it — it reuses the
    /// <c>IsHttpStep</c> predicate directly, which is why it can classify both families in one
    /// pass.) Narrowing costs nothing to reverse: this assembly is not packable (the root
    /// <c>Directory.Build.props</c> sets <c>IsPackable=false</c> and this project does not opt in)
    /// and no golden freeze gate
    /// snapshots its public surface, so the visibility is an ordinary design choice rather than a
    /// contract.
    /// </remarks>
    internal static IReadOnlySet<string> HttpSpeaking(IEnumerable<ScenarioAst?>? scenarios) =>
        TargetsOf(scenarios, IsHttpStep);

    /// <summary>
    /// The set of declared target names that at least one step will read a STAGED ENDPOINT for —
    /// the union of <see cref="HttpSpeaking(IEnumerable{ScenarioAst})"/> and
    /// <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/>.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios whose staging this set is about to drive; same scoping rule as
    /// <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/> — pass exactly the set whose staging
    /// you are protecting, which at the suite seam means the RUNNABLE scenarios.
    /// </param>
    /// <returns>An ordinal set of target names; never <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What it is for (#348).</strong> <c>EnvironmentMapper</c> refuses a
    /// <c>project:</c>-form service that declares no endpoint — but only when a step would
    /// actually read one for it. A .NET worker service (a <c>BackgroundService</c> consuming
    /// Kafka or a queue, no <c>applicationUrl</c>, no HTTP listener) is schema-legal, has no
    /// escape hatch — <c>$defs/service</c>'s project-form clause forbids every field an
    /// image-form service would use to declare a non-HTTP shape (grep that clause's <c>then</c>
    /// for the current roster rather than trusting a copy here), so its author cannot do what
    /// REQ-008 lets an image-form author do — and is the canonical shape this product exists to
    /// test. It must
    /// keep starting as part of the topology and simply never be staged, exactly as it did before
    /// that refusal existed. This set is what tells the two cases apart.
    /// </para>
    /// <para>
    /// <strong>Why the union is the right definition, and why it is derived rather than listed
    /// again.</strong> The five step types behind these two predicates are exactly the step types
    /// whose emitted CSX reads a <c>svc::</c> key derived from the step's own <c>target</c> — the
    /// claim <c>SuiteProtocolTargetsTests.ProtocolFamilyLists_CoverEverySvcKeyConsumingStepType</c>
    /// pins by scanning the provider sources for every spelling of such a read, discounting
    /// comments and JSON-schema descriptions. So among step types that DECLARE A <c>target</c>,
    /// "a step will read a staged endpoint for this target" and "this target appears in one of
    /// those two sets" are the same statement — and reusing the predicates means a sixth
    /// <c>svc::</c>-consuming step type cannot be added without that existing gate failing and
    /// this set widening with it. A second, hand-listed copy of the same families would drift
    /// silently instead.
    /// </para>
    /// <para>
    /// <strong>The <c>target</c>-declaring qualifier is real, and <c>script.csharp</c> is
    /// deliberately outside it.</strong> That step type has no <c>target</c> field at all and its
    /// provider documents full ambient access to <c>Vars</c>, so a script reading
    /// <c>Vars["svc::worker"]</c> is unclassifiable by this mechanism — no scan of declared
    /// targets can see it. The consequence is bounded and is not a wrong-endpoint reach: such a
    /// script gets a hard miss on its own line, naming the key it asked for, rather than the
    /// silent empty-string base URL #348 was about.
    /// </para>
    /// <para>
    /// A DEPENDENCY name CAN appear in this set, and that is normal rather than a flaw: an
    /// <c>mq-publish.kafka</c> step targeting a declared <c>kafka</c> dependency is the ordinary
    /// case, and the union inherits it from <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/>.
    /// (The intersection helper below can exclude dependencies because all three HTTP-family
    /// providers reject one outright, REQ-012 as narrowed — that argument belongs there, not
    /// here.) It is harmless: the mapper consults this set only from inside its SERVICES loop,
    /// keyed on a declared service name, so a dependency name in it is never looked up.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> EndpointConsuming(IEnumerable<ScenarioAst?>? scenarios) =>
        TargetsOf(scenarios, static step => IsHttpStep(step) || IsKafkaStep(step));

    /// <summary>
    /// The set for a single scenario — the single-scenario call sites' convenience over
    /// <see cref="EndpointConsuming(IEnumerable{ScenarioAst})"/>.
    /// </summary>
    /// <param name="scenario">The scenario, or <see langword="null"/>.</param>
    /// <returns>An ordinal set of target names; never <see langword="null"/>.</returns>
    public static IReadOnlySet<string> EndpointConsuming(ScenarioAst? scenario) =>
        EndpointConsuming(new[] { scenario });

    /// <summary>
    /// The declared target names addressed by BOTH an HTTP-family step and a Kafka-family step —
    /// each of which needs the staged value in a different, mutually exclusive form.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios whose staging this rejection is about — the same scoping rule as
    /// <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/>, and NOT "every scenario the suite
    /// declares": at the suite seam that means the RUNNABLE ones. This parameter used to read "the
    /// suite's scenarios", which was the whole-list rule the m7 fix retired.
    /// </param>
    /// <returns>
    /// The conflicting names, ordinally sorted so a diagnostic reads the same on every run; empty
    /// when no target is addressed by both.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Why this is a conflict rather than a preference.</strong> REQ-023 (as amended)
    /// stages exactly ONE value per target, in the form its consumer uses: an <c>https://</c> URL
    /// for the HTTP family, a bare <c>host:port</c> bootstrap authority for the Kafka families.
    /// One string cannot be both. Picking a winner would give the loser a value it must transform
    /// to use — which is precisely what that requirement forbids — and would do it silently, at
    /// run time, in a step whose failure names a broker or a URL rather than the declaration that
    /// caused it.
    /// </para>
    /// <para>
    /// <strong>Why rejecting narrows nothing that worked.</strong> Before this, such a suite's
    /// Kafka step could not run at all: it read <c>conn::&lt;name&gt;</c>, which is never staged
    /// for a service, and failed as an <c>EnvironmentError</c> — "kafka bootstrap not found". So
    /// the shape being rejected is one that has never had a passing run, and rejecting it at
    /// validation replaces a run-time environment error with an authoring diagnostic.
    /// </para>
    /// <para>
    /// A DEPENDENCY cannot reach this set: all three HTTP-family providers reject a <c>target</c>
    /// naming a declared dependency outright (REQ-012 as narrowed), so only a service name — or a
    /// name that is neither, which their own validators reject — can appear in both halves.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> BothHttpAndKafkaSpeaking(IEnumerable<ScenarioAst?>? scenarios)
    {
        var kafka = KafkaSpeaking(scenarios);
        if (kafka.Count == 0)
        {
            return Array.Empty<string>();
        }

        return HttpSpeaking(scenarios)
            .Where(kafka.Contains)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The author-facing diagnostic for <see cref="BothHttpAndKafkaSpeaking"/>, or
    /// <see langword="null"/> when no target is addressed by both families.
    /// </summary>
    /// <param name="scenarios">
    /// The scenarios the rejection is about — ONE scenario at the per-scenario compile seam, the
    /// suite's RUNNABLE scenarios at the suite seam. Both call sites must pass exactly the set whose
    /// staging they are protecting; see the remarks.
    /// </param>
    /// <returns>
    /// The diagnostic naming every conflicting target and both families, or <see langword="null"/>
    /// when there is nothing to reject.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Why the message lives here and not at the call sites.</strong> Two seams reject this
    /// one authoring error — <c>ProviderPipeline.Compile</c> (per scenario: <c>vouchfx validate</c>,
    /// <c>--parallel</c>, <c>--watch</c>, and the single-scenario <c>run</c>, where the one scenario
    /// IS the suite) and <c>ScenarioRunner.RunSuiteAsync</c> (the shared-topology suite seam, whose
    /// staging reads the union across its RUNNABLE scenarios). Two spellings of one rule is how
    /// the two drift: an author would get different words, and later a different meaning, for the
    /// same mistake depending on which door they came through. The detection already lived in one
    /// place; this puts the wording there too.
    /// </para>
    /// <para>
    /// <strong>The caller chooses the scope, and must match its own staging input — that is the
    /// whole rule, and it is NOT "pass every scenario".</strong> This helper deliberately does not
    /// know which seam is calling. The suite seam stages from
    /// <see cref="KafkaSpeaking(IEnumerable{ScenarioAst})"/> over its RUNNABLE scenarios, so it must
    /// pass that same list here — and it does so from ONE shared local, so the guard and the staging
    /// cannot disagree. Two failure directions, and this branch has been on both: a per-scenario
    /// call at the suite seam is blind to a suite that splits the two families across two scenarios,
    /// which is the hole this overload's existence closed; and widening it back to EVERY scenario
    /// lets a scenario that cannot run veto one that can, which is the defect m7 closed. Neither is
    /// fixed by counting scenarios — only by handing this helper the set whose staging it guards.
    /// </para>
    /// </remarks>
    public static string? DescribeProtocolConflict(IEnumerable<ScenarioAst?>? scenarios)
    {
        var conflicts = BothHttpAndKafkaSpeaking(scenarios);
        if (conflicts.Count == 0)
        {
            return null;
        }

        return $"Target(s) {string.Join(", ", conflicts.Select(n => $"'{n}'"))} are "
            + "addressed by both an HTTP-family step (http.rest / http.soap / "
            + "metrics-assert.prometheus) and a Kafka-family step (mq-publish.kafka / "
            + "mq-expect.kafka). The engine stages one endpoint value per target, in the "
            + "form that target's own clients consume — an 'https://host:port' URL for the "
            + "HTTP family, a bare 'host:port' bootstrap for the Kafka families — and one "
            + "string cannot be both. Declare the broker and the HTTP API as two separate "
            + "entries under environment.services, each addressed by one family.";
    }

    /// <summary>
    /// The union of every target named by a step matching <paramref name="predicate"/>.
    /// </summary>
    private static HashSet<string> TargetsOf(
        IEnumerable<ScenarioAst?>? scenarios, Func<StepNode, bool> predicate)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        if (scenarios is null)
        {
            return targets;
        }

        foreach (var scenario in scenarios)
        {
            if (scenario?.Steps is not { } steps)
            {
                continue;
            }

            foreach (var step in steps)
            {
                if (predicate(step) && TryReadTarget(step.RawNode, out var target))
                {
                    targets.Add(target);
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// True for the three step types that consume their target's staged value as an HTTP base URL.
    /// </summary>
    private static bool IsHttpStep(StepNode step)
    {
        foreach (var (family, provider) in HttpSpeakingStepTypes)
        {
            if (string.Equals(step.Kind.Family, family, StringComparison.Ordinal)
                && string.Equals(step.Kind.Provider, provider, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for <c>mq-publish.kafka</c> and <c>mq-expect.kafka</c> — the two step kinds REQ-015
    /// wires mutual TLS for, and therefore the two whose targets a probe can confirm as brokers.
    /// </summary>
    private static bool IsKafkaStep(StepNode step) =>
        string.Equals(step.Kind.Provider, KafkaProvider, StringComparison.Ordinal)
        && (string.Equals(step.Kind.Family, MqPublishFamily, StringComparison.Ordinal)
            || string.Equals(step.Kind.Family, MqExpectFamily, StringComparison.Ordinal));

    private static bool TryReadTarget(YamlMappingNode? rawNode, out string target)
    {
        target = string.Empty;

        if (rawNode is null)
        {
            return false;
        }

        foreach (var (key, value) in rawNode.Children)
        {
            if (key is YamlScalarNode { Value: TargetField }
                && value is YamlScalarNode { Value: { } scalar }
                && !string.IsNullOrWhiteSpace(scalar))
            {
                target = scalar;
                return true;
            }
        }

        return false;
    }
}
