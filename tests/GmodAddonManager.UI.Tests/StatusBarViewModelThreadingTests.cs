using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class StatusBarViewModelThreadingTests
{
    [AvaloniaFact]
    public void CompletedApplyWithNewerQueuedChangeShowsPendingInsteadOfApplying()
    {
        var source = new FakeStatusBarRuntimeSource { PendingChangesCount = 1 };
        using var viewModel = new StatusBarViewModel(source, startPeriodicUpdates: false);
        source.RaiseGmodStopped();
        source.RaiseApplyStarted();
        Assert.True(viewModel.IsApplyingChanges);
        // Core intentionally retains a replacement marker queued during apply.
        source.RaiseChangeApplied();
        Assert.True(viewModel.HasPendingChanges);
        Assert.False(viewModel.IsApplyingChanges);
        Assert.Equal(L.Format("Status.PendingChanges", 1), viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void ConflictThatClearsPendingMarkerDoesNotDisplayReady()
    {
        var source = new FakeStatusBarRuntimeSource { PendingChangesCount = 1 };
        using var viewModel = new StatusBarViewModel(source, startPeriodicUpdates: false);
        source.RaiseGmodStopped();
        source.RaiseApplyStarted();
        source.PendingChangesCount = 0;
        source.RaiseChangeFailed();
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.IsApplyingChanges);
        Assert.Equal(L.Get("Status.ApplyFailed"), viewModel.StatusMessage);
        source.RaiseChangeApplied();
        Assert.Equal(L.Get("Status.Ready"), viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void GameRestartDefersPendingChangesAndStopsApplyProgress()
    {
        var source = new FakeStatusBarRuntimeSource { PendingChangesCount = 1 };
        using var viewModel = new StatusBarViewModel(source, startPeriodicUpdates: false);
        source.RaiseGmodStopped();
        source.RaiseApplyStarted();
        Assert.True(viewModel.IsApplyingChanges);
        source.IsGmodRunning = true;
        source.RaiseGmodStarted();
        Assert.True(viewModel.HasPendingChanges);
        Assert.False(viewModel.IsApplyingChanges);
        Assert.Equal(L.Format("Status.GmodRunningWithChanges", 1), viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public async Task MalformedRuntimeFileKeepsDurablePendingIntentButStopsUiProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), "gam-status-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workshop = Path.Combine(root, "steamapps", "workshop", "content", "4000");
            var gmod = Path.Combine(root, "steamapps", "common", "GarrysMod");
            var appData = Path.Combine(root, "appdata");
            var noMount = Path.Combine(gmod, "garrysmod", "cfg", "addonnomount.txt");
            var manifest = Path.Combine(root, "appworkshop_4000.acf");
            Directory.CreateDirectory(workshop);
            Directory.CreateDirectory(Path.GetDirectoryName(noMount)!);
            File.WriteAllText(noMount, "\"addonnomount\"\n{\n}\n");
            File.WriteAllText(manifest, "\"AppWorkshop\" { \"WorkshopItemDetails\" { \"100\" { \"subscribedby\" \"1\" } } \"WorkshopItemsInstalled\" { \"100\" { \"size\" \"1\" } } }");
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomGmodInstallPath = gmod,
                CustomAppDataPath = appData,
                CustomWorkshopCacheFilePaths = [manifest],
                DisableCacheScan = true
            });
            manager.GmodRunningProvider = () => false;
            manager.StateMatchTimeout = TimeSpan.Zero;
            await manager.InitializeAsync();
            var pending = new PendingChangeManager(manager, appData);
            pending.QueueApplyStates();
            manager.PendingChangeCountProvider = () => pending.GetPendingChangeCount();
            var source = new FakeStatusBarRuntimeSource { IsGmodRunning = true, PendingChangesCount = 1 };
            using var viewModel = new StatusBarViewModel(source, startPeriodicUpdates: false);
            var failureObserved = false;
            var applyTransitions = new List<bool>();
            pending.ApplyStateChanged += (_, _) =>
            {
                applyTransitions.Add(pending.IsApplyingChanges);
                source.IsApplyingChanges = pending.IsApplyingChanges;
                source.RaiseApplyStateChanged();
            };
            pending.ChangeFailed += (_, _) =>
            {
                failureObserved = true;
                source.PendingChangesCount = pending.GetPendingChangeCount();
                source.RaiseChangeFailed();
            };
            pending.ChangeApplied += (_, _) =>
            {
                source.PendingChangesCount = pending.GetPendingChangeCount();
                source.RaiseChangeApplied();
            };
            source.IsGmodRunning = false;
            source.RaiseGmodStopped();
            Assert.False(viewModel.IsApplyingChanges);
            const string malformed = "\"addonnomount\"\n\"1\" \"100\"\n";
            File.WriteAllText(noMount, malformed);

            await pending.ApplyPendingChangesAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            // App's apply callback is registered before the status bar's stop
            // callback. A synchronous failure can therefore arrive before stop.
            source.RaiseGmodStopped();

            Assert.True(failureObserved);
            Assert.True(pending.HasPendingChanges());
            Assert.Equal(malformed, File.ReadAllText(noMount));
            Assert.False(viewModel.IsApplyingChanges);
            Assert.Equal(L.Format("Status.ApplyFailedWithPending", 1), viewModel.StatusMessage);
            var snapshot = await manager.CaptureDiagnosticSnapshotAsync();
            Assert.Equal(GmodAddonManager.Core.Models.DiagnosticRuntimeStatus.Invalid, snapshot.RuntimeStatus);
            Assert.Equal(1, snapshot.PendingChanges);
            Assert.False(snapshot.ApplyInProgress);

            // A corrected test fixture can be applied without losing pending intent.
            File.WriteAllText(noMount, "\"addonnomount\"\n{\n}\n");
            await pending.ApplyPendingChangesAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(pending.HasPendingChanges());
            Assert.False(viewModel.IsApplyingChanges);
            Assert.Equal(L.Get("Status.Ready"), viewModel.StatusMessage);
            Assert.Equal(new[] { true, false, true, false }, applyTransitions);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task FailedApplyStopsProgressPreservesPendingAndSuccessfulRetryClearsFailure()
    {
        var source = new FakeStatusBarRuntimeSource { IsGmodRunning = true, PendingChangesCount = 1 };
        using var viewModel = new StatusBarViewModel(source, startPeriodicUpdates: false);
        source.IsGmodRunning = false;
        source.RaiseGmodStopped();
        source.RaiseApplyStarted();
        Assert.True(viewModel.IsApplyingChanges);

        await Task.Run(source.RaiseChangeFailed);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.False(viewModel.IsApplyingChanges);
        Assert.True(viewModel.HasPendingChanges);
        Assert.Equal(L.Format("Status.ApplyFailedWithPending", 1), viewModel.StatusMessage);
        source.RaiseGmodStopped();
        source.RaiseApplyStarted();
        Assert.True(viewModel.IsApplyingChanges);
        source.PendingChangesCount = 0;
        source.RaiseChangeApplied();
        Assert.False(viewModel.IsApplyingChanges);
        Assert.False(viewModel.HasPendingChanges);
        Assert.Equal(L.Get("Status.Ready"), viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public async Task BackgroundRuntimeNotificationUpdatesPropertiesOnUiThread()
    {
        var source = new FakeStatusBarRuntimeSource();
        using var viewModel = new StatusBarViewModel(
            source,
            startPeriodicUpdates: false);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatusBarViewModel.IsGmodRunning) &&
                viewModel.IsGmodRunning)
            {
                completion.TrySetResult(Dispatcher.UIThread.CheckAccess());
            }
        };

        await Task.Run(() =>
        {
            source.IsGmodRunning = true;
            source.RaiseGmodStarted();
        });

        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task PendingChangesAppliedIsRaisedOnUiThread()
    {
        var source = new FakeStatusBarRuntimeSource
        {
            PendingChangesCount = 1
        };
        using var viewModel = new StatusBarViewModel(
            source,
            startPeriodicUpdates: false);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        viewModel.PendingChangesApplied += (_, _) =>
            completion.TrySetResult(Dispatcher.UIThread.CheckAccess());

        await Task.Run(() =>
        {
            source.PendingChangesCount = 0;
            source.RaiseChangeApplied();
        });

        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task BackgroundTemporaryMessageUpdatesPropertiesOnUiThread()
    {
        var source = new FakeStatusBarRuntimeSource();
        using var viewModel = new StatusBarViewModel(
            source,
            startPeriodicUpdates: false);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatusBarViewModel.StatusMessage) &&
                viewModel.StatusMessage == "Background message")
            {
                completion.TrySetResult(Dispatcher.UIThread.CheckAccess());
            }
        };

        await Task.Run(() => viewModel.ShowMessage(
            "Background message",
            StatusMessageType.Info,
            durationSeconds: 30));

        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task DisposedViewModelIgnoresLateBackgroundNotifications()
    {
        var source = new FakeStatusBarRuntimeSource();
        var viewModel = new StatusBarViewModel(
            source,
            startPeriodicUpdates: false);

        viewModel.Dispose();
        await Task.Run(() =>
        {
            source.IsGmodRunning = true;
            source.RaiseGmodStarted();
        });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.False(viewModel.IsGmodRunning);
    }

    private sealed class FakeStatusBarRuntimeSource : IStatusBarRuntimeSource
    {
        public bool IsGmodRunning { get; set; }

        public int PendingChangesCount { get; set; }
        public bool IsApplyingChanges { get; set; }
        public event EventHandler? ApplyStateChanged;

        public event EventHandler? GmodStarted;

        public event EventHandler? GmodStopped;

        public event EventHandler? ChangeApplied;

        public event EventHandler? ChangeFailed;

        public void RaiseGmodStarted() => GmodStarted?.Invoke(this, EventArgs.Empty);

        public void RaiseChangeApplied()
        {
            IsApplyingChanges = false;
            ChangeApplied?.Invoke(this, EventArgs.Empty);
            RaiseApplyStateChanged();
        }

        public void RaiseGmodStopped() => GmodStopped?.Invoke(this, EventArgs.Empty);

        public void RaiseChangeFailed()
        {
            IsApplyingChanges = false;
            ChangeFailed?.Invoke(this, EventArgs.Empty);
            RaiseApplyStateChanged();
        }

        public void RaiseApplyStarted()
        {
            IsApplyingChanges = true;
            RaiseApplyStateChanged();
        }

        public void RaiseApplyStateChanged() => ApplyStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
