using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using System.Timers;

namespace GmodAddonManager.UI.ViewModels;

public class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly GmodProcessWatcher processWatcher;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly Timer updateTimer;
    
    private bool isGmodRunning;
    private int pendingChangesCount;
    private string statusMessage = "";
    private bool isApplyingChanges;
    private string temporaryMessage = "";
    private StatusMessageType temporaryMessageType = StatusMessageType.Info;
    private Timer? temporaryMessageTimer;

    public StatusBarViewModel(
        GmodProcessWatcher processWatcher, 
        PendingChangeManager pendingChangeManager)
    {
        this.processWatcher = processWatcher;
        this.pendingChangeManager = pendingChangeManager;

        // タイマーの設定（1秒ごとに更新）
        updateTimer = new Timer(1000);
        updateTimer.Elapsed += OnTimerElapsed;
        updateTimer.Start();

        // Gmodプロセス監視イベントの登録
        processWatcher.GmodStarted += OnGmodStarted;
        processWatcher.GmodStopped += OnGmodStopped;

        // 保留変更マネージャーのイベント登録
        pendingChangeManager.ChangeApplied += OnChangeApplied;
        pendingChangeManager.ChangeFailed += OnChangeFailed;

        // 初期状態の設定
        statusMessage = L.Get("Status.Ready");
        UpdateStatus();
    }

    public bool IsGmodRunning
    {
        get => isGmodRunning;
        private set => SetAndRaise(ref isGmodRunning, value);
    }

    public int PendingChangesCount
    {
        get => pendingChangesCount;
        private set
        {
            var oldValue = pendingChangesCount;
            SetAndRaise(ref pendingChangesCount, value);
            this.RaisePropertyChanged(nameof(HasPendingChanges));
            
            // 保留中の変更が適用された（0になった）時にイベントを発生
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
                    StatusMessageType.Warning => "#FF9800", // Orange
                    StatusMessageType.Error => "#F44336", // Red
                    StatusMessageType.Success => "#4CAF50", // Green
                    _ => "#2196F3" // Blue (Info)
                };
            }
            return "#666666"; // Default gray
        }
    }

    public bool IsApplyingChanges
    {
        get => isApplyingChanges;
        private set => SetAndRaise(ref isApplyingChanges, value);
    }

    public string GmodStatusText => IsGmodRunning ? L.Get("Status.GmodRunning") : L.Get("Status.GmodStopped");
    public string GmodStatusColor => IsGmodRunning ? "#FFA500" : "#4CAF50"; // オレンジ/緑
    public bool HasPendingChanges => PendingChangesCount > 0;

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        UpdateStatus();
    }

    private void OnGmodStarted(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    private void OnGmodStopped(object? sender, EventArgs e)
    {
        IsApplyingChanges = true;
        StatusMessage = L.Get("Status.ApplyingChanges");
        UpdateStatus();
    }

    private void OnChangeApplied(object? sender, ChangeAppliedEventArgs e)
    {
        UpdateStatus();
    }

    private void OnChangeFailed(object? sender, ChangeFailedEventArgs e)
    {
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        try
        {
            // デバッグログを削除（ファイルI/Oが遅延の原因）
            
            // Gmodの実行状態を更新
            IsGmodRunning = processWatcher.IsGmodRunning;

            // 保留中の変更数を更新
            PendingChangesCount = pendingChangeManager.GetPendingChangeCount();

            // ステータスメッセージを更新
            if (IsApplyingChanges && !IsGmodRunning && PendingChangesCount > 0)
            {
                StatusMessage = L.Format("Status.ApplyingChangesCount", PendingChangesCount);
            }
            else if (IsGmodRunning)
            {
                if (PendingChangesCount > 0)
                {
                    StatusMessage = L.Format("Status.GmodRunningWithChanges", PendingChangesCount);
                }
                else
                {
                    StatusMessage = L.Get("Status.GmodRunning");
                }
            }
            else
            {
                if (PendingChangesCount > 0)
                {
                    StatusMessage = L.Format("Status.PendingChanges", PendingChangesCount);
                }
                else
                {
                    StatusMessage = L.Get("Status.Ready");
                    IsApplyingChanges = false;
                }
            }
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("app_startup.log", $"StatusBarViewModel.UpdateStatus error at: {DateTime.Now}\n{ex.ToString()}\n");
            throw;
        }
    }

    public void ShowMessage(string message, StatusMessageType type, int durationSeconds = 5)
    {
        // 既存の一時メッセージタイマーを停止
        temporaryMessageTimer?.Stop();
        temporaryMessageTimer?.Dispose();
        
        // 新しいメッセージを設定
        temporaryMessage = message;
        temporaryMessageType = type;
        this.RaisePropertyChanged(nameof(StatusMessage));
        this.RaisePropertyChanged(nameof(StatusMessageColor));
        
        // 指定時間後にメッセージをクリア
        temporaryMessageTimer = new Timer(durationSeconds * 1000);
        temporaryMessageTimer.AutoReset = false;
        temporaryMessageTimer.Elapsed += (s, e) =>
        {
            temporaryMessage = "";
            this.RaisePropertyChanged(nameof(StatusMessage));
            this.RaisePropertyChanged(nameof(StatusMessageColor));
            temporaryMessageTimer?.Dispose();
            temporaryMessageTimer = null;
        };
        temporaryMessageTimer.Start();
    }
    
    public void Dispose()
    {
        updateTimer?.Stop();
        updateTimer?.Dispose();
        temporaryMessageTimer?.Stop();
        temporaryMessageTimer?.Dispose();

        // イベントの登録解除
        processWatcher.GmodStarted -= OnGmodStarted;
        processWatcher.GmodStopped -= OnGmodStopped;
        pendingChangeManager.ChangeApplied -= OnChangeApplied;
        pendingChangeManager.ChangeFailed -= OnChangeFailed;
    }
}