using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerDiagnosticTests : IDisposable, IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gam-diagnostic-test-" + Guid.NewGuid().ToString("N"));
    private readonly string noMount;
    private readonly string manifest;
    private readonly CountingErrorHandler errors = new();
    private readonly AddonManager manager;
    private bool disposed;

    public AddonManagerDiagnosticTests()
    {
        var workshop = Path.Combine(root, "steamapps", "workshop", "content", "4000");
        var gmod = Path.Combine(root, "steamapps", "common", "GarrysMod");
        noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
        manifest = Path.Combine(root, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
        File.WriteAllText(noMount, "\"addonnomount\" { \"1\" \"200\" }", new UTF8Encoding(false));
        File.WriteAllText(manifest, "\"AppWorkshop\" { \"WorkshopItemDetails\" { \"100\" { \"subscribedby\" \"1\" } } \"WorkshopItemsInstalled\" { \"100\" { \"size\" \"1\" } } }");
        manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshop,
            CustomGmodInstallPath = gmod,
            CustomAppDataPath = Path.Combine(root, "appdata"),
            CustomWorkshopCacheFilePaths = [manifest],
            DisableCacheScan = true,
            ErrorHandler = errors
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
    }

    public async Task InitializeAsync()
    {
        await manager.InitializeAsync();
        await manager.SaveConfigurationImmediatelyAsync();
        File.WriteAllText(manifest, "Steam must not be read during diagnostics");
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CollectionUsesCurrentAssetResolverAndDoesNotSaveScanOrLog()
    {
        var config = manager.GetConfiguration();
        config.Assets.Clear();
        config.CreateDefaultAssets();
        config.SubscriptionBaselineInitialized = true;
        config.KnownSubscribedAddonIds = ["100", "200", "300", "200"];
        var excluded = new Asset("private asset name") { Addons = ["100"], Memo = "private memo" };
        excluded.SetWholeState(AddonState.Excluded);
        config.Assets.Add(excluded);
        config.PendingGamRuntimeWrite = new PendingGamRuntimeWrite { ConflictDetected = true };
        manager.PendingChangeCountProvider = () => 1;
        manager.GmodRunningProvider = () => false;
        var beforeConfig = JsonConvert.SerializeObject(config);
        var beforeFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var beforeLogs = errors.Count;

        var snapshot = await manager.CaptureDiagnosticSnapshotAsync();

        Assert.Equal(3, snapshot.LastKnownSubscriptions);
        Assert.Equal(2, snapshot.DesiredEnabled); // 200 and 300; exclusion wins over Subscribe.
        Assert.Equal(2, snapshot.RuntimeEnabled); // 100 and 300 in the file.
        Assert.Equal(2, snapshot.Mismatches);
        Assert.Equal(DiagnosticRuntimeStatus.Valid, snapshot.RuntimeStatus);
        Assert.Equal(1, snapshot.PendingChanges);
        Assert.False(snapshot.GmodRunning);
        Assert.True(snapshot.PendingRuntimeWrite);
        Assert.True(snapshot.RuntimeWriteConflict);
        Assert.Equal(beforeConfig, JsonConvert.SerializeObject(config));
        Assert.Equal(beforeLogs, errors.Count);
        Assert.Equal(beforeFiles.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in beforeFiles) Assert.Equal(bytes, File.ReadAllBytes(path));
        var serialized = JsonConvert.SerializeObject(snapshot);
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain(root, serialized);
        Assert.DoesNotContain("\"200\"", serialized);
    }

    [Theory]
    [InlineData("missing", DiagnosticRuntimeStatus.Missing)]
    [InlineData("invalid", DiagnosticRuntimeStatus.Invalid)]
    [InlineData("locked", DiagnosticRuntimeStatus.Unreadable)]
    public async Task FailedOrMissingRuntimeObservationNeverClaimsZeroMismatches(string mode, DiagnosticRuntimeStatus expected)
    {
        var config = manager.GetConfiguration();
        config.SubscriptionBaselineInitialized = true;
        config.KnownSubscribedAddonIds = ["100"];
        if (mode == "missing") File.Delete(noMount);
        if (mode == "invalid") File.WriteAllText(noMount, "\"addonnomount\"\n\"1\" \"100\"\n");
        using var locked = mode == "locked" ? new FileStream(noMount, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var logs = errors.Count;

        var snapshot = await manager.CaptureDiagnosticSnapshotAsync();

        Assert.Equal(expected, snapshot.RuntimeStatus);
        Assert.Null(snapshot.RuntimeEnabled);
        Assert.Null(snapshot.Mismatches);
        Assert.Equal(logs, errors.Count);
    }

    [Fact]
    public async Task UnknownSubscriptionsAndFailingProvidersRemainUnknown()
    {
        manager.GetConfiguration().KnownSubscribedAddonIds = ["100"];
        manager.GetConfiguration().SubscriptionBaselineInitialized = false;
        manager.PendingChangeCountProvider = () => throw new IOException("private path");
        manager.GmodRunningProvider = () => throw new InvalidOperationException("private context");
        var snapshot = await manager.CaptureDiagnosticSnapshotAsync();
        Assert.Equal(DiagnosticRuntimeStatus.Valid, snapshot.RuntimeStatus);
        Assert.Null(snapshot.LastKnownSubscriptions);
        Assert.Null(snapshot.DesiredEnabled);
        Assert.Null(snapshot.RuntimeEnabled);
        Assert.Null(snapshot.Mismatches);
        Assert.Null(snapshot.PendingChanges);
        Assert.Null(snapshot.GmodRunning);
    }

    [Fact]
    public async Task BackgroundResolutionUsesCapturedMemberships()
    {
        var config = manager.GetConfiguration();
        config.Assets.Clear();
        var asset = new Asset("capture fixture") { Addons = ["100"] };
        config.Assets.Add(asset);
        config.SubscriptionBaselineInitialized = true;
        config.KnownSubscribedAddonIds = ["100"];
        var capturing = manager.CaptureDiagnosticSnapshotAsync();
        asset.Addons.Clear();
        asset.SetWholeState(AddonState.Excluded);
        config.KnownSubscribedAddonIds.Clear();
        var captured = await capturing;
        Assert.Equal(1, captured.LastKnownSubscriptions);
        Assert.Equal(1, captured.DesiredEnabled);
        Assert.Null(captured.ApplyInProgress);
    }

    [Fact]
    public async Task KnownEmptySubscriptionsAreZeroButMissingProvidersAreUnknown()
    {
        manager.GetConfiguration().SubscriptionBaselineInitialized = true;
        manager.GetConfiguration().KnownSubscribedAddonIds.Clear();
        var snapshot = await manager.CaptureDiagnosticSnapshotAsync();
        Assert.Equal(0, snapshot.LastKnownSubscriptions);
        Assert.Equal(0, snapshot.Mismatches);
        Assert.Null(snapshot.PendingChanges);
        Assert.Null(snapshot.GmodRunning);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        manager.PendingChangeCountProvider = null;
        manager.GmodRunningProvider = null;
        manager.Dispose();
        Directory.Delete(root, true);
    }

    private sealed class CountingErrorHandler : IErrorHandler
    {
        public int Count { get; private set; }
        public void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error) => Count++;
        public void HandleInfo(string message, string context) => Count++;
        public void HandleWarning(string message, string context) => Count++;
    }
}
