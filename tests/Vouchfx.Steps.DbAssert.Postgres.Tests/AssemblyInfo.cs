using Xunit;

// The TLS pins mint certificates, so this assembly carries the certificate-store hygiene guard that
// every certificate-producing test host in the repository registers. An exclusive final collection
// leaves every other collection's parallelism alone. The certificates are generated only by
// PostgresTlsFixture, which belongs to a requires=docker class: in the Docker lane that fixture runs
// the same residue sweep when it is disposed, and in the non-Docker lanes nothing here generates a
// certificate, so the final guard test is a repository-wide convention there rather than a check.
[assembly: TestCollectionOrderer(
    "Vouchfx.Steps.DbAssert.Postgres.Tests.CertificateStoreGuardLastOrderer",
    "Vouchfx.Steps.DbAssert.Postgres.Tests")]
