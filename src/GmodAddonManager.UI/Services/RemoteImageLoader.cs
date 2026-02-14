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
        public static async Task<Bitmap?> LoadFromUrlAsync(Uri? uri)
        {
            try
            {
                if (uri == null)
                {
                    return null;
                }

                if (uri.IsFile)
                {
                    var localPath = uri.LocalPath;
                    if (File.Exists(localPath))
                    {
                        await _decodeSemaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            return await Task.Run(() => new Bitmap(localPath)).ConfigureAwait(false);
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

                using var response = await _httpClient.GetAsync(uri).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                
                var imageBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (imageBytes.Length == 0)
                {
                    return null;
                }

                await _decodeSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await Task.Run(() =>
                    {
                        // The bitmap owns the stream and will dispose it
                        var stream = new MemoryStream(imageBytes);
                        return new Bitmap(stream);
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _decodeSemaphore.Release();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
