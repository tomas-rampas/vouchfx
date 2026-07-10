// Orchestration-error model for the vouchfx engine (§12.1 Environment error).
//
// When the orchestration layer cannot bring up the required topology — because an
// image cannot be pulled, a container fails its health gate, an endpoint cannot
// be discovered, or a resource cannot be provisioned — the failure is an
// Environment error, never a test Fail.  Conflating the two destroys trust in
// the tool (CLAUDE.md hard invariant).
//
// OrchestrationErrorInfo is the typed, structured payload that carries the
// diagnosis.  OrchestrationException wraps it so callers can catch a specific
// exception type and inspect the info without relying on message-string parsing.

namespace Vouchfx.Engine.Orchestration;

// ---------------------------------------------------------------------------
// OrchestrationErrorKind
// ---------------------------------------------------------------------------

/// <summary>
/// Discriminates the category of orchestration failure that prevented the
/// topology from starting (§12.1 Environment error).
/// </summary>
public enum OrchestrationErrorKind
{
    /// <summary>
    /// A container image could not be pulled from the registry — the image
    /// reference is invalid, the registry is unreachable, or the credentials
    /// were rejected.
    /// </summary>
    ImagePull,

    /// <summary>
    /// A resource reached a terminal unhealthy state or its health gate timed
    /// out before the container became ready.
    /// </summary>
    HealthGate,

    /// <summary>
    /// An endpoint URL or connection string could not be resolved from the
    /// running topology (e.g. <c>GetEndpoint</c> / <c>GetConnectionString</c>
    /// returned nothing useful).
    /// </summary>
    Discovery,

    /// <summary>
    /// A resource could not be provisioned for a reason that does not fit any
    /// of the more specific categories.
    /// </summary>
    Provision,
}

// ---------------------------------------------------------------------------
// OrchestrationErrorInfo
// ---------------------------------------------------------------------------

/// <summary>
/// Structured description of an orchestration failure (§12.1 Environment error).
/// </summary>
/// <param name="Kind">
/// The broad category of the failure.
/// </param>
/// <param name="ResourceName">
/// The name of the Aspire resource that failed (e.g. <c>"appdb"</c>,
/// <c>"web"</c>).
/// </param>
/// <param name="RegistryHost">
/// The registry hostname parsed from the image reference (e.g.
/// <c>"docker.io"</c>, <c>"registry.example.com:5000"</c>).  <see langword="null"/>
/// when no image reference is applicable to the failure.
/// </param>
/// <param name="AuthStatus">
/// A short token describing the authentication/connectivity outcome during an
/// image-pull attempt.  Possible values: <c>"unauthenticated"</c> (HTTP 401 or
/// equivalent), <c>"access-denied"</c> (HTTP 403 or equivalent),
/// <c>"rate-limited"</c> (matched on <c>toomanyrequests</c>, <c>pull rate limit</c>,
/// or <c>rate limit</c> with pull context — the way a registry's HTTP 429 response
/// surfaces in Docker/DCP messages; the bare substring <c>"429"</c> is NOT matched),
/// <c>"anonymous"</c> (pull attempted without credentials, non-auth failure).
/// <see langword="null"/> when the registry host was unreachable (DNS / network),
/// or when not applicable (non-ImagePull kinds).
/// </param>
/// <param name="Detail">
/// A trimmed, single-line summary of the underlying exception message, capped
/// to a reasonable length for display in event streams and logs.
/// </param>
public sealed record OrchestrationErrorInfo(
    OrchestrationErrorKind Kind,
    string ResourceName,
    string? RegistryHost,
    string? AuthStatus,
    string Detail);

// ---------------------------------------------------------------------------
// OrchestrationException
// ---------------------------------------------------------------------------

/// <summary>
/// Thrown by the orchestration layer when an infrastructure failure prevents
/// the test topology from starting (§12.1 Environment error).
/// </summary>
/// <remarks>
/// <para>
/// Callers MUST catch <see cref="OrchestrationException"/> separately from
/// other exceptions and map it to the <c>EnvironmentError</c> verdict — never
/// to <c>Fail</c>.  Conflating an infra failure with a product defect destroys
/// trust in the tool (CLAUDE.md hard invariant).
/// </para>
/// <para>
/// The structured <see cref="Info"/> payload carries the registry host, auth
/// status, and error kind so that renderers and the Healer agent can act on the
/// diagnosis without parsing the exception message.
/// </para>
/// </remarks>
public sealed class OrchestrationException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="OrchestrationException"/> with the
    /// structured error information and an optional inner exception.
    /// </summary>
    /// <param name="info">
    /// The structured description of the orchestration failure.
    /// </param>
    /// <param name="inner">
    /// The underlying exception from Aspire / DCP / Docker, if available.
    /// </param>
    public OrchestrationException(OrchestrationErrorInfo info, Exception? inner = null)
        : base(BuildMessage(info), inner)
    {
        Info = info;
    }

    /// <summary>
    /// Gets the structured description of the orchestration failure.
    /// </summary>
    public OrchestrationErrorInfo Info { get; }

    private static string BuildMessage(OrchestrationErrorInfo info) =>
        $"Orchestration {info.Kind} on resource '{info.ResourceName}': {info.Detail}";
}
