// Heuristic classifier that maps raw Aspire / DCP / Docker exceptions onto the
// strongly-typed OrchestrationErrorInfo model (§12.1 Environment error).
//
// Design constraints:
//   • Pure static class — no dependencies on Aspire or Docker at runtime, so
//     every classification path is exercisable in fast unit tests without Docker.
//   • Heuristics are case-insensitive substring matches against the exception
//     message.  The ordering of checks is intentional: auth-flavoured image-pull
//     signals are tested before generic pull signals so the AuthStatus is set
//     correctly when both keywords are present (e.g. "pull access denied: 401").
//   • ParseRegistryHost follows Docker's own rule for distinguishing an explicit
//     registry from an implicit docker.io prefix: the first slash-delimited
//     component is the registry host only when it contains a '.' or a ':' or
//     equals "localhost".  Everything else is a Docker Hub short image name.

namespace Platform.Engine.Orchestration;

/// <summary>
/// Pure static classifier that converts a raw infrastructure exception into a
/// structured <see cref="OrchestrationErrorInfo"/> without requiring Docker or
/// Aspire at classification time.
/// </summary>
/// <remarks>
/// All classification is driven by heuristic, case-insensitive substring
/// matching against <see cref="Exception.Message"/>.  The heuristics cover the
/// messages emitted by the DCP process, Docker daemon, and container-registry
/// HTTP responses that surface in Aspire's event notifications.  They are
/// intentionally conservative — an unrecognised message falls back to
/// <see cref="OrchestrationErrorKind.Provision"/> rather than guessing.
/// </remarks>
public static class OrchestrationErrorClassifier
{
    // Maximum length of the Detail field.  Keeps event-stream lines bounded
    // and avoids emitting multi-kilobyte stack traces into a JSON Lines record.
    private const int MaxDetailLength = 256;

    /// <summary>
    /// Classifies an infrastructure exception as a structured
    /// <see cref="OrchestrationErrorInfo"/>.
    /// </summary>
    /// <param name="exception">
    /// The raw exception thrown by Aspire, DCP, or Docker.
    /// </param>
    /// <param name="imageRef">
    /// The Docker image reference string that was used for the failing resource
    /// (e.g. <c>"registry.example.com/app:1.0"</c>).  Pass <see langword="null"/>
    /// when no image reference is known or applicable (e.g. a Postgres managed
    /// dependency health gate).  <see cref="ParseRegistryHost"/> is always called
    /// on this value so the registry host is present even for non-ImagePull kinds.
    /// </param>
    /// <param name="resourceName">
    /// The Aspire resource name that failed (e.g. <c>"appdb"</c>, <c>"web"</c>).
    /// </param>
    /// <returns>
    /// A fully populated <see cref="OrchestrationErrorInfo"/>.
    /// </returns>
    public static OrchestrationErrorInfo Classify(
        Exception exception,
        string? imageRef,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        var registryHost = ParseRegistryHost(imageRef);
        var detail = BuildDetail(message);

        var (kind, authStatus) = ClassifyMessage(message, imageRef);

        return new OrchestrationErrorInfo(
            Kind: kind,
            ResourceName: resourceName,
            RegistryHost: registryHost,
            AuthStatus: authStatus,
            Detail: detail);
    }

    /// <summary>
    /// Parses the registry hostname from a Docker image reference string.
    /// </summary>
    /// <param name="imageRef">
    /// A Docker image reference such as <c>"registry.example.com:5000/x/y:tag"</c>,
    /// <c>"traefik/whoami"</c>, or <c>"ubuntu:22.04"</c>.
    /// </param>
    /// <returns>
    /// <para>
    /// The registry host (and optional port) when the first slash-delimited
    /// component contains a <c>.</c> or a <c>:</c>, or equals <c>"localhost"</c>
    /// (Docker's own rule for explicit registry components).
    /// </para>
    /// <para>
    /// <c>"docker.io"</c> when the first component does not satisfy the above
    /// condition — i.e. the image is on the implicit Docker Hub registry.
    /// </para>
    /// <para>
    /// <see langword="null"/> when <paramref name="imageRef"/> is
    /// <see langword="null"/> or empty.
    /// </para>
    /// </returns>
    /// <remarks>
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>"nonexistent.invalid/nope:latest"</c> → <c>"nonexistent.invalid"</c></item>
    ///   <item><c>"registry.example.com:5000/x/y"</c> → <c>"registry.example.com:5000"</c></item>
    ///   <item><c>"traefik/whoami"</c> → <c>"docker.io"</c></item>
    ///   <item><c>"ubuntu:22.04"</c> → <c>"docker.io"</c></item>
    ///   <item><c>null</c> → <c>null</c></item>
    /// </list>
    /// </remarks>
    public static string? ParseRegistryHost(string? imageRef)
    {
        if (string.IsNullOrEmpty(imageRef))
        {
            return null;
        }

        // Strip any digest suffix (@sha256:...) before parsing to avoid the '@'
        // character confusing the slash-split logic.
        var withoutDigest = imageRef;
        var atIndex = imageRef.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            withoutDigest = imageRef[..atIndex];
        }

