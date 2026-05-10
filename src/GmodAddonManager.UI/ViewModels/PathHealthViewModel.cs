using System;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels;

public sealed class PathHealthViewModel : ViewModelBase
{
    private readonly AddonManager addonManager;
    private PathHealthReport report = new PathHealthReport();
    private string statusMessage = string.Empty;
    private bool isBusy;

    public PathHealthViewModel(AddonManager addonManager)
    {
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        Refresh();
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetAndRaise(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            SetAndRaise(ref isBusy, value);
            RaiseActionProperties();
        }
    }

    public string CurrentSteamRoot => report.CurrentSnapshot.SteamRootPath ?? L.Get("PathHealth.None");
    public string CurrentGmodInstall => report.CurrentSnapshot.GmodInstall?.InstallPath ?? L.Get("PathHealth.None");
    public string CurrentWorkshopRoot => report.CurrentSnapshot.ActiveWorkshopRoot?.RootPath ?? L.Get("PathHealth.None");
    public string CurrentCachePath => report.CurrentSnapshot.GmodCacheWorkshopPath ?? L.Get("PathHealth.None");
    public string CurrentAddonNoMountPath => report.CurrentSnapshot.AddonNoMountPath ?? L.Get("PathHealth.None");
    public string PreviousGmodInstall => report.PreviousSnapshot?.GmodInstall?.InstallPath ?? L.Get("PathHealth.None");
    public string PreviousWorkshopRoot => report.PreviousSnapshot?.ActiveWorkshopRoot?.RootPath ?? L.Get("PathHealth.None");
    public string PreviousAddonNoMountPath => report.PreviousSnapshot?.AddonNoMountPath ?? L.Get("PathHealth.None");
    public int IssueCount => report.IssueCount;
    public int MetadataRepairCount => report.MetadataRepairCount;
    public int AddonNoMountMigrationCount => report.AddonNoMountMigrationCount;
    public int CleanupCandidateCount => report.CleanupCandidateCount;
    public int ManagedMigrationCandidateCount => report.ManagedMigrationCandidateCount;
    public bool CanRepairMetadata => !IsBusy && MetadataRepairCount > 0;
    public bool CanMigrateAddonNoMount => !IsBusy && AddonNoMountMigrationCount > 0;
    public bool CanCleanupEmptyFolders => !IsBusy && CleanupCandidateCount > 0;
    public bool CanMigrateManagedData => !IsBusy && ManagedMigrationCandidateCount > 0;

    public string IssueSummary => report.Issues.Count == 0
        ? L.Get("PathHealth.NoIssues")
        : string.Join(Environment.NewLine, report.Issues.Take(12));

    public string MetadataRepairSummary => BuildMetadataRepairSummary();
    public string CleanupSummary => BuildCleanupSummary();
    public string ManagedMigrationSummary => BuildManagedMigrationSummary();
    public string AddonNoMountSummary => BuildAddonNoMountSummary();

    public void Refresh()
    {
        report = addonManager.GetPathHealthReport();
        StatusMessage = L.Get("PathHealth.Ready");
        RaiseReportProperties();
    }

    public async Task<PathHealthOperationResult> RepairMetadataAsync()
    {
        return await RunOperationAsync(
            () => addonManager.RepairStalePathMetadataAsync(),
            result => L.Format("PathHealth.MetadataRepairComplete", result.ChangedCount, result.SkippedCount));
    }

    public async Task<PathHealthOperationResult> MigrateAddonNoMountAsync()
    {
        return await RunOperationAsync(
            () => addonManager.MigrateAddonNoMountEntriesAsync(),
            result => L.Format("PathHealth.AddonNoMountMigrationComplete", result.ChangedCount));
    }

    public async Task<PathHealthOperationResult> CleanupEmptyFoldersAsync()
    {
        return await RunOperationAsync(
            () => addonManager.CleanupStaleEmptyWorkshopFoldersAsync(),
            result => L.Format("PathHealth.CleanupComplete", result.DeletedCount, result.SkippedCount));
    }

