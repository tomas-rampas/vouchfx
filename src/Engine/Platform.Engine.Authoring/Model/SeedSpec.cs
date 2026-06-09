// Platform.Engine.Authoring — SeedSpec (S05-A-01).
//
// Strongly-typed records for the optional `environment.seed` block of a
// .e2e.yaml file (docs/02 §3.2.2).  The seed block declares reference data that
// the engine applies after the topology is healthy and before the first step
// runs, inside the same health-gated lifecycle (so a failed seed surfaces as an
// Environment error, never a misattributed assertion failure — §12.1).
//
// Grammar (A-01):
//   seed:
//     orders-db:
//       sql: [ "fixtures/a.sql", "fixtures/b.sql" ]
//
// Each top-level key under `seed` is a logical dependency name (matching a key in
// `environment.dependencies`).  Under it, `sql` is an ordered sequence of file
// paths whose SQL is executed, in declared order, against that dependency.

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
/// A-01 binds only <see cref="Sql"/>.  A later task (A-02) extends this record
/// with broker-publish and document-fixture forms; the record is deliberately
/// kept easy to extend with additional optional members without disturbing
/// existing callers.
/// </remarks>
/// <param name="Sql">
/// Ordered list of SQL file paths (relative to the seed base directory) whose
/// contents are executed against the dependency, in declared order.
/// <see langword="null"/> when the dependency declares no <c>sql</c> entry.
/// </param>
public sealed record DependencySeed(
    IReadOnlyList<string>? Sql);
