// The fake is test infrastructure, but it listens on a loopback port and reads lengths a client
// sends. These tests send it malformed first messages and check that it closes the connection
// instead of faulting its connection task or hanging, and that it still serves the next client.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

public sealed class PostgresWireFakeRobustnessTests
{
    private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(10);

    private static byte[] Int32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    public static IEnumerable<object[]> GarbageTheory() => new[]
    {
        new object[] { "negative length", Convert.ToHexString(Int32(-1).Concat(Int32(196608)).ToArray()) },
        new object[] { "length below a startup header", Convert.ToHexString(Int32(4).Concat(Int32(196608)).ToArray()) },
        new object[] { "length of two gibibytes", Convert.ToHexString(Int32(int.MaxValue).Concat(Int32(196608)).ToArray()) },
        new object[] { "startup parameters with no terminator", Convert.ToHexString(Int32(12).Concat(Int32(196608)).Concat("abcd"u8.ToArray()).ToArray()) },
        new object[]
        {
            "message length below its own header, after a valid startup",
            Convert.ToHexString(Int32(8).Concat(Int32(196608)).Concat("P"u8.ToArray()).Concat(Int32(0)).ToArray()),
        },
        new object[]
        {
            "bind message too short for the fields it claims, after a valid startup",
            Convert.ToHexString(Int32(8).Concat(Int32(196608)).Concat("B"u8.ToArray()).Concat(Int32(5)).Concat(new byte[] { 0 }).ToArray()),
        },
    };

    [Theory]
    [MemberData(nameof(GarbageTheory))]
    [Trait("requires", "none")]
    public async Task MalformedMessage_ClosesTheConnection_WithoutFaultingOrHangingTheFake(string description, string hex)
    {
        using var fake = new PostgresWireFake(_ => null);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, fake.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(hex));

        // Read until the fake closes its end; a hang shows as the cancellation, not as a pass.
        using var timeout = new CancellationTokenSource(s_limit);
        var buffer = new byte[4096];
        var closed = false;
        try
        {
            while (await stream.ReadAsync(buffer, timeout.Token) > 0)
            {
            }

            closed = true;
        }
        catch (IOException)
        {
            closed = true; // A reset also means the fake closed the connection.
        }
        catch (OperationCanceledException)
        {
            // Not closed within the limit.
        }

        Assert.True(closed, $"'{description}': the fake did not close the connection within {s_limit}.");
        Assert.Equal(0, await fake.FaultedConnectionsAfterSettlingAsync(s_limit));
    }

    [Fact]
    [Trait("requires", "none")]
    public async Task AfterMalformedClients_TheFakeStillServesAWellFormedOne()
    {
        var renderCase = PostgresRenderCases.Find("timestamp / fractional seconds");
        var column = new FakeColumn("v", renderCase.Oid, renderCase.Binary());
        using var fake = new PostgresWireFake(sql => sql == renderCase.Sql ? new[] { column } : null);

        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, fake.Port);
            await bad.GetStream().WriteAsync(Int32(-1).Concat(Int32(196608)).ToArray());
        }

        var connection =
            $"Host=127.0.0.1;Port={fake.Port};Username=u;Password=p;Database=d;Pooling=false;" +
            "SSL Mode=Disable;Server Compatibility Mode=NoTypeLoading";
        var rendered = await PostgresStepHarness.UnderCultureAsync(
            string.Empty, () => PostgresStepHarness.RenderedAsync(renderCase, connection));

        Assert.Equal(renderCase.RenderedUnder(string.Empty), rendered);
        Assert.Equal(0, await fake.FaultedConnectionsAfterSettlingAsync(s_limit));
    }
}
