using System.Text;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Tests;

public sealed class LegacyHardLayoutRecoveryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "gam-legacy-recovery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DisabledWorkshopPayload_IsAtomicallyRestoredAndMergedIntoNoMount()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "123", "payload.bin", "legacy");
        new GmodAddonStateStore(paths.Gmod).WriteDisabledIds(new[] { "999" });

        var service = new LegacyHardLayoutRecoveryService();
        var result = await service.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, result.RecoveredItemCount);
        Assert.Equal(new[] { "123" }, result.DisabledAddonIds);
        Assert.False(Directory.Exists(source));
        Assert.Equal(
            "legacy",
            File.ReadAllText(Path.Combine(paths.Workshop, "123", "payload.bin"), Encoding.UTF8));
        Assert.Equal(
            new[] { "123", "999" },
            new GmodAddonStateStore(paths.Gmod).ReadSnapshot().DisabledIds);
        Assert.True(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));

        var second = await service.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);
        Assert.Equal(LegacyHardLayoutRecoveryStatus.NotRequired, second.Status);
    }

    [Fact]
    public async Task ExactLegacyHardLink_IsRemovedWithoutDisablingAddon()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "456", "456.gma", "gma");
        var workshopDirectory = Path.Combine(paths.Workshop, "456");
        Directory.CreateDirectory(workshopDirectory);
        var linkedFile = Path.Combine(workshopDirectory, "456.gma");
        new JunctionService().CreateHardLink(linkedFile, Path.Combine(source, "456.gma"));

        var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Recovered, result.Status);
        Assert.Empty(result.DisabledAddonIds);
        Assert.False(Directory.Exists(source));
        Assert.Equal("gma", File.ReadAllText(linkedFile, Encoding.UTF8));
        Assert.False(new JunctionService().IsHardLink(
            linkedFile,
            Path.Combine(paths.Workshop, ".addon-manager", "addons", "456", "456.gma")));
    }

    [Fact]
    public async Task InterruptedAfterJunctionRemovalBeforeJournal_ResumesWithoutDataLoss()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(
            paths.Workshop,
            "457",
            "payload.txt",
            "junction-resume");
        var target = Path.Combine(paths.Workshop, "457");
        new JunctionService().CreateJunction(target, source);
        var firstService = new LegacyHardLayoutRecoveryService
        {
            AfterLegacyTargetRemovalBeforeJournalForTesting = _ =>
                throw new IOException("simulated interruption after junction removal")
        };

        var first = await firstService.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, first.Status);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));

        var resumed = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Recovered, resumed.Status);
        Assert.False(Directory.Exists(source));
        Assert.Equal(
            "junction-resume",
            File.ReadAllText(Path.Combine(target, "payload.txt"), Encoding.UTF8));
    }

    [Fact]
    public async Task InterruptedAfterHardLinkRemovalBeforeJournal_ResumesWithoutDataLoss()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "458", "458.gma", "link-resume");
        var target = Path.Combine(paths.Workshop, "458");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "458.gma");
        new JunctionService().CreateHardLink(targetFile, Path.Combine(source, "458.gma"));
        var firstService = new LegacyHardLayoutRecoveryService
        {
            AfterLegacyTargetRemovalBeforeJournalForTesting = _ =>
                throw new IOException("simulated interruption after hard-link removal")
        };

        var first = await firstService.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, first.Status);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));

        var resumed = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Recovered, resumed.Status);
        Assert.False(Directory.Exists(source));
        Assert.Equal("link-resume", File.ReadAllText(targetFile, Encoding.UTF8));
    }

    [Fact]
    public async Task NonLegacyTargetConflict_BlocksWithoutOverwritingEitherPayload()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "777", "legacy.txt", "legacy");
        var target = Path.Combine(paths.Workshop, "777");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "current.txt"), "current", Encoding.UTF8);

        var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, result.Status);
        Assert.Equal("legacy_recovery_inspection_failed", result.FailureCode);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "legacy.txt"), Encoding.UTF8));
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "current.txt"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));
    }

    [Fact]
    public async Task ReparsePointAtManagerRoot_BlocksWithoutMovingExternalPayload()
    {
        var paths = CreateLayout();
        var externalManager = Path.Combine(root, "external-manager");
        var externalPayload = Path.Combine(externalManager, "addons", "991");
        Directory.CreateDirectory(externalPayload);
        var externalFile = Path.Combine(externalPayload, "payload.txt");
        File.WriteAllText(externalFile, "external", Encoding.UTF8);
        var managerRoot = Path.Combine(paths.Workshop, ".addon-manager");
        new JunctionService().CreateJunction(managerRoot, externalManager);

        try
        {
            var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
                paths.Workshop,
                paths.Gmod,
                paths.AppData,
                isGmodRunning: false);

            Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, result.Status);
            Assert.Equal("legacy_recovery_inspection_failed", result.FailureCode);
            Assert.Equal("external", File.ReadAllText(externalFile, Encoding.UTF8));
            Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "991")));
            Assert.False(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));
        }
        finally
        {
            if (new JunctionService().IsJunction(managerRoot))
            {
                new JunctionService().RemoveJunction(managerRoot);
            }
        }
    }

    [Fact]
    public async Task ReparsePointAtManagedAddonsRoot_BlocksWithoutMovingExternalPayload()
    {
        var paths = CreateLayout();
        var managerRoot = Path.Combine(paths.Workshop, ".addon-manager");
        Directory.CreateDirectory(managerRoot);
        var externalAddons = Path.Combine(root, "external-addons");
        var externalPayload = Path.Combine(externalAddons, "992");
        Directory.CreateDirectory(externalPayload);
        var externalFile = Path.Combine(externalPayload, "payload.txt");
        File.WriteAllText(externalFile, "external", Encoding.UTF8);
        var managedRoot = Path.Combine(managerRoot, "addons");
        new JunctionService().CreateJunction(managedRoot, externalAddons);

        try
        {
            var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
                paths.Workshop,
                paths.Gmod,
                paths.AppData,
                isGmodRunning: false);

            Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, result.Status);
            Assert.Equal("legacy_recovery_inspection_failed", result.FailureCode);
            Assert.Equal("external", File.ReadAllText(externalFile, Encoding.UTF8));
            Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "992")));
        }
        finally
        {
            if (new JunctionService().IsJunction(managedRoot))
            {
                new JunctionService().RemoveJunction(managedRoot);
            }
        }
    }

    [Fact]
    public async Task ReparsePointAtManagedPayloadRoot_BlocksWithoutMovingExternalPayload()
    {
        var paths = CreateLayout();
        var managedRoot = Path.Combine(paths.Workshop, ".addon-manager", "addons");
        Directory.CreateDirectory(managedRoot);
        var externalPayload = Path.Combine(root, "external-payload");
        Directory.CreateDirectory(externalPayload);
        var externalFile = Path.Combine(externalPayload, "payload.txt");
        File.WriteAllText(externalFile, "external", Encoding.UTF8);
        var source = Path.Combine(managedRoot, "993");
        new JunctionService().CreateJunction(source, externalPayload);

        try
        {
            var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
                paths.Workshop,
                paths.Gmod,
                paths.AppData,
                isGmodRunning: false);

            Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, result.Status);
            Assert.Equal("legacy_recovery_inspection_failed", result.FailureCode);
            Assert.Equal("external", File.ReadAllText(externalFile, Encoding.UTF8));
            Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "993")));
        }
        finally
        {
            if (new JunctionService().IsJunction(source))
            {
                new JunctionService().RemoveJunction(source);
            }
        }
    }

    [Fact]
    public async Task InterruptedMove_ResumesFromDurableJournal()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "888", "payload.txt", "resume");
        var firstService = new LegacyHardLayoutRecoveryService
        {
            BeforePayloadMoveForTesting = _ => throw new IOException("simulated interruption")
        };

        var first = await firstService.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, first.Status);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));

        var resumed = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Recovered, resumed.Status);
        Assert.False(Directory.Exists(source));
        Assert.Equal(
            "resume",
            File.ReadAllText(Path.Combine(paths.Workshop, "888", "payload.txt"), Encoding.UTF8));
    }

    [Fact]
    public async Task TamperedJournalTarget_IsRejectedWithoutMovingOrDeletingPayload()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "889", "payload.txt", "safe");
        var firstService = new LegacyHardLayoutRecoveryService
        {
            BeforePayloadMoveForTesting = _ => throw new IOException("pause for journal tamper test")
        };
        var first = await firstService.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);
        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, first.Status);

        var journalPath = Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json");
        var journal = JObject.Parse(File.ReadAllText(journalPath, Encoding.UTF8));
        journal["Operations"]![0]!["TargetPath"] = Path.Combine(paths.Workshop, "999");
        File.WriteAllText(journalPath, journal.ToString(), Encoding.UTF8);

        var resumed = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, resumed.Status);
        Assert.Equal("legacy_recovery_inspection_failed", resumed.FailureCode);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "999")));
    }

    [Fact]
    public async Task TamperedJournalCannotInjectAnUnrelatedDisabledAddon()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "890", "payload.txt", "safe");
        var firstService = new LegacyHardLayoutRecoveryService
        {
            BeforePayloadMoveForTesting = _ => throw new IOException("pause for journal tamper test")
        };
        var first = await firstService.RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);
        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, first.Status);

        var journalPath = Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json");
        var journal = JObject.Parse(File.ReadAllText(journalPath, Encoding.UTF8));
        journal["DisabledAddonIds"] = new JArray("999");
        File.WriteAllText(journalPath, journal.ToString(), Encoding.UTF8);

        var resumed = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, resumed.Status);
        Assert.Equal("legacy_recovery_inspection_failed", resumed.FailureCode);
        Assert.True(Directory.Exists(source));
        Assert.Empty(new GmodAddonStateStore(paths.Gmod).ReadSnapshot().DisabledIds);
    }

    [Fact]
    public async Task MalformedNoMount_BlocksBeforeAnyPayloadMutation()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "321", "payload.txt", "safe");
        var noMount = Path.Combine(paths.Gmod, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(noMount, "not a valid document", Encoding.UTF8);

        var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.Blocked, result.Status);
        Assert.Equal("legacy_recovery_mutation_failed", result.FailureCode);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "321")));
        Assert.False(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));
    }

    [Fact]
    public async Task RunningGmod_DefersBeforeJournalOrPayloadMutation()
    {
        var paths = CreateLayout();
        var source = CreateManagedWorkshopPayload(paths.Workshop, "654", "payload.txt", "running");

        var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: true);

        Assert.Equal(
            LegacyHardLayoutRecoveryStatus.DeferredWhileGmodIsRunning,
            result.Status);
        Assert.True(Directory.Exists(source));
        Assert.False(File.Exists(Path.Combine(paths.AppData, "legacy-hard-layout-recovery.json")));
    }

    [Fact]
    public async Task EmptyAndZeroByteManagerRemnants_AreNotResurrected()
    {
        var paths = CreateLayout();
        Directory.CreateDirectory(Path.Combine(
            paths.Workshop,
            ".addon-manager",
            "addons",
            "111"));
        var cacheManager = Path.Combine(
            paths.Gmod,
            "garrysmod",
            "cache",
            "workshop",
            ".addon-manager",
            "addons");
        Directory.CreateDirectory(cacheManager);
        File.WriteAllBytes(Path.Combine(cacheManager, "222.gma"), Array.Empty<byte>());

        var result = await new LegacyHardLayoutRecoveryService().RecoverIfNeededAsync(
            paths.Workshop,
            paths.Gmod,
            paths.AppData,
            isGmodRunning: false);

        Assert.Equal(LegacyHardLayoutRecoveryStatus.NotRequired, result.Status);
        Assert.False(Directory.Exists(Path.Combine(paths.Workshop, "111")));
        Assert.False(File.Exists(Path.Combine(
            paths.Gmod,
            "garrysmod",
            "cache",
            "workshop",
            "222.gma")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }

    private TestPaths CreateLayout()
    {
        var workshop = Path.Combine(root, "steamapps", "workshop", "content", "4000");
        var gmod = Path.Combine(root, "steamapps", "common", "GarrysMod");
        var appData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.Combine(gmod, "garrysmod", "cache", "workshop"));
        Directory.CreateDirectory(appData);
        return new TestPaths(workshop, gmod, appData);
    }

    private static string CreateManagedWorkshopPayload(
        string workshop,
        string addonId,
        string fileName,
        string content)
    {
        var source = Path.Combine(workshop, ".addon-manager", "addons", addonId);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, fileName), content, Encoding.UTF8);
        return source;
    }

    private sealed record TestPaths(string Workshop, string Gmod, string AppData);
}
