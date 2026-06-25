-- examples/reference/fixtures/seed.sql
--
-- Reference scenario seed fixture: creates and populates the 'orders' table
-- that the db-assert.postgres step queries.
--
-- The scenario INSERTs a row whose 'hostname' column is the value captured from
-- the whoami REST call (via the script.csharp step).  The db-assert.postgres step
-- then queries with WHERE hostname = @h (the captured hostname), isolating the
-- inserted row from the seed sentinel, and asserts rowCount:1 — proving the INSERT
-- landed and the {hostname} placeholder substitution threaded correctly.
--
-- Seeded before step 1 (health-gated); the seed sentinel row ('seed-sentinel')
-- exists only to prove the seed ran, not to be counted in the assertion.

CREATE TABLE orders (
    id       SERIAL PRIMARY KEY,
    hostname TEXT   NOT NULL,
    status   TEXT   NOT NULL DEFAULT 'PENDING'
);

-- The seeded reference row: a known-good sentinel that proves the seed ran.
INSERT INTO orders (hostname, status) VALUES ('seed-sentinel', 'SEEDED');
