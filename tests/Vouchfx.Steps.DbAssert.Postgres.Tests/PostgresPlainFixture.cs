using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

/// <summary>One plain PostgreSQL container for the Docker-backed rendering pins.</summary>
public sealed class PostgresPlainFixture : IAsyncLifetime
{
    private PostgresDockerContainer? _server;

    internal PostgresDockerContainer Server => _server!;

    public async Task InitializeAsync() => _server = await PostgresDockerContainer.StartPlainAsync();

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
