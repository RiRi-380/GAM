using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Security.Cryptography;

namespace GmodAddonManager.Core.Services
{
    public enum UpdateCheckStatus
    {
        UpdateAvailable,
        UpToDate,
        Skipped,
        Error
    }

    public sealed class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; private set; }
        public UpdateInfo? UpdateInfo { get; private set; }
        public string? ErrorMessage { get; private set; }

        public static UpdateCheckResult UpdateAvailable(UpdateInfo info)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                UpdateInfo = info
            };
        }

        public static UpdateCheckResult UpToDate()
        {
            return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate };
        }

        public static UpdateCheckResult Skipped()
        {
            return new UpdateCheckResult { Status = UpdateCheckStatus.Skipped };
        }

        public static UpdateCheckResult Error(string message)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                ErrorMessage = message
            };
        }
    }

    public sealed class UpdateSource
    {
        public string? Repository { get; set; }
        public string? ApiUrl { get; set; }
        public bool IncludePrerelease { get; set; }
    }

    public class UpdateService
    {
        private const string DefaultGithubRepo = "RiRi-380/GAM";
        private const string UpdateCheckFile = "last_update_check.txt";
        private const string InnoSilentInstallArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS";
        private const string EnvUpdateRepo = "GAM_UPDATE_REPO";
        private const string EnvUpdateApiUrl = "GAM_UPDATE_API_URL";
        private const string EnvUpdateIncludePrerelease = "GAM_UPDATE_INCLUDE_PRERELEASE";
        private const string EnvGithubToken = "GAM_GITHUB_TOKEN";
        private static readonly TimeSpan UpdateDownloadTimeout = TimeSpan.FromMinutes(5);

        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string currentVersion;
        private readonly UpdateSource? configuredSource;
        private readonly string? configuredToken;

        static UpdateService()
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager");
        }

        public UpdateService(string currentVersion, UpdateSource? source = null, string? githubToken = null)
        {
            this.currentVersion = currentVersion;
            configuredSource = source;
            configuredToken = githubToken;
        }

        public async Task<UpdateCheckResult> CheckForUpdateAsync(bool forceCheck = false)
        {
            if (!forceCheck && await ShouldSkipUpdateCheck())
            {
                return UpdateCheckResult.Skipped();
            }

            var resolvedSource = ResolveUpdateSource();
            ApiEndpoints endpoints;
            try
            {
                endpoints = BuildApiEndpoints(resolvedSource);
            }
            catch (ArgumentException ex)
            {
                return UpdateCheckResult.Error(ex.Message);
            }
            var token = ResolveGithubToken();

            var releaseResult = await FetchLatestReleaseAsync(endpoints, token, resolvedSource.IncludePrerelease);
            if (!releaseResult.Success)
            {
                return UpdateCheckResult.Error(releaseResult.ErrorMessage ?? "Update check failed.");
            }

            var release = releaseResult.Release;
            if (release == null)
            {
                await SaveLastCheckTime();
                return UpdateCheckResult.UpToDate();
            }

            if (string.IsNullOrWhiteSpace(release.TagName))
            {
                return UpdateCheckResult.Error("Latest release tag is missing.");
            }

            if (!IsNewerVersion(release.TagName))
            {
                await SaveLastCheckTime();
                return UpdateCheckResult.UpToDate();
            }

            var installerAsset = SelectInstallerAsset(release.Assets);
            if (installerAsset == null)
            {
                await SaveLastCheckTime();
                return UpdateCheckResult.Error("No installer asset found in the latest release.");
            }

            if (!TryNormalizeSha256Digest(installerAsset.Digest, out _))
            {
                return UpdateCheckResult.Error("The installer asset is missing a valid SHA-256 digest.");
            }

            if (!Uri.TryCreate(installerAsset.BrowserDownloadUrl, UriKind.Absolute, out var installerUri) ||
                !string.Equals(installerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return UpdateCheckResult.Error("The installer asset download URL must use HTTPS.");
            }

            return UpdateCheckResult.UpdateAvailable(new UpdateInfo
            {
                Version = release.TagName,
                ReleaseNotes = release.Body ?? string.Empty,
                DownloadUrl = installerAsset.BrowserDownloadUrl,
                DownloadDigest = installerAsset.Digest,
                PublishedAt = release.PublishedAt
            });
        }

        private UpdateSource ResolveUpdateSource()
        {
            var repo = Environment.GetEnvironmentVariable(EnvUpdateRepo);
            var apiUrl = Environment.GetEnvironmentVariable(EnvUpdateApiUrl);
            var includePrerelease = configuredSource?.IncludePrerelease ?? false;
            var includePrereleaseEnv = Environment.GetEnvironmentVariable(EnvUpdateIncludePrerelease);
            if (!string.IsNullOrWhiteSpace(includePrereleaseEnv))
            {
                includePrerelease = ParseBool(includePrereleaseEnv, includePrerelease);
            }

            return new UpdateSource
            {
                Repository = !string.IsNullOrWhiteSpace(repo) ? repo : configuredSource?.Repository,
                ApiUrl = !string.IsNullOrWhiteSpace(apiUrl) ? apiUrl : configuredSource?.ApiUrl,
                IncludePrerelease = includePrerelease
            };
        }

        private string? ResolveGithubToken()
        {
            return Environment.GetEnvironmentVariable(EnvGithubToken)
                ?? configuredToken;
        }

        private static bool ParseBool(string value, bool defaultValue)
        {
            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return defaultValue;
        }

        private static ApiEndpoints BuildApiEndpoints(UpdateSource source)
        {
            if (!string.IsNullOrWhiteSpace(source.ApiUrl))
            {
                var trimmed = source.ApiUrl.Trim().TrimEnd('/');
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var apiUri) ||
                    !string.Equals(apiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The update API URL must be an absolute HTTPS URL.", nameof(source));
                }

                var latestUrl = trimmed.EndsWith("/latest", StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : $"{trimmed}/latest";
                var listUrl = trimmed.EndsWith("/latest", StringComparison.OrdinalIgnoreCase)
                    ? trimmed.Substring(0, trimmed.Length - "/latest".Length).TrimEnd('/')
                    : trimmed;

                return new ApiEndpoints(latestUrl, listUrl);
            }

            var repo = (source.Repository ?? DefaultGithubRepo).Trim();
            var latest = $"https://api.github.com/repos/{repo}/releases/latest";
            var list = $"https://api.github.com/repos/{repo}/releases";
            return new ApiEndpoints(latest, list);
        }

        private async Task<ReleaseFetchResult> FetchLatestReleaseAsync(ApiEndpoints endpoints, string? token, bool includePrerelease)
        {
            var latestResponse = await GetApiResponseAsync(endpoints.LatestUrl, token);
            if (latestResponse.StatusCode == HttpStatusCode.NotFound)
            {
                var listResponse = await GetApiResponseAsync(endpoints.ListUrl, token);
                if (!listResponse.IsSuccessStatusCode)
                {
                    return ReleaseFetchResult.Fail(BuildErrorMessage(endpoints.ListUrl, listResponse));
                }

                var releases = DeserializeJson<GitHubRelease[]>(listResponse.Body);
                var release = releases?
                    .FirstOrDefault(r => !r.Draft && (includePrerelease || !r.Prerelease));
                if (release == null)
                {
                    return ReleaseFetchResult.Fail("No releases found in the repository.");
                }

                return ReleaseFetchResult.FromSuccess(release);
            }

            if (!latestResponse.IsSuccessStatusCode)
            {
                return ReleaseFetchResult.Fail(BuildErrorMessage(endpoints.LatestUrl, latestResponse));
            }

            var latestRelease = DeserializeJson<GitHubRelease>(latestResponse.Body);
            if (latestRelease == null)
            {
                return ReleaseFetchResult.Fail("Failed to parse the latest release.");
            }

            if (latestRelease.Draft || (!includePrerelease && latestRelease.Prerelease))
            {
                var listResponse = await GetApiResponseAsync(endpoints.ListUrl, token);
                if (!listResponse.IsSuccessStatusCode)
                {
                    return ReleaseFetchResult.Fail(BuildErrorMessage(endpoints.ListUrl, listResponse));
                }

                var releases = DeserializeJson<GitHubRelease[]>(listResponse.Body);
                var release = releases?
                    .FirstOrDefault(r => !r.Draft && (includePrerelease || !r.Prerelease));
                if (release == null)
                {
                    return ReleaseFetchResult.Fail("No non-draft releases found in the repository.");
                }

                return ReleaseFetchResult.FromSuccess(release);
            }

            return ReleaseFetchResult.FromSuccess(latestRelease);
        }

        private static string BuildErrorMessage(string url, ApiResponse response)
        {
            var code = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            return $"Update check failed ({code} {reason}) for {url}.";
        }

        private async Task<ApiResponse> GetApiResponseAsync(string url, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return new ApiResponse(response.StatusCode, response.ReasonPhrase, response.IsSuccessStatusCode, body);
        }

        private static T? DeserializeJson<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonConvert.DeserializeObject<T>(json);
        }

        internal static GitHubAsset? SelectInstallerAsset(GitHubAsset[]? assets)
        {
            if (assets == null || assets.Length == 0)
            {
                return null;
            }

            var candidates = assets
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(a =>
                    a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates
                .Select(a => new { Asset = a, Score = ScoreInstallerName(a.Name) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Asset.Name.Length)
                .Select(x => x.Asset)
                .FirstOrDefault();
        }

        private static int ScoreInstallerName(string name)
        {
            var lower = name.ToLowerInvariant();
            var score = 0;

            if (lower.Contains("setup"))
            {
                score += 5;
            }

            if (lower.Contains("installer"))
            {
                score += 5;
            }

            if (lower.Contains("install"))
            {
                score += 2;
            }

            if (lower.Contains("portable"))
            {
                score -= 3;
            }

            if (lower.Contains("gam"))
            {
                score += 1;
            }

            return score;
        }

        private Task<bool> ShouldSkipUpdateCheck()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var checkFilePath = Path.Combine(appDataPath, "GmodAddonManager", UpdateCheckFile);

                if (File.Exists(checkFilePath))
                {
                    var lastCheck = File.ReadAllText(checkFilePath);
                    if (DateTime.TryParse(lastCheck, out var lastCheckDate))
                    {
                        return Task.FromResult((DateTime.Now - lastCheckDate).TotalDays < 1);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read last update check time: {ex.Message}");
            }

            return Task.FromResult(false);
        }

        private Task SaveLastCheckTime()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var gamPath = Path.Combine(appDataPath, "GmodAddonManager");
                Directory.CreateDirectory(gamPath);

                var checkFilePath = Path.Combine(gamPath, UpdateCheckFile);
                File.WriteAllText(checkFilePath, DateTime.Now.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save last update check time: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool IsNewerVersion(string remoteVersion)
        {
            var remote = NormalizeVersionNumber(remoteVersion);
            var current = NormalizeVersionNumber(currentVersion);

            return Version.TryParse(remote, out var remoteVer) &&
                   Version.TryParse(current, out var currentVer) &&
                   remoteVer > currentVer;
        }

        public static string NormalizeVersionLabel(string? version)
        {
            var normalized = NormalizeVersionNumber(version);
            return string.IsNullOrWhiteSpace(normalized)
                ? "unknown"
                : $"v{normalized}";
        }

        internal static string NormalizeVersionNumber(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return string.Empty;
            }

            var normalized = version.Trim().TrimStart('v', 'V');
            normalized = normalized.Split(new[] { '-', '+' }, 2)[0].Trim();

            if (!Version.TryParse(normalized, out var parsed))
            {
                return normalized;
            }

            var builder = new StringBuilder()
                .Append(parsed.Major)
                .Append('.')
                .Append(parsed.Minor);

            if (parsed.Build >= 0)
            {
                builder.Append('.').Append(parsed.Build);
            }

            if (parsed.Revision > 0)
            {
                builder.Append('.').Append(parsed.Revision);
            }

            return builder.ToString();
        }

        internal static string ResolveInstallerArguments(string downloadUrl)
        {
            if (TryGetFileName(downloadUrl, out var fileName) &&
                fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Contains("installer", StringComparison.OrdinalIgnoreCase)))
            {
                return InnoSilentInstallArgs;
            }

            return string.Empty;
        }

        private static bool TryGetFileName(string downloadUrl, out string fileName)
        {
            fileName = string.Empty;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            fileName = Path.GetFileName(uri.LocalPath) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(fileName);
        }

        public async Task DownloadAndInstallUpdateAsync(
            string downloadUrl,
            string expectedDigest,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new ArgumentException("Update download URL is required.", nameof(downloadUrl));
            }

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Update download URL must use HTTPS.", nameof(downloadUrl));
            }

            if (!TryNormalizeSha256Digest(expectedDigest, out _))
            {
                throw new InvalidDataException("A valid SHA-256 release-asset digest is required.");
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"GAM-Update-Setup-{Guid.NewGuid():N}.exe");
            var installerArguments = ResolveInstallerArguments(downloadUrl);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(UpdateDownloadTimeout);

            try
            {
                using (var response = await httpClient.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri is not Uri finalUri ||
                        !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The update download redirected to a non-HTTPS URL.");
                    }

                    using var fs = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true);
                    using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await CopyStreamAsync(contentStream, fs, timeoutCts.Token).ConfigureAwait(false);
                }

                VerifyDownloadedFileDigest(tempPath, expectedDigest);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(tempPath);
                throw new TimeoutException("Update download timed out.", ex);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            var launcherPath = Path.Combine(
                Path.GetTempPath(),
                $"GAM-Update-Launcher-{Guid.NewGuid():N}.ps1");
            try
            {
                await File.WriteAllTextAsync(
                    launcherPath,
                    BuildInstallerLauncherScript(Process.GetCurrentProcess().Id, tempPath, installerArguments),
                    Encoding.UTF8).ConfigureAwait(false);

                var launcherProcess = Process.Start(CreateInstallerLauncherStartInfo(launcherPath));
                if (launcherProcess == null)
                {
                    throw new InvalidOperationException("Failed to start the update installer launcher.");
                }
            }
            catch
            {
                TryDeleteFile(launcherPath);
                TryDeleteFile(tempPath);
                throw;
            }
        }

        internal static void VerifyDownloadedFileDigest(string filePath, string expectedDigest)
        {
            if (!TryNormalizeSha256Digest(expectedDigest, out var expectedHex))
            {
                throw new InvalidDataException("A valid SHA-256 release-asset digest is required.");
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha256 = SHA256.Create();
            var actualHex = BitConverter.ToString(sha256.ComputeHash(stream))
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            if (!string.Equals(actualHex, expectedHex, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The downloaded update does not match the GitHub release digest.");
            }
        }

        internal static bool TryNormalizeSha256Digest(string? digest, out string normalizedHex)
        {
            normalizedHex = string.Empty;
            const string prefix = "sha256:";
            if (string.IsNullOrWhiteSpace(digest) ||
                !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var hex = digest.Substring(prefix.Length).Trim();
            if (hex.Length != 64 || hex.Any(c => !Uri.IsHexDigit(c)))
            {
                return false;
            }

            normalizedHex = hex.ToLowerInvariant();
            return true;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup for partial update downloads.
            }
        }

        private static async Task CopyStreamAsync(
            Stream source,
            Stream destination,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static ProcessStartInfo CreateInstallerLauncherStartInfo(string launcherPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(launcherPath);
            return startInfo;
        }

        internal static string BuildInstallerLauncherScript(
            int currentProcessId,
            string installerPath,
            string installerArguments)
        {
            return string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'SilentlyContinue'",
                $"try {{ Wait-Process -Id {currentProcessId} -Timeout 60 }} catch {{ }}",
                $"$installerPath = {ToPowerShellSingleQuotedString(installerPath)}",
                $"$installerArguments = {ToPowerShellSingleQuotedString(installerArguments)}",
                "if ([string]::IsNullOrWhiteSpace($installerArguments)) {",
                "    Start-Process -FilePath $installerPath -Wait",
                "} else {",
                "    Start-Process -FilePath $installerPath -ArgumentList $installerArguments -Wait",
                "}",
                "Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue",
                "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
                string.Empty);
        }

        private static string ToPowerShellSingleQuotedString(string value)
        {
            return $"'{(value ?? string.Empty).Replace("'", "''")}'";
        }
    }

    internal readonly struct ApiEndpoints
    {
        public ApiEndpoints(string latestUrl, string listUrl)
        {
            LatestUrl = latestUrl;
            ListUrl = listUrl;
        }

        public string LatestUrl { get; }
        public string ListUrl { get; }
    }

    internal readonly struct ApiResponse
    {
        public ApiResponse(HttpStatusCode statusCode, string? reasonPhrase, bool isSuccessStatusCode, string body)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            IsSuccessStatusCode = isSuccessStatusCode;
            Body = body;
        }

        public HttpStatusCode StatusCode { get; }
        public string? ReasonPhrase { get; }
        public bool IsSuccessStatusCode { get; }
        public string Body { get; }
    }

    internal sealed class ReleaseFetchResult
    {
        private ReleaseFetchResult(bool success, GitHubRelease? release, string? errorMessage)
        {
            Success = success;
            Release = release;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; }
        public GitHubRelease? Release { get; }
        public string? ErrorMessage { get; }

        public static ReleaseFetchResult FromSuccess(GitHubRelease release)
        {
            return new ReleaseFetchResult(true, release, null);
        }

        public static ReleaseFetchResult Fail(string errorMessage)
        {
            return new ReleaseFetchResult(false, null, errorMessage);
        }
    }

    public class UpdateInfo
    {
        public string Version { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string DownloadDigest { get; set; } = string.Empty;
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

        [JsonProperty("draft")]
        public bool Draft { get; set; }

        [JsonProperty("prerelease")]
        public bool Prerelease { get; set; }
    }

    public class GitHubAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonProperty("digest")]
        public string Digest { get; set; } = string.Empty;
    }
}
