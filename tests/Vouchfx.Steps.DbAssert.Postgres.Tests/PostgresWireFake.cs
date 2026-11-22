// A minimal PostgreSQL v3 wire-protocol server on a loopback port, used so the Npgsql rendering
// pins can run in the non-Docker lanes (the Docker lanes run daily).
//
// What it is: enough of the protocol for Npgsql 8 to open a connection with
// "Server Compatibility Mode=NoTypeLoading" (which skips the pg_type catalogue query), send one
// unprepared query (Parse/Bind/Describe/Execute/Sync) and read ONE row of binary-format values
// that the test supplies, encoded exactly as PostgreSQL's binary send functions do. It performs
// no authentication, speaks no TLS (an SSLRequest is refused with 'N') and does not parse SQL:
// the query text is the key into the canned result.
//
// What it is NOT: evidence about a real server. The Docker-backed twin of every case runs the
// same query against a real PostgreSQL and asserts the same text; the two are meant to agree.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

/// <summary>One result column: name, type OID, and the binary value (<see langword="null"/> is SQL NULL).</summary>
internal sealed record FakeColumn(string Name, uint TypeOid, byte[]? Value);

/// <summary>Loopback PostgreSQL wire-protocol server returning canned single-row results.</summary>
internal sealed class PostgresWireFake : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<Task> _connections = new();
    private readonly Func<string, IReadOnlyList<FakeColumn>?> _results;

    public PostgresWireFake(Func<string, IReadOnlyList<FakeColumn>?> results)
    {
        _results = results;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Startup-message parameters of every connection accepted, in order.</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> StartupParameters { get; } = new();

    /// <summary>Result-column format codes (0 = text, 1 = binary) of every Bind received.</summary>
    public ConcurrentQueue<short[]> ResultFormats { get; } = new();

    /// <summary>Number of connections that opened with an SSLRequest.</summary>
    public int SslRequests => Volatile.Read(ref _sslRequests);

    private int _sslRequests;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _connections.Enqueue(Task.Run(() => ServeAsync(client)));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var ct = _stop.Token;

            // Startup, possibly preceded by an SSLRequest that is refused.
            while (true)
            {
                var head = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(head);
                var code = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
                if (code == 80877103)
                {
                    Interlocked.Increment(ref _sslRequests);
                    await stream.WriteAsync(new byte[] { (byte)'N' }, ct).ConfigureAwait(false);
                    continue;
                }

                var body = await ReadExactAsync(stream, length - 8, ct).ConfigureAwait(false);
                StartupParameters.Enqueue(ParseStartup(body));
                break;
            }

            await stream.WriteAsync(Message('R', Int32(0)), ct).ConfigureAwait(false);
            foreach (var (key, value) in new[]
            {
                ("server_version", "17.0"),
                ("server_encoding", "UTF8"),
                ("client_encoding", "UTF8"),
                ("DateStyle", "ISO, MDY"),
                ("integer_datetimes", "on"),
                ("standard_conforming_strings", "on"),
                ("TimeZone", "Etc/UTC"),
            })
            {
                await stream.WriteAsync(Message('S', CString(key), CString(value)), ct).ConfigureAwait(false);
            }

            await stream.WriteAsync(Message('K', Int32(1234), Int32(5678)), ct).ConfigureAwait(false);
            await stream.WriteAsync(Message('Z', new[] { (byte)'I' }), ct).ConfigureAwait(false);

            string? query = null;
            IReadOnlyList<FakeColumn>? columns = null;
            var failed = false;
            while (true)
            {
                var type = (char)(await ReadExactAsync(stream, 1, ct).ConfigureAwait(false))[0];
                var len = BinaryPrimitives.ReadInt32BigEndian(await ReadExactAsync(stream, 4, ct).ConfigureAwait(false));
                var body = await ReadExactAsync(stream, len - 4, ct).ConfigureAwait(false);

                switch (type)
                {
                    case 'X':
                        return;
                    case 'Q':
                        await stream.WriteAsync(Message('I'), ct).ConfigureAwait(false);
                        await stream.WriteAsync(Message('Z', new[] { (byte)'I' }), ct).ConfigureAwait(false);
                        break;
                    case 'P' when !failed:
                        query = ReadCString(body, 0, out var afterName);
                        query = ReadCString(body, afterName, out _);
                        columns = _results(query);
                        if (columns is null)
                        {
                            failed = true;
                            await stream.WriteAsync(
                                Message('E', Field('S', "ERROR"), Field('C', "42601"),
                                    Field('M', "fake server has no result for: " + query), new byte[] { 0 }), ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await stream.WriteAsync(Message('1'), ct).ConfigureAwait(false);
                        }

                        break;
                    case 'B' when !failed:
                        ResultFormats.Enqueue(ParseBindResultFormats(body));
                        await stream.WriteAsync(Message('2'), ct).ConfigureAwait(false);
                        break;
                    case 'D' when !failed:
                        if (body[0] == (byte)'S')
                        {
                            await stream.WriteAsync(Message('t', Int16(0)), ct).ConfigureAwait(false);
                        }

                        await stream.WriteAsync(RowDescription(columns!), ct).ConfigureAwait(false);
                        break;
                    case 'E' when !failed:
                        await stream.WriteAsync(DataRow(columns!), ct).ConfigureAwait(false);
                        await stream.WriteAsync(Message('C', CString("SELECT 1")), ct).ConfigureAwait(false);
                        break;
                    case 'C' when !failed:
                        await stream.WriteAsync(Message('3'), ct).ConfigureAwait(false);
                        break;
                    case 'S':
                        failed = false;
                        await stream.WriteAsync(Message('Z', new[] { (byte)'I' }), ct).ConfigureAwait(false);
                        break;
                    default:
                        // 'H' (Flush) and anything after an error until Sync: nothing to say.
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                       or SocketException or EndOfStreamException)
        {
            // The client went away or the fake is shutting down.
        }
        finally
        {
            client.Dispose();
        }
    }

    private static Dictionary<string, string> ParseStartup(byte[] body)
    {
        // protocol version already consumed with the length; the rest is key\0value\0 ... \0
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < body.Length && body[offset] != 0)
        {
            var key = ReadCString(body, offset, out offset);
            var value = ReadCString(body, offset, out offset);
            result[key] = value;
        }

        return result;
    }

    private static short[] ParseBindResultFormats(byte[] body)
    {
        var offset = 0;
        _ = ReadCString(body, offset, out offset); // portal
        _ = ReadCString(body, offset, out offset); // statement
        var parameterFormats = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset));
        offset += 2 + (parameterFormats * 2);
        var parameters = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset));
        offset += 2;
        for (var i = 0; i < parameters; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset));
            offset += 4 + Math.Max(length, 0);
        }

        var count = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset));
        offset += 2;
        var formats = new short[count];
        for (var i = 0; i < count; i++)
        {
            formats[i] = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(offset));
            offset += 2;
        }

        return formats;
    }

    private static byte[] RowDescription(IReadOnlyList<FakeColumn> columns)
    {
        var parts = new List<byte[]> { Int16((short)columns.Count) };
        foreach (var column in columns)
        {
            parts.Add(CString(column.Name));
            parts.Add(Int32(0)); // table oid
            parts.Add(Int16(0)); // column attribute number
            parts.Add(Int32((int)column.TypeOid));
            parts.Add(Int16(-1)); // type size
            parts.Add(Int32(-1)); // type modifier
            parts.Add(Int16(1)); // binary
        }

        return Message('T', parts.ToArray());
    }

    private static byte[] DataRow(IReadOnlyList<FakeColumn> columns)
    {
        var parts = new List<byte[]> { Int16((short)columns.Count) };
        foreach (var column in columns)
        {
            if (column.Value is null)
            {
                parts.Add(Int32(-1));
            }
            else
            {
                parts.Add(Int32(column.Value.Length));
                parts.Add(column.Value);
            }
        }

        return Message('D', parts.ToArray());
    }

    private static byte[] Message(char type, params byte[][] parts)
    {
        var payload = parts.Sum(p => p.Length);
        var buffer = new byte[1 + 4 + payload];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), 4 + payload);
        var offset = 5;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }

    private static byte[] Int32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] Int16(short value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] CString(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static byte[] Field(char code, string value) =>
        new[] { (byte)code }.Concat(CString(value)).ToArray();

    private static string ReadCString(byte[] buffer, int offset, out int next)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset);
        next = end + 1;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }

        return buffer;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
            Task.WaitAll(_connections.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Connection tasks swallow their own I/O faults; anything else is not worth failing teardown.
        }

        _stop.Dispose();
    }
}