        var slashIndex = withoutDigest.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
        {
            // No slash at all — a bare image name like "ubuntu:22.04" or "ubuntu".
            // Docker Hub implicit registry.
            return "docker.io";
        }

        var firstComponent = withoutDigest[..slashIndex];

        // Docker's rule: the first component is an explicit registry host only
        // when it contains a '.' or a ':', or equals "localhost" (case-sensitive,
        // matching Docker's own logic).
        var isExplicitRegistry =
            firstComponent.Contains('.', StringComparison.Ordinal) ||
            firstComponent.Contains(':', StringComparison.Ordinal) ||
            string.Equals(firstComponent, "localhost", StringComparison.Ordinal);

        return isExplicitRegistry ? firstComponent : "docker.io";
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives the <see cref="OrchestrationErrorKind"/> and optional
    /// <c>authStatus</c> string from the raw exception message and the
    /// (optional) image reference.
    /// </summary>
    /// <param name="message">The raw exception message to classify.</param>
    /// <param name="imageRef">
    /// The Docker image reference that was used for the failing resource, or
    /// <see langword="null"/> when no image reference is applicable.  When
    /// non-null, auth keywords are treated as image-pull signals without
    /// requiring a co-occurring pull token in the message.
    /// </param>
    /// <remarks>
    /// Auth keywords (<c>unauthorized</c> / <c>401</c> / <c>authentication</c> /
    /// <c>unauthenticated</c>, and <c>denied</c> / <c>forbidden</c> / <c>403</c>) are
    /// only classified as <see cref="OrchestrationErrorKind.ImagePull"/> when the
    /// message <em>also</em> contains a pull or registry token
    /// (<c>pull</c> / <c>manifest</c> / <c>registry</c> / <c>image</c> / <c>no such image</c>),
    /// OR when <paramref name="imageRef"/> is non-null (a resource-level image context
    /// is already known).  This prevents messages such as
    /// <c>"database authentication failed during health probe"</c> from being
    /// misclassified as <see cref="OrchestrationErrorKind.ImagePull"/>.
    /// </remarks>
    private static (OrchestrationErrorKind Kind, string? AuthStatus) ClassifyMessage(
        string message, string? imageRef)
    {
        // A pull/registry/image token in the message, OR a non-null imageRef, provides
        // the image-pull context required to treat auth keywords as ImagePull signals.
        var hasPullContext =
            imageRef is not null ||
            ContainsAny(message, "pull", "manifest", "registry", "image", "no such image");

        // Auth-flavoured pull signals — only classified as ImagePull when an image-pull
        // context is present.  Without that context the message falls through to the
        // HealthGate / Discovery / Provision checks below, preventing misclassification
        // of e.g. "database authentication failed during health probe".
        if (hasPullContext && ContainsAny(message,
            "unauthorized", "401", "authentication", "unauthenticated"))
        {
            return (OrchestrationErrorKind.ImagePull, "unauthenticated");
        }

        if (hasPullContext && ContainsAny(message, "denied", "forbidden", "403"))
        {
            return (OrchestrationErrorKind.ImagePull, "access-denied");
        }

        // Generic pull / image-not-found signals (already imply an image-pull context).
        if (ContainsAny(message,
            "pull", "manifest", "not found", "no such image",
            "pull access", "toomanyrequests"))
        {
            return (OrchestrationErrorKind.ImagePull, "anonymous");
        }

        // Health-gate signals.
        if (ContainsAny(message,
            "unhealthy", "failed to start", "exited", "failedtostart",
            "runtimeunhealthy", "health"))
        {
            return (OrchestrationErrorKind.HealthGate, null);
        }

        // Timeout / cancellation — most commonly a health gate that ran out of
        // time before the container became ready.
        if (IsTimeoutOrCancellation(message))
        {
            return (OrchestrationErrorKind.HealthGate, null);
        }

        // Discovery signals.
        if (ContainsAny(message,
            "endpoint", "connection string", "resolve", "discovery", "getendpoint"))
        {
            return (OrchestrationErrorKind.Discovery, null);
        }

        // Unknown — fall back to Provision.
        return (OrchestrationErrorKind.Provision, null);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="message"/> contains
    /// timeout or cancellation keywords, indicating a health-gate failure
    /// (the gate timed out waiting for a resource to become ready).
    /// </summary>
    private static bool IsTimeoutOrCancellation(string message) =>
        ContainsAny(message, "timeout", "timed out", "operation was cancelled",
            "operation canceled", "cancellation");

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="message"/> contains
    /// any of the supplied <paramref name="keywords"/> (case-insensitive).
    /// </summary>
    private static bool ContainsAny(string message, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a trimmed, single-line detail string from the raw exception
    /// message, capped at <see cref="MaxDetailLength"/> characters.
    /// </summary>
    private static string BuildDetail(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no detail available)";
        }

        // Collapse newlines to a single space so the detail is always one line.
        var oneLine = message
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return oneLine.Length <= MaxDetailLength
            ? oneLine
            : string.Concat(oneLine.AsSpan(0, MaxDetailLength), "…");
    }
}
