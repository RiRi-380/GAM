using GmodAddonManager.Core.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("GAM-Setup-1.0.1.exe")]
    [InlineData("GAM-Setup-v1.0.1.exe")]
    [InlineData("GAM-installer.exe")]
    public void IsInstallerAssetNameInstallerExeReturnsTrue(string assetName)
    {
        Assert.True(UpdateService.IsInstallerAssetName(assetName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GAM-Portable-1.0.1.zip")]
    [InlineData("VC_redist.x64.exe")]
    [InlineData("release-notes.txt")]
    public void IsInstallerAssetNameNonInstallerAssetReturnsFalse(string? assetName)
    {
        Assert.False(UpdateService.IsInstallerAssetName(assetName));
    }

    [Fact]
    public async Task DownloadInstallerAsyncReportsProgressAndWritesFile()
    {
        var bytes = new byte[32 * 1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        using var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);

        var service = new UpdateService("1.0.0", httpClient);
        var progressReports = new ListProgress<UpdateDownloadProgress>();
        var destinationPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            await service.DownloadInstallerAsync(
                "https://example.invalid/GAM-Setup.exe",
                destinationPath,
                progressReports,
                expectedSize: bytes.Length,
                expectedSha256: expectedSha256);

            Assert.True(File.Exists(destinationPath));
            Assert.Equal(bytes.Length, new FileInfo(destinationPath).Length);
            Assert.NotEmpty(progressReports.Reports);
            Assert.Equal(0, progressReports.Reports[0].DownloadedBytes);
            Assert.Equal(bytes.Length, progressReports.Reports[^1].DownloadedBytes);
            Assert.Equal(bytes.Length, progressReports.Reports[^1].TotalBytes);
            Assert.Equal(100d, progressReports.Reports[^1].Percentage);
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
    }

    [Fact]
    public async Task DownloadInstallerAsyncRejectsHashMismatchAndRemovesPartialFile()
    {
        var bytes = Encoding.UTF8.GetBytes("installer payload");
        using var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);

        var service = new UpdateService("1.0.0", httpClient);
        var destinationPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadInstallerAsync(
                    "https://example.invalid/GAM-Setup.exe",
                    destinationPath,
                    expectedSize: bytes.Length,
                    expectedSha256: new string('0', 64)));

            Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(destinationPath));
            Assert.False(File.Exists(destinationPath + ".download"));
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            if (File.Exists(destinationPath + ".download"))
            {
                File.Delete(destinationPath + ".download");
            }
        }
    }

    [Fact]
    public void VerifyManifestSignatureAcceptsValidRsaSignatureAndRejectsTampering()
    {
        var manifestBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var signature = rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(UpdateService.VerifyManifestSignature(manifestBytes, signature, publicKey));

        manifestBytes[0] = (byte)'[';
        Assert.False(UpdateService.VerifyManifestSignature(manifestBytes, signature, publicKey));
    }

    [Fact]
    public async Task DownloadInstallerAsyncTimesOutAndRemovesPartialFileWhenStreamStalls()
    {
        using var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverCompletingStream())
            };
            response.Content.Headers.ContentLength = 1024;
            return response;
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);

        var service = new UpdateService("1.0.0", httpClient)
        {
            DownloadInactivityTimeout = TimeSpan.FromMilliseconds(50)
        };
        var destinationPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                service.DownloadInstallerAsync(
                    "https://example.invalid/GAM-Setup.exe",
                    destinationPath));

            Assert.False(File.Exists(destinationPath));
            Assert.False(File.Exists(destinationPath + ".download"));
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            if (File.Exists(destinationPath + ".download"))
            {
                File.Delete(destinationPath + ".download");
            }
        }
    }

    [Fact]
    public void FormatDownloadProgressIncludesPercentWhenTotalIsKnown()
    {
        var text = UpdateService.FormatDownloadProgress(new UpdateDownloadProgress(27 * 1024 * 1024, 54L * 1024 * 1024));

        Assert.Contains("27.0 / 54.0 MB", text, StringComparison.Ordinal);
        Assert.Contains("(50%)", text, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();

        public void Report(T value)
        {
            Reports.Add(value);
        }
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
