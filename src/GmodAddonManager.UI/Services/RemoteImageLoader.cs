using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace GmodAddonManager.UI.Services
{
    /// <summary>
    /// リモート画像をダウンロードしてBitmapに変換するサービス
    /// NOTE: The caller is responsible for disposing the returned Bitmap
    /// </summary>
    public static class RemoteImageLoader
    {
        internal const int MaximumDecodedWidth = 256;
        internal const int MaximumDownloadBytes = 8 * 1024 * 1024;
        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _decodeSemaphore = new(4, 4);
        
        static RemoteImageLoader()
        {
            // Steam CDNからの画像取得用にUser-Agentを設定
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }
        
        /// <summary>
        /// Loads a bitmap from a URL. The caller MUST dispose the returned Bitmap.
        /// </summary>
        public static Task<Bitmap?> LoadFromUrlAsync(
            Uri? uri,
            CancellationToken cancellationToken = default)
        {
            return LoadFromUrlAsync(uri, _httpClient, cancellationToken);
        }

        internal static async Task<Bitmap?> LoadFromUrlAsync(
            Uri? uri,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uri == null)
                {
                    return null;
                }

                if (uri.IsFile)
                {
                    var localPath = uri.LocalPath;
                    if (File.Exists(localPath))
                    {
                        await _decodeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            return await Task.Run(() =>
                            {
                                using var stream = File.OpenRead(localPath);
                                return DecodeForAddonCard(stream);
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            _decodeSemaphore.Release();
                        }
                    }

                    return null;
                }

                if (!uri.IsAbsoluteUri)
                {
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                {
                    return null;
                }

                await using var responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var imageBytes = await ReadBoundedBytesAsync(
                        responseStream,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (imageBytes == null)
                {
                    return null;
                }

                if (imageBytes.Length == 0)
                {
                    return null;
                }

                await _decodeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(() =>
                    {
                        using var stream = new MemoryStream(imageBytes, writable: false);
                        return DecodeForAddonCard(stream);
                    }, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _decodeSemaphore.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> ReadBoundedBytesAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream
                    .ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > MaximumDownloadBytes)
                {
                    return null;
                }

                await buffer
                    .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static Bitmap DecodeForAddonCard(Stream stream)
        {
            // Cards render at 150 px. A bounded decode retains enough detail for
            // high-DPI displays without keeping every 512 px Workshop image in RAM.
            return Bitmap.DecodeToWidth(
                stream,
                MaximumDecodedWidth,
                BitmapInterpolationMode.MediumQuality);
        }
    }
}
