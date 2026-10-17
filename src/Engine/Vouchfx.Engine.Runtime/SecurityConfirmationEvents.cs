using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Engine.Runtime;

// Maps only the probe's structured public facts. Never serialize the orchestration record:
// it also holds private declaration identity/digest and free-form Detail.
internal static class SecurityConfirmationEvents
{
    internal static List<string> ToLines(IReadOnlyList<SecurityConfirmation> confirmations,
        string runId, DateTimeOffset timestamp, bool replayed) => confirmations.Select(confirmation =>
        EventStreamJson.ToLine(new SecurityConfirmationEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            TargetName = confirmation.TargetName,
            TargetKind = confirmation.TargetKind,
            DeclaredProfile = confirmation.DeclaredProfile,
            DeclaredEndpoint = confirmation.DeclaredEndpoint,
            ObservedAddress = confirmation.ObservedAddress,
            ObservedProtocol = confirmation.ObservedProtocol,
            ClientIdentityResolved = confirmation.ClientIdentityResolved,
            Level = confirmation.Level.ToString(),
            Replayed = replayed,
        })).ToList();
}
