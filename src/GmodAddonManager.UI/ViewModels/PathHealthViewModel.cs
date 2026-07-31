using System;
using System.Collections.Generic;
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
    public int ManagedMigrationCandidateCount => report.ManagedMigrationCandidateCount;
    public bool CanRepairMetadata => !IsBusy && MetadataRepairCount > 0;
    public bool CanMigrateAddonNoMount => !IsBusy && AddonNoMountMigrationCount > 0;
    public bool CanMigrateManagedData => !IsBusy && ManagedMigrationCandidateCount > 0;

    public string IssueSummary => BuildIssueSummary();

    public string MetadataRepairSummary => BuildMetadataRepairSummary();
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

        var lines = report.MetadataRepairCandidates
            .Take(8)
            .Select(candidate => $"{candidate.AddonId}: {candidate.OldPath} -> {candidate.NewPath}")
            .ToList();
        return AppendOmittedLine(lines, report.MetadataRepairCandidates.Count, 8);
    }

    private string BuildAddonNoMountSummary()
    {
        var plan = report.AddonNoMountMigrationPlan;
        if (!plan.HasWork)
        {
            return L.Get("PathHealth.NoAddonNoMountMigration");
        }

        var visibleIds = string.Join(", ", plan.ToMigrateIds.Take(20));
        if (plan.ToMigrateIds.Count > 20)
        {
            visibleIds += " " + L.Format(
                "PathHealth.MoreItemsInline",
                plan.ToMigrateIds.Count - 20);
        }

        return L.Format(
            "PathHealth.AddonNoMountMigrationSummary",
            plan.SourcePath ?? L.Get("PathHealth.None"),
            plan.TargetPath ?? L.Get("PathHealth.None"),
            visibleIds);
    }

    private string BuildManagedMigrationSummary()
    {
        if (report.ManagedDataMigrationCandidates.Count == 0)
        {
            return L.Get("PathHealth.NoManagedMigration");
        }

        var lines = report.ManagedDataMigrationCandidates
            .Take(10)
            .Select(candidate => $"{candidate.AddonId}: {candidate.SourcePath} -> {candidate.TargetPath}")
            .ToList();
        return AppendOmittedLine(lines, report.ManagedDataMigrationCandidates.Count, 10);
    }

    private string BuildIssueSummary()
    {
        if (report.Issues.Count == 0)
        {
            return L.Get("PathHealth.NoIssues");
        }

        var lines = report.Issues
            .Take(12)
            .Select(LocalizePathIssue)
            .ToList();
        return AppendOmittedLine(lines, report.Issues.Count, 12);
    }

    private static string LocalizePathIssue(string issue)
    {
        if (!LocalizationManager.Instance.CurrentLanguage.StartsWith(
                "ja",
                StringComparison.OrdinalIgnoreCase))
        {
            return issue;
        }

        if (string.Equals(
                issue,
                "Garry's Mod appmanifest_4000.acf was not found in any Steam library.",
                StringComparison.Ordinal))
        {
            return L.Get("PathHealth.Issue.GmodManifestMissing");
        }

        if (string.Equals(
                issue,
                "Garry's Mod workshop content root was not found in any Steam library.",
                StringComparison.Ordinal))
        {
            return L.Get("PathHealth.Issue.WorkshopRootMissing");
        }

        if (issue.StartsWith("Failed to refresh path snapshot:", StringComparison.Ordinal) ||
            issue.StartsWith("Startup path detection failed:", StringComparison.Ordinal))
        {
            return L.Get("PathHealth.Issue.RefreshFailed");
        }

        var changedMarker = " changed: ";
        var changedIndex = issue.IndexOf(changedMarker, StringComparison.Ordinal);
        if (changedIndex > 0)
        {
            var label = issue[..changedIndex] switch
            {
                "GMod install" => L.Get("PathHealth.GmodInstall"),
                "Workshop root" => L.Get("PathHealth.WorkshopRoot"),
                "addonnomount.txt" => L.Get("PathHealth.AddonNoMount"),
                _ => L.Get("PathHealth.Issue.Path")
            };
            return L.Format(
                "PathHealth.Issue.PathChanged",
                label,
                issue[(changedIndex + changedMarker.Length)..]);
        }

        var count = ReadLeadingCount(issue);
        if (count.HasValue)
        {
            if (issue.Contains("addon metadata path(s) can be repaired", StringComparison.Ordinal))
            {
                return L.Format("PathHealth.Issue.MetadataRepairAvailable", count.Value);
            }

            if (issue.Contains("addonnomount entrie(s) can be copied", StringComparison.Ordinal))
            {
                return L.Format("PathHealth.Issue.AddonNoMountCopyAvailable", count.Value);
            }

            if (issue.Contains("GAM-managed data item(s) can be moved", StringComparison.Ordinal))
            {
                return L.Format("PathHealth.Issue.ManagedMoveAvailable", count.Value);
            }
        }

        return L.Get("PathHealth.Issue.Unknown");
    }

    private static int? ReadLeadingCount(string text)
    {
        var separatorIndex = text.IndexOf(' ');
        return separatorIndex > 0 &&
               int.TryParse(text[..separatorIndex], out var count)
            ? count
            : null;
    }

    private static string AppendOmittedLine(
        IReadOnlyCollection<string> visibleLines,
        int totalCount,
        int limit)
    {
        var text = string.Join(Environment.NewLine, visibleLines);
        var omittedCount = totalCount - limit;
        return omittedCount > 0
            ? text + Environment.NewLine + L.Format("PathHealth.MoreItemsLine", omittedCount)
            : text;
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
        this.RaisePropertyChanged(nameof(ManagedMigrationCandidateCount));
        this.RaisePropertyChanged(nameof(IssueSummary));
        this.RaisePropertyChanged(nameof(MetadataRepairSummary));
        this.RaisePropertyChanged(nameof(AddonNoMountSummary));
        this.RaisePropertyChanged(nameof(ManagedMigrationSummary));
        RaiseActionProperties();
    }

    private void RaiseActionProperties()
    {
        this.RaisePropertyChanged(nameof(CanRepairMetadata));
        this.RaisePropertyChanged(nameof(CanMigrateAddonNoMount));
        this.RaisePropertyChanged(nameof(CanMigrateManagedData));
    }
}
