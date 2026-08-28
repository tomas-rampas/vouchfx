// EndpointTrustNotice — the advisory raised when the address staged for a `project:`-form service
// is an https listener the engine configures no trust material for.
//
// IT DOES NOT MATTER WHO PICKED THAT LISTENER, and the wording throughout this file is careful not
// to claim otherwise. The notice fires on the SELECTED endpoint: an `endpoint:` naming an https
// listener, and equally an https-ONLY project whose author wrote no `endpoint:` at all and whose
// listener the engine's own fixed rule therefore chose. Both address an unverified TLS listener;
// only the second used to pass in silence.
//
// A SEPARATE RECORD RATHER THAN A DISCRIMINATED ADDITION TO EndpointSelectionNotice, and the
// deciding argument is the data, not the taxonomy. EndpointSelectionNotice's third field,
// RejectedEndpoint, is a non-nullable string that exists because the downgrade notice always has
// a rejected sibling to name — that is the whole content of "the engine picked the plaintext one
// FOR YOU". This notice never has one: an https selection is either the author's own, in which
// case the engine rejected nothing, or the fixed rule's, in which case there was no plaintext
// listener to reject (the rule prefers http whenever one exists, so it only reaches https on a
// project that declares no http). Folding it in would mean either retyping RejectedEndpoint to
// `string?` (a MUTATION of an existing member, when the brief for this change is additive) or
// passing a meaningless value for it. A second type keeps both records total: every field of each
// one is always meaningful.
//
// The meanings stay distinguishable at the print site, which is the property that actually
// matters. The downgrade line says "your traffic went plaintext when TLS was on offer"; this one
// says "your traffic goes over TLS this engine configured no trust for". A reader who confused
// them would draw the opposite conclusion about what the run proved, so they must not merely
// differ in wording — they differ in type, in fields, and in sentence.
//
// This assembly is NOT packable (Directory.Build.props sets IsPackable=false and this csproj does
// not opt in), and no golden API gate covers it — SdkContractFreezeTests pins Vouchfx.Sdk,
// SdkTestingContractFreezeTests pins Vouchfx.Sdk.Testing, EventContractFreezeTests pins the event
// wire. So publishing a new public type here costs nothing that a frozen surface would have.
namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// A record that a <c>project:</c>-form service's staged address resolves to an https listener for
/// which the engine configures no client trust material of its own — whether that listener was
/// named by the service's <c>endpoint:</c> field or chosen by the engine's own fixed rule.
/// </summary>
/// <param name="ServiceName">The declared service the address was staged for.</param>
/// <param name="SelectedEndpoint">
/// The Aspire endpoint name <c>svc::&lt;ServiceName&gt;</c> resolves to. Its scheme is
/// <c>https</c>; that is the trigger, and it is read off the SELECTED ANNOTATION rather than off
/// any author-written string, because <c>endpoint:</c> matches by name and a project may name an
/// https listener anything at all.
/// </param>
/// <remarks>
/// <para>
/// <strong>What is absent is engine-configured trust, not verification.</strong> With no
/// <c>security</c> block — and a <c>project:</c>-form service cannot declare one —
/// <c>SecurityHelper.ConfigureHandler</c> returns without touching the handler, so the platform's
/// own trust store applies and the certificate is verified against it, full chain, exactly as any
/// other .NET HTTPS request would be. What vouchfx does NOT do is contribute a private anchor, pin
/// the peer, present a client identity, or make any assertion about the outcome; the engine
/// installs a validation callback only where a CA is actually declared, precisely so it never
/// replaces the platform's verdict with its own.
/// </para>
/// <para>
/// <strong>Why that still needs saying.</strong> The most plausible reading of an https address —
/// "this is now secured, and vouchfx checked" — is wrong in its second half. Addressing the https
/// listener changes WHICH listener is reached and nothing else. On a host that does not already
/// trust the certificate that listener presents — a fresh CI runner that never ran
/// <c>dotnet dev-certs https --trust</c>, say — the request fails the handshake, which is
/// classified an environment error, which does not fail the run unless <c>--fail-on-env-error</c>
/// is passed: a green run over a suite that verified nothing.
/// </para>
/// <para>
/// Announcing the absence of engine-configured trust creates none. This notice is the only thing
/// in such a run that says anything about transport at all, which is precisely why suppressing it —
/// the tidier rule, "never announce a choice back to the author who made it" — was rejected for the
/// https case while being kept for every other.
/// </para>
/// </remarks>
public sealed record EndpointTrustNotice(
    string ServiceName,
    string SelectedEndpoint)
{
    /// <summary>
    /// The author-facing line, rendered at the print sites rather than baked at construction —
    /// the same division of labour <see cref="EndpointSelectionNotice"/> uses, and for the same
    /// reasons.
    /// </summary>
    public override string ToString() =>
        $"transport: service '{ServiceName}' is addressed at endpoint '{SelectedEndpoint}', which "
        + "is an https listener, and the engine configures NO client trust material for it. A "
        + "'project'-form service cannot declare 'security', so the certificate that listener "
        + "presents is checked against this host's own default trust store and nothing else — "
        + "vouchfx asserts nothing about the outcome. If this host does not already trust that "
        + "certificate the request fails the handshake, which is classified an ENVIRONMENT ERROR "
        + "and does not fail the run unless '--fail-on-env-error' is passed.";
}
