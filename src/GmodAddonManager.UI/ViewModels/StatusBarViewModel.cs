using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;
using System;
using System.Threading;

namespace GmodAddonManager.UI.ViewModels;

internal interface IStatusBarRuntimeSource
{
    bool IsGmodRunning { get; }

    int PendingChangesCount { get; }
    bool IsApplyingChanges { get; }

    event EventHandler? GmodStarted;

    event EventHandler? GmodStopped;

    event EventHandler? ChangeApplied;

    event EventHandler? ChangeFailed;
    event EventHandler? ApplyStateChanged;
}

internal sealed class StatusBarRuntimeSource : IStatusBarRuntimeSource, IDisposable
{
    private readonly GmodProcessWatcher processWatcher;
    private readonly PendingChangeManager pendingChangeManager;
    private bool disposed;

    public StatusBarRuntimeSource(
        GmodProcessWatcher processWatcher,
        PendingChangeManager pendingChangeManager)
    {
        this.processWatcher = processWatcher ??
            throw new ArgumentNullException(nameof(processWatcher));
        this.pendingChangeManager = pendingChangeManager ??
            throw new ArgumentNullException(nameof(pendingChangeManager));

        processWatcher.GmodStarted += OnGmodStarted;
        processWatcher.GmodStopped += OnGmodStopped;
        pendingChangeManager.ChangeApplied += OnChangeApplied;
        pendingChangeManager.ChangeFailed += OnChangeFailed;
        pendingChangeManager.ApplyStateChanged += OnApplyStateChanged;
    }

    public bool IsGmodRunning => processWatcher.IsGmodRunning;

    public int PendingChangesCount => pendingChangeManager.GetPendingChangeCount();
    public bool IsApplyingChanges => pendingChangeManager.IsApplyingChanges;

    public event EventHandler? GmodStarted;

    public event EventHandler? GmodStopped;

    public event EventHandler? ChangeApplied;

    public event EventHandler? ChangeFailed;
    public event EventHandler? ApplyStateChanged;

    private void OnGmodStarted(object? sender, ProcessEventArgs e) =>
        GmodStarted?.Invoke(this, EventArgs.Empty);

    private void OnGmodStopped(object? sender, ProcessEventArgs e) =>
        GmodStopped?.Invoke(this, EventArgs.Empty);

    private void OnChangeApplied(object? sender, ChangeAppliedEventArgs e) =>
        ChangeApplied?.Invoke(this, EventArgs.Empty);

    private void OnChangeFailed(object? sender, ChangeFailedEventArgs e) =>
        ChangeFailed?.Invoke(this, EventArgs.Empty);

    private void OnApplyStateChanged(object? sender, EventArgs e) =>
        ApplyStateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        processWatcher.GmodStarted -= OnGmodStarted;
        processWatcher.GmodStopped -= OnGmodStopped;
        pendingChangeManager.ChangeApplied -= OnChangeApplied;
        pendingChangeManager.ChangeFailed -= OnChangeFailed;
        pendingChangeManager.ApplyStateChanged -= OnApplyStateChanged;
    }
}

