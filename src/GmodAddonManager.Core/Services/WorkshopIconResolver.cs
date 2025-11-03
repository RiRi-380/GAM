using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;

        private const int MaxConcurrentDownloads = 8;
        private const int DownloadDelayMs = 50;
        private const int IconSize = 512;

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
                        var result = outcome.Result;
                        var exception = outcome.Exception;
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

        public async Task<string?> GetIconAsync(ulong workshopId)
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
                return await DownloadFromNetworkAsync(workshopId);
            }
            catch (Exception ex)
            {
                // Log error
                return null;
            }
        }

        public async Task PrewarmIconsAsync(ulong[] workshopIds)
        {
            if (workshopIds == null || workshopIds.Length == 0)
                return;

            var tasks = new List<Task>();
            using var semaphore = new SemaphoreSlim(4, 4); // Limit concurrent prewarm operations

            foreach (var id in workshopIds)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await GetIconAsync(id);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
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
                }
            });
        }

        private string GetLocalIconPath(ulong workshopId)
        {
            return Path.Combine(_localIconsPath, $"{workshopId}.png");
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

        private async Task<string?> DownloadFromNetworkAsync(ulong workshopId)
        {
            await _downloadSemaphore.WaitAsync();
            try
            {
                // Add delay to respect rate limiting
                await Task.Delay(DownloadDelayMs);

                // Get preview URL from Steam API
                if (_workshopService == null)
                    return null;

                var details = await _workshopService.GetWorkshopDetailsAsync(workshopId.ToString());
                if (details?.PreviewUrl == null)
                    return null;

                // Download with retry policy
                var response = await _retryPolicy.ExecuteAsync(async () =>
                    await _httpClient.GetAsync(details.PreviewUrl));

                if (!response.IsSuccessStatusCode)
                    return null;

                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                
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