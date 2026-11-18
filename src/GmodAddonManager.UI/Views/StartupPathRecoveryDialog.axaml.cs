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
    private readonly bool openedFromSettings;
    private PathOverrideResolution? selectedResolution;

    public StartupPathRecoveryDialog()
        : this(new StartupPathRecoveryDecision())
    {
    }

    public StartupPathRecoveryDialog(StartupPathRecoveryDecision decision, bool openedFromSettings = false)
    {
        this.decision = decision ?? throw new ArgumentNullException(nameof(decision));
        this.openedFromSettings = openedFromSettings;
        InitializeComponent();
        PopulateInitialState();
    }

    public StartupPathRecoveryDialogResult Result { get; private set; } = new StartupPathRecoveryDialogResult();

    public static Task<StartupPathRecoveryDialogResult> ShowStandaloneAsync(StartupPathRecoveryDecision decision, bool openedFromSettings)
    {
        var dialog = new StartupPathRecoveryDialog(decision, openedFromSettings);
        var completion = new TaskCompletionSource<StartupPathRecoveryDialogResult>();
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
        dialog.Show();
        return completion.Task;
    }

    private void PopulateInitialState()
    {
        HeadingText.Text = L.Get(GetHeadingKey(decision.Reason));
        DescriptionText.Text = L.Get(GetDescriptionKey(decision.Reason));
        PreviousGmodText.Text = EmptyIfNull(decision.PreviousGmodInstallPath);
        PreviousWorkshopText.Text = EmptyIfNull(decision.PreviousWorkshopRootPath);
        CandidateGmodText.Text = EmptyIfNull(decision.DetectedGmodInstallPath);
        CandidateWorkshopText.Text = EmptyIfNull(decision.DetectedWorkshopRootPath);
        ReasonText.Text = L.Get($"StartupPathRecovery.Reason.{decision.Reason}");
        StatusText.Text = decision.HasDetectedCandidate
            ? L.Get("StartupPathRecovery.Ready")
            : L.Get("StartupPathRecovery.NoCandidate");
        UseButton.IsEnabled = decision.HasDetectedCandidate;
        LaterHintText.Text = GetLaterHint(decision, openedFromSettings);
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
                    out var resolution,
                    out _))
            {
                StatusText.Text = L.Get("StartupPathRecovery.InvalidFolder");
                return;
            }

            selectedResolution = resolution;
            CandidateHeading.Text = L.Get("StartupPathRecovery.SelectedPaths");
            LaterHintText.Text = GetLaterHint(decision, openedFromSettings, showingSelectedPaths: true);
            CandidateGmodText.Text = selectedResolution.GmodInstallPath;
            CandidateWorkshopText.Text = selectedResolution.WorkshopRootPath;
            StatusText.Text = L.Get("StartupPathRecovery.ManualReady");
            UseButton.IsEnabled = true;
        }
        catch (Exception)
        {
            StatusText.Text = L.Get("StartupPathRecovery.InvalidFolder");
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

    internal static string GetLaterHint(
        StartupPathRecoveryDecision decision, bool openedFromSettings, bool showingSelectedPaths = false)
    {
        if (openedFromSettings)
        {
            return L.Get("StartupPathRecovery.LaterHint.Manual");
        }

        if (!decision.HasDetectedCandidate)
        {
            return L.Get("StartupPathRecovery.LaterHint.Incomplete");
        }

        return showingSelectedPaths
            ? L.Format("StartupPathRecovery.LaterHint.AfterBrowse",
                decision.DetectedGmodInstallPath!, decision.DetectedWorkshopRootPath!)
            : L.Get("StartupPathRecovery.LaterHint.Detected");
    }

    private static string EmptyIfNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? L.Get("PathHealth.None") : value;
    }

    private static string GetHeadingKey(StartupPathRecoveryReason reason)
    {
        return reason switch
        {
            StartupPathRecoveryReason.ConfiguredPathInvalid => "StartupPathRecovery.Heading.Invalid",
            StartupPathRecoveryReason.GmodPathUnavailable => "StartupPathRecovery.Heading.GmodUnavailable",
            StartupPathRecoveryReason.WorkshopPathUnavailable => "StartupPathRecovery.Heading.WorkshopUnavailable",
            StartupPathRecoveryReason.RequiredPathsUnavailable => "StartupPathRecovery.Heading.RequiredPathsUnavailable",
            StartupPathRecoveryReason.RecordedPathMissing or
            StartupPathRecoveryReason.RecordedPathChanged => "StartupPathRecovery.Heading.Changed",
            StartupPathRecoveryReason.ManualRequest => "StartupPathRecovery.Heading.Manual",
            _ => "StartupPathRecovery.Heading.Default"
        };
    }

    private static string GetDescriptionKey(StartupPathRecoveryReason reason)
    {
        return reason switch
        {
            StartupPathRecoveryReason.ConfiguredPathInvalid => "StartupPathRecovery.Description.Invalid",
            StartupPathRecoveryReason.GmodPathUnavailable => "StartupPathRecovery.Description.GmodUnavailable",
            StartupPathRecoveryReason.WorkshopPathUnavailable => "StartupPathRecovery.Description.WorkshopUnavailable",
            StartupPathRecoveryReason.RequiredPathsUnavailable => "StartupPathRecovery.Description.RequiredPathsUnavailable",
            StartupPathRecoveryReason.RecordedPathMissing or
            StartupPathRecoveryReason.RecordedPathChanged => "StartupPathRecovery.Description.Changed",
            StartupPathRecoveryReason.ManualRequest => "StartupPathRecovery.Description.Manual",
            _ => "StartupPathRecovery.Description.Default"
        };
    }
}
