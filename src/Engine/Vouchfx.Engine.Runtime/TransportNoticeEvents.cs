// Vouchfx.Engine.Runtime — TransportNoticeEvents (#450, #453).
//
// The single producer of the §14 `transport-notice` event line. Both endpoint advisories
// SuiteTopology surfaces — EndpointSelectionNotice (the engine picked plaintext over an
// available https listener) and EndpointTrustNotice (the run addresses an https listener the
// engine configures no client trust for) — reach the JSON Lines stream through here and
// nowhere else.
//
// WHY A SHAPE-MIRROR OF EnvironmentErrorEvents RATHER THAN A COPY OF IT.
//   That type's `ToLine` — taking the classified record, a run id and a caller-supplied
//   timestamp — is the established run-level event path, and this file follows its shape: a
//   Create that maps a typed orchestration record onto the wire record, a serialise step through
//   the shared EventStreamJson options, and a caller-supplied timestamp so tests are
//   deterministic. (Named without its argument list on purpose: the routing gate in
//   SecretObservationLeakPenetrationTests greps this assembly for calls to it, and a mention in
//   prose must not read as one.) What it does NOT copy is the
//   Runtime-side scrubbing chokepoint (ScenarioRunner.EnvironmentErrorLine) that wraps that
//   factory — see "NOT SCRUBBED" below, which is a decision about these members specifically
//   and not an omission.
//
// WHY IT LIVES IN Runtime AND NOT BESIDE THE NOTICES IN Orchestration.
//   The notice records are Orchestration's; the EMISSION is not. Orchestration surfaces the
//   notices off SuiteTopology and stops there — it has no event destination, no runId and no
//   opinion about replay. Runtime owns all three, and it owns the three call sites. Keeping
//   the factory here puts the producer, its `replayed` policy and its call sites in one
//   assembly, which is what the source gate in the Runtime test project can then hold together
//   (TransportNoticeEventEmissionTests). EnvironmentErrorEvents sits in Orchestration because
//   OrchestrationErrorInfo's classification is Orchestration's own; nothing here is.
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Maps the two endpoint advisories a <see cref="SuiteTopology"/> surfaces onto the frozen
/// <see cref="TransportNoticeEvent"/> wire record (§14.4, #450 / #453).
/// </summary>
/// <remarks>
/// <para>
/// <strong>NOT SCRUBBED, and that is a decision about WHICH MEMBERS — not an omission.</strong>
/// <c>ScenarioRunner.EnvironmentErrorLine</c> scrubs an environment-error event against the
/// run's <c>ResolvedSecretLedger</c>, and it scrubs exactly one member:
/// <c>OrchestrationErrorInfo.Detail</c>, because that is the sole free-form member and every
/// construction site folds an underlying exception message into it. Its siblings —
/// <c>ResourceName</c>, a declared name; <c>RegistryHost</c>, parsed from an image reference;
/// <c>AuthStatus</c>, a closed engine token — are deliberately left alone, since scrubbing a
/// declared NAME corrupts the diagnosis for no gain.
/// </para>
/// <para>
/// Run the same analysis over this record and every member falls on the not-scrubbed side.
/// <see cref="TransportNoticeEvent.Service"/> is the service name as declared under
/// <c>environment.services</c>. <see cref="TransportNoticeEvent.SelectedEndpoint"/> and
/// <see cref="TransportNoticeEvent.RejectedEndpoint"/> are Aspire endpoint NAMES read off the
/// selected annotation (<c>EnvironmentMapper</c> passes <c>.Name</c>, never a resolved URL or a
/// <c>host:port</c> authority). <see cref="TransportNoticeEvent.Kind"/> is one of two closed
/// engine tokens. <see cref="TransportNoticeEvent.Replayed"/> is a boolean. None of them is
/// free-form, none folds an exception message, and no <c>${secret:}</c> reference is
/// substitutable into any of them — service names and endpoint names are not substitutable
/// fields.
/// </para>
/// <para>
/// Scrubbing anyway would not be free belt-and-braces. The scrub redacts <em>exact
/// occurrences</em> of every value the run has resolved, so a short secret that happens to
/// collide with a substring of an endpoint name — <c>http</c>, <c>api</c>, <c>db</c> — would
/// rewrite the record into an unreadable one and destroy the correlation back to the suite.
/// The cost is asymmetric and points the other way.
/// </para>
/// <para>
/// <strong>This paragraph is the thing to update, not delete, if a free-form member is ever
/// added to <see cref="TransportNoticeEvent"/>.</strong> The analysis above is a claim about
/// today's five members; a sixth carrying an exception message, a URL, or any author-supplied
/// free text would need the scrub, and inheriting this decision silently is exactly how the
/// environment-error path shipped an unscrubbed channel once already.
/// </para>
/// <para>
/// <strong>NOT SANITISED either — <c>DisplaySanitiser.SanitiseForDisplay</c> must not be
/// applied on this path.</strong> That helper's own remarks state the <c>--events</c> path
/// needs no equivalent: <c>System.Text.Json</c> always <c>\u</c>-escapes control characters, so
/// an ESC/CSI byte cannot appear literally on the wire whatever the service name contains.
/// Sanitising here would make the wire <c>service</c> differ from the author's YAML key, which
/// breaks the only correlation a consumer has back to the suite, and would bake a render-time
/// concern into a frozen contract. The terminal print sites keep their own sanitisation, which
/// is the correct seam and is untouched.
/// </para>
/// <para>
/// <strong>Advisory only.</strong> Nothing here reads or writes a verdict: the record carries
/// no <c>verdict</c> field (unlike <c>EnvironmentErrorEvent</c>), emission is unconditional on
/// whether any renderer or event destination is attached, and the §12.1 taxonomy and the exit
/// code are decided elsewhere and unchanged.
/// </para>
/// </remarks>
internal static class TransportNoticeEvents
{
    /// <summary>
    /// Maps an <see cref="EndpointSelectionNotice"/> onto its wire record — kind
    /// <see cref="TransportNoticeKinds.PlaintextDowngrade"/>, carrying both endpoint names.
    /// </summary>
    /// <param name="notice">The advisory raised by <c>EnvironmentMapper</c>.</param>
    /// <param name="runId">The identifier of the run this advisory belongs to.</param>
    /// <param name="timestamp">
    /// The emission timestamp, supplied by the caller (rather than read from
    /// <see cref="DateTimeOffset.UtcNow"/> here) so tests stay deterministic — the same
    /// division <c>EnvironmentErrorEvents.Create</c> uses. (Named without its argument list on
    /// purpose, as in the file header: <c>SecretObservationLeakPenetrationTests</c> greps every
    /// Runtime source for that name followed by <c>(</c>, so "tidying" this into a call-shaped
    /// mention reddens a secret-leak penetration test over a comment that leaks nothing.)
    /// </param>
    /// <param name="replayed">
    /// <see langword="true"/> only on the <c>--watch</c> replay against a kept topology; see
    /// <see cref="ToLines"/> for why a fresh build must leave the field UNSET rather than false.
    /// </param>
    internal static TransportNoticeEvent Create(
        EndpointSelectionNotice notice,
        string runId,
        DateTimeOffset timestamp,
        bool replayed) =>
        new()
        {
            RunId = runId,
            Timestamp = timestamp,
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = notice.ServiceName,
            // ALWAYS set for this kind, and by construction rather than by discipline: the
            // notice's own RejectedEndpoint is a non-nullable string that exists because a
            // downgrade always has a rejected sibling to name. There is no argument shape that
            // could produce a downgrade record without one.
            RejectedEndpoint = notice.RejectedEndpoint,
            // The endpoint NAME the notice carries, copied verbatim. Never a resolved URL and
            // never a host:port authority — EnvironmentMapper.StageServiceEndpoint does stage
            // bare authorities for some targets, so a producer that reached for the staged value
            // instead of the notice's field would put one on the wire. The disclosure analysis
            // that cleared this record for archived CI artefacts depends on it not doing that.
            SelectedEndpoint = notice.SelectedEndpoint,
            Replayed = replayed ? true : null,
        };

