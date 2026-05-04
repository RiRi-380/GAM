using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class DisableManifestImportServiceTests
{
    [Fact]
    public async Task ImportAsyncCreatesDisableAssetWithExcludedIdsAndMetadata()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var manifestPath = await env.WriteManifestAsync("104479467", "104483020");

        var service = new DisableManifestImportService(manager);
        var result = await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var config = manager.GetConfiguration();
        var asset = config.Assets.Single(asset => asset.Id == DisableManifestImportService.AssetId);

        Assert.True(result.AppliedImmediately);
        Assert.False(result.QueuedPendingApply);
        Assert.True(asset.Enabled);
        Assert.False(asset.IsSystem);
        Assert.Equal(AddonState.Disabled, asset.DefaultAddonState);
        Assert.Collection(
            asset.Addons,
            id => Assert.Equal("104479467", id),
            id => Assert.Equal("104483020", id));
        Assert.Equal(AddonState.Excluded, asset.AddonStates["104479467"]);
        Assert.Equal(AddonState.Excluded, asset.AddonStates["104483020"]);
        Assert.True(config.AddonMetadata.ContainsKey("104479467"));
        Assert.True(config.AddonMetadata.ContainsKey("104483020"));
    }

    [Fact]
    public async Task ImportAsyncNewModeCreatesDisabledNamedAssetWithoutApplying()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var manifestPath = await env.WriteRawManifestAsync("""
            # GAM-DISABLE v1
            # appid: 4000
            # action: exclude
            # mode: new
            # name: GPT車両除外候補
            104479467
            104483020
            """);

        var service = new DisableManifestImportService(manager);
        var preview = await service.PreviewAsync(manifestPath, TestContext.Current.CancellationToken);
        var result = await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var asset = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == result.AssetId);

        Assert.True(preview.CreatesDisabledAsset);
        Assert.False(preview.WillRequirePendingApply);
        Assert.True(result.CreatedDisabledAsset);
        Assert.False(result.AppliedImmediately);
        Assert.False(result.QueuedPendingApply);
        Assert.StartsWith(DisableManifestImportServiceConstants.NewAssetIdPrefix, asset.Id, StringComparison.Ordinal);
        Assert.Equal("GPT車両除外候補", asset.Name);
        Assert.False(asset.Enabled);
        Assert.False(asset.IsSystem);
        Assert.Collection(
            asset.Addons,
            id => Assert.Equal("104479467", id),
            id => Assert.Equal("104483020", id));
        Assert.Equal(AddonState.Excluded, asset.AddonStates["104479467"]);
        Assert.Equal(AddonState.Excluded, asset.AddonStates["104483020"]);
        Assert.Empty(env.ReadNoMountIds());
    }

    [Fact]
    public async Task ImportAsyncNewModeMakesDuplicateAssetNamesUnique()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().Assets.Add(new Asset("GPT車両除外候補"));
        var manifestPath = await env.WriteRawManifestAsync("""
            # GAM-DISABLE v1
            # action: exclude
            # mode: new
            # name: GPT車両除外候補
            104479467
            """);

        var service = new DisableManifestImportService(manager);
        var result = await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var asset = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == result.AssetId);

        Assert.Equal("GPT車両除外候補 (2)", asset.Name);
    }

    [Fact]
    public async Task ImportAsyncSoftModeWritesUnknownIdsToAddonnomount()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var manifestPath = await env.WriteManifestAsync("104479467", "104483020");

        var service = new DisableManifestImportService(manager);
        await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var disabledIds = env.ReadNoMountIds();

        Assert.Contains("104479467", disabledIds);
        Assert.Contains("104483020", disabledIds);
    }

    [Fact]
    public async Task ImportAsyncMergeModeKeepsExistingIds()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var firstManifest = await env.WriteManifestAsync("1", "2");
        var secondManifest = await env.WriteManifestAsync("2", "3");

        var service = new DisableManifestImportService(manager);
        await service.ImportAsync(
            firstManifest,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);
        await service.ImportAsync(
            secondManifest,
            new DisableManifestImportOptions
            {
                Mode = DisableManifestMode.Merge
            },
            TestContext.Current.CancellationToken);

        var asset = manager.GetConfiguration().Assets.Single(asset => asset.Id == DisableManifestImportService.AssetId);

        Assert.Collection(
            asset.Addons,
            id => Assert.Equal("1", id),
            id => Assert.Equal("2", id),
            id => Assert.Equal("3", id));
        Assert.All(asset.Addons, id => Assert.Equal(AddonState.Excluded, asset.AddonStates[id]));
    }

    [Fact]
    public async Task ImportAsyncReplaceModeReplacesExistingIds()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var firstManifest = await env.WriteManifestAsync("1", "2");
        var secondManifest = await env.WriteManifestAsync("3");

        var service = new DisableManifestImportService(manager);
        await service.ImportAsync(
            firstManifest,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);
        await service.ImportAsync(
            secondManifest,
            new DisableManifestImportOptions
            {
                Mode = DisableManifestMode.Replace
            },
            TestContext.Current.CancellationToken);

        var asset = manager.GetConfiguration().Assets.Single(asset => asset.Id == DisableManifestImportService.AssetId);
        var disabledIds = env.ReadNoMountIds();

        Assert.Collection(asset.Addons, id => Assert.Equal("3", id));
        Assert.Equal(AddonState.Excluded, asset.AddonStates["3"]);
        Assert.DoesNotContain("1", disabledIds);
        Assert.DoesNotContain("2", disabledIds);
        Assert.Contains("3", disabledIds);
    }

    [Fact]
    public async Task ImportAsyncRenamesLegacyDefaultAssetName()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().Assets.Add(new Asset(DisableManifest.LegacyDefaultName)
        {
            Id = DisableManifestImportService.AssetId,
            Enabled = true,
            IsSystem = false,
            DefaultAddonState = AddonState.Disabled
        });
        var manifestPath = await env.WriteManifestAsync("104479467");

        var service = new DisableManifestImportService(manager);
        await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var asset = manager.GetConfiguration().Assets.Single(asset => asset.Id == DisableManifestImportService.AssetId);

        Assert.Equal(DisableManifest.DefaultName, asset.Name);
    }

    [Fact]
    public async Task PreviewAsyncReportsAlreadyExcludedAndInvalidCounts()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var firstManifest = await env.WriteManifestAsync("1", "2");
        var previewManifest = await env.WriteRawManifestAsync("""
            # GAM-DISABLE v1
            # appid: 4000
            # action: exclude
            # mode: replace
            2
            3
            invalid line
            3 # duplicate
            """);

        var service = new DisableManifestImportService(manager, isGmodRunning: () => true);
        await service.ImportAsync(
            firstManifest,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var preview = await service.PreviewAsync(previewManifest, TestContext.Current.CancellationToken);

        Assert.Equal(2, preview.ValidCount);
        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(1, preview.InvalidCount);
        Assert.Equal(1, preview.AlreadyExcludedCount);
        Assert.Equal(1, preview.NewlyExcludedCount);
        Assert.True(preview.WillRequirePendingApply);
        Assert.True(preview.IsSoftMode);
        Assert.Equal(DisableManifestMode.Replace, preview.Mode);
    }

    [Fact]
    public async Task ImportAsyncQueuesPendingApplyWhenGmodIsRunning()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var pendingManager = new PendingChangeManager(manager, manager.GetManagerPath());
        var manifestPath = await env.WriteManifestAsync("104479467");

        var service = new DisableManifestImportService(
            manager,
            pendingManager,
            () => true);
        var result = await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions(),
            TestContext.Current.CancellationToken);

        var pendingChange = Assert.Single(pendingManager.GetPendingChanges());

        Assert.False(result.AppliedImmediately);
        Assert.True(result.QueuedPendingApply);
        Assert.Equal("apply_states", pendingChange.Action);
        Assert.Equal(DisableManifestImportService.AssetId, pendingChange.AddonId);
        Assert.DoesNotContain("104479467", env.ReadNoMountIds());
    }

    [Fact]
    public async Task ImportAsyncRejectsHardModeBeforeApplying()
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager(DisableMode.Hard);
        var manifestPath = await env.WriteManifestAsync("104479467");

        var service = new DisableManifestImportService(manager);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(
                manifestPath,
                new DisableManifestImportOptions(),
                TestContext.Current.CancellationToken));

        Assert.Contains("Soft disable mode", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# GAM-DISABLE v1\n# action: enable\n104479467", "Only action: exclude")]
    [InlineData("# GAM-DISABLE v1\n# appid: 123\n# action: exclude\n104479467", "appid 4000")]
    [InlineData("# action: exclude\n104479467", "Unsupported disable manifest schema")]
    [InlineData("# GAM-DISABLE v1\n# action: exclude\ninvalid", "No valid Workshop addon IDs")]
    public async Task ImportAsyncRejectsUnsupportedManifest(string content, string expectedMessage)
    {
        using var env = new TestEnvironment();
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var manifestPath = await env.WriteRawManifestAsync(content);

        var service = new DisableManifestImportService(manager);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(
                manifestPath,
                new DisableManifestImportOptions(),
                TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;
        private int manifestCounter;

        public TestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-disable-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            AppDataPath = Path.Combine(rootPath, "appdata");
            GmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
            NoMountPath = Path.Combine(GmodRootPath, "garrysmod", "cfg", "addonnomount.txt");

            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(AppDataPath);
            Directory.CreateDirectory(GmodRootPath);
        }

        public string WorkshopPath { get; }
        public string AppDataPath { get; }
        public string GmodRootPath { get; }
        public string NoMountPath { get; }

        public AddonManager CreateManager(DisableMode disableMode = DisableMode.Soft)
        {
            var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = disableMode,
                DisableCacheScan = true
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            return manager;
        }

        public Task<string> WriteManifestAsync(params string[] lines)
        {
            var manifestLines = new List<string>
            {
                "# GAM-DISABLE v1",
                "# appid: 4000",
                "# action: exclude",
                "# mode: merge"
            };
            manifestLines.AddRange(lines);
            var content = string.Join(Environment.NewLine, manifestLines);

            return WriteRawManifestAsync(content);
        }

        public async Task<string> WriteRawManifestAsync(string content)
        {
            var path = Path.Combine(rootPath, $"manifest-{++manifestCounter}.gamdisable");
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            return path;
        }

        public string[] ReadNoMountIds()
        {
            if (!File.Exists(NoMountPath))
            {
                return Array.Empty<string>();
            }

            return Regex.Matches(File.ReadAllText(NoMountPath), "\"\\d+\"\\s+\"(?<id>\\d+)\"")
                .Select(match => match.Groups["id"].Value)
                .ToArray();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failures from test file handles.
            }
        }
    }
}
