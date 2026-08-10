using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Globalization;
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

    public enum UpdatePackageKind
    {
        Installer,
        PortableArchive
    }

    public enum UpdateInstallDisposition
    {
        InstallerLaunched,
        PortableArchiveReady
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
        public const string PortableMarkerFileName = ".gam-portable.json";
        private const string InnoSilentInstallArgs =
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /LAUNCHAFTERINSTALL=1";
        private const string EnvUpdateRepo = "GAM_UPDATE_REPO";
        private const string EnvUpdateApiUrl = "GAM_UPDATE_API_URL";
        private const string EnvUpdateIncludePrerelease = "GAM_UPDATE_INCLUDE_PRERELEASE";
        private const string EnvGithubToken = "GAM_GITHUB_TOKEN";
        private const long DefaultMaxUpdateDownloadBytes = 512L * 1024 * 1024;
        private const long DefaultMaxApiResponseBytes = 8L * 1024 * 1024;
        private static readonly TimeSpan UpdateDownloadTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(30);

        private static readonly HttpClient sharedHttpClient = new HttpClient();
        private readonly string currentVersion;
        private readonly UpdateSource? configuredSource;
        private readonly string? configuredToken;
        private readonly HttpClient client;
        private readonly long maxUpdateDownloadBytes;
        private readonly long maxApiResponseBytes;
        private readonly string temporaryDirectory;
        private readonly string updateStateDirectory;
        private readonly bool portableInstallation;
        private Uri? updateApiOrigin;
        private string? selectedDownloadUrl;
        private string? selectedAssetApiUrl;
        private string? selectedInstallerName;

        static UpdateService()
        {
            sharedHttpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager");
        }

        public UpdateService(string currentVersion, UpdateSource? source = null, string? githubToken = null)
            : this(currentVersion, source, githubToken, sharedHttpClient)
        {
        }

        internal UpdateService(
            string currentVersion,
            UpdateSource? source,
            string? githubToken,
            HttpClient client,
            long maxUpdateDownloadBytes = DefaultMaxUpdateDownloadBytes,
            long maxApiResponseBytes = DefaultMaxApiResponseBytes,
            string? temporaryDirectory = null,
            string? updateStateDirectory = null,
            bool? portableInstallation = null)
        {
            this.currentVersion = currentVersion;
            configuredSource = source;
            configuredToken = githubToken;
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            if (maxUpdateDownloadBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxUpdateDownloadBytes));
            }

            if (maxApiResponseBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxApiResponseBytes));
            }

            this.maxUpdateDownloadBytes = maxUpdateDownloadBytes;
            this.maxApiResponseBytes = maxApiResponseBytes;
            this.temporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
                ? Path.GetTempPath()
                : temporaryDirectory;
            this.updateStateDirectory = string.IsNullOrWhiteSpace(updateStateDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GmodAddonManager")
                : updateStateDirectory;
            this.portableInstallation = portableInstallation ??
                IsPortableInstallation(AppContext.BaseDirectory);
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
                updateApiOrigin = new Uri(endpoints.ListUrl, UriKind.Absolute);
            }
            catch (ArgumentException ex)
            {
                return UpdateCheckResult.Error(ex.Message);
            }
            var token = ResolveGithubToken();

            ReleaseFetchResult releaseResult;
            try
            {
                releaseResult = await FetchLatestReleaseAsync(
                    endpoints,
                    token,
                    resolvedSource.IncludePrerelease);
            }
            catch (HttpRequestException ex)
            {
                return UpdateCheckResult.Error($"Update check request failed: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                return UpdateCheckResult.Error($"Update check timed out: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return UpdateCheckResult.Error($"Update check returned invalid JSON: {ex.Message}");
            }
            catch (InvalidDataException ex)
            {
                return UpdateCheckResult.Error($"Update check response was rejected: {ex.Message}");
            }

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

            var installerAsset = portableInstallation
                ? SelectPortableAsset(release.Assets)
                : SelectInstallerAsset(release.Assets);
            if (installerAsset == null)
            {
                await SaveLastCheckTime();
                return UpdateCheckResult.Error(portableInstallation
                    ? "No portable archive found in the latest release."
                    : "No installer asset found in the latest release.");
            }

            if (!TryNormalizeSha256Digest(installerAsset.Digest, out _))
            {
                return UpdateCheckResult.Error("The installer asset is missing a valid SHA-256 digest.");
            }

            var browserDownloadUrl = NormalizeHttpsUrl(installerAsset.BrowserDownloadUrl);
            var assetApiUrl = NormalizeHttpsUrl(installerAsset.ApiUrl);
            if (assetApiUrl != null &&
                !HaveSameOrigin(assetApiUrl, endpoints.ListUrl))
            {
                // Never forward a repository token to an origin supplied by
                // release JSON. Official GitHub and GitHub Enterprise asset API
                // URLs share the release API origin.
                assetApiUrl = null;
            }
            var downloadUrl = browserDownloadUrl ?? assetApiUrl;
            if (downloadUrl == null)
            {
                return UpdateCheckResult.Error("The installer asset download URL must use HTTPS.");
            }

            // GitHub's authenticated binary-download contract uses the asset API
            // URL with application/octet-stream. Keep the browser URL in the UI
            // result so installer naming remains stable, and map it back to the API
            // URL when the download starts. Public releases work through either
            // endpoint; private releases require this authenticated API request.
            selectedDownloadUrl = downloadUrl;
            selectedAssetApiUrl = assetApiUrl;
            selectedInstallerName = installerAsset.Name;

            return UpdateCheckResult.UpdateAvailable(new UpdateInfo
            {
                Version = release.TagName,
                ReleaseNotes = release.Body ?? string.Empty,
                DownloadUrl = downloadUrl,
                DownloadDigest = installerAsset.Digest,
                PackageKind = portableInstallation
                    ? UpdatePackageKind.PortableArchive
                    : UpdatePackageKind.Installer,
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
            var environmentToken = Environment.GetEnvironmentVariable(EnvGithubToken);
            return !string.IsNullOrWhiteSpace(environmentToken)
                ? environmentToken
                : configuredToken;
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
            if (includePrerelease)
            {
                // GitHub's /latest endpoint intentionally excludes prereleases.
                // Query the ordered release list directly when the caller opted in.
                return await FetchReleaseListAsync(
                    endpoints.ListUrl,
                    token,
                    includePrerelease: true,
                    "No releases found in the repository.");
            }

            var latestResponse = await GetApiResponseAsync(endpoints.LatestUrl, token);
            if (latestResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return await FetchReleaseListAsync(
                    endpoints.ListUrl,
                    token,
                    includePrerelease: false,
                    "No releases found in the repository.");
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
                return await FetchReleaseListAsync(
                    endpoints.ListUrl,
                    token,
                    includePrerelease: false,
                    "No non-draft releases found in the repository.");
            }

            return ReleaseFetchResult.FromSuccess(latestRelease);
        }

        private async Task<ReleaseFetchResult> FetchReleaseListAsync(
            string listUrl,
            string? token,
            bool includePrerelease,
            string emptyMessage)
        {
            var listResponse = await GetApiResponseAsync(listUrl, token);
            if (!listResponse.IsSuccessStatusCode)
            {
                return ReleaseFetchResult.Fail(BuildErrorMessage(listUrl, listResponse));
            }

            var releases = DeserializeJson<GitHubRelease[]>(listResponse.Body);
            var release = releases?
                .FirstOrDefault(r => !r.Draft && (includePrerelease || !r.Prerelease));
            return release == null
                ? ReleaseFetchResult.Fail(emptyMessage)
                : ReleaseFetchResult.FromSuccess(release);
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

            using var timeoutCts = new CancellationTokenSource(UpdateCheckTimeout);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            RejectDeclaredOversize(
                response.Content.Headers.ContentLength,
                maxApiResponseBytes,
                "Update API response");
            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bodyStream = new MemoryStream();
            await CopyStreamAsync(
                contentStream,
                bodyStream,
                maxApiResponseBytes,
                "Update API response",
                timeoutCts.Token).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bodyStream.ToArray());
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

        private static string? NormalizeHttpsUrl(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return uri.AbsoluteUri;
        }

        private static bool HaveSameOrigin(string left, string right)
        {
            return Uri.TryCreate(left, UriKind.Absolute, out var leftUri) &&
                   Uri.TryCreate(right, UriKind.Absolute, out var rightUri) &&
                   string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   leftUri.Port == rightUri.Port;
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

        internal static GitHubAsset? SelectPortableAsset(GitHubAsset[]? assets)
        {
            if (assets == null || assets.Length == 0)
            {
                return null;
            }

            return assets
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name))
                .Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Where(asset => asset.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(asset =>
                    asset.Name.StartsWith("GAM-Portable-", StringComparison.OrdinalIgnoreCase))
                .ThenBy(asset => asset.Name.Length)
                .FirstOrDefault();
        }

        internal static bool IsPortableInstallation(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName));
            }
            catch (Exception)
            {
                return false;
            }
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
                var checkFilePath = Path.Combine(updateStateDirectory, UpdateCheckFile);

                if (File.Exists(checkFilePath))
                {
                    var lastCheck = File.ReadAllText(checkFilePath);
                    return Task.FromResult(ShouldSkipUpdateCheck(lastCheck, DateTimeOffset.UtcNow));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read last update check time: {ex.Message}");
            }

            return Task.FromResult(false);
        }

        internal static bool ShouldSkipUpdateCheck(string? lastCheck, DateTimeOffset now)
        {
            if (!DateTimeOffset.TryParse(
                    lastCheck,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastCheckDate))
            {
                return false;
            }

            var age = now.ToUniversalTime() - lastCheckDate.ToUniversalTime();
            return age >= TimeSpan.Zero && age < TimeSpan.FromDays(1);
        }

        public Task DeferUpdateCheckAsync()
        {
            return SaveLastCheckTime();
        }

        private Task SaveLastCheckTime()
        {
            try
            {
                Directory.CreateDirectory(updateStateDirectory);

                var checkFilePath = Path.Combine(updateStateDirectory, UpdateCheckFile);
                File.WriteAllText(
                    checkFilePath,
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save last update check time: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool IsNewerVersion(string remoteVersion)
        {
            return IsRemoteVersionNewer(currentVersion, remoteVersion);
        }

        internal static bool IsRemoteVersionNewer(string currentVersion, string remoteVersion)
        {
            return TryParseComparableVersion(currentVersion, out var current) &&
                   TryParseComparableVersion(remoteVersion, out var remote) &&
                   CompareVersions(remote, current) > 0;
        }

        public static string NormalizeVersionLabel(string? version)
        {
            if (TryParseComparableVersion(version, out var parsed))
            {
                var prerelease = parsed.PrereleaseIdentifiers.Length == 0
                    ? string.Empty
                    : $"-{string.Join(".", parsed.PrereleaseIdentifiers)}";
                return $"v{parsed.NormalizedCore}{prerelease}";
            }

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

        private static bool TryParseComparableVersion(
            string? version,
            out ComparableVersion parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var normalized = version.Trim().TrimStart('v', 'V');
            var buildSeparator = normalized.IndexOf('+');
            if (buildSeparator >= 0)
            {
                var buildMetadata = normalized.Substring(buildSeparator + 1);
                if (!AreValidIdentifiers(buildMetadata, allowNumericLeadingZero: true) ||
                    buildMetadata.Contains('+'))
                {
                    return false;
                }

                normalized = normalized.Substring(0, buildSeparator);
            }

            var prereleaseSeparator = normalized.IndexOf('-');
            var coreText = prereleaseSeparator >= 0
                ? normalized.Substring(0, prereleaseSeparator)
                : normalized;
            var prereleaseText = prereleaseSeparator >= 0
                ? normalized.Substring(prereleaseSeparator + 1)
                : string.Empty;

            var coreParts = coreText.Split('.');
            if (coreParts.Length < 2 || coreParts.Length > 4)
            {
                return false;
            }

            var core = new int[coreParts.Length];
            for (var index = 0; index < coreParts.Length; index++)
            {
                if (!int.TryParse(
                        coreParts[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out core[index]))
                {
                    return false;
                }
            }

            var prereleaseIdentifiers = Array.Empty<string>();
            if (prereleaseSeparator >= 0)
            {
                if (!AreValidIdentifiers(prereleaseText, allowNumericLeadingZero: false))
                {
                    return false;
                }

                prereleaseIdentifiers = prereleaseText.Split('.');
            }

            parsed = new ComparableVersion(core, prereleaseIdentifiers);
            return true;
        }

        private static bool AreValidIdentifiers(string value, bool allowNumericLeadingZero)
        {
            var identifiers = value.Split('.');
            foreach (var identifier in identifiers)
            {
                if (identifier.Length == 0 || identifier.Any(character =>
                        !(character >= '0' && character <= '9') &&
                        !(character >= 'A' && character <= 'Z') &&
                        !(character >= 'a' && character <= 'z') &&
                        character != '-'))
                {
                    return false;
                }

                if (!allowNumericLeadingZero &&
                    identifier.Length > 1 &&
                    identifier[0] == '0' &&
                    identifier.All(character => character >= '0' && character <= '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareVersions(ComparableVersion left, ComparableVersion right)
        {
            var coreCount = Math.Max(left.Core.Length, right.Core.Length);
            for (var index = 0; index < coreCount; index++)
            {
                var leftPart = index < left.Core.Length ? left.Core[index] : 0;
                var rightPart = index < right.Core.Length ? right.Core[index] : 0;
                var coreComparison = leftPart.CompareTo(rightPart);
                if (coreComparison != 0)
                {
                    return coreComparison;
                }
            }

            var leftPrerelease = left.PrereleaseIdentifiers;
            var rightPrerelease = right.PrereleaseIdentifiers;
            if (leftPrerelease.Length == 0 || rightPrerelease.Length == 0)
            {
                return leftPrerelease.Length == rightPrerelease.Length
                    ? 0
                    : leftPrerelease.Length == 0 ? 1 : -1;
            }

            var identifierCount = Math.Min(leftPrerelease.Length, rightPrerelease.Length);
            for (var index = 0; index < identifierCount; index++)
            {
                var identifierComparison = ComparePrereleaseIdentifiers(
                    leftPrerelease[index],
                    rightPrerelease[index]);
                if (identifierComparison != 0)
                {
                    return identifierComparison;
                }
            }

            return leftPrerelease.Length.CompareTo(rightPrerelease.Length);
        }

        private static int ComparePrereleaseIdentifiers(string left, string right)
        {
            var leftNumeric = left.All(character => character >= '0' && character <= '9');
            var rightNumeric = right.All(character => character >= '0' && character <= '9');
            if (leftNumeric && rightNumeric)
            {
                var lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : string.CompareOrdinal(left, right);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return string.CompareOrdinal(left, right);
        }

        private readonly struct ComparableVersion
        {
            public ComparableVersion(int[] core, string[] prereleaseIdentifiers)
            {
                Core = core;
                PrereleaseIdentifiers = prereleaseIdentifiers;

                var displayLength = core.Length == 4 && core[3] == 0
                    ? 3
                    : core.Length;
                NormalizedCore = string.Join(
                    ".",
                    core.Take(displayLength).Select(part => part.ToString(CultureInfo.InvariantCulture)));
            }

            public int[] Core { get; }
            public string[] PrereleaseIdentifiers { get; }
            public string NormalizedCore { get; }
        }

        internal static string ResolveInstallerArguments(string downloadUrl)
        {
            if (TryGetFileName(downloadUrl, out var fileName) &&
                !string.IsNullOrWhiteSpace(fileName))
            {
                return ResolveInstallerArgumentsFromFileName(fileName);
            }

            return string.Empty;
        }

        private static string ResolveInstallerArgumentsFromFileName(string? fileName)
        {
            return !string.IsNullOrWhiteSpace(fileName) &&
                   fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                   (fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("installer", StringComparison.OrdinalIgnoreCase))
                ? InnoSilentInstallArgs
                : string.Empty;
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

        public async Task<UpdateInstallDisposition> DownloadAndInstallUpdateAsync(
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

            var selectedFileName = string.Equals(
                    downloadUrl,
                    selectedDownloadUrl,
                    StringComparison.Ordinal)
                ? selectedInstallerName
                : TryGetFileName(downloadUrl, out var urlFileName)
                    ? urlFileName
                    : null;
            var selectedKind = DeterminePackageKind(selectedFileName);
            var expectedKind = portableInstallation
                ? UpdatePackageKind.PortableArchive
                : UpdatePackageKind.Installer;
            if (selectedKind != expectedKind)
            {
                throw new InvalidDataException(portableInstallation
                    ? "Portable GAM requires a GAM-Portable ZIP update package."
                    : "Installed GAM requires a GAM Setup executable update package.");
            }

            var extension = selectedKind == UpdatePackageKind.PortableArchive
                ? ".zip"
                : ".exe";
            var tempPath = Path.Combine(
                temporaryDirectory,
                $"GAM-Update-Package-{Guid.NewGuid():N}{extension}");
            var requestUrl = ResolveDownloadRequestUrl(downloadUrl);
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri) ||
                !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Update asset API URL must use HTTPS.", nameof(downloadUrl));
            }

            var installerArguments = string.Equals(
                    downloadUrl,
                    selectedDownloadUrl,
                    StringComparison.Ordinal)
                ? ResolveInstallerArgumentsFromFileName(selectedInstallerName)
                : ResolveInstallerArguments(downloadUrl);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(UpdateDownloadTimeout);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
                using (var response = await SendDownloadRequestAsync(
                    request,
                    timeoutCts.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri is not Uri finalUri ||
                        !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The update download redirected to a non-HTTPS URL.");
                    }

                    RejectDeclaredOversize(
                        response.Content.Headers.ContentLength,
                        maxUpdateDownloadBytes,
                        "Update package");

                    using var fs = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true);
                    using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await CopyStreamAsync(
                        contentStream,
                        fs,
                        maxUpdateDownloadBytes,
                        "Update package",
                        timeoutCts.Token).ConfigureAwait(false);
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

            if (selectedKind == UpdatePackageKind.PortableArchive)
            {
                try
                {
                    var revealProcess = Process.Start(
                        CreatePortablePackageRevealStartInfo(tempPath));
                    if (revealProcess == null)
                    {
                        throw new InvalidOperationException(
                            "Failed to reveal the downloaded portable update package.");
                    }

                    return UpdateInstallDisposition.PortableArchiveReady;
                }
                catch
                {
                    // The verified archive is intentionally retained so the
                    // user can still install it manually from the temp path.
                    throw;
                }
            }

            var launcherPath = Path.Combine(
                temporaryDirectory,
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

                return UpdateInstallDisposition.InstallerLaunched;
            }
            catch
            {
                TryDeleteFile(launcherPath);
                TryDeleteFile(tempPath);
                throw;
            }
        }

        private static UpdatePackageKind? DeterminePackageKind(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                fileName.Contains("portable", StringComparison.OrdinalIgnoreCase))
            {
                return UpdatePackageKind.PortableArchive;
            }

            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Contains("installer", StringComparison.OrdinalIgnoreCase)))
            {
                return UpdatePackageKind.Installer;
            }

            return null;
        }

        private async Task<HttpResponseMessage> SendDownloadRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var token = ResolveGithubToken();
            if (!string.IsNullOrWhiteSpace(token) &&
                request.RequestUri != null &&
                updateApiOrigin != null &&
                HaveSameOrigin(request.RequestUri.AbsoluteUri, updateApiOrigin.AbsoluteUri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }

        internal string ResolveDownloadRequestUrl(string downloadUrl)
        {
            return string.Equals(downloadUrl, selectedDownloadUrl, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(selectedAssetApiUrl)
                ? selectedAssetApiUrl!
                : downloadUrl;
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

        private static void RejectDeclaredOversize(
            long? contentLength,
            long maximumBytes,
            string description)
        {
            if (contentLength.HasValue && contentLength.Value > maximumBytes)
            {
                throw new InvalidDataException(
                    $"{description} exceeds the {maximumBytes}-byte size limit.");
            }
        }

        private static async Task CopyStreamAsync(
            Stream source,
            Stream destination,
            long maximumBytes,
            string description,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytesRead > maximumBytes - totalBytes)
                {
                    throw new InvalidDataException(
                        $"{description} exceeds the {maximumBytes}-byte size limit.");
                }

                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;
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

        internal static ProcessStartInfo CreatePortablePackageRevealStartInfo(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                throw new ArgumentException("Package path is required.", nameof(packagePath));
            }

            var fullPath = Path.GetFullPath(packagePath);
            if (fullPath.Contains("\"", StringComparison.Ordinal))
            {
                throw new ArgumentException("Package path contains an invalid quote.", nameof(packagePath));
            }

            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            };
        }

        internal static string BuildInstallerLauncherScript(
            int currentProcessId,
            string installerPath,
            string installerArguments)
        {
            return string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                $"$installerPath = {ToPowerShellSingleQuotedString(installerPath)}",
                $"$installerArguments = {ToPowerShellSingleQuotedString(installerArguments)}",
                @"$logPath = Join-Path $env:APPDATA 'GmodAddonManager\logs\update-installer.log'",
                "try {",
                $"    $currentProcess = Get-Process -Id {currentProcessId} -ErrorAction SilentlyContinue",
                "    if ($null -ne $currentProcess) {",
                "        $currentProcess | Wait-Process -Timeout 60",
                "    }",
                "    if ([string]::IsNullOrWhiteSpace($installerArguments)) {",
                "        $installerProcess = Start-Process -FilePath $installerPath -PassThru",
                "    } else {",
                "        $installerProcess = Start-Process -FilePath $installerPath -ArgumentList $installerArguments -PassThru",
                "    }",
                "    $installerProcess | Wait-Process",
                "    if ($installerProcess.ExitCode -ne 0) {",
                "        throw \"Installer process exited with code $($installerProcess.ExitCode).\"",
                "    }",
                "    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue",
                "} catch {",
                "    $failureMessage = $_.Exception.ToString()",
                "    try {",
                "        New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null",
                "        Add-Content -LiteralPath $logPath -Value (\"[{0:O}] {1}\" -f (Get-Date), $failureMessage)",
                "    } catch { }",
                "    exit 1",
                "} finally {",
                "    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
                "}",
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
        public UpdatePackageKind PackageKind { get; set; } = UpdatePackageKind.Installer;
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
        [JsonProperty("url")]
        public string ApiUrl { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonProperty("digest")]
        public string Digest { get; set; } = string.Empty;
    }
}
