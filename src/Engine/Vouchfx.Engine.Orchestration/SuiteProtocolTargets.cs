// Vouchfx.Engine.Orchestration — SuiteProtocolTargets (authenticated-infrastructure-mtls,
// slice E — REQ-005, REQ-011).
//
// Answers one question for REQ-005's probe: which declared target names is this suite about to
// speak KAFKA to?
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
    /// The suite's scenarios. A multi-scenario suite shares ONE topology, so the union across all
    /// of them is the right set: a target any scenario will speak Kafka to is a target the one
    /// shared probe should confirm as a broker.
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
    /// <param name="scenarios">The suite's scenarios; a multi-scenario suite shares ONE topology.</param>
    /// <returns>An ordinal set of target names; never <see langword="null"/>.</returns>
    public static IReadOnlySet<string> HttpSpeaking(IEnumerable<ScenarioAst?>? scenarios) =>
        TargetsOf(scenarios, IsHttpStep);

    /// <summary>
    /// The declared target names addressed by BOTH an HTTP-family step and a Kafka-family step —
    /// each of which needs the staged value in a different, mutually exclusive form.
    /// </summary>
    /// <param name="scenarios">The suite's scenarios.</param>
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