/// <summary>
/// PostgreSQL binary send-format encoders for the types the pin tests return. Each mirrors the
/// server's <c>*_send</c> function: big-endian, microsecond-resolution, 2000-01-01 epoch.
/// </summary>
internal static class PgBinary
{
    public const uint OidBool = 16;
    public const uint OidBytea = 17;
    public const uint OidInt4 = 23;
    public const uint OidJson = 114;
    public const uint OidInt4Array = 1007;
    public const uint OidDate = 1082;
    public const uint OidTime = 1083;
    public const uint OidTimestamp = 1114;
    public const uint OidTimestampTz = 1184;
    public const uint OidInterval = 1186;
    public const uint OidTimeTz = 1266;
    public const uint OidNumeric = 1700;
    public const uint OidUuid = 2950;
    public const uint OidJsonb = 3802;

    private static readonly DateTime s_epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static byte[] Date(int year, int month, int day) =>
        Int32((int)(new DateTime(year, month, day) - s_epoch).TotalDays);

    public static byte[] Time(long microseconds) => Int64(microseconds);

    /// <summary>timetz: microseconds since midnight, then the zone in seconds WEST of UTC.</summary>
    public static byte[] TimeTz(long microseconds, int secondsWestOfUtc) =>
        Int64(microseconds).Concat(Int32(secondsWestOfUtc)).ToArray();

