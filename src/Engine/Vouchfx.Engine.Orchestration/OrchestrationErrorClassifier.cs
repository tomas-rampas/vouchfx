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

namespace Vouchfx.Engine.Orchestration;

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
    /// <param name="containerNeverCreated">
    /// <see langword="true"/> when the caller has independent, non-textual evidence that the
    /// container runtime never created a container instance for this resource at all (e.g.
    /// DCP's resource snapshot carries no <c>container.id</c>) — as opposed to a container
    /// that WAS created and then crashed or never passed its own health check.
    /// <para>
    /// This disambiguates a specific ambiguous message shape: Aspire's health-gate wrapper
    /// exception ("Stopped waiting for resource 'X' to become healthy because it failed to
    /// start.") is IDENTICAL for both a bad-image pull failure and a genuine health-check
    /// failure, and DCP surfaces no pull-specific text anywhere the classifier can otherwise
    /// see (verified empirically — see <c>ResourceCreationEvidence</c> in
    /// <c>Vouchfx.Engine.Orchestration</c>). Only used when <paramref name="imageRef"/> is
    /// also non-null (a resource with no image reference can never be an image-pull failure);
    /// defaults to <see langword="false"/> so every existing call site is unaffected until it
    /// opts in.
    /// </para>
    /// </param>
    /// <returns>
    /// A fully populated <see cref="OrchestrationErrorInfo"/>.
    /// </returns>
    public static OrchestrationErrorInfo Classify(
        Exception exception,
        string? imageRef,
        string resourceName,
        bool containerNeverCreated = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        var registryHost = ParseRegistryHost(imageRef);
        var detail = Annotate(BuildDetail(message), message, exception);

        var (kind, authStatus) = ClassifyMessage(message, imageRef, containerNeverCreated);

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
    ///
    /// Rate-limit keywords (<c>toomanyrequests</c> / <c>pull rate limit</c>) are tested
    /// <em>before</em> the generic-anonymous-pull branch so that the Docker Hub cold-runner
    /// failure (§10 named risk) is surfaced with authStatus <c>"rate-limited"</c> rather than
    /// the less-specific <c>"anonymous"</c>.  The bare token <c>rate limit</c> is also
    /// matched, but only when an image-pull context is already established (to avoid
    /// false-positives on non-pull messages that happen to contain the phrase).
    /// Note: the engine matches on the string tokens above — <c>HTTP 429</c> is the
    /// conceptual HTTP cause, but the bare substring <c>"429"</c> is NOT matched
    /// (a 3-digit number is too broad and can collide with sizes, ports, or IDs).
    ///
    /// Registry-connectivity keywords (<c>no such host</c> / <c>connection refused</c> /
    /// <c>no route to host</c> / <c>network is unreachable</c> / <c>name resolution</c> /
    /// <c>i/o timeout</c> / <c>server misbehaving</c>) are tested <em>after</em> the
    /// auth branches but <em>before</em> the Discovery branch, and only when an
    /// image-pull context is present.  Without that context (e.g. a DB host lookup
    /// failure) the message falls through to Discovery / Provision as expected.
    /// When matched, <c>AuthStatus</c> is <see langword="null"/> — no auth was
    /// attempted; the registry host itself was unreachable.
    ///
    /// The <paramref name="containerNeverCreated"/> structural signal is checked AFTER every
    /// message-based pull heuristic above (so a message that DOES carry specific pull-failure
    /// text — e.g. "pull access denied: 401" — still gets its more precise AuthStatus) but
    /// BEFORE the Health-gate check (so the ambiguous generic health-gate wrapper message does
    /// not shadow a bad-image failure just because DCP surfaced no pull-specific text for it).
    /// </remarks>
    private static (OrchestrationErrorKind Kind, string? AuthStatus) ClassifyMessage(
        string message, string? imageRef, bool containerNeverCreated)
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

        // Rate-limit signals — placed before the generic-anonymous-pull branch so
        // Docker Hub cold-runner rate-limiting is reported as "rate-limited", not
        // "anonymous".
        //
        // "toomanyrequests" and "pull rate limit" are inherently pull-specific tokens
        // and are matched unconditionally.  The bare "rate limit" phrase is broader and
        // is only matched when a pull context is already established (hasPullContext),
        // preventing false-positives on non-pull messages such as API throttling errors.
        //
        // Note: the engine does NOT match on the bare substring "429" — that 3-digit
        // number is too loose and can collide with sizes, ports, or IDs.  These tokens
        // cover how a registry's HTTP 429 response surfaces in Docker/DCP messages.
        if (ContainsAny(message, "toomanyrequests", "pull rate limit") ||
            (hasPullContext && ContainsAny(message, "rate limit")))
        {
            return (OrchestrationErrorKind.ImagePull, "rate-limited");
        }

        // Registry-connectivity / DNS signals — placed BEFORE the generic-pull branch so
        // that a message containing both a pull token and a connectivity failure signal
        // (e.g. "failed to pull image: connection refused to registry host") is
        // classified as a connectivity failure (AuthStatus=null) rather than a generic
        // anonymous pull.
        //
        // Only classified as ImagePull when a pull context is established (imageRef non-null
        // OR a pull/image token already appeared in the message).  This ensures that e.g.
        // "lookup mydb.internal: no such host" (a service-discovery DNS failure with no
        // imageRef and no pull token) still falls through to Discovery / Provision.
        //
        // AuthStatus is null because no auth was attempted — the registry host itself
        // was unreachable.
        if (hasPullContext && ContainsAny(message,
            "no such host", "name resolution", "connection refused",
            "no route to host", "network is unreachable",
            "i/o timeout", "server misbehaving"))
        {
            return (OrchestrationErrorKind.ImagePull, null);
        }

        // Generic pull / image-not-found signals (already imply an image-pull context).
        if (ContainsAny(message,
            "pull", "manifest", "not found", "no such image", "pull access"))
        {
            return (OrchestrationErrorKind.ImagePull, "anonymous");
        }

        // Structural signal (independent of message wording) — see the parameter doc comment
        // above and ResourceCreationEvidence for the empirical basis. A resource that never had
        // a container created for it, but DOES carry a known image reference, cannot be a
        // genuine health-check failure (the app can never even have run); classify it as
        // ImagePull rather than falling into the ambiguous Health-gate bucket below.
        if (containerNeverCreated && imageRef is not null)
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
            : string.Concat(oneLine.AsSpan(0, MaxDetailLength), "...");
    }

    /// <summary>
    /// Appends the #420 known-fault note and the DCP flight-recorder capture summary to
    /// <paramref name="detail"/>, when either applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Enrichment, never reclassification.</strong> Neither the
    /// <see cref="OrchestrationErrorKind"/> nor the <c>AuthStatus</c> moves: #420's throw already
    /// classifies as <see cref="OrchestrationErrorKind.Provision"/>, which is the correct kind for
    /// an orchestrator that could not provision a host port, and a diagnostic that changed the
    /// diagnosis would be a worse trade than one that does not exist. Only the human-readable
    /// <c>Detail</c> gains content, and <c>Detail</c> is content-only on the §14 wire — the
    /// property, its CLR type and its JSON name are untouched, so the frozen event contract does
    /// not move.
    /// </para>
    /// <para>
    /// <strong>Why the result may exceed <see cref="MaxDetailLength"/>.</strong> That bound exists
    /// to keep an UNBOUNDED input — a multi-kilobyte stack trace arriving as an exception message
    /// — out of a JSON Lines record, and it still does exactly that: the message is truncated to
    /// the same 256 characters at the same point, and the pins that assert so are unaffected
    /// because they classify messages carrying neither signature nor annotation. What follows the
    /// message is engine-authored and separately bounded by
    /// <see cref="DcpCapture.MaxAnnexLength"/>. Truncating the message to make room for a pointer
    /// to the evidence would spend the evidence to pay for the pointer.
    /// </para>
    /// <para>
    /// Both inputs are read from the exception the caller already has: the signature from its
    /// message (the same case-insensitive substring rule as every other heuristic here), and the
    /// capture summary from <see cref="Exception.Data"/>, where
    /// <c>HeadlessTopology.StartAsync</c> attached it on the way out. An exception carrying
    /// neither is returned untouched, which is every classification this repository made before
    /// the recorder existed.
    /// </para>
    /// </remarks>
    private static string Annotate(string detail, string message, Exception exception)
    {
        var annex = DcpCapture.BuildAnnex(
            message, exception, System.OperatingSystem.IsWindows());
        return annex.Length == 0 ? detail : detail + " | " + annex;
    }
}
