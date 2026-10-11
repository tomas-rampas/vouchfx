# SQL seed fixtures

`environment.seed.<dependency>.sql` applies SQL files in declared order to `postgres`, `sqlserver` or `mysql` dependencies after they become healthy. A failed seed produces an environment error.

Fixture paths must be relative to the suite directory. Rooted paths and paths that resolve above or outside that directory are refused before existence checks, file reads or relational driver access. Normalisation is lexical: it does not follow symbolic links or Windows junctions. This follows the same trusted suite-author policy as security artefact paths; it is not a filesystem sandbox, and `script.csharp` already permits arbitrary file access.

Report diagnostics name the declared fixture and dependency, with the exception type and a driver error code where available. They withhold raw driver messages, which can echo SQL fragments or host paths. Accepted resolved fixture paths remain recorded in the run's path-disclosure ledger.

Ordinary scenario isolation clears relational rows and preserves tables. When a kept topology is explicitly reset and reseeded, as in watch reuse, seeded relational dependencies also have their schema objects cleared before SQL is reapplied. This allows a fixture containing plain `CREATE TABLE` to run again. Unseeded dependencies and empty seed entries are unaffected by this additional reset.

- Postgres drops and recreates the `public` schema.
- SQL Server removes user tables (including temporal tables), foreign keys, views, routines, synonyms, sequences and user-defined types in the selected database. Schema-bound object dependencies determine the removal order. Database configuration and schema namespaces remain in place; database-level objects such as partition schemes, XML schema collections and DDL triggers are outside this reset.
- MySQL recreates the selected user database using its existing database definition, preserving its character set and collation. System databases are refused.

MySQL DDL still implicitly commits. A seed file that fails after DDL can leave both schema and successful earlier writes behind; the reset before reseeding does not make an individual SQL file atomic. Use idempotent SQL when directly retrying a failed fixture against that partial state. Postgres and SQL Server apply each seed file within a transaction, including its DDL.
