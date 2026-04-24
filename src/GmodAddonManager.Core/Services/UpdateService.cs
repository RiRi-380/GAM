using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    public class UpdateService
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/RiRi-380/GAM/releases/latest";
        private const string UPDATE_CHECK_FILE = "last_update_check.txt";
        private const string UpdateManifestAssetPrefix = "GAM-UpdateManifest-";
        private const string UpdateManifestJsonExtension = ".json";
        private const string UpdateManifestSignatureExtension = ".sig";
        private const long MaxManifestBytes = 64 * 1024;
        private const long MaxSignatureBytes = 16 * 1024;
        private const long MaxInstallerBytes = 512L * 1024 * 1024;
        private const string UpdateManifestPublicKeySpkiBase64 =
            "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyGUpUR+SaWQJFSqucX45Gbl0pBn9tlaYbr3U5wDTv/GT4yvoqGHDiNYyt7el59Z1DV8V8tU5Kstc0IdGWOlv+V1dKqC+1ShSC7cj9AqegmnxG8jnDvJSpYg4S7iTc8JEV8c5t1WLBAVjswT63EBU9DsqdhO21r6GmCJZemu+8wa09EZu+IAO69SSjZrBaXW0vwaEq+Q6bsloRwvGlAKmaiUCjz8BJJv/82yLZTLpJH4lpwOYI5MrS+3/w0GQ+pK9Xq7yNH1KfO+ZfGdXDqqnOHzeBVqBj+gDr7fxDyRI5PE60Dw73u9RFh31l93dM6KtWYHwUE8mm1p2xV02bnqpshNO0DrgAnPh1jo7cBFazVNEDiBiNWsCrJ57i3fOVn57uIf8X5oE7JblNEKKDxCNknqL0mZMcR8d+KurA+u3lha9z1uussLigmWYFUmNvMUfEpAr332UYwvneCOWxDRxbMjcfuB6KRHqZZ9SpBKm2HzKwOJkIp1rf62hCe7EXYiJAgMBAAE=";

        private static readonly HttpClient sharedHttpClient = new HttpClient();
        private static readonly TimeSpan DefaultDownloadInactivityTimeout = TimeSpan.FromSeconds(30);
        private readonly HttpClient httpClient;
        private readonly string currentVersion;

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
                {
                    return null;
                }

                var response = await httpClient.GetStringAsync(GITHUB_API_URL);
                var release = JsonConvert.DeserializeObject<GitHubRelease>(response);
                SaveLastCheckTime();

                if (release == null || !IsNewerVersion(release.TagName))
                {
                    return null;
                }

                var verifiedManifest = await GetVerifiedManifestAsync(release);
                if (verifiedManifest == null)
                {
                    Debug.WriteLine("Update check skipped: release does not have a valid signed update manifest.");
                    return null;
                }

                return new UpdateInfo
                {
                    Version = release.TagName,
                    ReleaseNotes = release.Body,
                    DownloadUrl = verifiedManifest.InstallerAsset.BrowserDownloadUrl,
                    PublishedAt = release.PublishedAt,
                    InstallerAssetName = verifiedManifest.Manifest.InstallerAssetName,
                    InstallerSha256 = verifiedManifest.Manifest.InstallerSha256,
                    InstallerSize = verifiedManifest.Manifest.InstallerSize
                };
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

        private async Task<VerifiedUpdateManifest?> GetVerifiedManifestAsync(GitHubRelease release)
        {
            var assets = release.Assets ?? Array.Empty<GitHubAsset>();
            var releaseVersion = release.TagName.TrimStart('v');
            var manifestAssetName = $"{UpdateManifestAssetPrefix}{releaseVersion}{UpdateManifestJsonExtension}";
            var signatureAssetName = $"{UpdateManifestAssetPrefix}{releaseVersion}{UpdateManifestSignatureExtension}";
            var manifestAsset = assets.FirstOrDefault(a => string.Equals(a.Name, manifestAssetName, StringComparison.Ordinal));
            var signatureAsset = assets.FirstOrDefault(a => string.Equals(a.Name, signatureAssetName, StringComparison.Ordinal));

            if (manifestAsset == null || signatureAsset == null)
            {
                return null;
            }

            if (!IsHttpsUrl(manifestAsset.BrowserDownloadUrl) || !IsHttpsUrl(signatureAsset.BrowserDownloadUrl))
            {
                return null;
            }

            var manifestBytes = await DownloadBoundedBytesAsync(manifestAsset.BrowserDownloadUrl, MaxManifestBytes);
            var signatureBytes = await DownloadBoundedBytesAsync(signatureAsset.BrowserDownloadUrl, MaxSignatureBytes);

            if (!VerifyManifestSignature(manifestBytes, signatureBytes))
            {
                return null;
            }

            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(Encoding.UTF8.GetString(manifestBytes));
            if (!TryValidateManifestForRelease(manifest, release, out var installerAsset))
            {
                return null;
            }

            return new VerifiedUpdateManifest(manifest!, installerAsset!);
        }

        private async Task<byte[]> DownloadBoundedBytesAsync(string url, long maxBytes)
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long length && length > maxBytes)
            {
                throw new InvalidDataException($"Update metadata is too large: {length} bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            long totalBytes = 0;

            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidDataException($"Update metadata exceeded {maxBytes} bytes.");
                }

                memory.Write(buffer, 0, bytesRead);
            }

            return memory.ToArray();
        }

        internal static bool VerifyManifestSignature(byte[] manifestBytes, byte[] signatureBytes)
        {
            return VerifyManifestSignature(
                manifestBytes,
                signatureBytes,
                Convert.FromBase64String(UpdateManifestPublicKeySpkiBase64));
        }

        internal static bool VerifyManifestSignature(byte[] manifestBytes, byte[] signatureBytes, byte[] publicKeySubjectPublicKeyInfo)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeySubjectPublicKeyInfo, out _);
            return rsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        private static bool TryValidateManifestForRelease(UpdateManifest? manifest, GitHubRelease release, out GitHubAsset? installerAsset)
        {
            installerAsset = null;
            if (manifest == null ||
                manifest.SchemaVersion != 1 ||
                !VersionsMatch(manifest.Version, release.TagName) ||
                !IsSafeAssetFileName(manifest.InstallerAssetName) ||
                !IsInstallerAssetName(manifest.InstallerAssetName) ||
                !IsValidSha256Hex(manifest.InstallerSha256) ||
                manifest.InstallerSize <= 0 ||
                manifest.InstallerSize > MaxInstallerBytes)
            {
                return false;
            }

            installerAsset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, manifest.InstallerAssetName, StringComparison.Ordinal));
            if (installerAsset == null || !IsHttpsUrl(installerAsset.BrowserDownloadUrl))
            {
                return false;
            }

            return installerAsset.Size <= 0 || installerAsset.Size == manifest.InstallerSize;
        }

        private static bool VersionsMatch(string manifestVersion, string releaseTagName)
        {
            return string.Equals(
                NormalizeVersionTag(manifestVersion),
                NormalizeVersionTag(releaseTagName),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersionTag(string version)
        {
            var trimmed = version.Trim();
            return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed : $"v{trimmed}";
        }

        private static bool IsSafeAssetFileName(string assetName)
        {
            return !string.IsNullOrWhiteSpace(assetName) &&
                   string.Equals(assetName, Path.GetFileName(assetName), StringComparison.Ordinal) &&
                   assetName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static bool IsValidSha256Hex(string value)
        {
            return value.Length == 64 && value.All(IsHex);
        }

        private static bool IsHex(char value)
        {
            return (value >= '0' && value <= '9') ||
                   (value >= 'a' && value <= 'f') ||
                   (value >= 'A' && value <= 'F');
        }

        private static bool IsHttpsUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   uri.Scheme == Uri.UriSchemeHttps;
        }

        public async Task DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl) ||
                string.IsNullOrWhiteSpace(updateInfo.InstallerSha256) ||
                updateInfo.InstallerSize <= 0)
            {
                throw new InvalidOperationException("Update metadata is incomplete. Refusing to execute installer.");
            }

            var installerFileName = IsSafeAssetFileName(updateInfo.InstallerAssetName)
                ? updateInfo.InstallerAssetName
                : "GAM-Update-Setup.exe";
            var tempPath = Path.Combine(Path.GetTempPath(), installerFileName);

            await DownloadInstallerAsync(
                updateInfo.DownloadUrl,
                tempPath,
                progress,
                cancellationToken,
                updateInfo.InstallerSize,
                updateInfo.InstallerSha256);

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            Environment.Exit(0);
        }

        internal async Task DownloadInstallerAsync(
            string downloadUrl,
            string destinationPath,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            long? expectedSize = null,
            string? expectedSha256 = null)
        {
            if (!IsHttpsUrl(downloadUrl))
            {
                throw new InvalidOperationException("Update installer must be downloaded over HTTPS.");
            }

            if (expectedSize is long size && (size <= 0 || size > MaxInstallerBytes))
            {
                throw new InvalidDataException($"Unexpected update installer size: {size} bytes.");
            }

            var expectedHash = NormalizeSha256(expectedSha256);
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
                if (totalBytes is long contentLength)
                {
                    if (contentLength > MaxInstallerBytes)
                    {
                        throw new InvalidDataException($"Update installer is too large: {contentLength} bytes.");
                    }

                    if (expectedSize is long expectedContentLength && contentLength != expectedContentLength)
                    {
                        throw new InvalidDataException("Update installer size does not match the signed manifest.");
                    }
                }

                progress?.Report(new UpdateDownloadProgress(0, totalBytes));

                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

                    downloadedBytes += bytesRead;
                    if (downloadedBytes > MaxInstallerBytes)
                    {
                        throw new InvalidDataException($"Update installer exceeded {MaxInstallerBytes} bytes.");
                    }

                    if (expectedSize is long expectedBytes && downloadedBytes > expectedBytes)
                    {
                        throw new InvalidDataException("Update installer exceeded the signed manifest size.");
                    }

                    sha256.AppendData(buffer, 0, bytesRead);
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    progress?.Report(new UpdateDownloadProgress(downloadedBytes, totalBytes));
                }

                await fileStream.FlushAsync(cancellationToken);

                if (expectedSize is long finalExpectedSize && downloadedBytes != finalExpectedSize)
                {
                    throw new InvalidDataException("Update installer size does not match the signed manifest.");
                }

                var actualSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
                if (expectedHash != null && !HashesEqual(expectedHash, actualSha256))
                {
                    throw new InvalidDataException("Update installer SHA-256 does not match the signed manifest.");
                }

                await fileStream.DisposeAsync();
                await responseStream.DisposeAsync();

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

        private static string? NormalizeSha256(string? expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return null;
            }

            var normalized = expectedSha256.Trim().ToLowerInvariant();
            if (!IsValidSha256Hex(normalized))
            {
                throw new InvalidDataException("Signed update manifest contains an invalid SHA-256 value.");
            }

            return normalized;
        }

        private static bool HashesEqual(string expectedHash, string actualHash)
        {
            var expectedBytes = Encoding.ASCII.GetBytes(expectedHash);
            var actualBytes = Encoding.ASCII.GetBytes(actualHash);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
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
        public string InstallerAssetName { get; set; } = string.Empty;
        public string InstallerSha256 { get; set; } = string.Empty;
        public long InstallerSize { get; set; }
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

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    internal sealed class UpdateManifest
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("installerAssetName")]
        public string InstallerAssetName { get; set; } = string.Empty;

        [JsonProperty("installerSha256")]
        public string InstallerSha256 { get; set; } = string.Empty;

        [JsonProperty("installerSize")]
        public long InstallerSize { get; set; }
    }

    internal sealed class VerifiedUpdateManifest
    {
        public VerifiedUpdateManifest(UpdateManifest manifest, GitHubAsset installerAsset)
        {
            Manifest = manifest;
            InstallerAsset = installerAsset;
        }

        public UpdateManifest Manifest { get; }
        public GitHubAsset InstallerAsset { get; }
    }
}
