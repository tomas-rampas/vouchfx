// Vouchfx.Engine.Orchestration — ElasticsearchScenarioIsolation (S04-A-01 follow-up chunk).
//
// Per-scenario isolation implementation for Elasticsearch: POSTs a match_all _delete_by_query
// against every non-system, non-hidden index between scenarios. No Elastic.Clients.Elasticsearch
// / NEST typed client is used — plain BCL HttpClient only, mirroring
// Vouchfx.Steps.CacheAssert.Elasticsearch's CacheAssertElasticsearchProvider (the engine has no
// pinned Elasticsearch client package, and none is added here — §19).
//
// Design notes:
//   • _delete_by_query, not index DELETE: preserves index mappings/settings — a running SUT
//     typically creates its mappings ONCE at startup, so deleting the index would silently
//     recreate it mapping-less on the SUT's next write, changing behaviour under test (the same
//     trap RespawnRelationalIsolation and MongoScenarioIsolation avoid for their stores).
//     Elasticsearch 8's action.destructive_requires_name default would reject a wildcard index
//     DELETE anyway, so index deletion was never a viable reset strategy here.
//   • The "*" wildcard target, combined with expand_wildcards=open (the endpoint's default),
//     excludes dot-prefixed system/hidden indices BY CONSTRUCTION — no explicit exclusion list
//     is needed.
//   • refresh=true makes the deletions visible to the next scenario's queries immediately —
//     Elasticsearch is near-real-time (a query issued before the next automatic refresh could
//     otherwise still see the "deleted" documents).
//   • conflicts=proceed tolerates documents whose version was bumped between the query phase and
//     the delete phase of _delete_by_query's internal scroll — without it, a concurrent write
//     mid-reset would abort the whole operation.
//   • An empty cluster (no indices yet) returns 200 under the endpoint's allow_no_indices
//     default — the very first scenario's EndScenarioAsync is therefore never a special case.
//   • Auth/URL handling mirrors CacheAssertElasticsearchProvider: any URL userinfo is extracted
//     once and used ONLY to build a Basic Authorization header; the base URL retained and reused
//     for every request is rebuilt as scheme://host:port, so no credential or the raw endpoint
//     URL is ever retained beyond that extraction (§17 — never a URL, connection string, or
//     response body in an error Detail; a non-2xx Detail carries only the HTTP status code).
//   • One lazily created HttpClient per topology (~30 s timeout), cached for the lifetime of the
//     topology and disposed in DisposeAsync. A cached HttpClient is correct here — this is engine
//     code running in the Default ALC; the "new HttpClient per invocation" rule in the provider
//     above applies only to CSX emitted into the collectible ALC (§5), not to this class.
//   • Every driver/HTTP failure is wrapped via ScenarioIsolationErrors.Wrap so the ResourceName
//     on the resulting OrchestrationException is always the LOGICAL DEPENDENCY NAME (§12.1).

using System.Net.Http.Headers;
using System.Text;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Per-scenario isolation implementation that deletes all documents from every non-system index
/// of an Elasticsearch dependency via <c>_delete_by_query</c> between scenarios, without deleting
/// indices or their mappings (S04-A-01 follow-up chunk).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy initialisation:</strong> the underlying <see cref="HttpClient"/> (and the
/// base URL / auth header derived from the endpoint URL) are created on the first call to
/// <see cref="EndScenarioAsync"/> — not <see cref="BeginScenarioAsync"/> — and reused for the
/// lifetime of the topology. Construction never performs I/O or URL parsing: a malformed
/// endpoint URL is accepted at construction and only fails, as a wrapped Provision error, at the
/// first <see cref="EndScenarioAsync"/>.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any HTTP failure (including a non-2xx response) is wrapped
/// in <see cref="OrchestrationException"/> with kind <see cref="OrchestrationErrorKind.Provision"/>
/// and <see cref="OrchestrationErrorInfo.ResourceName"/> set to the logical dependency name
/// supplied at construction, so the runner classifies it as <c>EnvironmentError</c> — never as
/// <c>Fail</c> — and names the offending dependency. A non-2xx Detail carries only the HTTP
/// status code — never the response body or the endpoint URL's userinfo (§17).
/// </para>
/// <para>
/// <strong>Disposal:</strong> call <see cref="DisposeAsync"/> once the topology is torn down to
/// release the underlying <see cref="HttpClient"/>.
/// </para>
/// </remarks>
public sealed class ElasticsearchScenarioIsolation : IScenarioIsolation, IAsyncDisposable
{
    private const string StoreToken = "elasticsearch";

    /// <summary>Request timeout for the delete-by-query call — generous but bounded.</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private readonly string _dependencyName;
    private readonly string _endpointUrl;

