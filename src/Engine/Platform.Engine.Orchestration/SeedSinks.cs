// Platform.Engine.Orchestration — seed deferred seams (S05-A-02).
//
// The broker-publish and document-fixture seed forms are WIRED-BUT-DEFERRED in
// M2: the seed pipeline reads each referenced fixture, content-hashes it (so the
// reproducibility envelope, S05-B-03, can record it), and records the publish /
// document INTENT through one of these sinks — but it does NOT perform the actual
// broker publish or document-store write yet.  Those land with their providers in
// LATER sprints:
//   • Broker publish  → Kafka provider, Sprint 6.
//   • Document write   → document-store provider, later.
//
// The sinks are the Sprint-6 (and later) plug-in points.  Until those providers
// land, the engine uses the recording defaults below so the pipeline is provably
// "wired" (a test asserts the recorded topic + payload content hash) without
// faking a publish or a write.

namespace Platform.Engine.Orchestration;

// ---------------------------------------------------------------------------
// Recorded-intent records (strongly-typed; never Dictionary<string,object>).
// ---------------------------------------------------------------------------

/// <summary>
/// A broker warm-up publish recorded by the seed pipeline (S05-A-02), without the
/// actual publish being performed.
/// </summary>
/// <param name="DependencyName">
/// The logical broker dependency the warm-up message targets.
/// </param>
/// <param name="Topic">
/// The topic the warm-up message would be published to.
/// </param>
/// <param name="PayloadPath">
/// The resolved absolute path of the payload fixture file (already verified to
/// exist when the record is created).
/// </param>
/// <param name="PayloadContentHash">
/// The lower-case hex SHA-256 of the payload fixture's bytes — the same hash the
/// reproducibility envelope (S05-B-03) records (docs/02 §3.2.2).
/// </param>
public sealed record RecordedPublish(
    string DependencyName,
    string Topic,
    string PayloadPath,
    string PayloadContentHash);

/// <summary>
/// A document fixture recorded by the seed pipeline (S05-A-02), without the actual
/// document-store write being performed.
/// </summary>
/// <param name="DependencyName">
/// The logical document-store dependency the fixture targets.
/// </param>
/// <param name="Collection">
/// The optional target collection / container name, or <see langword="null"/>.
/// </param>
/// <param name="DocumentPath">
/// The resolved absolute path of the document fixture file (already verified to
/// exist when the record is created).
/// </param>
/// <param name="DocumentContentHash">
/// The lower-case hex SHA-256 of the document fixture's bytes — the same hash the
/// reproducibility envelope (S05-B-03) records (docs/02 §3.2.2).
/// </param>
public sealed record RecordedDocument(
    string DependencyName,
    string? Collection,
    string DocumentPath,
    string DocumentContentHash);

// ---------------------------------------------------------------------------
// Deferred seams (Sprint-6 / later plug-in points).
// ---------------------------------------------------------------------------

/// <summary>
/// The deferred seam for broker warm-up publishes (S05-A-02).  The default
/// implementation records the intent; the Kafka provider (Sprint 6) will supply an
/// implementation that performs the actual publish.
/// </summary>
public interface IBrokerWarmupSink
{
    /// <summary>
    /// Records (or, in a later provider, performs) one broker warm-up publish.
    /// </summary>
    /// <param name="publish">The recorded publish intent (topic + payload hash).</param>
    /// <param name="ct">Propagated to any future async I/O.</param>
    ValueTask PublishAsync(RecordedPublish publish, CancellationToken ct);
}

/// <summary>
/// The deferred seam for document fixtures (S05-A-02).  The default implementation
/// records the intent; a document-store provider (later) will supply an
/// implementation that performs the actual write.
/// </summary>
public interface IDocumentSeedSink
{
    /// <summary>
    /// Records (or, in a later provider, performs) one document-fixture write.
    /// </summary>
    /// <param name="document">The recorded document intent (collection + content hash).</param>
    /// <param name="ct">Propagated to any future async I/O.</param>
    ValueTask WriteAsync(RecordedDocument document, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Recording default implementations (M2 wired-but-deferred).
// ---------------------------------------------------------------------------

/// <summary>
/// The default <see cref="IBrokerWarmupSink"/> for M2: records each warm-up publish
/// intent (topic + payload content hash) without publishing.  Proves the pipeline
/// is wired; the Kafka provider (Sprint 6) replaces it.
/// </summary>
public sealed class RecordingBrokerWarmupSink : IBrokerWarmupSink
{
    private readonly List<RecordedPublish> _recorded = new();

    /// <summary>
    /// Gets the warm-up publishes recorded so far, in the order they were processed.
    /// </summary>
    public IReadOnlyList<RecordedPublish> Recorded => _recorded;

    /// <inheritdoc />
    public ValueTask PublishAsync(RecordedPublish publish, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ct.ThrowIfCancellationRequested();
        _recorded.Add(publish);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The default <see cref="IDocumentSeedSink"/> for M2: records each document-fixture
/// intent (collection + content hash) without writing to a store.  Proves the
/// pipeline is wired; a document-store provider (later) replaces it.
/// </summary>
public sealed class RecordingDocumentSeedSink : IDocumentSeedSink
{
    private readonly List<RecordedDocument> _recorded = new();

    /// <summary>
    /// Gets the document fixtures recorded so far, in the order they were processed.
    /// </summary>
    public IReadOnlyList<RecordedDocument> Recorded => _recorded;

    /// <inheritdoc />
    public ValueTask WriteAsync(RecordedDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ct.ThrowIfCancellationRequested();
        _recorded.Add(document);
        return ValueTask.CompletedTask;
    }
}
