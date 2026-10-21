using System.Net;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Vouchfx.Cli.Providers;

// No feed-controlled endpoint or redirect may silently downgrade HTTPS.
internal sealed class ProviderHttpResource : ResourceProvider
{
    internal const long MaximumDownloadBytes = 64L * 1024 * 1024;
    internal ProviderHttpResource() : base(typeof(HttpHandlerResource), nameof(ProviderHttpResource), "HttpHandlerResourceV3Provider") { }

    public override async Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        var original = await new HttpHandlerResourceV3Provider().TryCreate(source, token);
        if (!original.Item1 || original.Item2 is null)
            return original;
        var resource = (HttpHandlerResource)original.Item2;
        resource.ClientHandler.AllowAutoRedirect = false;
        return Tuple.Create<bool, INuGetResource?>(true,
            new HttpHandlerResourceV3(resource.ClientHandler, new HttpsHandler(resource.MessageHandler)));
    }

    internal sealed class HttpsHandler : DelegatingHandler
    {
        internal HttpsHandler(HttpMessageHandler inner) : base(inner) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new ProviderLoadException("Provider feeds and their content endpoints must use HTTPS.");
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is 300 or 301 or 302 or 303 or 305 or 307 or 308)
            {
                response.Dispose();
                throw new ProviderLoadException("Provider feed redirects are refused. Configure the final HTTPS source endpoint.");
            }
            if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            {
                response.Dispose();
                throw new ProviderLoadException("Provider download exceeds the 64 MiB limit.");
            }
            response.Content = new BoundedContent(response.Content);
            return response;
        }
    }

    private sealed class BoundedContent : HttpContent
    {
        private readonly HttpContent _inner;
        internal BoundedContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var input = new BoundedStream(await _inner.ReadAsStreamAsync(cancellationToken), MaximumDownloadBytes);
            await input.CopyToAsync(stream, cancellationToken);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new BoundedStream(await _inner.ReadAsStreamAsync(cancellationToken), MaximumDownloadBytes);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

// Bounds actual streamed bytes, including responses with missing or false Content-Length.
internal sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximum;
    private readonly bool _leaveOpen;
    private long _transferred;
    internal BoundedStream(Stream inner, long maximum, bool leaveOpen = false)
    { _inner = inner; _maximum = maximum; _leaveOpen = leaveOpen; }
    private void Account(int count)
    {
        _transferred += count;
        if (_transferred > _maximum)
            throw new ProviderLoadException("Provider data exceeds the configured size limit.");
    }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = _inner.Read(buffer, offset, count); Account(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var read = await _inner.ReadAsync(buffer, cancellationToken); Account(read); return read; }
    public override void Write(byte[] buffer, int offset, int count)
    { Account(count); _inner.Write(buffer, offset, count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Account(buffer.Length); await _inner.WriteAsync(buffer, cancellationToken); }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value)
    { if (value > _maximum) throw new ProviderLoadException("Provider data exceeds the configured size limit."); _inner.SetLength(value); }
    protected override void Dispose(bool disposing)
    { if (disposing && !_leaveOpen) _inner.Dispose(); base.Dispose(disposing); }
}
