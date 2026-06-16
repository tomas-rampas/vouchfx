// Platform.Engine.Telemetry — TelemetryTransportOptions (S12-G-01, Phase A).
//
// Resolves the HTTP-transport configuration from the environment.  The transport is
// INERT unless BOTH an endpoint and a token are present AND the endpoint is a valid
// absolute http/https URI — exactly mirroring the consent gate's "off by default"
// posture: a missing or malformed configuration silently leaves the engine in
// local-outbox-only mode (no network call is ever made).
//
// §17 token handling: the token is read here and carried ONLY on the request
// Authorization header by HttpOutboxClient.  It is never logged, echoed, or placed in
// an exception message anywhere in the transport.
//
// Optional outbox caps (bytes / age-days / lines) are read from the environment too, so
// an operator can tune the local outbox ceiling without a code change; each falls back
// to a conservative default when unset or unparseable.

using System.Globalization;

namespace Platform.Engine.Telemetry;

/// <summary>
/// The resolved HTTP-transport + outbox-cap configuration for the telemetry drain
/// (S12-G-01).  Built from environment variables; inert unless fully configured.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default:</strong> <see cref="IsConfigured"/> is <see langword="true"/>
/// only when an endpoint AND a token are both present and the endpoint is a valid
/// absolute <c>http</c>/<c>https</c> URI with neither a query nor a fragment.  Any other
/// state (either missing, a malformed endpoint, or a base carrying a query/fragment that
/// the relative-path resolution would silently drop) leaves the engine in
/// local-outbox-only mode — the drain decorator then wraps a no-op client so the outbox
/// CAP still runs, but no network call is ever made.
/// </para>
/// <para>
/// <strong>Privacy / §17:</strong> the token is data the operator supplies, carried ONLY
/// on the request <c>Authorization</c> header by <see cref="HttpOutboxClient"/>.  It is
/// never logged, echoed in a diagnostic, or placed in any exception message.
/// </para>
/// </remarks>
public sealed class TelemetryTransportOptions
{
    /// <summary>The environment variable naming the telemetry ingest endpoint base URI.</summary>
    public const string EndpointEnvVar = "VOUCHFX_TELEMETRY_ENDPOINT";

    /// <summary>The environment variable carrying the bearer token (header-only; never logged).</summary>
    public const string TokenEnvVar = "VOUCHFX_TELEMETRY_TOKEN";

    /// <summary>The environment variable overriding the outbox maximum size in bytes.</summary>
    public const string MaxBytesEnvVar = "VOUCHFX_TELEMETRY_OUTBOX_MAX_BYTES";

    /// <summary>The environment variable overriding the outbox maximum line age in days.</summary>
    public const string MaxAgeDaysEnvVar = "VOUCHFX_TELEMETRY_OUTBOX_MAX_AGE_DAYS";

    /// <summary>The environment variable overriding the outbox maximum line count.</summary>
    public const string MaxLinesEnvVar = "VOUCHFX_TELEMETRY_OUTBOX_MAX_LINES";

    /// <summary>The default outbox byte ceiling (5 MB) when the override is unset/invalid.</summary>
    public const long DefaultMaxBytes = 5L * 1024 * 1024;

    /// <summary>The default outbox line-age ceiling (30 days) when the override is unset/invalid.</summary>
    public const int DefaultMaxAgeDays = 30;

    /// <summary>The default outbox line-count ceiling (10 000) when the override is unset/invalid.</summary>
    public const int DefaultMaxLines = 10_000;

    private TelemetryTransportOptions(
        Uri? endpoint,
        string? token,
        long maxBytes,
        int maxAgeDays,
        int maxLines)
    {
        Endpoint = endpoint;
        Token = token;
        MaxBytes = maxBytes;
        MaxAgeDays = maxAgeDays;
        MaxLines = maxLines;
    }

    /// <summary>
    /// The validated absolute ingest endpoint base URI, or <see langword="null"/> when
    /// unset/malformed.  When non-null it is always absolute, <c>http</c>/<c>https</c>, and
    /// carries neither a query nor a fragment (those are rejected — see <c>ParseEndpoint</c>).
    /// </summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// The bearer token, or <see langword="null"/> when unset.  Header-only; never logged.
    /// </summary>
    public string? Token { get; }

