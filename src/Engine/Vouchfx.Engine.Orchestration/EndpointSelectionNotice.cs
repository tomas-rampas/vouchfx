// EndpointSelectionNotice — the typed record behind the transport-downgrade advisory (#348).
//
// Typed rather than a pre-formatted string, matching its sibling SecurityConfirmation, which
// SuiteTopology also surfaces and the runner also prints. The reasons are the same ones that
// applied there: the orchestration layer should not be assembling author-facing English; a test
// asserting on fields does not pin a particular wording as the contract; and #450, if it routes
// this to the event stream, needs the parts rather than a sentence to re-parse.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// A record that the engine selected one endpoint of a service over another, where the choice was
/// consequential enough for the author to be told.
/// </summary>
/// <param name="ServiceName">The declared service the choice was made for.</param>
/// <param name="SelectedEndpoint">The Aspire endpoint name <c>svc::&lt;ServiceName&gt;</c> resolves to.</param>
/// <param name="RejectedEndpoint">The endpoint that was available and not chosen.</param>
/// <remarks>
/// Today there is exactly one producer: a <c>project:</c>-form service declaring both an http and
/// an https endpoint, where the engine stages the plaintext one because it holds no trust material
/// for a form that cannot declare <c>security</c>.
/// </remarks>
public sealed record EndpointSelectionNotice(
    string ServiceName,
    string SelectedEndpoint,
    string RejectedEndpoint)
{
    /// <summary>
    /// The author-facing line, rendered at the print sites rather than baked at construction.
    /// </summary>
    public override string ToString() =>
        $"transport: service '{ServiceName}' declares both '{SelectedEndpoint}' (http) and "
        + $"'{RejectedEndpoint}' (https); steps targeting it will use PLAINTEXT. A 'project'-form "
        + "service cannot declare 'security', so the engine holds no trust material for its TLS "
        + "listener and a request to it would fail the handshake rather than verify anything.";
}
