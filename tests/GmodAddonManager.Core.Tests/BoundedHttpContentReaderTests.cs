using System.Net;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class BoundedHttpContentReaderTests
{
    [Fact]
    public async Task ReadAsync_ContentAtLimit_ReturnsAllBytes()
    {
        var expected = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var content = new ByteArrayContent(expected);

        var actual = await BoundedHttpContentReader.ReadAsync(content, expected.Length);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadAsync_DeclaredLengthExceedsLimit_RejectsBeforeOpeningStream()
    {
        using var content = new TrackingContent(new byte[33], declareLength: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpContentReader.ReadAsync(content, 32));

        Assert.False(content.StreamWasOpened);
    }

    [Fact]
    public async Task ReadAsync_UnknownLengthExceedsLimit_StopsAfterFirstExcessByte()
    {
        using var content = new TrackingContent(new byte[128], declareLength: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpContentReader.ReadAsync(content, 32));

        Assert.True(content.StreamWasOpened);
        Assert.Equal(33, content.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_CancelledToken_DoesNotOpenStream()
    {
        using var content = new TrackingContent(new byte[32], declareLength: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedHttpContentReader.ReadAsync(content, 32, cancellation.Token));

        Assert.False(content.StreamWasOpened);
    }

    [Fact]
    public async Task ReadAsync_MaximumIntLimit_DoesNotOverflowBufferSizing()
    {
        using var content = new ByteArrayContent([42]);

        var actual = await BoundedHttpContentReader.ReadAsync(content, int.MaxValue);

        Assert.Equal([42], actual);
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly bool _declareLength;

        public TrackingContent(byte[] bytes, bool declareLength)
        {
            _bytes = bytes;
            _declareLength = declareLength;
            if (declareLength)
            {
                Headers.ContentLength = bytes.Length;
            }
        }

        public bool StreamWasOpened { get; private set; }

        public int BytesRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return _declareLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamWasOpened = true;
            return Task.FromResult<Stream>(new TrackingStream(_bytes, count => BytesRead += count));
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        private readonly Action<int> _onRead;

        public TrackingStream(byte[] bytes, Action<int> onRead)
            : base(bytes, writable: false)
        {
            _onRead = onRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await base.ReadAsync(buffer, cancellationToken);
            _onRead(bytesRead);
            return bytesRead;
        }
    }
}
