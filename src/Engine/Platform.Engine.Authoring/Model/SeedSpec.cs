// Platform.Engine.Authoring — SeedSpec (S05-A-01).
//
// Strongly-typed records for the optional `environment.seed` block of a
// .e2e.yaml file (docs/02 §3.2.2).  The seed block declares reference data that
// the engine applies after the topology is healthy and before the first step
// runs, inside the same health-gated lifecycle (so a failed seed surfaces as an
// Environment error, never a misattributed assertion failure — §12.1).
//
// Grammar (A-01 + A-02):
//   seed:
//     orders-db:                       # postgres → SQL (A-01)
//       sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
//     events:                          # broker → warm-up publish (A-02; deferred seam)
//       publish:
//         - topic: catalog.snapshot
//           payload: { from: "fixtures/catalog.json" }
//     catalog-store:                   # document store → document fixture (A-02; deferred seam)
//       documents:
//         - collection: products       # optional target name
//           from: "fixtures/products.json"
//
// Each top-level key under `seed` is a logical dependency name (matching a key in
// `environment.dependencies`).  Under it, exactly one seed kind is expected and
// the kind must match the dependency's declared `type` (postgres → `sql`, a
// broker → `publish`, a document store → `documents`).  The seed APPLIER enforces
// that match; the PARSER binds whichever kinds are present.

namespace Platform.Engine.Authoring.Model;

/// <summary>
/// The parsed <c>environment.seed</c> block (docs/02 §3.2.2): reference data
/// applied to managed dependencies after the topology is healthy and before the
/// first step runs.
/// </summary>
/// <remarks>
/// <para>
/// Each entry maps a logical dependency name (a key in
/// <see cref="EnvironmentSpec.Dependencies"/>) to its <see cref="DependencySeed"/>.
/// The engine applies dependencies in declared order; within a dependency, files
/// are applied in declared order.
/// </para>
/// <para>
/// A failed seed is an <strong>Environment error</strong> (§12.1), never a test
/// <c>Fail</c>: it runs inside the orchestration lifecycle and any failure is
/// wrapped in <c>OrchestrationException</c> (<c>Provision</c> kind).
/// </para>
/// </remarks>
/// <param name="Dependencies">
/// Map from logical dependency name to its seed specification.  Never empty when
/// the record is non-<see langword="null"/>; the parser returns
/// <see langword="null"/> for an absent or empty <c>seed</c> block.
/// </param>
public sealed record SeedSpec(
    IReadOnlyDictionary<string, DependencySeed> Dependencies);

/// <summary>
/// The seed specification for a single managed dependency (docs/02 §3.2.2).
/// </summary>
/// <remarks>
/// <para>
/// A dependency declares exactly one seed kind: <see cref="Sql"/> for a Postgres
/// dependency (applied in A-01), <see cref="Publish"/> for a broker warm-up, or
/// <see cref="Documents"/> for a document store.  The parser binds whichever kind
/// is present; the seed applier dispatches on the dependency's declared
/// <c>type</c> and rejects a mismatch (e.g. <c>publish</c> under a Postgres
/// dependency) as an Environment error (§12.1).
/// </para>
/// <para>
/// A-02 adds <see cref="Publish"/> and <see cref="Documents"/> as
/// <strong>wired-but-deferred seams</strong>: the seed pipeline reads the
/// referenced fixture file and computes its content hash (which the reproducibility
/// envelope — S05-B-03 — will record per docs/02 §3.2.2), and records the
/// publish/document intent through an injectable sink, but the actual broker
/// publish (Sprint 6, Kafka provider) and document-store write (later) are not
/// performed yet.
/// </para>
/// </remarks>
/// <param name="Sql">
/// Ordered list of SQL file paths (relative to the seed base directory) whose
/// contents are executed against a Postgres dependency, in declared order.
/// <see langword="null"/> when the dependency declares no <c>sql</c> entry.
/// </param>
/// <param name="Publish">
/// Ordered list of broker warm-up messages to publish to a broker dependency
/// (e.g. Kafka).  <see langword="null"/> when the dependency declares no
/// <c>publish</c> entry.  A-02 records the intent and content-hashes the payload
/// fixture; the actual publish lands with the Kafka provider in Sprint 6.
/// </param>
/// <param name="Documents">
/// Ordered list of document fixtures to load into a document-store dependency.
/// <see langword="null"/> when the dependency declares no <c>documents</c> entry.
/// A-02 records the intent and content-hashes the fixture file; the actual write
/// lands with the document-store provider in a later sprint.
/// </param>
public sealed record DependencySeed(
    IReadOnlyList<string>? Sql,
    IReadOnlyList<PublishSeed>? Publish = null,
    IReadOnlyList<DocumentSeed>? Documents = null);

/// <summary>
/// A single broker warm-up message in a dependency's <c>publish</c> sequence
/// (docs/02 §3.2.2).
/// </summary>
/// <remarks>
/// Grammar:
/// <code>
/// publish:
///   - topic: catalog.snapshot
///     payload: { from: "fixtures/catalog.json" }
/// </code>
/// The payload is referenced by file (<c>payload.from</c>) rather than inlined, so
/// the fixture lives in source control beside the test and the reproducibility
/// envelope can record its content hash (S05-B-03).
/// </remarks>
/// <param name="Topic">
/// The broker topic the warm-up message is published to.
/// </param>
/// <param name="PayloadFrom">
/// The <c>payload.from</c> file path (relative to the seed base directory) whose
/// bytes form the message payload.
/// </param>
public sealed record PublishSeed(
    string Topic,
    string PayloadFrom);

/// <summary>
/// A single document fixture in a dependency's <c>documents</c> sequence
/// (docs/02 §3.2.2).
/// </summary>
/// <remarks>
/// Grammar:
/// <code>
/// documents:
///   - collection: products       # optional target name
///     from: "fixtures/products.json"
/// </code>
/// </remarks>
/// <param name="Collection">
/// Optional target collection / container name within the document store.
/// <see langword="null"/> when the fixture omits <c>collection</c>.
/// </param>
/// <param name="From">
/// The <c>from</c> file path (relative to the seed base directory) whose contents
/// form the document fixture.  Required.
/// </param>
public sealed record DocumentSeed(
    string? Collection,
    string From);