    public async Task<PathHealthOperationResult> MigrateManagedDataAsync()
    {
        return await RunOperationAsync(
            () => addonManager.MigrateManagedDataAsync(),
            result => L.Format("PathHealth.ManagedMigrationComplete", result.MovedCount, result.SkippedCount));
    }

    private async Task<PathHealthOperationResult> RunOperationAsync(
        Func<Task<PathHealthOperationResult>> action,
        Func<PathHealthOperationResult, string> statusBuilder)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            var completedStatus = statusBuilder(result);
            Refresh();
            StatusMessage = completedStatus;
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildMetadataRepairSummary()
    {
        if (report.MetadataRepairCandidates.Count == 0)
        {
            return L.Get("PathHealth.NoMetadataRepair");
        }

        return string.Join(
            Environment.NewLine,
            report.MetadataRepairCandidates
                .Take(8)
                .Select(candidate => $"{candidate.AddonId}: {candidate.OldPath} -> {candidate.NewPath}"));
    }

    private string BuildAddonNoMountSummary()
    {
        var plan = report.AddonNoMountMigrationPlan;
        if (!plan.HasWork)
        {
            return L.Get("PathHealth.NoAddonNoMountMigration");
        }

        return L.Format(
            "PathHealth.AddonNoMountMigrationSummary",
            plan.SourcePath ?? L.Get("PathHealth.None"),
            plan.TargetPath ?? L.Get("PathHealth.None"),
            string.Join(", ", plan.ToMigrateIds.Take(20)));
    }

    private string BuildCleanupSummary()
    {
        if (report.CleanupCandidates.Count == 0)
        {
            return L.Get("PathHealth.NoCleanup");
        }

        return string.Join(
            Environment.NewLine,
            report.CleanupCandidates
                .Take(10)
                .Select(candidate => $"{candidate.AddonId}: {candidate.FolderPath}"));
    }

    private string BuildManagedMigrationSummary()
    {
        if (report.ManagedDataMigrationCandidates.Count == 0)
        {
            return L.Get("PathHealth.NoManagedMigration");
        }

        return string.Join(
            Environment.NewLine,
            report.ManagedDataMigrationCandidates
                .Take(10)
                .Select(candidate => $"{candidate.AddonId}: {candidate.SourcePath} -> {candidate.TargetPath}"));
    }

    private void RaiseReportProperties()
    {
        this.RaisePropertyChanged(nameof(CurrentSteamRoot));
        this.RaisePropertyChanged(nameof(CurrentGmodInstall));
        this.RaisePropertyChanged(nameof(CurrentWorkshopRoot));
        this.RaisePropertyChanged(nameof(CurrentCachePath));
        this.RaisePropertyChanged(nameof(CurrentAddonNoMountPath));
        this.RaisePropertyChanged(nameof(PreviousGmodInstall));
        this.RaisePropertyChanged(nameof(PreviousWorkshopRoot));
        this.RaisePropertyChanged(nameof(PreviousAddonNoMountPath));
        this.RaisePropertyChanged(nameof(IssueCount));
        this.RaisePropertyChanged(nameof(MetadataRepairCount));
        this.RaisePropertyChanged(nameof(AddonNoMountMigrationCount));
        this.RaisePropertyChanged(nameof(CleanupCandidateCount));
        this.RaisePropertyChanged(nameof(ManagedMigrationCandidateCount));
        this.RaisePropertyChanged(nameof(IssueSummary));
        this.RaisePropertyChanged(nameof(MetadataRepairSummary));
        this.RaisePropertyChanged(nameof(AddonNoMountSummary));
        this.RaisePropertyChanged(nameof(CleanupSummary));
        this.RaisePropertyChanged(nameof(ManagedMigrationSummary));
        RaiseActionProperties();
    }

    private void RaiseActionProperties()
    {
        this.RaisePropertyChanged(nameof(CanRepairMetadata));
        this.RaisePropertyChanged(nameof(CanMigrateAddonNoMount));
        this.RaisePropertyChanged(nameof(CanCleanupEmptyFolders));
        this.RaisePropertyChanged(nameof(CanMigrateManagedData));
    }
}
