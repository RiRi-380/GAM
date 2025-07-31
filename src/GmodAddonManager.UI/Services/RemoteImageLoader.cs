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
        
        static RemoteImageLoader()
        {
            // Steam CDNからの画像取得用にUser-Agentを設定
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }
        
        /// <summary>
        /// Loads a bitmap from a URL. The caller MUST dispose the returned Bitmap.
        /// </summary>
        public static async Task<Bitmap?> LoadFromUrlAsync(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                
                // Load into memory stream first to ensure proper disposal
                using var responseStream = await response.Content.ReadAsStreamAsync();
                var memoryStream = new MemoryStream();
                await responseStream.CopyToAsync(memoryStream);
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
    }
}