using System.Text;
using System.Globalization;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerStartupScanTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-startup-scan-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FreshParallelScanPreservesExactSizesAndRuntimeStates()
    {
        var workshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
        var gmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
        var noMountPath = Path.Combine(gmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(noMountPath)!);

        var addonIds = Enumerable.Range(1000, 12)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var expectedSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (addonId, index) in addonIds.Select((id, index) => (id, index)))
        {
            var payloadPath = Path.Combine(workshopPath, addonId, "lua", "payload.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            var payload = new byte[64 + index];
            File.WriteAllBytes(payloadPath, payload);
            expectedSizes[addonId] = payload.Length;
        }

        var disabledIds = addonIds.Where((_, index) => index % 3 == 0).ToArray();
        File.WriteAllText(noMountPath, BuildNoMount(disabledIds), new UTF8Encoding(false));
        var manifestPath = WorkshopManifestTestData.Write(rootPath, addonIds);

        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero,
            MaxParallelWorkshopScans = 4
        });
        await manager.InitializeAsync();

        var addons = await manager.ScanWorkshopFolderAsync();

        Assert.Equal(addonIds.Length, addons.Count);
        foreach (var addon in addons)
        {
            Assert.Equal(expectedSizes[addon.Id], addon.Size);
            Assert.Equal(!disabledIds.Contains(addon.Id, StringComparer.Ordinal), addon.IsEnabled);
        }
    }

    [Fact]
    public async Task BulkInvalidFoldersProduceOneAggregateInfoEntry()
    {
        var workshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
        var gmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(gmodRootPath);
        for (var index = 0; index < 7; index++)
        {
            var directory = Path.Combine(
                workshopPath,
                (2000 + index).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ".gam_disabled"), string.Empty);
        }

        var manifestPath = WorkshopManifestTestData.Write(rootPath);
        var errorHandler = new CapturingErrorHandler();
        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ErrorHandler = errorHandler,
            ScanCacheTtl = TimeSpan.Zero
        });
        await manager.InitializeAsync();

        var addons = await manager.ScanWorkshopFolderAsync();

        Assert.Empty(addons);
        var scanMessages = errorHandler.InfoEntries
            .Where(entry => entry.Context == "ScanWorkshopFolderSoftAsync")
            .ToList();
        var summary = Assert.Single(
            scanMessages,
            entry => entry.Message.StartsWith(
                "Skipped 7 empty or invalid Workshop folders.",
                StringComparison.Ordinal));
        Assert.Contains("2000", summary.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            scanMessages,
            entry => entry.Message.Contains("Skipping invalid addon payload", StringComparison.Ordinal));
    }

    private static string BuildNoMount(IEnumerable<string> disabledIds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"addonnomount\"");
        builder.AppendLine("{");
        var index = 1;
        foreach (var addonId in disabledIds)
        {
            builder.Append("\t\"").Append(index++).Append("\"\t\t\"")
                .Append(addonId).AppendLine("\"");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    public void Dispose()
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
                return;
            }
            catch (Exception ex) when (
                attempt < 9 &&
                (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class CapturingErrorHandler : IErrorHandler
    {
        public List<(string Message, string Context)> InfoEntries { get; } = [];

        public void HandleError(
            Exception ex,
            string context,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
        }

        public void HandleInfo(string message, string context)
        {
            InfoEntries.Add((message, context));
        }

        public void HandleWarning(string message, string context)
        {
        }
    }
}
