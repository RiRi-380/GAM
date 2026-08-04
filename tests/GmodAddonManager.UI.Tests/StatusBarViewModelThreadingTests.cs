using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class StatusBarViewModelThreadingTests
{
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

        public event EventHandler? GmodStarted;

        public event EventHandler? GmodStopped;

        public event EventHandler? ChangeApplied;

        public event EventHandler? ChangeFailed;

        public void RaiseGmodStarted() => GmodStarted?.Invoke(this, EventArgs.Empty);

        public void RaiseChangeApplied() => ChangeApplied?.Invoke(this, EventArgs.Empty);

        public void RaiseGmodStopped() => GmodStopped?.Invoke(this, EventArgs.Empty);

        public void RaiseChangeFailed() => ChangeFailed?.Invoke(this, EventArgs.Empty);
    }
}
