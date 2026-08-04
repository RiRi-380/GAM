using System;
using System.Collections.Concurrent;
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
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public class WorkshopItemDetails
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? PreviewUrl { get; set; }
        public long TimeCreated { get; set; }
        public long TimeUpdated { get; set; }
        public string? Creator { get; set; }
        public ulong FileSize { get; set; }
        public string[]? Tags { get; set; }
    }

    public class SteamWorkshopService
    {
        private static readonly HttpClient httpClient = new HttpClient(new HttpClientHandler
        {
            MaxConnectionsPerServer = 100, // CDNへの接続数を大幅増加
            UseProxy = false, // プロキシをバイパスして直接接続
            CheckCertificateRevocationList = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });
        private readonly SemaphoreSlim rateLimiter = new SemaphoreSlim(20, 20); // 並列数を増加
        private DateTime lastRequestTime = DateTime.MinValue;
        private const int MinRequestInterval = 50; // インターバルを短縮
        private const string SteamApiUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
        private const string SteamCollectionApiUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";
        internal const int MaximumSteamApiJsonBytes = 16 * 1024 * 1024;
        private const long MaxCacheSizeBytes = 100L * 1024 * 1024; // 100MB
        private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan MetadataNegativeTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan HotDiskCacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan ColdDiskCacheTtl = TimeSpan.FromHours(72);
        
        private readonly IIconResolver? _iconResolver;
        private readonly ConcurrentDictionary<string, CacheEntry> _metadataCache = new();
        private static readonly WorkshopMetadataCacheStore DiskMetadataCache = new WorkshopMetadataCacheStore();

        private sealed class CacheEntry
        {
            public WorkshopItemDetails? Details { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        private enum CollectionChildrenFetchStatus
        {
            Success,
            Unavailable
        }

        private sealed class CollectionChildrenFetchResult
        {
            public CollectionChildrenFetchResult(
                CollectionChildrenFetchStatus status,
                Dictionary<string, List<string>> childrenMap)
            {
                Status = status;
                ChildrenMap = childrenMap;
            }

            public CollectionChildrenFetchStatus Status { get; }
            public Dictionary<string, List<string>> ChildrenMap { get; }
        }

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
            var details = await GetWorkshopDetailsAsync(workshopId);
            return details?.PreviewUrl;
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

                using var response = await httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var imageBytes = await BoundedHttpContentReader.ReadAsync(
                    response.Content,
                    BoundedHttpContentReader.DefaultImageLimitBytes);
                
                // キャッシュサイズをチェックしてクリーンアップ
                var cacheDir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(cacheDir))
                {
                    await CleanupCacheIfNeeded(cacheDir, imageBytes.Length);
                }
                
                await Task.Run(() => File.WriteAllBytes(cachePath, imageBytes));
                
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId)
        {
            return GetWorkshopDetailsAsync(workshopId, true);
        }

        public async Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId, bool treatAsHot)
        {
            if (string.IsNullOrEmpty(workshopId))
            {
                return null;
            }
            if (TryGetCachedDetails(workshopId, out var cached))
            {
                if (cached != null)
                {
                    TryHydrateFullDescription(cached);
                }
                return cached;
            }

            var map = await GetWorkshopDetailsBatchAsync(new List<string> { workshopId }, default, treatAsHot);
            if (map.TryGetValue(workshopId, out var details))
            {
                TryHydrateFullDescription(details);
                return details;
            }

            return null;
        }

        public async Task<Dictionary<string, WorkshopItemDetails>> GetWorkshopDetailsBatchAsync(
            IReadOnlyList<string> workshopIds,
            CancellationToken cancellationToken = default,
            bool treatAsHot = true,
            bool requireTags = false)
        {
            var results = new Dictionary<string, WorkshopItemDetails>(StringComparer.Ordinal);
            if (workshopIds == null || workshopIds.Count == 0)
            {
                return results;
            }

            var normalized = workshopIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalized.Count == 0)
            {
                return results;
            }

            var remaining = new List<string>();
            Dictionary<string, WorkshopItemInfo>? localDetails = null;
            var localDetailsLoaded = false;
            var nowUtc = DateTime.UtcNow;
            var negativeEntries = DiskMetadataCache.GetNegativeBatch(normalized);
            long? GetLocalUpdatedUnix(string id)
            {
                if (!localDetailsLoaded)
                {
                    localDetails = SteamWorkshopCacheReader.GetAddonDetails();
                    localDetailsLoaded = true;
                }

                if (localDetails != null && localDetails.TryGetValue(id, out var info) && info.TimeUpdated.HasValue)
                {
                    return new DateTimeOffset(info.TimeUpdated.Value).ToUnixTimeSeconds();
                }

                return null;
            }

            var diskEntries = DiskMetadataCache.GetCoreBatch(normalized);
            foreach (var id in normalized)
            {
                if (TryGetCachedDetails(id, out var cached))
                {
                    if (cached == null)
                    {
                        continue;
                    }

                    if (!requireTags || HasTags(cached))
                    {
                        results[id] = cached;
                        continue;
                    }
                }

                if (diskEntries.TryGetValue(id, out var diskEntry) && diskEntry?.Details != null)
                {
                    var localUpdated = GetLocalUpdatedUnix(id);
                    if (IsDiskCacheValid(diskEntry.Details, diskEntry.FetchedAtUtc, localUpdated, nowUtc, treatAsHot) &&
                        (!requireTags || HasTags(diskEntry.Details)))
                    {
                        results[id] = diskEntry.Details;
                        SetCachedDetails(id, diskEntry.Details, MetadataCacheTtl);
                        continue;
                    }
                }

                if (negativeEntries.TryGetValue(id, out var negativeFetchedAtUtc))
                {
                    if (nowUtc - negativeFetchedAtUtc <= MetadataNegativeTtl)
                    {
                        SetNegativeCache(id);
                        continue;
                    }
                }

                remaining.Add(id);
            }

            if (remaining.Count == 0)
            {
                return results;
            }

            var (batchSize, maxConcurrency) = SelectBatchPlan(remaining.Count);
            batchSize = Math.Clamp(batchSize, 1, 100);
            maxConcurrency = Math.Max(1, maxConcurrency);

            var batches = new List<List<string>>();
            for (int i = 0; i < remaining.Count; i += batchSize)
            {
                batches.Add(remaining.Skip(i).Take(batchSize).ToList());
            }

            var fetched = new ConcurrentDictionary<string, WorkshopItemDetails>(StringComparer.Ordinal);
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var batchResult = await FetchPublishedFileDetailsAsync(batch, cancellationToken);
                    foreach (var kvp in batchResult)
                    {
                        fetched[kvp.Key] = kvp.Value;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            foreach (var kvp in fetched)
            {
                results[kvp.Key] = kvp.Value;
                SetCachedDetails(kvp.Key, kvp.Value, MetadataCacheTtl);
            }

            foreach (var id in remaining)
            {
                if (!results.ContainsKey(id))
                {
                    SetNegativeCache(id);
                }
            }

            var missing = remaining.Where(id => !results.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                DiskMetadataCache.UpsertNegative(missing);
            }
            if (missing.Count > 0)
            {
                if (!localDetailsLoaded)
                {
                    localDetails = SteamWorkshopCacheReader.GetAddonDetails();
                    localDetailsLoaded = true;
                }

                if (localDetails != null)
                {
                    foreach (var id in missing)
                    {
                        if (localDetails.TryGetValue(id, out var info))
                        {
                            var fallback = new WorkshopItemDetails
                            {
                                Id = id,
                                Title = info.Title,
                                TimeUpdated = info.TimeUpdated.HasValue
                                    ? new DateTimeOffset(info.TimeUpdated.Value).ToUnixTimeSeconds()
                                    : 0
                            };
                            results[id] = fallback;
                            SetCachedDetails(id, fallback, TimeSpan.FromMinutes(30));
                        }
                    }
                }
            }

            if (fetched.Count > 0)
            {
                DiskMetadataCache.UpsertBatch(fetched.Values);
            }
            return results;
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
                    System.Diagnostics.Debug.WriteLine($"[SteamWorkshopService] CleanupCacheIfNeeded failed: {ex.Message}");
                }
            });
        }

        public async Task<List<string>> GetWorkshopThumbnailUrlsAsync(List<string> workshopIds)
        {
            var results = new List<string>();
            if (workshopIds == null || workshopIds.Count == 0)
                return results;

            var detailsMap = await GetWorkshopDetailsBatchAsync(workshopIds);
            foreach (var id in workshopIds)
            {
                if (detailsMap.TryGetValue(id, out var details) && !string.IsNullOrEmpty(details.PreviewUrl))
                {
                    results.Add(details.PreviewUrl);
                }
                else
                {
                    results.Add(string.Empty);
                }
            }

            return results;
        }

        public async Task<WorkshopCollectionInfo?> GetCollectionDetailsAsync(
            string collectionId,
            CancellationToken cancellationToken = default)
        {
            var lookupResult = await GetCollectionDetailsWithStatusAsync(collectionId, cancellationToken);
            return lookupResult.IsFound ? lookupResult.CollectionInfo : null;
        }

        public async Task<WorkshopCollectionLookupResult> GetCollectionDetailsWithStatusAsync(
            string collectionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(collectionId))
            {
                return WorkshopCollectionLookupResult.NotFound();
            }

            var childrenResult = await FetchCollectionChildrenAsync(new List<string> { collectionId }, cancellationToken);
            if (childrenResult.Status == CollectionChildrenFetchStatus.Unavailable)
            {
                return WorkshopCollectionLookupResult.Unavailable();
            }

            if (!childrenResult.ChildrenMap.TryGetValue(collectionId, out var addonIds))
            {
                return WorkshopCollectionLookupResult.NotFound();
            }

            var details = await GetWorkshopDetailsAsync(collectionId);
            var collectionInfo = new WorkshopCollectionInfo
            {
                Id = collectionId,
                Title = details?.Title ?? string.Empty,
                Description = details?.Description ?? string.Empty,
                PreviewUrl = details?.PreviewUrl ?? string.Empty,
                AddonIds = addonIds
            };

            return WorkshopCollectionLookupResult.Found(collectionInfo);
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

        private bool TryGetCachedDetails(string workshopId, out WorkshopItemDetails? details)
        {
            details = null;
            if (string.IsNullOrWhiteSpace(workshopId))
            {
                return false;
            }

            if (_metadataCache.TryGetValue(workshopId, out var entry))
            {
                if (entry.ExpiresUtc >= DateTime.UtcNow)
                {
                    details = entry.Details;
                    return true;
                }

                _metadataCache.TryRemove(workshopId, out _);
            }

            return false;
        }

        private void SetCachedDetails(string workshopId, WorkshopItemDetails details, TimeSpan ttl)
        {
            _metadataCache[workshopId] = new CacheEntry
            {
                Details = details,
                ExpiresUtc = DateTime.UtcNow.Add(ttl)
            };
        }

        private void SetNegativeCache(string workshopId)
        {
            _metadataCache[workshopId] = new CacheEntry
            {
                Details = null,
                ExpiresUtc = DateTime.UtcNow.Add(MetadataNegativeTtl)
            };
        }

        private void TryHydrateFullDescription(WorkshopItemDetails details)
        {
            if (details == null || string.IsNullOrWhiteSpace(details.Id))
            {
                return;
            }

            if (DiskMetadataCache.TryGetFullDescription(details.Id, out var fullDescription) &&
                !string.IsNullOrWhiteSpace(fullDescription))
            {
                details.Description = fullDescription;
                SetCachedDetails(details.Id, details, MetadataCacheTtl);
            }
        }

        private static bool IsDiskCacheValid(
            WorkshopItemDetails details,
            DateTime fetchedAtUtc,
            long? localUpdatedUnix,
            DateTime nowUtc,
            bool treatAsHot)
        {
            if (details == null)
            {
                return false;
            }

            if (localUpdatedUnix.HasValue && localUpdatedUnix.Value > 0)
            {
                if (details.TimeUpdated > 0 && details.TimeUpdated >= localUpdatedUnix.Value)
                {
                    return true;
                }

                return false;
            }

            var ttl = treatAsHot ? HotDiskCacheTtl : ColdDiskCacheTtl;
            return nowUtc - fetchedAtUtc <= ttl;
        }

        private static bool HasTags(WorkshopItemDetails details)
        {
            return details.Tags != null && details.Tags.Any(tag => !string.IsNullOrWhiteSpace(tag));
        }

        private static (int batchSize, int maxConcurrency) SelectBatchPlan(int count)
        {
            if (count <= 25)
            {
                return (count, 1);
            }

            if (count <= 120)
            {
                return (25, 4);
            }

            if (count <= 300)
            {
                return (25, 6);
            }

            if (count <= 750)
            {
                return (50, 8);
            }

            return (50, 8);
        }

        private async Task<Dictionary<string, WorkshopItemDetails>> FetchPublishedFileDetailsAsync(
            List<string> ids,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, WorkshopItemDetails>(StringComparer.Ordinal);
            if (ids == null || ids.Count == 0)
            {
                return results;
            }

            await rateLimiter.WaitAsync(cancellationToken);
            try
            {
                var elapsed = (DateTime.Now - lastRequestTime).TotalMilliseconds;
                if (elapsed < MinRequestInterval)
                {
                    await Task.Delay(MinRequestInterval - (int)elapsed, cancellationToken);
                }

                var parameters = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("itemcount", ids.Count.ToString())
                };

                for (int i = 0; i < ids.Count; i++)
                {
                    parameters.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", ids[i]));
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, SteamApiUrl)
                {
                    Content = new FormUrlEncodedContent(parameters)
                };
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                lastRequestTime = DateTime.Now;

                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                var json = await ReadSteamApiJsonAsync(
                    response.Content,
                    cancellationToken);
                var jsonObject = JObject.Parse(json);

                var fileDetailsArray = jsonObject["response"]?["publishedfiledetails"] as JArray;
                if (fileDetailsArray == null)
                {
                    return results;
                }

                foreach (var fileDetails in fileDetailsArray)
                {
                    var result = fileDetails?["result"]?.Value<int>() ?? 0;
                    var id = fileDetails?["publishedfileid"]?.ToString();
                    if (result != 1 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    var details = new WorkshopItemDetails
                    {
                        Id = id,
                        Title = fileDetails?["title"]?.ToString(),
                        Description = fileDetails?["description"]?.ToString(),
                        PreviewUrl = fileDetails?["preview_url"]?.ToString(),
                        TimeCreated = fileDetails?["time_created"]?.Value<long>() ?? 0,
                        TimeUpdated = fileDetails?["time_updated"]?.Value<long>() ?? 0,
                        Creator = fileDetails?["creator"]?.ToString(),
                        FileSize = (ulong)(fileDetails?["file_size"]?.Value<long>() ?? 0),
                        Tags = ExtractTags(fileDetails?["tags"])
                    };

                    results[id] = details;
                }

                return results;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return results;
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        private static string[]? ExtractTags(JToken? token)
        {
            if (token == null)
            {
                return null;
            }

            var tags = new List<string>();
            if (token is JArray array)
            {
                foreach (var entry in array)
                {
                    var tag = entry?["tag"]?.ToString() ?? entry?.ToString();
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag.Trim());
                    }
                }
            }
            else
            {
                var raw = token.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var separators = (raw.Contains(',') || raw.Contains(';'))
                        ? new[] { ',', ';' }
                        : new[] { ' ', '\t', '\r', '\n' };

                    foreach (var part in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = part.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            tags.Add(trimmed);
                        }
                    }
                }
            }

            if (tags.Count == 0)
            {
                return null;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var tag in tags)
            {
                if (unique.Add(tag))
                {
                    result.Add(tag);
                }
            }

            return result.Count == 0 ? null : result.ToArray();
        }

        internal static async Task<string> ReadSteamApiJsonAsync(
            HttpContent content,
            CancellationToken cancellationToken = default,
            int maximumBytes = MaximumSteamApiJsonBytes)
        {
            var bytes = await BoundedHttpContentReader.ReadAsync(
                content,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(bytes);
            return json.Length > 0 && json[0] == '\uFEFF'
                ? json.Substring(1)
                : json;
        }

        private async Task<CollectionChildrenFetchResult> FetchCollectionChildrenAsync(
            List<string> collectionIds,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (collectionIds == null || collectionIds.Count == 0)
            {
                return new CollectionChildrenFetchResult(CollectionChildrenFetchStatus.Success, results);
            }

            await rateLimiter.WaitAsync(cancellationToken);
            try
            {
                var elapsed = (DateTime.Now - lastRequestTime).TotalMilliseconds;
                if (elapsed < MinRequestInterval)
                {
                    await Task.Delay(MinRequestInterval - (int)elapsed, cancellationToken);
                }

                var parameters = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("collectioncount", collectionIds.Count.ToString())
                };

                for (int i = 0; i < collectionIds.Count; i++)
                {
                    parameters.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", collectionIds[i]));
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, SteamCollectionApiUrl)
                {
                    Content = new FormUrlEncodedContent(parameters)
                };
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                lastRequestTime = DateTime.Now;

                if (!response.IsSuccessStatusCode)
                {
                    return new CollectionChildrenFetchResult(CollectionChildrenFetchStatus.Unavailable, results);
                }

                var json = await ReadSteamApiJsonAsync(
                    response.Content,
                    cancellationToken);
                var jsonObject = JObject.Parse(json);
                var collections = jsonObject["response"]?["collectiondetails"] as JArray;
                if (collections == null)
                {
                    return new CollectionChildrenFetchResult(CollectionChildrenFetchStatus.Unavailable, results);
                }

                foreach (var collection in collections)
                {
                    var id = collection?["publishedfileid"]?.ToString();
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    var resultCode = collection?["result"]?.Value<int?>();
                    if (resultCode.HasValue && resultCode.Value != 1)
                    {
                        continue;
                    }

                    var children = new List<string>();
                    var childrenArray = collection?["children"] as JArray;
                    if (childrenArray != null)
                    {
                        foreach (var child in childrenArray)
                        {
                            var childId = child?["publishedfileid"]?.ToString();
                            if (!string.IsNullOrEmpty(childId))
                            {
                                children.Add(childId);
                            }
                        }
                    }

                    results[id] = children;
                }

                return new CollectionChildrenFetchResult(CollectionChildrenFetchStatus.Success, results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new CollectionChildrenFetchResult(CollectionChildrenFetchStatus.Unavailable, results);
            }
            finally
            {
                rateLimiter.Release();
            }
        }
    }
}