    public static byte[] Timestamp(DateTime value) => Int64((value - s_epoch).Ticks / 10);

    public static byte[] Interval(long microseconds, int days, int months) =>
        Int64(microseconds).Concat(Int32(days)).Concat(Int32(months)).ToArray();

    public static byte[] Boolean(bool value) => new[] { value ? (byte)1 : (byte)0 };

    public static byte[] Uuid(Guid value)
    {
        // Guid.ToByteArray is little-endian for the first three groups; the wire form is RFC 4122 order.
        var bytes = new byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    public static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    public static byte[] Jsonb(string value) => new byte[] { 1 }.Concat(Encoding.UTF8.GetBytes(value)).ToArray();

    /// <summary>numeric from its decimal text, e.g. "12.3400" (dscale preserved, so trailing zeros survive).</summary>
    public static byte[] Numeric(string text)
    {
        var negative = text.StartsWith('-');
        var unsigned = negative ? text[1..] : text;
        var dot = unsigned.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? unsigned : unsigned[..dot];
        var fractionPart = dot < 0 ? string.Empty : unsigned[(dot + 1)..];
        var scale = fractionPart.Length;

        // Pad so both halves split into base-10000 digits on the decimal point.
        integerPart = integerPart.TrimStart('0');
        var integerPadded = integerPart.PadLeft(((integerPart.Length + 3) / 4) * 4, '0');
        var fractionPadded = fractionPart.PadRight(((fractionPart.Length + 3) / 4) * 4, '0');

        var digits = new List<short>();
        for (var i = 0; i < integerPadded.Length; i += 4)
        {
            digits.Add(short.Parse(integerPadded.AsSpan(i, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture));
        }

        var weight = digits.Count - 1;
        for (var i = 0; i < fractionPadded.Length; i += 4)
        {
            digits.Add(short.Parse(fractionPadded.AsSpan(i, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture));
        }

        // PostgreSQL strips leading and trailing zero digit-groups from the stored digits.
        while (digits.Count > 0 && digits[0] == 0)
        {
            digits.RemoveAt(0);
            weight--;
        }

        while (digits.Count > 0 && digits[^1] == 0)
        {
            digits.RemoveAt(digits.Count - 1);
        }

        var parts = new List<byte[]>
        {
            Int16((short)digits.Count),
            Int16((short)weight),
            Int16(negative ? (short)0x4000 : (short)0),
            Int16((short)scale),
        };
        parts.AddRange(digits.Select(Int16));
        return parts.SelectMany(p => p).ToArray();
    }

    /// <summary>A one-dimensional int4[] with no NULL elements and lower bound 1.</summary>
    public static byte[] Int4Array(params int[] values)
    {
        var parts = new List<byte[]>
        {
            Int32(1), // dimensions
            Int32(0), // has-nulls flag
            Int32((int)OidInt4),
            Int32(values.Length),
            Int32(1), // lower bound
        };
        foreach (var value in values)
        {
            parts.Add(Int32(4));
            parts.Add(Int32(value));
        }

        return parts.SelectMany(p => p).ToArray();
    }

    private static byte[] Int32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] Int64(long value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] Int16(short value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        return buffer;
    }
}
