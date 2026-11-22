using Xunit;

// The TLS pins mint certificates, so this assembly carries the certificate-store hygiene guard that
// every certificate-producing test host in the repository registers. An exclusive final collection
// leaves every other collection's parallelism alone.
[assembly: TestCollectionOrderer(
    "Vouchfx.Steps.DbAssert.Postgres.Tests.CertificateStoreGuardLastOrderer",
    "Vouchfx.Steps.DbAssert.Postgres.Tests")]
