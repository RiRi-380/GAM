using GmodAddonManager.Core.Services;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace GmodAddonManager.Core.Tests;

[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Tests intentionally validate raw URL string inputs.")]
public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_RejectsNonHttpsCustomApiBeforeNetworkAccess()
    {
        var service = new UpdateService(
            "1.0.0",
            new UpdateSource { ApiUrl = "http://updates.example.invalid/releases" });

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("HTTPS", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkFailureReturnsErrorResult()
    {
        using var client = new HttpClient(new RecordingHandler(
            (_, _) => throw new HttpRequestException("offline")));
        var service = new UpdateService(
            "1.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            githubToken: null,
            client);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("request failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdateAsync_InvalidJsonReturnsErrorResult()
    {
        using var client = new HttpClient(new RecordingHandler(
            (_, _) => JsonResponse("{ not-json")));
        var service = new UpdateService(
            "1.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            githubToken: null,
            client);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("invalid JSON", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdateAsync_PrereleaseOptInQueriesListAndSelectsPrerelease()
    {
        var handler = new RecordingHandler((_, _) => JsonResponse(
            """
            [
              {
                "tag_name": "v2.1.0-beta.1",
                "body": "preview",
                "published_at": "2026-08-03T00:00:00Z",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "url": "https://api.example.test/assets/21",
                    "name": "GAM-Setup-2.1.0-beta.1.exe",
                    "browser_download_url": "https://downloads.example.test/GAM-Setup-2.1.0-beta.1.exe",
                    "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                  }
                ]
              },
              {
                "tag_name": "v2.0.0",
                "draft": false,
                "prerelease": false,
                "assets": []
              }
            ]
            """));
        using var client = new HttpClient(handler);
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource
            {
                ApiUrl = "https://updates.example.test/releases",
                IncludePrerelease = true
            },
            githubToken: null,
            client);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.1.0-beta.1", result.UpdateInfo?.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://updates.example.test/releases", request.Uri);
    }

    [Fact]
    public async Task PrivateReleaseDownloadUsesAuthenticatedAssetApiEndpoint()
    {
        var handler = new RecordingHandler((_, requestNumber) =>
            requestNumber == 0
                ? JsonResponse(
                    """
                    {
                      "tag_name": "v2.1.0",
                      "body": "private release",
                      "published_at": "2026-08-03T00:00:00Z",
                      "draft": false,
                      "prerelease": false,
                      "assets": [
                        {
                          "url": "https://updates.example.test/releases/assets/42",
                          "name": "GAM-Setup-2.1.0.exe",
                          "browser_download_url": "https://downloads.example.test/GAM-Setup-2.1.0.exe",
                          "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                        }
                      ]
                    }
                    """)
                : BinaryResponse("not the expected installer"));
        using var client = new HttpClient(handler);
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            "private-test-token",
            client);

        var check = await service.CheckForUpdateAsync(forceCheck: true);
        var update = Assert.IsType<UpdateInfo>(check.UpdateInfo);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallUpdateAsync(update.DownloadUrl, update.DownloadDigest));

        Assert.Equal(2, handler.Requests.Count);
        var downloadRequest = handler.Requests[1];
        Assert.Equal("https://updates.example.test/releases/assets/42", downloadRequest.Uri);
        Assert.Equal("Bearer private-test-token", downloadRequest.Authorization);
        Assert.Contains("application/octet-stream", downloadRequest.Accept);
    }

    [Fact]
    public async Task CrossOriginAssetApiDoesNotReceiveRepositoryToken()
    {
        var handler = new RecordingHandler((_, requestNumber) =>
            requestNumber == 0
                ? JsonResponse(
                    """
                    {
                      "tag_name": "v2.1.0",
                      "draft": false,
                      "prerelease": false,
                      "assets": [
                        {
                          "url": "https://untrusted.example.test/assets/42",
                          "name": "GAM-Setup-2.1.0.exe",
                          "browser_download_url": "https://downloads.example.test/GAM-Setup-2.1.0.exe",
                          "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                        }
                      ]
                    }
                    """)
                : BinaryResponse("not the expected installer"));
        using var client = new HttpClient(handler);
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            "private-test-token",
            client);

        var check = await service.CheckForUpdateAsync(forceCheck: true);
        var update = Assert.IsType<UpdateInfo>(check.UpdateInfo);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallUpdateAsync(update.DownloadUrl, update.DownloadDigest));

        var downloadRequest = handler.Requests[1];
        Assert.Equal("https://downloads.example.test/GAM-Setup-2.1.0.exe", downloadRequest.Uri);
        Assert.Null(downloadRequest.Authorization);
    }

    [Fact]
    public async Task DownloadRejectsDeclaredOversizeBeforeLeavingATempFile()
    {
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var handler = new RecordingHandler((_, requestNumber) =>
                requestNumber == 0
                    ? UpdateAvailableResponse()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[9])
                    });
            using var client = new HttpClient(handler);
            var service = new UpdateService(
                "2.0.0",
                new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
                githubToken: null,
                client,
                maxUpdateDownloadBytes: 8,
                temporaryDirectory: tempDirectory);

            var check = await service.CheckForUpdateAsync(forceCheck: true);
            var update = Assert.IsType<UpdateInfo>(check.UpdateInfo);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAndInstallUpdateAsync(update.DownloadUrl, update.DownloadDigest));

            Assert.Contains("8-byte size limit", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(tempDirectory, "GAM-Update-Setup-*.exe"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRejectsUnknownLengthOversizeAndDeletesPartialTempFile()
    {
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var handler = new RecordingHandler((_, requestNumber) =>
                requestNumber == 0
                    ? UpdateAvailableResponse()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new UnknownLengthContent(new byte[20])
                    });
            using var client = new HttpClient(handler);
            var service = new UpdateService(
                "2.0.0",
                new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
                githubToken: null,
                client,
                maxUpdateDownloadBytes: 8,
                temporaryDirectory: tempDirectory);

            var check = await service.CheckForUpdateAsync(forceCheck: true);
            var update = Assert.IsType<UpdateInfo>(check.UpdateInfo);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAndInstallUpdateAsync(update.DownloadUrl, update.DownloadDigest));

            Assert.Contains("8-byte size limit", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(tempDirectory, "GAM-Update-Setup-*.exe"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckForUpdateAsync_RejectsUnknownLengthOversizeApiResponse()
    {
        using var client = new HttpClient(new RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(new byte[17])
            }));
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            githubToken: null,
            client,
            maxApiResponseBytes: 16);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("16-byte size limit", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_RejectsDeclaredOversizeApiResponse()
    {
        using var client = new HttpClient(new RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 17), Encoding.UTF8, "application/json")
            }));
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            githubToken: null,
            client,
            maxApiResponseBytes: 16);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("16-byte size limit", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.1.0-beta.1", "2.1.0", true)]
    [InlineData("2.1.0-beta.1", "2.1.0-beta.2", true)]
    [InlineData("2.1.0-beta.2", "2.1.0-beta.10", true)]
    [InlineData("2.1.0-alpha", "2.1.0-alpha.1", true)]
    [InlineData("2.1.0-alpha.1", "2.1.0-alpha.beta", true)]
    [InlineData("2.1.0-beta.11", "2.1.0-rc.1", true)]
    [InlineData("2.1.0", "2.1.0-beta.2", false)]
    [InlineData("2.1.0+local", "2.1.0+remote", false)]
    [InlineData("2.1.0-beta.2", "2.1.0-beta.02", false)]
    public void IsRemoteVersionNewer_UsesSemVerPrecedence(
        string current,
        string remote,
        bool expected)
    {
        var actual = UpdateService.IsRemoteVersionNewer(current, remote);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2026-08-03T11:00:00.0000000+00:00", "2026-08-03T12:00:00.0000000+00:00", true)]
    [InlineData("2026-08-02T12:00:00.0000000+00:00", "2026-08-03T12:00:00.0000000+00:00", false)]
    [InlineData("2026-08-04T12:00:00.0000000+00:00", "2026-08-03T12:00:00.0000000+00:00", false)]
    [InlineData("not-a-timestamp", "2026-08-03T12:00:00.0000000+00:00", false)]
    public void ShouldSkipUpdateCheck_OnlyAcceptsRecentNonFutureUtcAge(
        string lastCheck,
        string now,
        bool expected)
    {
        var actual = UpdateService.ShouldSkipUpdateCheck(
            lastCheck,
            DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task DeferUpdateCheckAsync_SkipsAutomaticCheckButNotForcedCheck()
    {
        var stateDirectory = CreateTemporaryDirectory();
        try
        {
            var handler = new RecordingHandler((_, _) => JsonResponse(
                """
                {
                  "tag_name": "v2.0.0",
                  "draft": false,
                  "prerelease": false,
                  "assets": []
                }
                """));
            using var client = new HttpClient(handler);
            var service = new UpdateService(
                "2.0.0",
                new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
                githubToken: null,
                client,
                updateStateDirectory: stateDirectory);

            await service.DeferUpdateCheckAsync();

            var automatic = await service.CheckForUpdateAsync(forceCheck: false);
            var forced = await service.CheckForUpdateAsync(forceCheck: true);

            Assert.Equal(UpdateCheckStatus.Skipped, automatic.Status);
            Assert.Equal(UpdateCheckStatus.UpToDate, forced.Status);
            Assert.Single(handler.Requests);

            var persisted = File.ReadAllText(Path.Combine(stateDirectory, "last_update_check.txt"));
            var timestamp = DateTimeOffset.Parse(
                persisted,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("v1.0.7.0", "1.0.7")]
    [InlineData("1.0.8.0", "1.0.8")]
    [InlineData("v1.0.9+69e8bf562388f955", "1.0.9")]
    [InlineData("v1.0.10-beta+local", "1.0.10")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void NormalizeVersionNumber_RemovesDisplayOnlySuffixesAndTrailingZeroRevision(
        string version,
        string expected)
    {
        var normalized = UpdateService.NormalizeVersionNumber(version);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("v1.0.7.0", "v1.0.7")]
    [InlineData("1.0.8+abc", "v1.0.8")]
    [InlineData("v2.1.0-beta.1+local", "v2.1.0-beta.1")]
    [InlineData("2.1.0-rc.2", "v2.1.0-rc.2")]
    [InlineData("", "unknown")]
    public void NormalizeVersionLabel_UsesConsistentUserFacingFormat(string version, string expected)
    {
        var label = UpdateService.NormalizeVersionLabel(version);

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("https://github.com/RiRi-380/GAM/releases/download/v1.0.0/GAM-Setup-v1.0.0.exe")]
    [InlineData("https://example.com/path/installer-latest.exe")]
    [InlineData("https://example.com/path/GAM-Setup-v1.0.0.exe?download=1")]
    public void ResolveInstallerArguments_SetupOrInstallerExe_ReturnsSilentArgs(string downloadUrl)
    {
        var args = UpdateService.ResolveInstallerArguments(downloadUrl);

        Assert.Equal(
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /LAUNCHAFTERINSTALL=1",
            args);
        Assert.DoesNotContain("/CURRENTUSER", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/ALLUSERS", args, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://example.com/path/GAM-Portable-v1.0.0.zip")]
    [InlineData("https://example.com/path/GAM.exe")]
    [InlineData("not-a-url")]
    public void ResolveInstallerArguments_NonInstallerInput_ReturnsEmpty(string downloadUrl)
    {
        var args = UpdateService.ResolveInstallerArguments(downloadUrl);

        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void SelectInstallerAsset_VersionedSetupExe_IsSelectedWithoutManifestRequirement()
    {
        var assets = new[]
        {
            new GitHubAsset { Name = "GAM-Portable-1.0.5.zip", BrowserDownloadUrl = "https://example.com/GAM-Portable-1.0.5.zip" },
            new GitHubAsset { Name = "GAM-UpdateManifest-1.0.5.json", BrowserDownloadUrl = "https://example.com/GAM-UpdateManifest-1.0.5.json" },
            new GitHubAsset { Name = "GAM-UpdateManifest-1.0.5.sig", BrowserDownloadUrl = "https://example.com/GAM-UpdateManifest-1.0.5.sig" },
            new GitHubAsset { Name = "GAM-Setup-1.0.5.exe", BrowserDownloadUrl = "https://example.com/GAM-Setup-1.0.5.exe", Digest = $"sha256:{new string('a', 64)}" }
        };

        var selected = UpdateService.SelectInstallerAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("GAM-Setup-1.0.5.exe", selected.Name);
    }

    [Fact]
    public void SelectInstallerAsset_PrefersSetupExeOverGenericExe()
    {
        var assets = new[]
        {
            new GitHubAsset { Name = "GAM.exe", BrowserDownloadUrl = "https://example.com/GAM.exe" },
            new GitHubAsset { Name = "GAM-Setup.exe", BrowserDownloadUrl = "https://example.com/GAM-Setup.exe" }
        };

        var selected = UpdateService.SelectInstallerAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("GAM-Setup.exe", selected.Name);
    }

    [Fact]
    public void SelectInstallerAsset_GenericExecutableIsNotTreatedAsAnInstaller()
    {
        var selected = UpdateService.SelectInstallerAsset(new[]
        {
            new GitHubAsset { Name = "GAM.exe", BrowserDownloadUrl = "https://example.com/GAM.exe" }
        });

        Assert.Null(selected);
    }

    [Fact]
    public void SelectPortableAsset_ChoosesVersionedGamPortableZipOnly()
    {
        var selected = UpdateService.SelectPortableAsset(new[]
        {
            new GitHubAsset { Name = "source.zip" },
            new GitHubAsset { Name = "GAM-Setup-2.0.0.exe" },
            new GitHubAsset { Name = "GAM-Portable-2.0.0.zip" }
        });

        Assert.NotNull(selected);
        Assert.Equal("GAM-Portable-2.0.0.zip", selected.Name);
    }

    [Fact]
    public async Task PortableCheck_SelectsPortableArchiveAndNeverSetupExecutable()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) => JsonResponse(
            """
            {
              "tag_name": "v2.1.0",
              "published_at": "2026-08-05T00:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "url": "https://updates.example.test/assets/setup",
                  "name": "GAM-Setup-2.1.0.exe",
                  "browser_download_url": "https://downloads.example.test/GAM-Setup-2.1.0.exe",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
                {
                  "url": "https://updates.example.test/assets/portable",
                  "name": "GAM-Portable-2.1.0.zip",
                  "browser_download_url": "https://downloads.example.test/GAM-Portable-2.1.0.zip",
                  "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                }
              ]
            }
            """)));
        var service = new UpdateService(
            "2.0.0",
            new UpdateSource { ApiUrl = "https://updates.example.test/releases" },
            githubToken: null,
            client,
            portableInstallation: true);

        var result = await service.CheckForUpdateAsync(forceCheck: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        var update = Assert.IsType<UpdateInfo>(result.UpdateInfo);
        Assert.Equal(UpdatePackageKind.PortableArchive, update.PackageKind);
        Assert.EndsWith("GAM-Portable-2.1.0.zip", update.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPortableInstallation_RequiresExplicitPackageMarker()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            Assert.False(UpdateService.IsPortableInstallation(directory));
            File.WriteAllText(
                Path.Combine(directory, UpdateService.PortableMarkerFileName),
                "{}");
            Assert.True(UpdateService.IsPortableInstallation(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PortableInstall_RejectsSetupPackageBeforeNetworkAccess()
    {
        var handler = new RecordingHandler((_, _) => BinaryResponse("must not download"));
        using var client = new HttpClient(handler);
        var service = new UpdateService(
            "2.0.0",
            source: null,
            githubToken: null,
            client,
            portableInstallation: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallUpdateAsync(
                "https://downloads.example.test/GAM-Setup-2.1.0.exe",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void BuildInstallerLauncherScript_WaitsForCurrentProcessAndStartsInstaller()
    {
        var script = UpdateService.BuildInstallerLauncherScript(
            12345,
            @"C:\Temp\GAM's Setup.exe",
            "/VERYSILENT /SP-");

        Assert.Contains("Get-Process -Id 12345 -ErrorAction SilentlyContinue", script);
        Assert.Contains("$currentProcess | Wait-Process -Timeout 60", script);
        Assert.Contains("$installerPath = 'C:\\Temp\\GAM''s Setup.exe'", script);
        Assert.Contains("$installerArguments = '/VERYSILENT /SP-'", script);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        Assert.Contains("Start-Process -FilePath $installerPath -ArgumentList $installerArguments -PassThru", script);
        Assert.Contains("$installerProcess | Wait-Process", script);
        Assert.DoesNotContain("Start-Process -FilePath $installerPath -Wait", script);
        Assert.DoesNotContain("Start-Process -FilePath $installerPath -ArgumentList $installerArguments -Wait", script);
        Assert.Contains("if ($installerProcess.ExitCode -ne 0)", script);
        Assert.Contains("update-installer.log", script);
        Assert.Contains("Remove-Item -LiteralPath $installerPath", script);
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", script);
        Assert.DoesNotContain("$ErrorActionPreference = 'SilentlyContinue'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstallerLauncherScript_ProducesValidWindowsPowerShellSyntax()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"gam-update-launcher-parse-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(
                scriptPath,
                UpdateService.BuildInstallerLauncherScript(
                    12345,
                    @"C:\Temp\GAM's Setup.exe",
                    "/VERYSILENT /SP-"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var escapedPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
            var parserCommand =
                "$tokens = $null; $errors = $null; " +
                $"[void][System.Management.Automation.Language.Parser]::ParseFile('{escapedPath}', [ref]$tokens, [ref]$errors); " +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_) }; exit 1 }";
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(parserCommand);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var parserExited = process.WaitForExit(30_000);
            if (!parserExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Assert.True(parserExited, "PowerShell parser timed out after 30 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"PowerShell parser rejected the launcher: {process.StandardError.ReadToEnd()}");
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    [Fact]
    public void CreateInstallerLauncherStartInfo_RunsPowerShellScriptHidden()
    {
        var startInfo = UpdateService.CreateInstallerLauncherStartInfo(@"C:\Temp\launcher.ps1");

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Contains("-File", startInfo.ArgumentList);
        Assert.Contains(@"C:\Temp\launcher.ps1", startInfo.ArgumentList);
    }

    [Fact]
    public void InstallerLauncher_CleansPackageWithoutWaitingForRelaunchedDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        var fakeInstallerPath = Path.Combine(directory, "fake-installer.exe");
        var fakeInstallerScriptPath = Path.Combine(directory, "fake-installer.ps1");
        var childScriptPath = Path.Combine(directory, "relaunch-child.ps1");
        var childPidPath = Path.Combine(directory, "relaunch-child.pid");
        var launcherPath = Path.Combine(directory, "launcher.ps1");
        Process? launcherProcess = null;
        Process? childProcess = null;
        try
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeInstallerPath);
            File.WriteAllText(
                childScriptPath,
                "Start-Sleep -Seconds 30",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            static string PowerShellLiteral(string value) =>
                $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            var quotedChildPath = PowerShellLiteral($"\"{childScriptPath}\"");
            File.WriteAllText(
                fakeInstallerScriptPath,
                string.Join(
                    Environment.NewLine,
                    "$childProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @("
                        + "'-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "
                        + quotedChildPath
                        + ") -PassThru",
                    $"[System.IO.File]::WriteAllText({PowerShellLiteral(childPidPath)}, [string]$childProcess.Id)",
                    string.Empty),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var installerArguments =
                "/d /s /c powershell.exe -NoProfile -NonInteractive "
                + "-ExecutionPolicy Bypass -File \""
                + fakeInstallerScriptPath
                + "\"";
            File.WriteAllText(
                launcherPath,
                UpdateService.BuildInstallerLauncherScript(
                    int.MaxValue,
                    fakeInstallerPath,
                    installerArguments),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            launcherProcess = Process.Start(
                UpdateService.CreateInstallerLauncherStartInfo(launcherPath));
            Assert.NotNull(launcherProcess);
            Assert.True(
                launcherProcess.WaitForExit(15_000),
                "The launcher waited for the relaunched descendant instead of only the installer.");
            Assert.Equal(0, launcherProcess.ExitCode);

            Assert.True(File.Exists(childPidPath), "The fake installer did not launch its child process.");
            childProcess = Process.GetProcessById(int.Parse(
                File.ReadAllText(childPidPath),
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(childProcess.HasExited);
            Assert.False(File.Exists(fakeInstallerPath));
            Assert.False(File.Exists(launcherPath));
        }
        finally
        {
            if (launcherProcess is { HasExited: false })
            {
                launcherProcess.Kill(entireProcessTree: true);
                launcherProcess.WaitForExit();
            }
            if (childProcess is null
                && File.Exists(childPidPath)
                && int.TryParse(
                    File.ReadAllText(childPidPath),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var childProcessId))
            {
                try
                {
                    childProcess = Process.GetProcessById(childProcessId);
                }
                catch (ArgumentException)
                {
                    // The short-lived test child already exited.
                }
            }
            if (childProcess is { HasExited: false })
            {
                childProcess.Kill(entireProcessTree: true);
                childProcess.WaitForExit();
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreatePortablePackageRevealStartInfo_UsesExplorerSelectWithoutShellText()
    {
        var startInfo = UpdateService.CreatePortablePackageRevealStartInfo(
            @"C:\Temp\GAM-Portable-2.1.0.zip");

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(
            "/select,\"C:\\Temp\\GAM-Portable-2.1.0.zip\"",
            startInfo.Arguments);
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("", false)]
    [InlineData("sha256:1234", false)]
    [InlineData("md5:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void TryNormalizeSha256Digest_ValidatesGitHubDigestFormat(string digest, bool expected)
    {
        var actual = UpdateService.TryNormalizeSha256Digest(digest, out var normalized);

        Assert.Equal(expected, actual);
        Assert.Equal(expected ? digest.Substring(digest.IndexOf(':') + 1).ToLowerInvariant() : string.Empty, normalized);
    }

    [Fact]
    public void VerifyDownloadedFileDigest_RejectsTamperedPayload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gam-update-digest-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "verified payload");
            UpdateService.VerifyDownloadedFileDigest(
                path,
                "sha256:3aac0a1146ffe55bac7c05f61401fb1e7e4e6a94110b91585c646fe8cf745f28");

            File.AppendAllText(path, " tampered");
            Assert.Throws<InvalidDataException>(() => UpdateService.VerifyDownloadedFileDigest(
                path,
                "sha256:3aac0a1146ffe55bac7c05f61401fb1e7e4e6a94110b91585c646fe8cf745f28"));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage BinaryResponse(string value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value))
        };
    }

    private static HttpResponseMessage UpdateAvailableResponse()
    {
        return JsonResponse(
            """
            {
              "tag_name": "v2.1.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "url": "https://updates.example.test/releases/assets/42",
                  "name": "GAM-Setup-2.1.0.exe",
                  "browser_download_url": "https://downloads.example.test/GAM-Setup-2.1.0.exe",
                  "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                }
              ]
            }
            """);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"gam-update-size-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory;
        private int requestCount;

        public RecordingHandler(
            Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = requestCount++;
            Requests.Add(new RequestSnapshot(
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.Select(value => value.MediaType ?? string.Empty).ToArray()));
            var response = responseFactory(request, requestNumber);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record RequestSnapshot(
        string Uri,
        string? Authorization,
        string[] Accept);

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] payload;

        public UnknownLengthContent(byte[] payload)
        {
            this.payload = payload;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return stream.WriteAsync(payload, 0, payload.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
