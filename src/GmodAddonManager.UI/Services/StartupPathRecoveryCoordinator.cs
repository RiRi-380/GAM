using System;
using System.IO;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Utils;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Views;
using Newtonsoft.Json;

namespace GmodAddonManager.UI.Services;

public sealed class StartupPathRecoveryRunResult
{
    public bool Accepted { get; init; }
    public bool ApplyRepairs { get; init; }
}

public static class StartupPathRecoveryCoordinator
{
    public static Task<StartupPathRecoveryRunResult> RunStartupAsync(AppSettings settings, string appDataPath)
    {
        return RunAsync(settings, appDataPath, forcePrompt: false);
    }

    public static Task<StartupPathRecoveryRunResult> RunManualAsync(AppSettings settings, string appDataPath)
    {
        return RunAsync(settings, appDataPath, forcePrompt: true);
    }

    public static async Task ApplyRepairsAsync(
        AddonManager manager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher,
        IErrorHandler errorHandler)
    {
        try
        {
            var metadataResult = await manager.RepairStalePathMetadataAsync();
            var addonNoMountResult = await manager.MigrateAddonNoMountEntriesAsync();
            var stateApplyResult = "applied";
            if (processWatcher.IsGmodRunning)
            {
                pendingChangeManager.QueueApplyStates();
                stateApplyResult = "queued";
            }
            else
            {
                await manager.UpdateAddonStatesAsync();
                await manager.SaveConfigurationAsync();
            }

            errorHandler.HandleInfo(
                $"Startup path recovery applied: metadata={metadataResult.ChangedCount}, addonnomount={addonNoMountResult.ChangedCount}, stateApply={stateApplyResult}",
                "StartupPathRecovery");
        }
        catch (Exception ex)
        {
            errorHandler.HandleWarning($"Startup path recovery repair failed: {ex.Message}", "StartupPathRecovery");
        }
    }

    private static async Task<StartupPathRecoveryRunResult> RunAsync(
        AppSettings settings,
        string appDataPath,
        bool forcePrompt)
    {
        var configuration = TryLoadExistingConfiguration(appDataPath);
        var snapshot = DetectStartupPathSnapshot(settings);
        var pathSignature = BuildPathRecoverySignature(snapshot);
        var promptForUnconfirmedPaths =
            forcePrompt ||
            (!string.IsNullOrWhiteSpace(pathSignature) &&
             !string.Equals(settings.DismissedPathRecoverySignature, pathSignature, StringComparison.OrdinalIgnoreCase));
        var decision = StartupPathRecoveryEvaluator.Evaluate(
            configuration,
            snapshot,
            settings.CustomGmodInstallPath,
            settings.CustomWorkshopPath,
            promptForUnconfirmedPaths,
            settings.ConfirmedGmodInstallPath,
            settings.ConfirmedWorkshopPath);

        if (forcePrompt && !decision.ShouldPrompt)
        {
            decision.ShouldPrompt = true;
            decision.Reason = L.Get("StartupPathRecovery.ManualReason");
        }

        if (!decision.ShouldPrompt)
        {
            return new StartupPathRecoveryRunResult();
        }

        var result = await StartupPathRecoveryDialog.ShowStandaloneAsync(decision);
        if (!result.Accepted)
        {
            if (!forcePrompt && !string.IsNullOrWhiteSpace(pathSignature))
            {
                settings.DismissedPathRecoverySignature = pathSignature;
                settings.Save();
            }

            return new StartupPathRecoveryRunResult();
        }

        settings.CustomGmodInstallPath = result.GmodInstallPath;
        settings.CustomWorkshopPath = result.WorkshopRootPath;
        settings.ConfirmedGmodInstallPath = result.GmodInstallPath;
        settings.ConfirmedWorkshopPath = result.WorkshopRootPath;
        settings.DismissedPathRecoverySignature = null;
        settings.Save();
        return new StartupPathRecoveryRunResult { Accepted = true, ApplyRepairs = true };
    }

    private static string? BuildPathRecoverySignature(PathSnapshot snapshot)
    {
        var gmod = snapshot.GmodInstall?.InstallPath;
        var workshop = snapshot.ActiveWorkshopRoot?.RootPath;
        if (string.IsNullOrWhiteSpace(gmod) || string.IsNullOrWhiteSpace(workshop))
        {
            return null;
        }

        return $"{NormalizePathForSignature(gmod)}|{NormalizePathForSignature(workshop)}";
    }

    private static string NormalizePathForSignature(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
    }

    private static PathSnapshot DetectStartupPathSnapshot(AppSettings settings)
    {
        if (PathOverrideResolver.TryCreateSnapshot(
                settings.CustomGmodInstallPath,
                settings.CustomWorkshopPath,
                out var overrideSnapshot,
                out _))
        {
            return overrideSnapshot;
        }

        try
        {
            return new SteamPathDetector().DetectPathSnapshot();
        }
        catch (Exception ex)
        {
            return new PathSnapshot
            {
                HealthIssues = new[] { $"Startup path detection failed: {ex.Message}" }
            };
        }
    }

    private static Configuration? TryLoadExistingConfiguration(string appDataPath)
    {
        try
        {
            var configPath = Path.Combine(appDataPath, "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            return JsonConvert.DeserializeObject<Configuration>(json);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("StartupPathRecoveryCoordinator.TryLoadExistingConfiguration", ex);
            return null;
        }
    }
}
