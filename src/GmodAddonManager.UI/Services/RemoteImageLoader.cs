using System;
using System.IO;
using System.Net.Http;
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
        private static readonly HttpClient _httpClient = new();
        private const long MaxImageBytes = 5L * 1024 * 1024;
        
        static RemoteImageLoader()
        {
            // Steam CDNからの画像取得用にUser-Agentを設定
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }
        
        /// <summary>
        /// Loads a bitmap from a URL. The caller MUST dispose the returned Bitmap.
        /// </summary>
        public static async Task<Bitmap?> LoadFromUrlAsync(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    return null;
                }

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!AllowsImageDownload(mediaType))
                {
                    return null;
                }

                if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxImageBytes)
                {
                    return null;
                }

                // Load into memory stream first to ensure proper disposal
                using var responseStream = await response.Content.ReadAsStreamAsync();
                var memoryStream = new MemoryStream();
                var buffer = new byte[8192];
                long totalBytes = 0;

                while (true)
                {
                    var bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytes += bytesRead;
                    if (totalBytes > MaxImageBytes)
                    {
                        memoryStream.Dispose();
                        return null;
                    }

                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                }
                memoryStream.Position = 0;
                
                // Create bitmap from memory stream
                // The bitmap owns the stream and will dispose it
                var bitmap = new Bitmap(memoryStream);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static bool AllowsImageDownload(string? mediaType)
        {
            return string.IsNullOrEmpty(mediaType) ||
                   mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
        }
    }
}
