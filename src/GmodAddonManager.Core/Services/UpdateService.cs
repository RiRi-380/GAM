using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
        private const string EnvGithubTokenFallback = "GITHUB_TOKEN";

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
            var endpoints = BuildApiEndpoints(resolvedSource);
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

            await SaveLastCheckTime();
            return UpdateCheckResult.UpdateAvailable(new UpdateInfo
            {
                Version = release.TagName,
                ReleaseNotes = release.Body ?? string.Empty,
                DownloadUrl = installerAsset.BrowserDownloadUrl,
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
                ?? Environment.GetEnvironmentVariable(EnvGithubTokenFallback)
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

        private static GitHubAsset? SelectInstallerAsset(GitHubAsset[]? assets)
        {
            if (assets == null || assets.Length == 0)
            {
                return null;
            }

            var candidates = assets
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
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
            var remote = remoteVersion.TrimStart('v', 'V');
            var current = currentVersion.TrimStart('v', 'V');
            remote = remote.Split(new[] { '-', '+' }, 2)[0];
            current = current.Split(new[] { '-', '+' }, 2)[0];

            return Version.TryParse(remote, out var remoteVer) &&
                   Version.TryParse(current, out var currentVer) &&
                   remoteVer > currentVer;
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

        public async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "GAM-Update-Setup.exe");

            using (var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = ResolveInstallerArguments(downloadUrl),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
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
    }
}
