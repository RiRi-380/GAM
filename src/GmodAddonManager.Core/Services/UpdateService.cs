using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GmodAddonManager.Core.Services
{
    public class UpdateService
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/RiRi-380/GAM/releases/latest";
        private const string UPDATE_CHECK_FILE = "last_update_check.txt";
        private static readonly HttpClient sharedHttpClient = new HttpClient();
        private readonly HttpClient httpClient;
        private readonly string currentVersion;
        private static readonly TimeSpan DefaultDownloadInactivityTimeout = TimeSpan.FromSeconds(30);

        static UpdateService()
        {
            sharedHttpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager");
            sharedHttpClient.Timeout = TimeSpan.FromMinutes(15);
        }

        public UpdateService(string currentVersion)
            : this(currentVersion, null)
        {
        }

        internal UpdateService(string currentVersion, HttpClient? httpClient)
        {
            this.currentVersion = currentVersion;
            this.httpClient = httpClient ?? sharedHttpClient;
        }

        internal TimeSpan DownloadInactivityTimeout { get; set; } = DefaultDownloadInactivityTimeout;

        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                if (ShouldSkipUpdateCheck())
                    return null;

                var response = await httpClient.GetStringAsync(GITHUB_API_URL);
                var release = JsonConvert.DeserializeObject<GitHubRelease>(response);
                SaveLastCheckTime();

                if (release != null && IsNewerVersion(release.TagName))
                {
                    var installerAsset = release.Assets?.FirstOrDefault(a => IsInstallerAssetName(a.Name));

                    if (installerAsset != null)
                    {
                        return new UpdateInfo
                        {
                            Version = release.TagName,
                            ReleaseNotes = release.Body,
                            DownloadUrl = installerAsset.BrowserDownloadUrl,
                            PublishedAt = release.PublishedAt
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex.Message}");
            }

            return null;
        }

        private bool ShouldSkipUpdateCheck()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var checkFilePath = Path.Combine(appDataPath, "GmodAddonManager", UPDATE_CHECK_FILE);

                if (File.Exists(checkFilePath))
                {
                    var lastCheck = File.ReadAllText(checkFilePath);
                    if (DateTime.TryParse(lastCheck, out var lastCheckDate))
                    {
                        return (DateTime.Now - lastCheckDate).TotalDays < 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read last update check time: {ex.Message}");
            }

            return false;
        }

        internal static bool IsInstallerAssetName(string? assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            return assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                   (assetName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                    assetName.Contains("installer", StringComparison.OrdinalIgnoreCase));
        }

        private void SaveLastCheckTime()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var gamPath = Path.Combine(appDataPath, "GmodAddonManager");
                Directory.CreateDirectory(gamPath);

                var checkFilePath = Path.Combine(gamPath, UPDATE_CHECK_FILE);
                File.WriteAllText(checkFilePath, DateTime.Now.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save last update check time: {ex.Message}");
            }
        }

        private bool IsNewerVersion(string remoteVersion)
        {
            var remote = remoteVersion.TrimStart('v');
            var current = currentVersion.TrimStart('v');

            return Version.TryParse(remote, out var remoteVer) &&
                   Version.TryParse(current, out var currentVer) &&
                   remoteVer > currentVer;
        }

        public async Task DownloadAndInstallUpdateAsync(string downloadUrl, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "GAM-Update-Setup.exe");
            await DownloadInstallerAsync(downloadUrl, tempPath, progress, cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            Environment.Exit(0);
        }

        internal async Task DownloadInstallerAsync(string downloadUrl, string destinationPath, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var partialPath = destinationPath + ".download";
            try
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                using var response = await GetDownloadResponseAsync(downloadUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                progress?.Report(new UpdateDownloadProgress(0, totalBytes));

                {
                    await using var responseStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = new FileStream(
                        partialPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        useAsync: true);

                    var buffer = new byte[81920];
                    long downloadedBytes = 0;

                    while (true)
                    {
                        var bytesRead = await ReadWithInactivityTimeoutAsync(responseStream, buffer, cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        downloadedBytes += bytesRead;
                        progress?.Report(new UpdateDownloadProgress(downloadedBytes, totalBytes));
                    }

                    await fileStream.FlushAsync(cancellationToken);
                }

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.Move(partialPath, destinationPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(partialPath))
                    {
                        File.Delete(partialPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup; preserve the original download error.
                }

                throw;
            }
        }

        private async Task<HttpResponseMessage> GetDownloadResponseAsync(string downloadUrl, CancellationToken cancellationToken)
        {
            using var timeoutCts = CreateDownloadTimeoutToken(cancellationToken);
            try
            {
                return await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateDownloadTimeoutException();
            }
        }

        private async Task<int> ReadWithInactivityTimeoutAsync(Stream responseStream, byte[] buffer, CancellationToken cancellationToken)
        {
            using var timeoutCts = CreateDownloadTimeoutToken(cancellationToken);
            try
            {
                return await responseStream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateDownloadTimeoutException();
            }
        }

        private CancellationTokenSource CreateDownloadTimeoutToken(CancellationToken cancellationToken)
        {
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (DownloadInactivityTimeout != Timeout.InfiniteTimeSpan)
            {
                timeoutCts.CancelAfter(DownloadInactivityTimeout);
            }

            return timeoutCts;
        }

        private TimeoutException CreateDownloadTimeoutException()
        {
            return new TimeoutException($"No update download data was received for {DownloadInactivityTimeout.TotalSeconds:F0} seconds.");
        }

        public static string FormatDownloadProgress(UpdateDownloadProgress progress)
        {
            var downloadedMiB = progress.DownloadedBytes / 1024d / 1024d;

            if (progress.TotalBytes is long totalBytes && totalBytes > 0)
            {
                var totalMiB = totalBytes / 1024d / 1024d;
                var percent = Math.Clamp(progress.Percentage ?? 0d, 0d, 100d);
                return $"Downloading update... {downloadedMiB:F1} / {totalMiB:F1} MB ({percent:F0}%)";
            }

            return $"Downloading update... {downloadedMiB:F1} MB";
        }
    }

    public readonly struct UpdateDownloadProgress
    {
        public UpdateDownloadProgress(long downloadedBytes, long? totalBytes)
        {
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
        }

        public long DownloadedBytes { get; }
        public long? TotalBytes { get; }

        public double? Percentage =>
            TotalBytes is long totalBytes && totalBytes > 0
                ? DownloadedBytes * 100d / totalBytes
                : null;
    }

    public class UpdateInfo
    {
        public string Version { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
    }

    public class GitHubRelease
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonProperty("body")]
        public string Body { get; set; } = string.Empty;

        [JsonProperty("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonProperty("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    public class GitHubAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