    /// <summary>
    /// Maps an <see cref="EndpointTrustNotice"/> onto its wire record — kind
    /// <see cref="TransportNoticeKinds.NoEngineTrust"/>, carrying the selected endpoint name and
    /// no rejected one.
    /// </summary>
    /// <param name="notice">The advisory raised by <c>EnvironmentMapper</c>.</param>
    /// <param name="runId">The identifier of the run this advisory belongs to.</param>
    /// <param name="timestamp">The emission timestamp, supplied by the caller.</param>
    /// <param name="replayed">
    /// <see langword="true"/> only on the <c>--watch</c> replay against a kept topology.
    /// </param>
    /// <remarks>
    /// <see cref="TransportNoticeEvent.RejectedEndpoint"/> is left <see langword="null"/> —
    /// nothing was rejected. An https selection is either the author's own <c>endpoint:</c>, in
    /// which case the engine rejected nothing, or the fixed rule's on a project that declares no
    /// http listener at all, in which case there was nothing to reject. The wire record's shape
    /// permits a rejected endpoint on either kind, so only this producer can enforce the
    /// pairing; it does so by not having a value to set, and
    /// <c>TransportNoticeEventEmissionTests</c> asserts the pairing against both producers.
    /// </remarks>
    internal static TransportNoticeEvent Create(
        EndpointTrustNotice notice,
        string runId,
        DateTimeOffset timestamp,
        bool replayed) =>
        new()
        {
            RunId = runId,
            Timestamp = timestamp,
            Kind = TransportNoticeKinds.NoEngineTrust,
            Service = notice.ServiceName,
            SelectedEndpoint = notice.SelectedEndpoint,
            Replayed = replayed ? true : null,
        };

