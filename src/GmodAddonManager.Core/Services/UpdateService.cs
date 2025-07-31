using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GmodAddonManager.Core.Services
{
    public class UpdateService
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/RiRi-380/GAM/releases/latest";
        private const string UPDATE_CHECK_FILE = "last_update_check.txt";
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string currentVersion;
        
        static UpdateService()
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GmodAddonManager");
        }

        public UpdateService(string currentVersion)
        {
            this.currentVersion = currentVersion;
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                if (await ShouldSkipUpdateCheck())
                    return null;

                var response = await httpClient.GetStringAsync(GITHUB_API_URL);
                var release = JsonConvert.DeserializeObject<GitHubRelease>(response);

                if (release != null && IsNewerVersion(release.TagName))
                {
                    var installerAsset = release.Assets?.FirstOrDefault(a => 
                        a.Name.EndsWith("-Setup.exe") || a.Name.EndsWith("-installer.exe"));

                    if (installerAsset != null)
                    {
                        await SaveLastCheckTime();
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
                // Update check failed: {ex.Message}
            }

            return null;
        }

        private async Task<bool> ShouldSkipUpdateCheck()
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
                // Failed to check last update time - log and assume check is needed
                System.Diagnostics.Debug.WriteLine($"Failed to read last update check time: {ex.Message}");
            }
            
            return false;
        }

        private async Task SaveLastCheckTime()
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
                // Failed to save last check time - non-critical
                System.Diagnostics.Debug.WriteLine($"Failed to save last update check time: {ex.Message}");
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

        public async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "GAM-Update-Setup.exe");
            
            using (var response = await httpClient.GetAsync(downloadUrl))
            {
                using (var fs = new FileStream(tempPath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fs);
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            Environment.Exit(0);
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
    }

    public class GitHubAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}