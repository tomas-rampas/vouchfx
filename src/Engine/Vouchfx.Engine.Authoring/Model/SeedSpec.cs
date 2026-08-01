// Vouchfx.Engine.Authoring — SeedSpec (S05-A-01).
//
// Strongly-typed records for the optional `environment.seed` block of a
// .e2e.yaml file (docs/02 §3.2.5).  The seed block declares reference data that
// the engine applies after the topology is healthy and before the first step
// runs, inside the same health-gated lifecycle (so a failed seed surfaces as an
// Environment error, never a misattributed assertion failure — §12.1).
//
// Grammar (A-01; `sql` generalised beyond Postgres to every relational
// db-assert-backed dependency kind, merged in #332):
//   seed:
//     orders-db:                       # postgres/sqlserver/mysql → SQL
//       sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
//
// Each top-level key under `seed` is a logical dependency name (matching a key in
// `environment.dependencies`).  `sql` is the only seed kind in the v1 language —
// the `publish` (broker warm-up) and `documents` (document-store fixture) kinds
// introduced as wired-but-deferred seams in A-02 never performed an actual
// broker publish or document-store write (they only read and content-hashed the
// referenced fixture and recorded the intent through an injectable sink) and
// were REMOVED before general availability: a field that silently does nothing
// is worse than one that does not exist, and re-adding either kind once it is
// genuinely implemented is purely additive. A suite still writing `publish:` or
// `documents:` under a seed dependency now fails schema validation instead of
// silently no-opping.

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// The parsed <c>environment.seed</c> block (docs/02 §3.2.5): reference data
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
/// The seed specification for a single managed dependency (docs/02 §3.2.5).
/// </summary>
/// <remarks>
/// <c>sql</c> is the only seed kind the v1 language recognises: applied against a
/// relational dependency (postgres, sqlserver, or mysql), in declared order. The
/// parser binds it when present; the seed applier dispatches on the dependency's
/// declared <c>type</c> and rejects a mismatch (e.g. <c>sql</c> under a Kafka
/// dependency) as an Environment error (§12.1).
/// </remarks>
/// <param name="Sql">
/// Ordered list of SQL file paths (relative to the seed base directory) whose
/// contents are executed against a relational dependency (postgres, sqlserver, or
/// mysql), in declared order. <see langword="null"/> when the dependency declares
/// no <c>sql</c> entry (a deliberate no-op).
/// </param>
public sealed record DependencySeed(
    IReadOnlyList<string>? Sql);
