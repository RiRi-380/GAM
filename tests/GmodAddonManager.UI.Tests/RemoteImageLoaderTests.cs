using System.Net;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Tests;

public sealed class RemoteImageLoaderTests
{
    [AvaloniaFact]
    public async Task LocalWorkshopImageIsDecodedAtCardSize()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            $"gam-image-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var imagePath = Path.Combine(rootPath, "workshop.bmp");

        try
        {
            await File.WriteAllBytesAsync(imagePath, CreateRgbBitmap(512, 512));

            using var bitmap = await RemoteImageLoader.LoadFromUrlAsync(
                new Uri(imagePath));

            Assert.NotNull(bitmap);
            Assert.Equal(256, bitmap.PixelSize.Width);
            Assert.Equal(256, bitmap.PixelSize.Height);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task DeclaredOversizeResponseIsRejectedWithoutReadingTheBody()
    {
        var body = new CountingStream(1);
        using var content = new StreamContent(body);
        content.Headers.ContentLength = RemoteImageLoader.MaximumDownloadBytes + 1L;
        using var client = CreateClient(content);

        using var bitmap = await RemoteImageLoader.LoadFromUrlAsync(
            new Uri("https://example.invalid/oversize.png"),
            client,
            CancellationToken.None);

        Assert.Null(bitmap);
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task ChunkedOversizeResponseStopsAtTheConfiguredDownloadBound()
    {
        var body = new CountingStream(RemoteImageLoader.MaximumDownloadBytes + 1L);
        using var content = new StreamContent(body);
        Assert.Null(content.Headers.ContentLength);
        using var client = CreateClient(content);

        using var bitmap = await RemoteImageLoader.LoadFromUrlAsync(
            new Uri("https://example.invalid/chunked-oversize.png"),
            client,
            CancellationToken.None);

        Assert.Null(bitmap);
        Assert.True(body.BytesRead > RemoteImageLoader.MaximumDownloadBytes);
        Assert.True(
            body.BytesRead <= RemoteImageLoader.MaximumDownloadBytes + 81920L,
            $"The loader read too far past its bound: {body.BytesRead} bytes.");
    }

    [Fact]
    public async Task CancellationStopsAStreamingDownload()
    {
        var body = new BlockingStream();
        using var content = new StreamContent(body);
        using var client = CreateClient(content);
        using var cancellation = new CancellationTokenSource();

        var load = RemoteImageLoader.LoadFromUrlAsync(
            new Uri("https://example.invalid/slow.png"),
            client,
            cancellation.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public void RemoteLoaderUsesHeaderOnlyCompletionBeforeReadingTheBody()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Services",
            "RemoteImageLoader.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "HttpCompletionOption.ResponseHeadersRead",
            source,
            StringComparison.Ordinal);
    }

    private static byte[] CreateRgbBitmap(int width, int height)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var pixelBytes = rowSize * height;
        using var stream = new MemoryStream(54 + pixelBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + pixelBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write(new byte[pixelBytes]);
        writer.Flush();

        return stream.ToArray();
    }

    private static HttpClient CreateClient(HttpContent content)
    {
        return new HttpClient(new StubHandler(content));
    }

    private static string FindRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpContent content;

        public StubHandler(HttpContent content)
        {
            this.content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private sealed class CountingStream : Stream
    {
        private long remaining;

        public CountingStream(long length)
        {
            remaining = length;
        }

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, read);
            remaining -= read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Clear();
            remaining -= read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