public sealed class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly IStatusBarRuntimeSource runtimeSource;
    private readonly IDisposable? ownedRuntimeSource;
    private readonly DispatcherTimer updateTimer;

    private bool isGmodRunning;
    private int pendingChangesCount;
    private string statusMessage = "";
    private bool isApplyingChanges;
    private bool lastApplyFailed;
    private string temporaryMessage = "";
    private StatusMessageType temporaryMessageType = StatusMessageType.Info;
    private DispatcherTimer? temporaryMessageTimer;
    private int disposed;

    public StatusBarViewModel(
        GmodProcessWatcher processWatcher,
        PendingChangeManager pendingChangeManager)
        : this(new StatusBarRuntimeSource(processWatcher, pendingChangeManager), true)
    {
    }

    internal StatusBarViewModel(
        IStatusBarRuntimeSource runtimeSource,
        bool startPeriodicUpdates = true)
    {
        this.runtimeSource = runtimeSource ??
            throw new ArgumentNullException(nameof(runtimeSource));
        ownedRuntimeSource = runtimeSource as IDisposable;

        updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        updateTimer.Tick += OnTimerTick;
        if (startPeriodicUpdates)
        {
            updateTimer.Start();
        }

        runtimeSource.GmodStarted += OnGmodStarted;
        runtimeSource.GmodStopped += OnGmodStopped;
        runtimeSource.ChangeApplied += OnChangeApplied;
        runtimeSource.ChangeFailed += OnChangeFailed;
        runtimeSource.ApplyStateChanged += OnApplyStateChanged;

        statusMessage = L.Get("Status.Ready");
        RequestStatusUpdate();
    }

    public bool IsGmodRunning
    {
        get => isGmodRunning;
        private set
        {
            if (isGmodRunning == value)
            {
                return;
            }

            SetAndRaise(ref isGmodRunning, value);
            this.RaisePropertyChanged(nameof(GmodStatusText));
            this.RaisePropertyChanged(nameof(GmodStatusColor));
        }
    }

    public int PendingChangesCount
    {
        get => pendingChangesCount;
        private set
        {
            var oldValue = pendingChangesCount;
            SetAndRaise(ref pendingChangesCount, value);
            this.RaisePropertyChanged(nameof(HasPendingChanges));

            if (oldValue > 0 && value == 0 && !IsGmodRunning)
            {
                PendingChangesApplied?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? PendingChangesApplied;

    public string StatusMessage
    {
        get => !string.IsNullOrEmpty(temporaryMessage) ? temporaryMessage : statusMessage;
        private set => SetAndRaise(ref statusMessage, value);
    }

    public string StatusMessageColor
    {
        get
        {
            if (!string.IsNullOrEmpty(temporaryMessage))
            {
                return temporaryMessageType switch
                {
                    StatusMessageType.Warning => "#FF9800",
                    StatusMessageType.Error => "#F44336",
                    StatusMessageType.Success => "#4CAF50",
                    _ => "#2196F3"
                };
            }

            return "#666666";
        }
    }

    public bool IsApplyingChanges
    {
        get => isApplyingChanges;
        private set => SetAndRaise(ref isApplyingChanges, value);
    }

    public string GmodStatusText =>
        IsGmodRunning ? L.Get("Status.GmodRunning") : L.Get("Status.GmodStopped");

    public string GmodStatusColor => IsGmodRunning ? "#FFA500" : "#4CAF50";

    public bool HasPendingChanges => PendingChangesCount > 0;

    private void OnTimerTick(object? sender, EventArgs e) => RequestStatusUpdate();

    private void OnGmodStarted(object? sender, EventArgs e) => RequestStatusUpdate();

    private void OnGmodStopped(object? sender, EventArgs e) => RequestStatusUpdate();

    private void OnApplyStateChanged(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        if (runtimeSource.IsApplyingChanges) lastApplyFailed = false;
        UpdateStatusCore();
    });

    private void OnChangeApplied(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        lastApplyFailed = false;
        IsApplyingChanges = false;
        UpdateStatusCore();
    });

    private void OnChangeFailed(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        lastApplyFailed = true;
        IsApplyingChanges = false;
        UpdateStatusCore();
    });

    private void RequestStatusUpdate() => RunOnUiThread(UpdateStatusCore);

    private void RunOnUiThread(Action action)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                action();
            }
        });
    }

    private void UpdateStatusCore()
    {
        try
        {
            IsGmodRunning = runtimeSource.IsGmodRunning;
            PendingChangesCount = runtimeSource.PendingChangesCount;
            IsApplyingChanges = runtimeSource.IsApplyingChanges && !IsGmodRunning && !lastApplyFailed;

            if (IsApplyingChanges && !IsGmodRunning && PendingChangesCount > 0)
            {
                StatusMessage = L.Format("Status.ApplyingChangesCount", PendingChangesCount);
            }
            else if (IsGmodRunning)
            {
                IsApplyingChanges = false;
                StatusMessage = PendingChangesCount > 0
                    ? L.Format("Status.GmodRunningWithChanges", PendingChangesCount)
                    : L.Get("Status.GmodRunning");
            }
            else if (PendingChangesCount > 0)
            {
                StatusMessage = L.Format(
                    lastApplyFailed ? "Status.ApplyFailedWithPending" : "Status.PendingChanges",
                    PendingChangesCount);
            }
            else
            {
                StatusMessage = L.Get(lastApplyFailed ? "Status.ApplyFailed" : "Status.Ready");
                IsApplyingChanges = false;
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("StatusBarViewModel.UpdateStatus", ex);
        }
    }

    public void ShowMessage(
        string message,
        StatusMessageType type,
        int durationSeconds = 5)
    {
        RunOnUiThread(() => ShowMessageCore(message, type, durationSeconds));
    }

    private void ShowMessageCore(
        string message,
        StatusMessageType type,
        int durationSeconds)
    {
        StopTemporaryMessageTimer();

        temporaryMessage = message;
        temporaryMessageType = type;
        this.RaisePropertyChanged(nameof(StatusMessage));
        this.RaisePropertyChanged(nameof(StatusMessageColor));

        temporaryMessageTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, durationSeconds))
        };
        temporaryMessageTimer.Tick += OnTemporaryMessageTimerTick;
        temporaryMessageTimer.Start();
    }

    private void OnTemporaryMessageTimerTick(object? sender, EventArgs e)
    {
        StopTemporaryMessageTimer();
        temporaryMessage = "";
        this.RaisePropertyChanged(nameof(StatusMessage));
        this.RaisePropertyChanged(nameof(StatusMessageColor));
    }

    private void StopTemporaryMessageTimer()
    {
        if (temporaryMessageTimer is null)
        {
            return;
        }

        temporaryMessageTimer.Stop();
        temporaryMessageTimer.Tick -= OnTemporaryMessageTimerTick;
        temporaryMessageTimer = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        runtimeSource.GmodStarted -= OnGmodStarted;
        runtimeSource.GmodStopped -= OnGmodStopped;
        runtimeSource.ChangeApplied -= OnChangeApplied;
        runtimeSource.ChangeFailed -= OnChangeFailed;
        runtimeSource.ApplyStateChanged -= OnApplyStateChanged;
        ownedRuntimeSource?.Dispose();

        void StopTimers()
        {
            updateTimer.Stop();
            updateTimer.Tick -= OnTimerTick;
            StopTemporaryMessageTimer();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            StopTimers();
        }
        else
        {
            Dispatcher.UIThread.Post(StopTimers);
        }
    }
}
