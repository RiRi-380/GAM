using System.Net;
using System.Text;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamWorkshopServiceResponseBoundaryTests
{
    [Fact]
    public void SteamApiJsonLimit_IsBoundedAtSixteenMiB()
    {
        Assert.Equal(
            16 * 1024 * 1024,
            SteamWorkshopService.MaximumSteamApiJsonBytes);
    }

    [Fact]
    public async Task ReadSteamApiJsonAsync_DeclaredLengthOverLimit_IsRejected()
    {
        using var content = new ByteArrayContent(new byte[33]);
        content.Headers.ContentLength = 33;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SteamWorkshopService.ReadSteamApiJsonAsync(
                content,
                maximumBytes: 32));
    }

    [Fact]
    public async Task ReadSteamApiJsonAsync_UnknownLengthOverLimit_IsRejected()
    {
        using var content = new UnknownLengthContent(new byte[33]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SteamWorkshopService.ReadSteamApiJsonAsync(
                content,
                maximumBytes: 32));
    }

    [Fact]
    public async Task ReadSteamApiJsonAsync_PreservesUtf8JsonAndRemovesBom()
    {
        const string expected = "{\"response\":{\"title\":\"日本語\"}}";
        var payload = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(expected))
            .ToArray();
        using var content = new ByteArrayContent(payload);

        var actual = await SteamWorkshopService.ReadSteamApiJsonAsync(content);

        Assert.Equal(expected, actual);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] payload;

        public UnknownLengthContent(byte[] payload)
        {
            this.payload = payload;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return stream.WriteAsync(payload, 0, payload.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(
                new MemoryStream(payload, writable: false));
        }
    }
}