    // Guard: the client and the derived base URL / auth header are created once, lazily, on
    // first reset.
    private HttpClient? _http;
    private string? _baseUrl;
    private bool _disposed;

    /// <summary>
    /// The logical dependency name, exposed for tests (InternalsVisibleTo) so factory dispatch
    /// and composite ordering can be asserted per dependency.
    /// </summary>
    internal string DependencyName => _dependencyName;

    /// <summary>
    /// Initialises a new <see cref="ElasticsearchScenarioIsolation"/> for the given Elasticsearch
    /// dependency. Never opens a connection — construction is pure validation.
    /// </summary>
    /// <param name="dependencyName">
    /// The logical dependency name declared in <c>environment.dependencies</c> (used to name the
    /// dependency in any reset-failure observation). Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="endpointUrl">
    /// The Elasticsearch HTTP endpoint URL for the managed dependency (obtained from
    /// <see cref="SuiteTopology.DiscoveredServices"/> after <see cref="SuiteTopology.StartAsync"/>
    /// completes). Must not be <see langword="null"/> or empty.
    /// </param>
    public ElasticsearchScenarioIsolation(string dependencyName, string endpointUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyName);
        ArgumentException.ThrowIfNullOrEmpty(endpointUrl);

        _dependencyName = dependencyName;
        _endpointUrl = endpointUrl;
    }

    // ── IScenarioIsolation ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// This is a no-op.  A freshly-started Aspire dependency yields a clean cluster, so no
    /// pre-scenario reset is required; the HTTP client is created lazily, and state reset
    /// performed, in <see cref="EndScenarioAsync"/>.
    /// </remarks>
    public Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Builds the HTTP client on first use, then POSTs a match_all <c>_delete_by_query</c>
    /// against every non-system index. Any failure — including a non-2xx response — is wrapped
    /// in <see cref="OrchestrationException"/> (§12.1: Environment error, never Fail).
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureClient();
        await ResetAsync(ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the underlying <see cref="HttpClient"/>. Safe to call multiple times.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _http?.Dispose();
        _http = null;

        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the endpoint URL, extracts any userinfo into a cached Basic <c>Authorization</c>
    /// header, rebuilds the base URL as <c>scheme://host:port</c> (never retaining credentials),
    /// and creates the shared <see cref="HttpClient"/> once. A no-op once the client exists.
    /// </summary>
    private void EnsureClient()
    {
        if (_http is not null)
        {
            return;
        }

        Uri parsed;
        try
        {
            parsed = new Uri(_endpointUrl);
        }
        catch (Exception)
        {
            // Deliberately NOT the parse exception itself: a malformed-URL message can echo
            // fragments of the URL, including userinfo (§17 discipline).
            throw ScenarioIsolationErrors.Wrap(
                StoreToken,
                "init",
                _dependencyName,
                new InvalidOperationException("endpoint URL could not be parsed."));
        }

        string? authHeader = null;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            var parts = parsed.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(parts[0]);
            var pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + pass));
        }

        var http = new HttpClient { Timeout = HttpTimeout };
        if (authHeader is not null)
        {
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", authHeader);
        }

        // Base URL rebuilt WITHOUT credentials — the raw endpoint URL (which may carry
        // userinfo) is never retained beyond this method (§17).
        _baseUrl = parsed.Scheme + "://" + parsed.Host + ":" + parsed.Port;
        _http = http;
    }

    /// <summary>
    /// POSTs <c>{base}/*/_delete_by_query?conflicts=proceed&amp;refresh=true&amp;expand_wildcards=open</c>
    /// with a match_all body, deleting every document from every non-system, non-hidden index
    /// while leaving every index's mapping and settings untouched.
    /// </summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        try
        {
            var url = _baseUrl + "/*/_delete_by_query?conflicts=proceed&refresh=true&expand_wildcards=open";
            using var content = new StringContent(
                "{\"query\":{\"match_all\":{}}}", Encoding.UTF8, "application/json");

            using var response = await _http!.PostAsync(url, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // §17: the Detail carries the HTTP status code only — never the response body
                // (which may echo index contents) nor the endpoint URL.
                throw ScenarioIsolationErrors.Wrap(
                    StoreToken,
                    "reset",
                    _dependencyName,
                    new InvalidOperationException(
                        $"delete_by_query returned HTTP {(int)response.StatusCode}."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation propagates raw — it is neither a product Fail nor an
            // EnvironmentError (§12.1); see RespawnRelationalIsolation.EnsureConnectionAsync for
            // the identical rationale.
            throw;
        }
        catch (OrchestrationException)
        {
            // Already classified immediately above — propagate as-is rather than double-wrapping.
            throw;
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreToken, "reset", _dependencyName, ex);
        }
    }
}
