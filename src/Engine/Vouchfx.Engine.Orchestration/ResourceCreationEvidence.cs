// Vouchfx.Engine.Orchestration — structural evidence gathering for the Environment-error
// classifier (§12.1), added to make bad-image failures classify correctly under the
// generic health-gate wrapper message shape.
//
// Background: Aspire's ResourceNotificationService.WaitForResourceHealthyAsync throws the
// SAME generic DistributedApplicationException message — "Stopped waiting for resource 'X'
// to become healthy because it failed to start." — whether the underlying cause is (a) the
// image could never be pulled (the container was never created at all) or (b) the container
// WAS created and started, then crashed or never passed its own health check. Message-only
// classification (OrchestrationErrorClassifier) cannot distinguish these two cases from that
// wrapper text alone.
//
// Empirically verified (live Docker, no CI access needed) against a resource with a
// non-existent registry host:
//   • ResourceNotificationService.TryGetCurrentState returns a snapshot whose
//     ResourceStateSnapshot.Text is "FailedToStart" and whose "container.id" property is an
//     EMPTY string — DCP never assigned a container id because the container runtime never
//     created a container instance.
//   • ResourceLoggerService.GetAllAsync yields ZERO log lines — DCP does not surface the
//     underlying docker/pull error text as a resource log for this failure shape.
// By contrast, a container that DOES get created — verified with both a fast-exiting image
// (State.Text="Exited") and a long-running-but-never-healthy image (State.Text="Running") —
// always has a non-empty "container.id" property (a real Docker container id).
//
// This makes "container.id absent/empty" a reliable STRUCTURAL (non-textual) signal that the
// container was never created, independent of exact daemon/DCP message wording across
// versions — exactly the kind of evidence OrchestrationErrorClassifier.Classify's new
// containerNeverCreated parameter is designed to consume.

using Aspire.Hosting;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Gathers structural (non-textual) evidence from a running <see cref="DistributedApplication"/>
/// about whether a resource's container was ever actually created by the container runtime —
/// used to disambiguate the Environment-error classifier's <c>HealthGate</c> vs
/// <c>ImagePull</c> decision when the wrapping exception message is the generic health-gate
/// wording shared by both failure shapes (see file header for the empirical basis).
/// </summary>
internal static class ResourceCreationEvidence
{
    /// <summary>
    /// Property name DCP annotates on every container-backed resource snapshot once (and only
    /// once) the container runtime has actually created a container instance.
    /// </summary>
    private const string ContainerIdPropertyName = "container.id";

    /// <summary>
    /// Returns <see langword="true"/> when there is no evidence that the container runtime
    /// ever created a container instance for <paramref name="resourceName"/> — i.e. the
    /// resource's <c>container.id</c> property is absent or empty, or no resource snapshot is
    /// available at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers pass the result as <c>containerNeverCreated</c> to
    /// <see cref="OrchestrationErrorClassifier.Classify"/>.  The classifier only acts on this
    /// signal when an image reference is also known (<c>imageRef is not null</c>) — a
    /// project-based resource has no <c>container.id</c> property either, but also never
    /// carries an <c>imageRef</c>, so this method returning <see langword="true"/> for a
    /// project resource is harmless: the classifier's <c>imageRef</c> gate excludes it.
    /// </para>
    /// <para>
    /// Never throws: any failure reading the resource-notification snapshot is treated as
    /// "no evidence available" (<see langword="true"/>), which is the conservative default
    /// already used by the "no snapshot at all" branch below.
    /// </para>
    /// </remarks>
    public static bool ContainerNeverCreated(DistributedApplication app, string resourceName)
    {
        if (!app.ResourceNotifications.TryGetCurrentState(resourceName, out var resourceEvent))
        {
            // No recorded snapshot at all. A resource that reached Running/Exited always has
            // one by the time WaitForResourceHealthyAsync throws, so treat "none recorded" the
            // same as "never created" — the safer of the two possible assumptions here.
            return true;
        }

        foreach (var property in resourceEvent.Snapshot.Properties)
        {
            if (string.Equals(property.Name, ContainerIdPropertyName, StringComparison.Ordinal))
            {
                return property.Value is not string containerId || string.IsNullOrEmpty(containerId);
            }
        }

        // No container.id property at all — not a container-backed resource (e.g. a
        // dotnet-project service). Not evidence of a pull failure; let the classifier fall
        // through to its ordinary message heuristics.
        return false;
    }
}
