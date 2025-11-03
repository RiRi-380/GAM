using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Services
{
    public class WorkshopItemDetails
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? PreviewUrl { get; set; }
        public long TimeCreated { get; set; }
        public long TimeUpdated { get; set; }
        public string? Creator { get; set; }
    }

    public class SteamWorkshopService
    {
        private static readonly HttpClient httpClient = new HttpClient(new HttpClientHandler
        {
            MaxConnectionsPerServer = 100, // CDNへの接続数を大幅増加
            UseProxy = false, // プロキシをバイパスして直接接続
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });
        private readonly SemaphoreSlim rateLimiter = new SemaphoreSlim(20, 20); // 並列数を増加
        private DateTime lastRequestTime = DateTime.MinValue;
        private const int MinRequestInterval = 50; // インターバルを短縮
        private const string SteamApiUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
        private const long MaxCacheSizeBytes = 100L * 1024 * 1024; // 100MB
        
        private readonly IIconResolver? _iconResolver;

        static SteamWorkshopService()
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(30); // タイムアウトを延長
            
            // Keep-Aliveで接続を再利用
            httpClient.DefaultRequestHeaders.ConnectionClose = false;
            httpClient.DefaultRequestHeaders.Add("Keep-Alive", "timeout=600");
            
            // HTTP/2を優先的に使用（CDNの多くがHTTP/2対応）
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.ReusePort = true;
        }

        public SteamWorkshopService()
        {
        }
        
        public SteamWorkshopService(IIconResolver iconResolver)
        {
            _iconResolver = iconResolver;
        }

        public async Task<string?> GetWorkshopThumbnailUrlAsync(string workshopId)
        {
            if (string.IsNullOrEmpty(workshopId))
            {
                return null;
            }

            await rateLimiter.WaitAsync();
            try
            {
                var elapsed = (DateTime.Now - lastRequestTime).TotalMilliseconds;
                if (elapsed < MinRequestInterval)
                {
                    await Task.Delay(MinRequestInterval - (int)elapsed);
                }

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("itemcount", "1"),
                    new KeyValuePair<string, string>("publishedfileids[0]", workshopId)
                });

                var response = await httpClient.PostAsync(SteamApiUrl, content);
                lastRequestTime = DateTime.Now;

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonObject = JObject.Parse(json);
                
                var fileDetails = jsonObject["response"]?["publishedfiledetails"]?[0];
                if (fileDetails != null)
                {
                    var previewUrl = fileDetails["preview_url"]?.ToString();
                    if (!string.IsNullOrEmpty(previewUrl))
                    {
                        return previewUrl;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        public async Task<bool> DownloadThumbnailAsync(string url, string cachePath)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(cachePath))
            {
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                
                // 画像が大きすぎる場合の警告（1MB以上）
                if (imageBytes.Length > 1024 * 1024)
                {
                }
                
                // キャッシュサイズをチェックしてクリーンアップ
                var cacheDir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(cacheDir))
                {
                    await CleanupCacheIfNeeded(cacheDir, imageBytes.Length);
                }
                
                await Task.Run(() => File.WriteAllBytes(cachePath, imageBytes));
                
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public async Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId)
        {
            if (string.IsNullOrEmpty(workshopId))
            {
                return null;
            }

            await rateLimiter.WaitAsync();
            try
            {
                var elapsed = (DateTime.Now - lastRequestTime).TotalMilliseconds;
                if (elapsed < MinRequestInterval)
                {
                    await Task.Delay(MinRequestInterval - (int)elapsed);
                }

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("itemcount", "1"),
                    new KeyValuePair<string, string>("publishedfileids[0]", workshopId)
                });

                var response = await httpClient.PostAsync(SteamApiUrl, content);
                lastRequestTime = DateTime.Now;

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonObject = JObject.Parse(json);
                
                var fileDetails = jsonObject["response"]?["publishedfiledetails"]?[0];
                if (fileDetails != null)
                {
                    var details = new WorkshopItemDetails
                    {
                        Title = fileDetails["title"]?.ToString(),
                        Description = fileDetails["description"]?.ToString(),
                        PreviewUrl = fileDetails["preview_url"]?.ToString(),
                        TimeCreated = fileDetails["time_created"]?.Value<long>() ?? 0,
                        TimeUpdated = fileDetails["time_updated"]?.Value<long>() ?? 0,
                        Creator = fileDetails["creator"]?.ToString()
                    };
                    
                    return details;
                }

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        private async Task CleanupCacheIfNeeded(string cacheDirectory, long newFileSize)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(cacheDirectory))
                        return;

                    var directoryInfo = new DirectoryInfo(cacheDirectory);
                    var files = directoryInfo.GetFiles("*_thumb.jpg")
                        .OrderBy(f => f.LastAccessTime)
                        .ToList();

                    long totalSize = files.Sum(f => f.Length) + newFileSize;

                    // キャッシュサイズが制限を超えている場合、古いファイルから削除
                    while (totalSize > MaxCacheSizeBytes && files.Count > 0)
                    {
                        var oldestFile = files[0];
                        totalSize -= oldestFile.Length;
                        oldestFile.Delete();
                        files.RemoveAt(0);
                    }
                }
                catch (Exception ex)
                {
                }
            });
        }

        public async Task<List<string>> GetWorkshopThumbnailUrlsAsync(List<string> workshopIds)
        {
            var results = new List<string>();
            if (workshopIds == null || workshopIds.Count == 0)
                return results;

            // バッチ処理（Steam APIは最大100件まで一度に処理可能）
            const int batchSize = 20; // 安全のため20件ずつ処理
            for (int i = 0; i < workshopIds.Count; i += batchSize)
            {
                var batch = workshopIds.Skip(i).Take(batchSize).ToList();
                var batchResults = await GetBatchThumbnailUrlsAsync(batch);
                results.AddRange(batchResults);
            }

            return results;
        }

        private async Task<List<string>> GetBatchThumbnailUrlsAsync(List<string> workshopIds)
        {
            var results = new List<string>();

            await rateLimiter.WaitAsync();
            try
            {
                var elapsed = (DateTime.Now - lastRequestTime).TotalMilliseconds;
                if (elapsed < MinRequestInterval)
                {
                    await Task.Delay(MinRequestInterval - (int)elapsed);
                }

                var parameters = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("itemcount", workshopIds.Count.ToString())
                };

                for (int i = 0; i < workshopIds.Count; i++)
                {
                    parameters.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", workshopIds[i]));
                }

                var content = new FormUrlEncodedContent(parameters);
                var response = await httpClient.PostAsync(SteamApiUrl, content);
                lastRequestTime = DateTime.Now;

                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonObject = JObject.Parse(json);
                
                var fileDetailsArray = jsonObject["response"]?["publishedfiledetails"];
                if (fileDetailsArray != null)
                {
                    foreach (var fileDetails in fileDetailsArray)
                    {
                        var previewUrl = fileDetails["preview_url"]?.ToString();
                        results.Add(previewUrl ?? string.Empty);
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                return results;
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        /// <summary>
        /// Get the local icon path for a workshop item, using multi-stage fallback
        /// </summary>
        public async Task<string?> GetIconAsync(ulong workshopId)
        {
            if (_iconResolver == null)
            {
                // Fallback to downloading thumbnail URL if no icon resolver is configured
                var url = await GetWorkshopThumbnailUrlAsync(workshopId.ToString());
                return url;
            }

            return await _iconResolver.GetIconAsync(workshopId);
        }

        /// <summary>
        /// Prewarm icons for multiple workshop items
        /// </summary>
        public async Task PrewarmIconsAsync(ulong[] workshopIds)
        {
            if (_iconResolver == null || workshopIds == null || workshopIds.Length == 0)
                return;

            await _iconResolver.PrewarmIconsAsync(workshopIds);
        }
    }
}