namespace Vouchfx.Engine.Runtime.TransportAcceptance.Tests;

// RFC 5246 sections 6.2.1, 7.4.2 and 7.4.6. Capture ends at ChangeCipherSpec;
// no traffic keys or decrypted application data are recorded. TLS1.2 is fixture-only.
internal sealed class Tls12CertificateCapture
{
    private const int MaximumBytes = 256 * 1024;
    private readonly List<byte> _records = new();
    private readonly List<byte> _handshakes = new();
    private bool _encrypted;
    private int _recordBytesBeforeEncryption;
    internal List<byte[]> Certificates { get; } = new();
    internal byte[]? CertificateMessage { get; private set; }
    internal int? ServerHelloVersion { get; private set; }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (_encrypted) { return; }
        if (_records.Count + _handshakes.Count + bytes.Length > MaximumBytes) { throw new InvalidDataException("TLS fixture capture exceeded its bound."); }
        foreach (var value in bytes) { _records.Add(value); }
        while (_records.Count >= 5)
        {
            var length = (_records[3] << 8) | _records[4];
            if (length > 18432) { throw new InvalidDataException("Invalid TLS record length."); }
            if (_records.Count < 5 + length)
            {
                CheckPendingRecordBudget();
                return;
            }
            var type = _records[0];
            if (type == 20) { _encrypted = true; _records.Clear(); return; }
            var recordBytes = 5 + length;
            if (recordBytes > MaximumBytes - _recordBytesBeforeEncryption) { throw new InvalidDataException("TLS fixture cumulative record capture exceeded its bound."); }
            _recordBytesBeforeEncryption += recordBytes;
            if (type == 22)
            {
                _handshakes.AddRange(_records.GetRange(5, length));
                ParseHandshakes();
            }
            _records.RemoveRange(0, 5 + length);
        }
        CheckPendingRecordBudget();
    }

    private void CheckPendingRecordBudget()
    {
        // Count raw record headers and payloads cumulatively, even when parsed vectors
        // drain. A partial non-CCS record also consumes the remaining budget. CCS and
        // encrypted trailing bytes are outside the pre-encryption observation scope.
        if (_records.Count > 0 && _records[0] != 20 &&
            _records.Count > MaximumBytes - _recordBytesBeforeEncryption)
        {
            throw new InvalidDataException("TLS fixture cumulative record capture exceeded its bound.");
        }
    }

    private void ParseHandshakes()
    {
        while (_handshakes.Count >= 4)
        {
            var length = U24(_handshakes, 1);
            if (length > MaximumBytes) { throw new InvalidDataException("Invalid TLS handshake length."); }
            if (_handshakes.Count < 4 + length) { return; }
            var type = _handshakes[0];
            var body = _handshakes.GetRange(4, length);
            if (type == 2 && body.Count >= 2) { ServerHelloVersion = (body[0] << 8) | body[1]; }
            if (type == 11)
            {
                if (CertificateMessage is not null) { throw new InvalidDataException("Multiple initial Certificate messages."); }
                CertificateMessage = _handshakes.GetRange(0, length + 4).ToArray();
                if (body.Count < 3 || U24(body, 0) != body.Count - 3) { throw new InvalidDataException("Invalid certificate-list framing."); }
                var offset = 3;
                while (offset < body.Count)
                {
                    if (body.Count - offset < 3) { throw new InvalidDataException("Truncated certificate length."); }
                    var size = U24(body, offset); offset += 3;
                    if (size == 0 || size > body.Count - offset) { throw new InvalidDataException("Invalid certificate DER length."); }
                    Certificates.Add(body.GetRange(offset, size).ToArray()); offset += size;
                }
            }
            _handshakes.RemoveRange(0, length + 4);
        }
    }

    private static int U24(IReadOnlyList<byte> bytes, int offset) => (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
}

internal sealed class CapturingStream : Stream
{
    private readonly Stream _inner;
    internal CapturingStream(Stream inner) => _inner = inner;
    internal Tls12CertificateCapture Received { get; } = new();
    internal Tls12CertificateCapture Sent { get; } = new();
    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count); Received.Append(buffer.AsSpan(offset, read)); return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Received.Append(buffer.AsSpan(offset, read)); return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Received.Append(buffer.Span[..read]); return read;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count); Sent.Append(buffer.AsSpan(offset, count));
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Sent.Append(buffer.AsSpan(offset, count));
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Sent.Append(buffer.Span);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