    /// <summary>
    /// Builds one §14 event line per advisory in <paramref name="selectionNotices"/> and
    /// <paramref name="trustNotices"/> — the whole of what the three
    /// <see cref="ScenarioRunner"/> print sites emit.
    /// </summary>
    /// <param name="selectionNotices">
    /// <c>SuiteTopology.EndpointSelectionNotices</c> — the same collection the caller prints.
    /// </param>
    /// <param name="trustNotices">
    /// <c>SuiteTopology.EndpointTrustNotices</c> — the same collection the caller prints.
    /// </param>
    /// <param name="runId">The identifier of the run this topology's advisories belong to.</param>
    /// <param name="timestamp">The emission timestamp, supplied by the caller.</param>
    /// <param name="replayed">
    /// <see langword="true"/> on the <c>--watch</c> replay against a kept topology;
    /// <see langword="false"/> on the two fresh-build paths.
    /// </param>
    /// <returns>
    /// One line per notice, selection notices first. <strong>Ordering is not part of the wire
    /// contract</strong> and no consumer may rely on it — it is simply the order the caller
    /// prints in, kept so a reader diffing the terminal against the stream is not puzzled.
    /// Empty when both collections are empty, which is what keeps a run with no advisory
    /// byte-identical to one from before this record existed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Cardinality is the terminal's, by construction.</strong> One line per element of
    /// the two collections the caller also iterates to print — not one per run, and not one per
    /// scenario. That equivalence — records emitted == notices printed, per call site — is what
    /// the tests assert, rather than any fixed count, because no fixed count holds on every
    /// path: <c>RunSuiteAsync</c> builds ONE topology for the whole selection and so reports
    /// each advisory once however many files that selection held, while
    /// <c>RunScenarioOwningTopologyAsync</c> is entered once per scenario-owned topology and
    /// reports once per build. Stated as an equivalence rather than a count, it needs no
    /// knowledge of which of the two <c>RunCommand</c>'s dispatch picked.
    /// </para>
    /// <para>
    /// <strong><paramref name="replayed"/> is written only when true.</strong>
    /// <see cref="TransportNoticeEvent.Replayed"/> is <c>bool?</c> and the shared serialiser sets
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c>, which omits a null and <em>writes</em> a
    /// <see langword="false"/>. Passing <c>replayed: false</c> therefore has to map to
    /// <see langword="null"/>, not to <see langword="false"/>: writing <c>"replayed":false</c> on
    /// every fresh-build record would put a new field on every stream that carries an advisory
    /// and break the byte-identity argument above. Read an absent field as "not a replay".
    /// </para>
    /// </remarks>
    internal static List<string> ToLines(
        IReadOnlyList<EndpointSelectionNotice> selectionNotices,
        IReadOnlyList<EndpointTrustNotice> trustNotices,
        string runId,
        DateTimeOffset timestamp,
        bool replayed)
    {
        ArgumentNullException.ThrowIfNull(selectionNotices);
        ArgumentNullException.ThrowIfNull(trustNotices);
        ArgumentNullException.ThrowIfNull(runId);

        var lines = new List<string>(selectionNotices.Count + trustNotices.Count);

        foreach (var notice in selectionNotices)
        {
            lines.Add(EventStreamJson.ToLine(Create(notice, runId, timestamp, replayed)));
        }

        foreach (var notice in trustNotices)
        {
            lines.Add(EventStreamJson.ToLine(Create(notice, runId, timestamp, replayed)));
        }

        return lines;
    }
}
