// A TLS-enabled PostgreSQL fixture: five TLS containers, one per server-certificate shape, plus one
// server without TLS, started concurrently from certificates generated at test time (never
// committed) by TestCertificateAuthority.WriteDatabaseServerSuiteDirectory.
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

/// <summary>The server-certificate shapes a client can be asked to validate.</summary>
public enum TlsServerShape
{
    /// <summary>A leaf issued directly by the root the suite provides.</summary>
    DirectUnderProvidedRoot,

    /// <summary>A leaf under an intermediate under the root the suite provides; the server sends leaf and intermediate.</summary>
    ThroughIntermediateSentByServer,

    /// <summary>As above, but the server sends the leaf alone (the intermediate is missing from the handshake).</summary>
    ThroughIntermediateNotSent,

    /// <summary>A leaf issued by an authority the suite does not provide.</summary>
    UnknownAuthority,

    /// <summary>A leaf from the provided root whose only name is not the host being connected to.</summary>
    NameDoesNotMatchHost,

    /// <summary>A server with TLS switched off (the engine's own managed postgres is of this kind).</summary>
    ServerWithoutTls,
}

public sealed class PostgresTlsFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vouchfx-pgtls-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<TlsServerShape, PostgresDockerContainer> _servers = new();

    internal PostgresDockerContainer Server(TlsServerShape shape) => _servers[shape];

    /// <summary>The root PEM a suite would provide as its trust anchor for <paramref name="shape"/>.</summary>
    internal string ProvidedRootPath(TlsServerShape shape) => Path.Combine(
        _directory,
        shape is TlsServerShape.ThroughIntermediateSentByServer or TlsServerShape.ThroughIntermediateNotSent
            ? TestCertificateAuthority.DatabaseServerTwoTierRootFileName
            : TestCertificateAuthority.DatabaseServerRootFileName);

    public async Task InitializeAsync()
    {
        TestCertificateAuthority.WriteDatabaseServerSuiteDirectory(_directory);

        string Path_(string name) => Path.Combine(_directory, name);
        var plan = new (TlsServerShape Shape, string Cert, string Key)[]
        {
            (TlsServerShape.DirectUnderProvidedRoot,
                Path_(TestCertificateAuthority.DatabaseServerDirectCertFileName),
                Path_(TestCertificateAuthority.DatabaseServerDirectKeyFileName)),
            (TlsServerShape.ThroughIntermediateSentByServer,
                Path_(TestCertificateAuthority.DatabaseServerTwoTierChainFileName),
                Path_(TestCertificateAuthority.DatabaseServerTwoTierKeyFileName)),
            (TlsServerShape.ThroughIntermediateNotSent,
                Path_(TestCertificateAuthority.DatabaseServerTwoTierLeafFileName),
                Path_(TestCertificateAuthority.DatabaseServerTwoTierKeyFileName)),
            (TlsServerShape.UnknownAuthority,
                Path_(TestCertificateAuthority.DatabaseServerForeignCertFileName),
                Path_(TestCertificateAuthority.DatabaseServerForeignKeyFileName)),
            (TlsServerShape.NameDoesNotMatchHost,
                Path_(TestCertificateAuthority.DatabaseServerMismatchCertFileName),
                Path_(TestCertificateAuthority.DatabaseServerMismatchKeyFileName)),
        };

        var starts = plan
            .Select(async p => (p.Shape, Server: await PostgresDockerContainer.StartTlsAsync(p.Cert, p.Key)))
            .Append(Task.Run(async () => (Shape: TlsServerShape.ServerWithoutTls, Server: await PostgresDockerContainer.StartPlainAsync())))
            .ToArray();
        try
        {
            await Task.WhenAll(starts);
        }
        catch
        {
            // Dispose whatever did start before failing the fixture.
            foreach (var start in starts.Where(s => s.IsCompletedSuccessfully))
            {
                await start.Result.Server.DisposeAsync();
            }

            throw;
        }

        foreach (var start in starts)
        {
            _servers[start.Result.Shape] = start.Result.Server;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            await server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a teardown over.
        }
    }
}