    /// <summary>The resolved outbox byte ceiling (oldest-first eviction beyond this).</summary>
    public long MaxBytes { get; }

    /// <summary>The resolved outbox line-age ceiling in days (oldest-first eviction beyond this).</summary>
    public int MaxAgeDays { get; }

    /// <summary>The resolved outbox line-count ceiling (oldest-first eviction beyond this).</summary>
    public int MaxLines { get; }

    /// <summary>
    /// Whether the HTTP transport is fully configured: <see cref="Endpoint"/> is non-null
    /// (which, by construction in <c>ParseEndpoint</c>, means it was already validated as an
    /// absolute <c>http</c>/<c>https</c> URI with no query/fragment — a malformed, non-web,
    /// or query/fragment-bearing endpoint parses to <see langword="null"/>) AND
    /// <see cref="Token"/> is non-empty.  When <see langword="false"/>, no network call is
    /// ever made (local-outbox-only mode).
    /// </summary>
    public bool IsConfigured => Endpoint is not null && !string.IsNullOrEmpty(Token);

    /// <summary>
    /// Resolves the options from the process environment.  Never throws — a malformed
    /// endpoint or an unparseable cap simply falls back to "unconfigured" / the default.
    /// </summary>
    /// <returns>The resolved options (possibly unconfigured).</returns>
    public static TelemetryTransportOptions FromEnvironment() =>
        FromValues(
            Environment.GetEnvironmentVariable(EndpointEnvVar),
            Environment.GetEnvironmentVariable(TokenEnvVar),
            Environment.GetEnvironmentVariable(MaxBytesEnvVar),
            Environment.GetEnvironmentVariable(MaxAgeDaysEnvVar),
            Environment.GetEnvironmentVariable(MaxLinesEnvVar));

    /// <summary>
    /// Resolves the options from explicit string values (the seam the environment reader
    /// delegates to, exposed so tests drive every parse branch without mutating real
    /// process environment variables).  Never throws.
    /// </summary>
    /// <param name="endpoint">The raw endpoint value (or <see langword="null"/>).</param>
    /// <param name="token">The raw token value (or <see langword="null"/>).</param>
    /// <param name="maxBytes">The raw max-bytes override (or <see langword="null"/>).</param>
    /// <param name="maxAgeDays">The raw max-age-days override (or <see langword="null"/>).</param>
    /// <param name="maxLines">The raw max-lines override (or <see langword="null"/>).</param>
    /// <returns>The resolved options.</returns>
    public static TelemetryTransportOptions FromValues(
        string? endpoint,
        string? token,
        string? maxBytes = null,
        string? maxAgeDays = null,
        string? maxLines = null)
    {
        var parsedEndpoint = ParseEndpoint(endpoint);
        var parsedToken = string.IsNullOrWhiteSpace(token) ? null : token;

        return new TelemetryTransportOptions(
            parsedEndpoint,
            parsedToken,
            ParsePositiveLong(maxBytes, DefaultMaxBytes),
            ParsePositiveInt(maxAgeDays, DefaultMaxAgeDays),
            ParsePositiveInt(maxLines, DefaultMaxLines));
    }

    // An endpoint is accepted ONLY when it is an absolute http/https URI carrying NEITHER a
    // query NOR a fragment; anything else (empty, relative, a non-web scheme such as file://,
    // or a base with a query/fragment) is treated as "unset", keeping the transport inert
    // rather than attempting a malformed request.  A query/fragment on the BASE is rejected
    // (fail-closed) because the transport resolves fixed relative paths against it
    // (HttpOutboxClient.Combine) which would silently DROP the base query/fragment — an
    // operator who set one would get a different request than intended, so we refuse it.
    private static Uri? ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        // Fail closed: a base query/fragment would be silently discarded by the relative-path
        // resolution downstream, so reject it rather than send to an unintended URL.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri;
    }

    // Parse a strictly-positive long, falling back to the default on null / non-numeric /
    // non-positive input.  A zero or negative cap is meaningless (it would evict everything)
    // so it is rejected in favour of the conservative default.
    private static long ParsePositiveLong(string? raw, long fallback)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        return fallback;
    }

    private static int ParsePositiveInt(string? raw, int fallback)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        return fallback;
    }
}
