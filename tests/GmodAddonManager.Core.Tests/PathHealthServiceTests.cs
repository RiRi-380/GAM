using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class PathHealthServiceTests
{
    [Fact]
    public void UpdatePathState_StoresPreviousSnapshotAndPathChanges()
    {
        using var env = new TestEnvironment();
        var config = new Configuration();
        var first = CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot);
        var second = CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot);

        PathHealthService.UpdatePathState(config, first, env.OldManagerPath, env.OldAddonsPath);
        PathHealthService.UpdatePathState(config, second, env.CurrentManagerPath, env.CurrentAddonsPath);

        Assert.Equal(env.CurrentGmodPath, config.PathState.LastDetectedSnapshot!.GmodInstall!.InstallPath);
        Assert.Equal(env.OldGmodPath, config.PathState.PreviousDetectedSnapshot!.GmodInstall!.InstallPath);
        Assert.Contains(config.PathState.Changes, change => change.PathKind == "GModInstall");
        Assert.Contains(config.PathState.Changes, change => change.PathKind == "WorkshopRoot");
        Assert.Contains(config.PathState.Changes, change => change.PathKind == "ManagedAddonsPath");
    }

    [Fact]
    public void UpdatePathState_DoesNotPromotePartialSnapshotToLastKnownGood()
    {
        using var env = new TestEnvironment();
        var config = new Configuration();
        var healthy = CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot);
        PathHealthService.UpdatePathState(
            config,
            healthy,
            env.OldManagerPath,
            env.OldAddonsPath);

        var partial = CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot);
        partial.ActiveWorkshopRoot = null;
        partial.WorkshopRoots = Array.Empty<WorkshopRootCandidate>();
        PathHealthService.UpdatePathState(
            config,
            partial,
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Same(partial, config.PathState.LastDetectedSnapshot);
        Assert.Same(healthy, config.PathState.LastKnownGoodSnapshot);
        Assert.Equal(
            env.OldWorkshopRoot,
            config.PathState.LastKnownGoodSnapshot!.ActiveWorkshopRoot!.RootPath);
    }

    [Fact]
    public void BuildReport_FindsStaleWorkshopMetadataRepairCandidate()
    {
        using var env = new TestEnvironment();
        WritePayload(Path.Combine(env.CurrentWorkshopRoot, "123"));
        var config = new Configuration();
        config.AddonMetadata["123"] = new WorkshopAddon("123", Path.Combine(env.OldWorkshopRoot, "123"));
        PathHealthService.UpdatePathState(config, CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot), env.OldManagerPath, env.OldAddonsPath);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        var candidate = Assert.Single(report.MetadataRepairCandidates);
        Assert.Equal("123", candidate.AddonId);
        Assert.Equal(Path.Combine(env.CurrentWorkshopRoot, "123"), candidate.NewPath);
    }

    [Fact]
    public void MigrateAddonNoMountEntries_CopiesMissingIdsOnly()
    {
        using var env = new TestEnvironment();
        var oldNoMount = Path.Combine(env.OldGmodPath, "garrysmod", "cfg", "addonnomount.txt");
        var currentNoMount = Path.Combine(env.CurrentGmodPath, "garrysmod", "cfg", "addonnomount.txt");
        PathHealthService.WriteAddonNoMountIds(oldNoMount, new[] { "100", "200" });
        PathHealthService.WriteAddonNoMountIds(currentNoMount, new[] { "200", "300" });
        var config = new Configuration();
        PathHealthService.UpdatePathState(config, CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot), env.OldManagerPath, env.OldAddonsPath);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Equal(new[] { "100" }, report.AddonNoMountMigrationPlan.ToMigrateIds);

        var result = PathHealthService.MigrateAddonNoMountEntries(report.AddonNoMountMigrationPlan);
        var ids = PathHealthService.ReadAddonNoMountIds(currentNoMount);

        Assert.Equal(1, result.ChangedCount);
        Assert.True(ids.SetEquals(new[] { "100", "200", "300" }));
    }

    [Fact]
    public void BuildReport_DoesNotOfferSteamManagedFoldersForCleanup()
    {
        using var env = new TestEnvironment();
        var emptyWorkshopFolder = Path.Combine(env.OldWorkshopRoot, "100");
        Directory.CreateDirectory(emptyWorkshopFolder);
        WritePayload(Path.Combine(env.OldWorkshopRoot, "200"));
        var config = new Configuration();
        PathHealthService.UpdatePathState(config, CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot), env.OldManagerPath, env.OldAddonsPath);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                     issue.Contains("cleanup", StringComparison.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(emptyWorkshopFolder));
        Assert.True(Directory.Exists(Path.Combine(env.OldWorkshopRoot, "200")));
    }

    [Fact]
    public void BuildReport_CorruptManagedPathPointingAtWorkshopRootDoesNotOfferPayloadForMigration()
    {
        using var env = new TestEnvironment();
        var workshopPayload = Path.Combine(env.OldWorkshopRoot, "111");
        WritePayload(workshopPayload);
        var config = new Configuration();
        config.PathState.LastManagerPath = Path.GetDirectoryName(env.OldWorkshopRoot);
        config.PathState.LastAddonsPath = env.OldWorkshopRoot;

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Empty(report.ManagedDataMigrationCandidates);
        Assert.True(Directory.Exists(workshopPayload));
    }

    [Fact]
    public void BuildReport_CorruptManagedPathDoesNotOfferWorkshopPayloadForMigration()
    {
        using var env = new TestEnvironment();
        var workshopPayload = Path.Combine(env.OldWorkshopRoot, "123");
        WritePayload(workshopPayload);
        var fakeManagedRoot = Path.Combine(env.OldWorkshopRoot, "addons");
        Directory.CreateDirectory(fakeManagedRoot);
        WritePayload(Path.Combine(fakeManagedRoot, "456"));
        var config = new Configuration();
        config.PathState.LastManagerPath = env.OldWorkshopRoot;
        config.PathState.LastAddonsPath = fakeManagedRoot;
        config.PathState.LastDetectedSnapshot = CreateSnapshot(
            env.OldGmodPath,
            env.OldWorkshopRoot);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Empty(report.ManagedDataMigrationCandidates);
        Assert.Contains(
            report.Issues,
            issue => issue.Contains(
                "untrusted previous managed addons root",
                StringComparison.Ordinal));
        Assert.True(Directory.Exists(workshopPayload));
        Assert.True(Directory.Exists(Path.Combine(fakeManagedRoot, "456")));
    }

    [Fact]
    public void BuildReport_CorruptManagedPathDoesNotOfferGmodLocalAddonsForMigration()
    {
        using var env = new TestEnvironment();
        var garrysmodRoot = Path.Combine(env.OldGmodPath, "garrysmod");
        var localAddonsRoot = Path.Combine(garrysmodRoot, "addons");
        var localPayload = Path.Combine(localAddonsRoot, "789");
        WritePayload(localPayload);
        var config = new Configuration();
        config.PathState.LastManagerPath = garrysmodRoot;
        config.PathState.LastAddonsPath = localAddonsRoot;
        config.PathState.LastDetectedSnapshot = CreateSnapshot(
            env.OldGmodPath,
            env.OldWorkshopRoot);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Empty(report.ManagedDataMigrationCandidates);
        Assert.True(Directory.Exists(localPayload));
    }

    [Fact]
    public void MigrateManagedData_ForgedWorkshopCandidateIsRejectedAtExecution()
    {
        using var env = new TestEnvironment();
        var workshopPayload = Path.Combine(env.OldWorkshopRoot, "321");
        var target = Path.Combine(env.CurrentAddonsPath, "321");
        WritePayload(workshopPayload);

        var result = PathHealthService.MigrateManagedData(
            new[]
            {
                new ManagedDataMigrationCandidate
                {
                    AddonId = "321",
                    SourcePath = workshopPayload,
                    TargetPath = target,
                    IsDirectory = true
                }
            },
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(Directory.Exists(workshopPayload));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void BuildReport_ArbitraryGmodAddonManagerFolderIsNotAnOwnedLegacySource()
    {
        using var env = new TestEnvironment();
        var forgedManager = Path.Combine(env.RootPath, "Victim", "GmodAddonManager");
        var forgedAddons = Path.Combine(forgedManager, "addons");
        var payload = Path.Combine(forgedAddons, "654");
        WritePayload(payload);
        var config = new Configuration();
        config.PathState.LastManagerPath = forgedManager;
        config.PathState.LastAddonsPath = forgedAddons;

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Empty(report.ManagedDataMigrationCandidates);
        Assert.True(Directory.Exists(payload));
    }

    [Fact]
    public void MigrateManagedData_TargetOutsideRuntimeManagedRootIsRejectedAtExecution()
    {
        using var env = new TestEnvironment();
        var legacyPayload = Path.Combine(env.OldAddonsPath, "987");
        var forgedTargetRoot = Path.Combine(env.RootPath, "ForgedTarget", "addons");
        var forgedTarget = Path.Combine(forgedTargetRoot, "987");
        WritePayload(legacyPayload);
        Directory.CreateDirectory(forgedTargetRoot);

        var result = PathHealthService.MigrateManagedData(
            new[]
            {
                new ManagedDataMigrationCandidate
                {
                    AddonId = "987",
                    SourcePath = legacyPayload,
                    TargetPath = forgedTarget,
                    IsDirectory = true
                }
            },
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(Directory.Exists(legacyPayload));
        Assert.False(Directory.Exists(forgedTarget));
    }

    [Fact]
    public void PublicPathHealthContract_DoesNotExposeWorkshopCleanup()
    {
        Assert.DoesNotContain(
            typeof(PathHealthReport).GetProperties(),
            property => property.Name.Contains("Cleanup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(PathHealthService).GetMethods(),
            method => method.Name.Contains("Cleanup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(AddonManager).GetMethods(),
            method => method.Name.Contains("CleanupStaleEmptyWorkshop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MigrateManagedData_MovesOnlyManagedRootEntriesWhenTargetMissing()
    {
        using var env = new TestEnvironment();
        var oldAddon = Path.Combine(env.OldAddonsPath, "123");
        var existingOldAddon = Path.Combine(env.OldAddonsPath, "456");
        Directory.CreateDirectory(oldAddon);
        WritePayload(oldAddon);
        Directory.CreateDirectory(existingOldAddon);
        WritePayload(existingOldAddon);
        Directory.CreateDirectory(Path.Combine(env.CurrentAddonsPath, "456"));
        var config = new Configuration();
        PathHealthService.UpdatePathState(config, CreateSnapshot(env.OldGmodPath, env.OldWorkshopRoot), env.OldManagerPath, env.OldAddonsPath);

        var report = PathHealthService.BuildReport(
            config,
            CreateSnapshot(env.CurrentGmodPath, env.CurrentWorkshopRoot),
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        var candidate = Assert.Single(report.ManagedDataMigrationCandidates);
        Assert.Equal("123", candidate.AddonId);

        var result = PathHealthService.MigrateManagedData(
            report.ManagedDataMigrationCandidates,
            env.CurrentManagerPath,
            env.CurrentAddonsPath);

        Assert.Equal(1, result.MovedCount);
        Assert.False(Directory.Exists(oldAddon));
        Assert.True(Directory.Exists(Path.Combine(env.CurrentAddonsPath, "123")));
        Assert.True(Directory.Exists(existingOldAddon));
    }

    private static PathSnapshot CreateSnapshot(string gmodPath, string workshopRoot)
    {
        return new PathSnapshot
        {
            SteamRootPath = Path.GetPathRoot(gmodPath),
            GmodInstall = new GmodInstallCandidate
            {
                InstallPath = gmodPath,
                Confidence = PathCandidateConfidence.High,
                DirectoryExists = true,
                GarrysmodDirectoryExists = true
            },
            ActiveWorkshopRoot = new WorkshopRootCandidate
            {
                RootPath = workshopRoot,
                Confidence = PathCandidateConfidence.High,
                ContentRootExists = true,
                ValidPayloadCount = 1
            },
            WorkshopRoots = new[]
            {
                new WorkshopRootCandidate
                {
                    RootPath = workshopRoot,
                    Confidence = PathCandidateConfidence.High,
                    ContentRootExists = true,
                    ValidPayloadCount = 1
                }
            },
            GmodCacheWorkshopPath = Path.Combine(gmodPath, "garrysmod", "cache", "workshop"),
            AddonNoMountPath = Path.Combine(gmodPath, "garrysmod", "cfg", "addonnomount.txt")
        };
    }

    private static void WritePayload(string addonPath)
    {
        var luaPath = Path.Combine(addonPath, "lua");
        Directory.CreateDirectory(luaPath);
        File.WriteAllText(Path.Combine(luaPath, "autorun.lua"), "print('ok')");
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public TestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-path-health-tests-" + Guid.NewGuid().ToString("N"));
            OldGmodPath = Path.Combine(rootPath, "OldSteam", "steamapps", "common", "GarrysMod");
            CurrentGmodPath = Path.Combine(rootPath, "CurrentSteam", "steamapps", "common", "GarrysMod");
            OldWorkshopRoot = Path.Combine(rootPath, "OldSteam", "steamapps", "workshop", "content", "4000");
            CurrentWorkshopRoot = Path.Combine(rootPath, "CurrentSteam", "steamapps", "workshop", "content", "4000");
            OldManagerPath = Path.Combine(rootPath, "OldSteam", "steamapps", "workshop", "content", "4000", ".addon-manager");
            CurrentManagerPath = Path.Combine(rootPath, "AppData", "Roaming", "GmodAddonManager");
            OldAddonsPath = Path.Combine(OldManagerPath, "addons");
            CurrentAddonsPath = Path.Combine(CurrentManagerPath, "addons");

            Directory.CreateDirectory(Path.Combine(OldGmodPath, "garrysmod", "cfg"));
            Directory.CreateDirectory(Path.Combine(CurrentGmodPath, "garrysmod", "cfg"));
            Directory.CreateDirectory(OldWorkshopRoot);
            Directory.CreateDirectory(CurrentWorkshopRoot);
            Directory.CreateDirectory(OldAddonsPath);
            Directory.CreateDirectory(CurrentAddonsPath);
        }

        public string OldGmodPath { get; }
        public string RootPath => rootPath;
        public string CurrentGmodPath { get; }
        public string OldWorkshopRoot { get; }
        public string CurrentWorkshopRoot { get; }
        public string OldManagerPath { get; }
        public string CurrentManagerPath { get; }
        public string OldAddonsPath { get; }
        public string CurrentAddonsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
