using Xunit;

// An exclusive final collection leaves ordinary CLI test collections parallel.
[assembly: TestCollectionOrderer(
    "Vouchfx.Cli.Tests.CertificateStoreGuardLastOrderer", "Vouchfx.Cli.Tests")]
