using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using SkiaSharp;

namespace GmodAddonManager.Core.Services
{
    public class WorkshopIconResolver : IIconResolver
    {
        private readonly ISteamPathDetector _steamPathDetector;
        private SteamWorkshopService? _workshopService;
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _localIconsPath;
        private readonly string _iconIndexPath;
        private readonly object _iconIndexLock = new object();
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;

        private const int MaxConcurrentDownloads = 16;
        private static readonly int DownloadDelayMs = 0;
        private const int IconSize = 256;

        // PNG signature bytes
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        
        static WorkshopIconResolver()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager/1.0");
        }

        public WorkshopIconResolver(
            ISteamPathDetector steamPathDetector,
            SteamWorkshopService? workshopService,
            string appDataPath)
        {
            _steamPathDetector = steamPathDetector;
            _workshopService = workshopService;
            
            _localIconsPath = Path.Combine(appDataPath, "icons");
            if (!Directory.Exists(_localIconsPath))
            {
                Directory.CreateDirectory(_localIconsPath);
            }
            _iconIndexPath = Path.Combine(_localIconsPath, "index.json");

            _downloadSemaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);

            // Setup Polly retry policy
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        outcome.Result?.Dispose();
                        // Log retry attempt
                    });
        }

        /// <summary>
        /// Set the SteamWorkshopService instance (for circular dependency resolution)
        /// </summary>
        public void SetWorkshopService(SteamWorkshopService workshopService)
        {
            _workshopService = workshopService;
        }

        public Task<string?> GetIconAsync(ulong workshopId)
        {
            return GetIconAsync(workshopId, null);
        }

        public async Task<string?> GetIconAsync(ulong workshopId, string? previewUrl)
        {
            try
            {
                // Stage A: Check local icons cache
                var localIconPath = GetLocalIconPath(workshopId);
                if (File.Exists(localIconPath))
                {
                    return localIconPath;
                }

                // Stage B: Check GMOD .cache files
                var cacheIcon = await TryCopyFromGModCacheAsync(workshopId);
                if (cacheIcon != null)
                {
                    return cacheIcon;
                }

                // Stage C: Check Steam library cache
                var libraryIcon = await TryConvertFromSteamLibraryCacheAsync(workshopId);
                if (libraryIcon != null)
                {
                    return libraryIcon;
                }

                // Stage D: Download from network
                return await DownloadFromNetworkAsync(workshopId, previewUrl);
            }
            catch (Exception)
            {
                // Log error
                return null;
            }
        }

        public async Task PrewarmIconsAsync(ulong[] workshopIds)
        {
            if (workshopIds == null || workshopIds.Length == 0)
                return;

            var missingIds = new List<ulong>();
            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
            var tasks = workshopIds.Select(async id =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var localIconPath = GetLocalIconPath(id);
                    if (File.Exists(localIconPath))
                    {
                        return;
                    }

                    var cacheIcon = await TryCopyFromGModCacheAsync(id);
                    if (cacheIcon != null)
                    {
                        return;
                    }

                    var libraryIcon = await TryConvertFromSteamLibraryCacheAsync(id);
                    if (libraryIcon != null)
                    {
                        return;
                    }

                    lock (missingIds)
                    {
                        missingIds.Add(id);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            if (missingIds.Count == 0 || _workshopService == null)
            {
                return;
            }

            var distinctMissing = missingIds.Distinct().ToList();
            var detailsMap = await _workshopService.GetWorkshopDetailsBatchAsync(
                distinctMissing.Select(id => id.ToString()).ToList(),
                default,
                treatAsHot: false);

            var urlMap = new Dictionary<ulong, string>();
            foreach (var id in distinctMissing)
            {
                if (detailsMap.TryGetValue(id.ToString(), out var details) &&
                    !string.IsNullOrWhiteSpace(details.PreviewUrl))
                {
                    urlMap[id] = details.PreviewUrl;
                }
            }

            await DownloadPreviewsAsync(urlMap);
        }

        public Task ClearCacheAsync()
        {
            return Task.Run(() =>
            {
                if (Directory.Exists(_localIconsPath))
                {
                    var files = Directory.GetFiles(_localIconsPath, "*.png");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore file deletion errors
                        }
                    }

                    try
                    {
                        if (File.Exists(_iconIndexPath))
                        {
                            File.Delete(_iconIndexPath);
                        }
                    }
                    catch
                    {
                        // Ignore index deletion errors
                    }
                }
            });
        }

        public Task CleanupStaleIconsAsync(IReadOnlyCollection<string>? activeIds, TimeSpan staleAfter)
        {
            if (staleAfter <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_localIconsPath))
                    {
                        return;
                    }

                    var activeSet = new HashSet<string>(StringComparer.Ordinal);
                    if (activeIds != null)
                    {
                        foreach (var id in activeIds)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                activeSet.Add(id);
                            }
                        }
                    }

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var cutoffSeconds = (long)staleAfter.TotalSeconds;
                    var index = LoadIconIndex();

                    var fileIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var file in Directory.EnumerateFiles(_localIconsPath, "*.png"))
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }
                        fileIds.Add(id);
                    }

                    if (index.Count > 0)
                    {
                        foreach (var key in index.Keys.ToList())
                        {
                            if (!fileIds.Contains(key))
                            {
                                index.Remove(key);
                            }
                        }
                    }

                    foreach (var id in fileIds)
                    {
                        if (activeSet.Contains(id))
                        {
                            index[id] = now;
                            continue;
                        }

                        if (!index.TryGetValue(id, out var lastSeen))
                        {
                            index[id] = now;
                            continue;
                        }

                        if (now - lastSeen >= cutoffSeconds)
                        {
                            try
                            {
                                File.Delete(Path.Combine(_localIconsPath, $"{id}.png"));
                            }
                            catch
                            {
                                // Ignore delete errors
                            }

                            index.Remove(id);
                        }
                    }

                    SaveIconIndex(index);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            });
        }

        private string GetLocalIconPath(ulong workshopId)
        {
            return Path.Combine(_localIconsPath, $"{workshopId}.png");
        }

        private Dictionary<string, long> LoadIconIndex()
        {
            lock (_iconIndexLock)
            {
                try
                {
                    if (!File.Exists(_iconIndexPath))
                    {
                        return new Dictionary<string, long>(StringComparer.Ordinal);
                    }

                    var json = File.ReadAllText(_iconIndexPath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, long>>(json);
                    if (data == null)
                    {
                        return new Dictionary<string, long>(StringComparer.Ordinal);
                    }

                    return new Dictionary<string, long>(data, StringComparer.Ordinal);
                }
                catch
                {
                    return new Dictionary<string, long>(StringComparer.Ordinal);
                }
            }
        }

        private void SaveIconIndex(Dictionary<string, long> index)
        {
            lock (_iconIndexLock)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(index);
                    var tempPath = _iconIndexPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Copy(tempPath, _iconIndexPath, true);
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore index write errors
                }
            }
        }

        private async Task<string?> TryCopyFromGModCacheAsync(ulong workshopId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var steamPath = _steamPathDetector.DetectSteamPath();
                    if (string.IsNullOrEmpty(steamPath))
                        return null;

                    // Check multiple possible Steam library locations
                    var possiblePaths = new List<string>
                    {
                        Path.Combine(steamPath, "steamapps", "common", "GarrysMod", "garrysmod", "cache", "workshop", $"{workshopId}.cache")
                    };

                    // Add library paths
                    var libraryPaths = _steamPathDetector.GetSteamLibraryPaths(steamPath);
                    foreach (var libraryPath in libraryPaths)
                    {
                        possiblePaths.Add(Path.Combine(libraryPath, "steamapps", "common", "GarrysMod", "garrysmod", "cache", "workshop", $"{workshopId}.cache"));
                    }

                    foreach (var cachePath in possiblePaths)
                    {
                        if (File.Exists(cachePath))
                        {
                            // Verify it's actually a PNG file
                            if (IsPngFile(cachePath))
                            {
                                var localPath = GetLocalIconPath(workshopId);
                                File.Copy(cachePath, localPath, overwrite: true);
                                return localPath;
                            }
                        }
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            });
        }

        private async Task<string?> TryConvertFromSteamLibraryCacheAsync(ulong workshopId)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var steamPath = _steamPathDetector.DetectSteamPath();
                    if (string.IsNullOrEmpty(steamPath))
                        return null;

                    // Check multiple possible Steam library locations
                    var possiblePaths = new List<string>
                    {
                        Path.Combine(steamPath, "steamapps", "workshop", "librarycache", $"{workshopId}_preview.jpg")
                    };

                    // Add library paths
                    var libraryPaths = _steamPathDetector.GetSteamLibraryPaths(steamPath);
                    foreach (var libraryPath in libraryPaths)
                    {
                        possiblePaths.Add(Path.Combine(libraryPath, "steamapps", "workshop", "librarycache", $"{workshopId}_preview.jpg"));
                    }

                    foreach (var jpegPath in possiblePaths)
                    {
                        if (File.Exists(jpegPath))
                        {
                            var localPath = GetLocalIconPath(workshopId);
                            await ConvertJpegToPngAsync(jpegPath, localPath);
                            return localPath;
                        }
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            });
        }

        private async Task<string?> DownloadFromNetworkAsync(ulong workshopId, string? previewUrl)
        {
            await _downloadSemaphore.WaitAsync();
            try
            {
                // Add delay to respect rate limiting
                if (DownloadDelayMs > 0)
                {
                    await Task.Delay(DownloadDelayMs);
                }

                // Get preview URL from Steam API (fallback if not provided)
                var url = previewUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    if (_workshopService == null)
                        return null;

                    var details = await _workshopService.GetWorkshopDetailsAsync(workshopId.ToString());
                    url = details?.PreviewUrl;
                }

                if (string.IsNullOrWhiteSpace(url))
                    return null;

                // Download with retry policy
                using var response = await _retryPolicy.ExecuteAsync(async () =>
                    await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead));

                if (!response.IsSuccessStatusCode)
                    return null;

                var imageBytes = await BoundedHttpContentReader.ReadAsync(
                    response.Content,
                    BoundedHttpContentReader.DefaultImageLimitBytes);
                
                // Process and save as PNG using SkiaSharp
                using var bitmap = SKBitmap.Decode(imageBytes);
                if (bitmap == null)
                    return null;
                    
                // Resize to 512x512 if needed
                using var resized = ResizeBitmap(bitmap, IconSize, IconSize);
                
                var localPath = GetLocalIconPath(workshopId);
                using var data = resized.Encode(SKEncodedImageFormat.Png, 100);
                await Task.Run(() => File.WriteAllBytes(localPath, data.ToArray()));
                
                return localPath;
            }
            catch
            {
                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        private async Task DownloadPreviewsAsync(Dictionary<ulong, string> urlMap)
        {
            if (urlMap == null || urlMap.Count == 0)
            {
                return;
            }

            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
            var tasks = urlMap.Select(async kvp =>
            {
                var id = kvp.Key;
                var url = kvp.Value;
                var localPath = GetLocalIconPath(id);
                if (File.Exists(localPath))
                {
                    return;
                }

                await semaphore.WaitAsync();
                try
                {
                    using var response = await _retryPolicy.ExecuteAsync(async () =>
                        await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead));
                    if (!response.IsSuccessStatusCode)
                    {
                        return;
                    }

                    var imageBytes = await BoundedHttpContentReader.ReadAsync(
                        response.Content,
                        BoundedHttpContentReader.DefaultImageLimitBytes);
                    using var bitmap = SKBitmap.Decode(imageBytes);
                    if (bitmap == null)
                    {
                        return;
                    }

                    using var resized = ResizeBitmap(bitmap, IconSize, IconSize);
                    using var data = resized.Encode(SKEncodedImageFormat.Png, 90);
                    await Task.Run(() => File.WriteAllBytes(localPath, data.ToArray()));
                }
                catch
                {
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private async Task ConvertJpegToPngAsync(string jpegPath, string pngPath)
        {
            await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(jpegPath);
                if (bitmap == null)
                    return;
                    
                // Resize to 512x512, maintaining aspect ratio with padding if needed
                using var resized = ResizeBitmap(bitmap, IconSize, IconSize);
                
                using var data = resized.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(pngPath, data.ToArray());
            });
        }
        
        /// <summary>
        /// Resizes a bitmap to fit within the target dimensions while maintaining aspect ratio.
        /// NOTE: The caller is responsible for disposing both the source and returned bitmaps.
        /// </summary>
        private SKBitmap ResizeBitmap(SKBitmap source, int targetWidth, int targetHeight)
        {
            // Calculate scaling factor while maintaining aspect ratio
            float scale = Math.Min((float)targetWidth / source.Width, (float)targetHeight / source.Height);
            int scaledWidth = (int)(source.Width * scale);
            int scaledHeight = (int)(source.Height * scale);
            
            // Create new bitmap with target size
            var result = new SKBitmap(targetWidth, targetHeight);
            using var canvas = new SKCanvas(result);
            
            // Clear with transparent background
            canvas.Clear(SKColors.Transparent);
            
            // Calculate position to center the image
            int x = (targetWidth - scaledWidth) / 2;
            int y = (targetHeight - scaledHeight) / 2;
            
            // Draw the resized image
            var destRect = new SKRect(x, y, x + scaledWidth, y + scaledHeight);
            canvas.DrawBitmap(source, destRect);
            
            return result;
        }

        private bool IsPngFile(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                if (fs.Length < PngSignature.Length)
                    return false;

                var buffer = new byte[PngSignature.Length];
                var bytesRead = fs.Read(buffer, 0, buffer.Length);
                
                return bytesRead == PngSignature.Length && buffer.SequenceEqual(PngSignature);
            }
            catch
            {
                return false;
            }
        }
    }
}
