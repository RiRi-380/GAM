using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public sealed class StartupPathRecoveryDialogResult
{
    public bool Accepted { get; set; }
    public bool ManualSelection { get; set; }
    public string? GmodInstallPath { get; set; }
    public string? WorkshopRootPath { get; set; }
}

public partial class StartupPathRecoveryDialog : Window
{
    private readonly StartupPathRecoveryDecision decision;
    private PathOverrideResolution? selectedResolution;

    public StartupPathRecoveryDialog()
        : this(new StartupPathRecoveryDecision())
    {
    }

    public StartupPathRecoveryDialog(StartupPathRecoveryDecision decision)
    {
        this.decision = decision ?? throw new ArgumentNullException(nameof(decision));
        InitializeComponent();
        PopulateInitialState();
    }

    public StartupPathRecoveryDialogResult Result { get; private set; } = new StartupPathRecoveryDialogResult();

    public static Task<StartupPathRecoveryDialogResult> ShowStandaloneAsync(StartupPathRecoveryDecision decision)
    {
        var dialog = new StartupPathRecoveryDialog(decision);
        var completion = new TaskCompletionSource<StartupPathRecoveryDialogResult>();
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
        dialog.Show();
        return completion.Task;
    }

    private void PopulateInitialState()
    {
        PreviousGmodText.Text = EmptyIfNull(decision.PreviousGmodInstallPath);
        PreviousWorkshopText.Text = EmptyIfNull(decision.PreviousWorkshopRootPath);
        CandidateGmodText.Text = EmptyIfNull(decision.DetectedGmodInstallPath);
        CandidateWorkshopText.Text = EmptyIfNull(decision.DetectedWorkshopRootPath);
        ReasonText.Text = decision.Reason;
        StatusText.Text = decision.HasDetectedCandidate
            ? L.Get("StartupPathRecovery.Ready")
            : L.Get("StartupPathRecovery.NoCandidate");
        UseButton.IsEnabled = decision.HasDetectedCandidate;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusText.Text = L.Get("StartupPathRecovery.PickerUnavailable");
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.Get("StartupPathRecovery.PickerTitle"),
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }

            var selectedPath = folders[0].TryGetLocalPath();
            if (!PathOverrideResolver.TryResolveSelectedFolder(
                    selectedPath,
                    out selectedResolution,
                    out var error))
            {
                StatusText.Text = L.Format("StartupPathRecovery.InvalidFolder", error);
                return;
            }

            CandidateGmodText.Text = selectedResolution.GmodInstallPath;
            CandidateWorkshopText.Text = selectedResolution.WorkshopRootPath;
            StatusText.Text = L.Get("StartupPathRecovery.ManualReady");
            UseButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = L.Format("StartupPathRecovery.InvalidFolder", ex.Message);
        }
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (selectedResolution != null)
        {
            Result = new StartupPathRecoveryDialogResult
            {
                Accepted = true,
                ManualSelection = true,
                GmodInstallPath = selectedResolution.GmodInstallPath,
                WorkshopRootPath = selectedResolution.WorkshopRootPath
            };
        }
        else if (decision.HasDetectedCandidate)
        {
            Result = new StartupPathRecoveryDialogResult
            {
                Accepted = true,
                ManualSelection = false,
                GmodInstallPath = decision.DetectedGmodInstallPath,
                WorkshopRootPath = decision.DetectedWorkshopRootPath
            };
        }

        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e)
    {
        Result = new StartupPathRecoveryDialogResult();
        Close();
    }

    private static string EmptyIfNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? L.Get("PathHealth.None") : value;
    }
}
