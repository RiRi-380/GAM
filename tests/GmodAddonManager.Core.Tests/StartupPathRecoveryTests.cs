using System.Diagnostics;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Tests;

public sealed class StartupPathRecoveryTests
{
    [Fact]
    public void PathOverrideResolver_AcceptsGmodInstallFolder()
    {
        using var env = new TestSteamLayout();

        var ok = PathOverrideResolver.TryResolveSelectedFolder(
            env.GmodInstallPath,
            out var resolution,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(env.GmodInstallPath, resolution.GmodInstallPath);
        Assert.Equal(env.WorkshopRootPath, resolution.WorkshopRootPath);
        Assert.Equal(env.GmodInstallPath, resolution.Snapshot.GmodInstall!.InstallPath);
        Assert.Equal(PathCandidateConfidence.High, resolution.Snapshot.ActiveWorkshopRoot!.Confidence);
        Assert.Equal(0, resolution.Snapshot.ActiveWorkshopRoot.ValidPayloadCount);
        Assert.Equal(0, resolution.Snapshot.ActiveWorkshopRoot.EmptyOrInvalidFolderCount);
    }

    [Fact]
    public void PathOverrideResolver_AcceptsSteamLibraryFolder()
    {
        using var env = new TestSteamLayout();

        var ok = PathOverrideResolver.TryResolveSelectedFolder(
            env.LibraryPath,
            out var resolution,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(env.GmodInstallPath, resolution.GmodInstallPath);
        Assert.Equal(env.WorkshopRootPath, resolution.WorkshopRootPath);
    }

    [Fact]
    public void PathOverrideResolver_RejectsSteamLibraryWhoseManifestHasWrongAppId()
    {
        using var env = new TestSteamLayout();
        File.WriteAllText(
            Path.Combine(env.LibraryPath, "steamapps", "appmanifest_4000.acf"),
            """
            "AppState"
            {
                "appid" "9999"
                "installdir" "GarrysMod"
            }
            """);

        var ok = PathOverrideResolver.TryResolveSelectedFolder(
            env.LibraryPath,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("Select the Garry's Mod install folder", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PathOverrideResolver_AcceptsWorkshopRootFolder()
    {
        using var env = new TestSteamLayout();

        var ok = PathOverrideResolver.TryResolveSelectedFolder(
            env.WorkshopRootPath,
            out var resolution,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(env.GmodInstallPath, resolution.GmodInstallPath);
        Assert.Equal(env.WorkshopRootPath, resolution.WorkshopRootPath);
    }

    [Fact]
    public void PathOverrideResolver_DoesNotApplyLibraryManifestToCustomWorkshopRoot()
    {
        using var env = new TestSteamLayout();
        var customWorkshopRoot = Path.Combine(env.LibraryPath, "custom-workshop");
        var luaPath = Path.Combine(customWorkshopRoot, "456", "lua");
        Directory.CreateDirectory(luaPath);
        File.WriteAllText(Path.Combine(luaPath, "autorun.lua"), "print('ok')");

        var ok = PathOverrideResolver.TryCreateSnapshot(
            env.GmodInstallPath,
            customWorkshopRoot,
            out var snapshot,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(snapshot.ActiveWorkshopRoot);
        Assert.False(snapshot.ActiveWorkshopRoot!.HasAppWorkshopManifest);
        Assert.Equal(PathCandidateConfidence.Medium, snapshot.ActiveWorkshopRoot.Confidence);
        Assert.Equal(0, snapshot.ActiveWorkshopRoot.ValidPayloadCount);
    }

    [Fact]
    public void PathOverrideResolver_RatesAuthoritativeManifestWithoutMatchingFolderAsLow()
    {
        using var env = new TestSteamLayout();
        WorkshopManifestTestData.Write(
            Path.Combine(env.LibraryPath, "steamapps", "workshop"));

        var ok = PathOverrideResolver.TryCreateSnapshot(
            env.GmodInstallPath,
            env.WorkshopRootPath,
            out var snapshot,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(snapshot.ActiveWorkshopRoot);
        Assert.True(snapshot.ActiveWorkshopRoot!.HasAppWorkshopManifest);
        Assert.Equal(PathCandidateConfidence.Low, snapshot.ActiveWorkshopRoot.Confidence);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_PromptsWhenPreviousPathIsMissingAndNewCandidateExists()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var config = new Configuration();
        PathHealthService.UpdatePathState(
            config,
            oldEnv.CreateSnapshot(),
            Path.Combine(oldEnv.WorkshopRootPath, ".addon-manager"),
            Path.Combine(oldEnv.WorkshopRootPath, ".addon-manager", "addons"));
        oldEnv.DeleteLayout();

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, currentEnv.CreateSnapshot());

        Assert.True(decision.ShouldPrompt);
        Assert.True(decision.HasDetectedCandidate);
        Assert.Equal(StartupPathRecoveryReason.RecordedPathMissing, decision.Reason);
        Assert.Equal(currentEnv.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Equal(currentEnv.WorkshopRootPath, decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_PromptsWhenPreviousPathDiffersEvenIfOldPathStillExists()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var config = new Configuration();
        PathHealthService.UpdatePathState(
            config,
            oldEnv.CreateSnapshot(),
            Path.Combine(oldEnv.WorkshopRootPath, ".addon-manager"),
            Path.Combine(oldEnv.WorkshopRootPath, ".addon-manager", "addons"));

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, currentEnv.CreateSnapshot());

        Assert.True(decision.ShouldPrompt);
        Assert.True(decision.HasDetectedCandidate);
        Assert.Equal(StartupPathRecoveryReason.RecordedPathChanged, decision.Reason);
        Assert.Equal(oldEnv.GmodInstallPath, decision.PreviousGmodInstallPath);
        Assert.Equal(oldEnv.WorkshopRootPath, decision.PreviousWorkshopRootPath);
        Assert.Equal(currentEnv.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Equal(currentEnv.WorkshopRootPath, decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_DoesNotTreatLegacyMetadataAsARecordedInstallPath()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var config = new Configuration();
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(oldEnv.WorkshopRootPath, "123"));
        oldEnv.DeleteLayout();

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, currentEnv.CreateSnapshot());

        Assert.False(decision.ShouldPrompt);
        Assert.True(decision.HasDetectedCandidate);
        Assert.Null(decision.PreviousGmodInstallPath);
        Assert.Null(decision.PreviousWorkshopRootPath);
        Assert.Equal(currentEnv.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Equal(currentEnv.WorkshopRootPath, decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_DoesNotPromptForLegacyConfigAlreadyOnDetectedWorkshopRoot()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration();
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(env.WorkshopRootPath, "123"));

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, env.CreateSnapshot());

        Assert.False(decision.ShouldPrompt);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_DoesNotPromptForLegacyInventoryWithoutRecordedPaths()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration { SchemaVersion = 1 };
        for (var i = 1; i <= 1021; i++)
        {
            var addonId = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            config.AddonMetadata[addonId] = new WorkshopAddon(
                addonId,
                Path.Combine(env.WorkshopRootPath, addonId));
        }

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, env.CreateSnapshot());

        Assert.False(decision.ShouldPrompt);
        Assert.True(decision.HasDetectedCandidate);
        Assert.Equal(StartupPathRecoveryReason.None, decision.Reason);
        Assert.Equal(env.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Equal(env.WorkshopRootPath, decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_DoesNotPromptForConfirmedExistingConfig()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration();
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(env.WorkshopRootPath, "123"));

        var decision = StartupPathRecoveryEvaluator.Evaluate(
            config,
            env.CreateSnapshot(),
            confirmedGmodInstallPath: env.GmodInstallPath,
            confirmedWorkshopRootPath: env.WorkshopRootPath);

        Assert.False(decision.ShouldPrompt);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_PromptsWhenConfirmedPathDiffersFromDetectedPath()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var config = new Configuration();
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(currentEnv.WorkshopRootPath, "123"));

        var decision = StartupPathRecoveryEvaluator.Evaluate(
            config,
            currentEnv.CreateSnapshot(),
            confirmedGmodInstallPath: oldEnv.GmodInstallPath,
            confirmedWorkshopRootPath: oldEnv.WorkshopRootPath);

        Assert.True(decision.ShouldPrompt);
        Assert.True(decision.HasDetectedCandidate);
        Assert.Equal(StartupPathRecoveryReason.RecordedPathChanged, decision.Reason);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_PromptsWhenExistingInventoryHasUnreadableWorkshopCandidate()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration { SchemaVersion = 1 };
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(env.WorkshopRootPath, "123"));
        var snapshot = env.CreateSnapshot();
        snapshot.ActiveWorkshopRoot = new WorkshopRootCandidate
        {
            RootPath = Path.Combine(env.LibraryPath, "missing-workshop-root"),
            Confidence = PathCandidateConfidence.Low
        };

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, snapshot);

        Assert.True(decision.ShouldPrompt);
        Assert.False(decision.HasDetectedCandidate);
        Assert.Equal(StartupPathRecoveryReason.WorkshopPathUnavailable, decision.Reason);
        Assert.Null(decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_IdentifiesWorkshopWhenOnlyConfiguredWorkshopIsUnreadable()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration { SchemaVersion = 1 };
        config.AddonMetadata["123"] = new WorkshopAddon(
            "123",
            Path.Combine(env.WorkshopRootPath, "123"));
        var missingWorkshopRoot = Path.Combine(env.LibraryPath, "missing-workshop-root");
        var snapshot = env.CreateSnapshot();
        snapshot.ActiveWorkshopRoot = new WorkshopRootCandidate
        {
            RootPath = missingWorkshopRoot,
            Confidence = PathCandidateConfidence.Rejected
        };

        var decision = StartupPathRecoveryEvaluator.Evaluate(
            config,
            snapshot,
            configuredGmodInstallPath: env.GmodInstallPath,
            configuredWorkshopRootPath: missingWorkshopRoot);

        Assert.True(decision.ShouldPrompt);
        Assert.Equal(StartupPathRecoveryReason.WorkshopPathUnavailable, decision.Reason);
        Assert.Equal(env.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Null(decision.DetectedWorkshopRootPath);
    }

    [Fact]
    public void PathOverrideResolver_RejectsDanglingWorkshopJunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "gam-dangling-junction-test-" + Guid.NewGuid().ToString("N"));
        var junction = Path.Combine(root, "4000");
        var missingTarget = Path.Combine(root, "missing-target");
        var gmodInstall = Path.Combine(root, "GarrysMod");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(gmodInstall, "garrysmod"));
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(junction);
            startInfo.ArgumentList.Add(missingTarget);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Could not create test junction: {process.StandardError.ReadToEnd()}");
            Assert.True(Directory.Exists(junction));
            Assert.False(PathOverrideResolver.IsDirectoryUsable(junction));
            Assert.False(
                PathOverrideResolver.TryCreateSnapshot(gmodInstall, junction, out _, out var error));
            Assert.Contains("unreadable", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void StartupPathRecoveryEvaluator_DoesNotPromptWhenPreviousPathStillMatches()
    {
        using var env = new TestSteamLayout();
        var config = new Configuration();
        var snapshot = env.CreateSnapshot();
        PathHealthService.UpdatePathState(
            config,
            snapshot,
            Path.Combine(env.WorkshopRootPath, ".addon-manager"),
            Path.Combine(env.WorkshopRootPath, ".addon-manager", "addons"));

        var decision = StartupPathRecoveryEvaluator.Evaluate(config, snapshot);

        Assert.False(decision.ShouldPrompt);
    }

    [Fact]
    public async Task StartupRecoveryFlow_ReusesExistingConfigAndRewritesCurrentNoMountFromRecoveredState()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var appDataPath = Path.Combine(Path.GetTempPath(), "gam-startup-recovery-appdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);
        try
        {
            var config = new Configuration();
            var asset = new Asset("Recovered Asset");
            asset.AddAddon("123");
            asset.SetWholeState(AddonState.Excluded);
            config.Assets.Add(asset);
            config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(oldEnv.WorkshopRootPath, "123"));
            PathHealthService.UpdatePathState(
                config,
                oldEnv.CreateSnapshot(),
                Path.Combine(appDataPath, "manager"),
                Path.Combine(appDataPath, "addons"));
            File.WriteAllText(
                Path.Combine(appDataPath, "config.json"),
                JsonConvert.SerializeObject(config, Formatting.Indented));
            oldEnv.DeleteLayout();

            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomAppDataPath = appDataPath,
                CustomGmodInstallPath = currentEnv.GmodInstallPath,
                CustomWorkshopPath = currentEnv.WorkshopRootPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = [currentEnv.WorkshopManifestPath],
                ScanCacheTtl = TimeSpan.Zero
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();

            var repairResult = await manager.RepairStalePathMetadataAsync();
            await manager.UpdateAddonStatesAsync();

            Assert.Equal(1, repairResult.ChangedCount);
            Assert.Single(manager.GetConfiguration().Assets, a => a.Name == "Recovered Asset");
            Assert.Equal(Path.Combine(currentEnv.WorkshopRootPath, "123"), manager.GetConfiguration().AddonMetadata["123"].FolderPath);
            var noMountText = File.ReadAllText(Path.Combine(currentEnv.GmodInstallPath, "garrysmod", "cfg", "addonnomount.txt"));
            Assert.Contains("123", noMountText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(appDataPath))
            {
                Directory.Delete(appDataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartupRecoveryFlow_RepairsLegacyConfigWithoutPathState()
    {
        using var oldEnv = new TestSteamLayout();
        using var currentEnv = new TestSteamLayout();
        var appDataPath = Path.Combine(Path.GetTempPath(), "gam-startup-recovery-legacy-appdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);
        try
        {
            var config = new Configuration();
            var asset = new Asset("Legacy Asset");
            asset.AddAddon("123");
            asset.SetWholeState(AddonState.Excluded);
            config.Assets.Add(asset);
            config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(oldEnv.WorkshopRootPath, "123"));
            File.WriteAllText(
                Path.Combine(appDataPath, "config.json"),
                JsonConvert.SerializeObject(config, Formatting.Indented));
            oldEnv.DeleteLayout();

            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomAppDataPath = appDataPath,
                CustomGmodInstallPath = currentEnv.GmodInstallPath,
                CustomWorkshopPath = currentEnv.WorkshopRootPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = [currentEnv.WorkshopManifestPath],
                ScanCacheTtl = TimeSpan.Zero
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();

            var repairResult = await manager.RepairStalePathMetadataAsync();
            await manager.UpdateAddonStatesAsync();

            Assert.Equal(1, repairResult.ChangedCount);
            Assert.Single(manager.GetConfiguration().Assets, a => a.Name == "Legacy Asset");
            Assert.Equal(Path.Combine(currentEnv.WorkshopRootPath, "123"), manager.GetConfiguration().AddonMetadata["123"].FolderPath);
            var noMountText = File.ReadAllText(Path.Combine(currentEnv.GmodInstallPath, "garrysmod", "cfg", "addonnomount.txt"));
            Assert.Contains("123", noMountText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(appDataPath))
            {
                Directory.Delete(appDataPath, recursive: true);
            }
        }
    }

    [Fact]
    public void PendingChangeManager_ParsesApplyStatesAction()
    {
        Assert.Equal(PendingChangeActionType.ApplyStates, PendingChangeManager.ParseActionType("apply_states"));
    }

    private sealed class TestSteamLayout : IDisposable
    {
        private readonly string rootPath;

        public TestSteamLayout()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-startup-path-tests-" + Guid.NewGuid().ToString("N"));
            LibraryPath = Path.Combine(rootPath, "SteamLibrary");
            GmodInstallPath = Path.Combine(LibraryPath, "steamapps", "common", "GarrysMod");
            WorkshopRootPath = Path.Combine(LibraryPath, "steamapps", "workshop", "content", "4000");

            Directory.CreateDirectory(Path.Combine(GmodInstallPath, "garrysmod", "cfg"));
            Directory.CreateDirectory(WorkshopRootPath);
            Directory.CreateDirectory(Path.Combine(LibraryPath, "steamapps", "workshop"));
            File.WriteAllText(Path.Combine(LibraryPath, "steamapps", "appmanifest_4000.acf"), BuildAppManifest());
            WorkshopManifestPath = WorkshopManifestTestData.Write(
                Path.Combine(LibraryPath, "steamapps", "workshop"),
                "123");
            WritePayload(Path.Combine(WorkshopRootPath, "123"));
        }

        public string LibraryPath { get; }
        public string GmodInstallPath { get; }
        public string WorkshopRootPath { get; }
        public string WorkshopManifestPath { get; }

        public PathSnapshot CreateSnapshot()
        {
            var ok = PathOverrideResolver.TryCreateSnapshot(
                GmodInstallPath,
                WorkshopRootPath,
                out var snapshot,
                out var error);
            Assert.True(ok, error);
            return snapshot;
        }

        public void DeleteLayout()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        public void Dispose()
        {
            DeleteLayout();
        }

        private static string BuildAppManifest()
        {
            return """
                   "AppState"
                   {
                       "appid" "4000"
                       "installdir" "GarrysMod"
                   }
                   """;
        }

        private static void WritePayload(string addonPath)
        {
            var luaPath = Path.Combine(addonPath, "lua");
            Directory.CreateDirectory(luaPath);
            File.WriteAllText(Path.Combine(luaPath, "autorun.lua"), "print('ok')");
        }
    }
}
