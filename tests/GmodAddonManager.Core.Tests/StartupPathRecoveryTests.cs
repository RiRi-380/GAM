using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

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
        Assert.Equal(currentEnv.GmodInstallPath, decision.DetectedGmodInstallPath);
        Assert.Equal(currentEnv.WorkshopRootPath, decision.DetectedWorkshopRootPath);
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
            File.WriteAllText(Path.Combine(LibraryPath, "steamapps", "workshop", "appworkshop_4000.acf"), "\"AppWorkshop\"{}");
            WritePayload(Path.Combine(WorkshopRootPath, "123"));
        }

        public string LibraryPath { get; }
        public string GmodInstallPath { get; }
        public string WorkshopRootPath { get; }

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
