using System;
using System.IO;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Utils;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Views;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.UI.Services;

public sealed class StartupPathRecoveryRunResult
{
    public bool Accepted { get; init; }
    public bool ApplyRepairs { get; init; }
    public string? ResolvedGmodInstallPath { get; init; }
    public string? ResolvedWorkshopRootPath { get; init; }
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

            errorHandler.HandleInfo(
                $"Startup path recovery applied: metadata={metadataResult.ChangedCount}, addonnomount={addonNoMountResult.ChangedCount}, stateApply=not-requested",
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
        var decision = StartupPathRecoveryEvaluator.Evaluate(
            configuration,
            snapshot,
            settings.CustomGmodInstallPath,
            settings.CustomWorkshopPath,
            settings.ConfirmedGmodInstallPath,
            settings.ConfirmedWorkshopPath);

        if (forcePrompt && !decision.ShouldPrompt)
        {
            decision.ShouldPrompt = true;
            decision.Reason = StartupPathRecoveryReason.ManualRequest;
        }

        if (!decision.ShouldPrompt)
        {
            return CreateRunResult(decision);
        }

        var result = await StartupPathRecoveryDialog.ShowStandaloneAsync(decision);
        if (!result.Accepted)
        {
            // AddonManager would perform the same automatic discovery after a
            // declined prompt. Reuse the already validated candidates without
            // persisting them or treating the prompt as accepted.
            return CreateRunResult(decision);
        }

        settings.CustomGmodInstallPath = result.GmodInstallPath;
        settings.CustomWorkshopPath = result.WorkshopRootPath;
        settings.ConfirmedGmodInstallPath = result.GmodInstallPath;
        settings.ConfirmedWorkshopPath = result.WorkshopRootPath;
        settings.DismissedPathRecoverySignature = null;
        settings.Save();
        return new StartupPathRecoveryRunResult
        {
            Accepted = true,
            ApplyRepairs = forcePrompt,
            ResolvedGmodInstallPath = result.GmodInstallPath,
            ResolvedWorkshopRootPath = result.WorkshopRootPath
        };
    }

    private static StartupPathRecoveryRunResult CreateRunResult(
        StartupPathRecoveryDecision decision)
    {
        return new StartupPathRecoveryRunResult
        {
            ResolvedGmodInstallPath = decision.DetectedGmodInstallPath,
            ResolvedWorkshopRootPath = decision.DetectedWorkshopRootPath
        };
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
        var configPath = Path.Combine(appDataPath, "config.json");
        var backupPath = configPath + ".bak";
        if (!File.Exists(configPath) && !File.Exists(backupPath))
        {
            return null;
        }

        try
        {
            try
            {
                return ReadValidatedConfiguration(configPath);
            }
            catch (UnsupportedConfigurationSchemaException)
            {
                throw;
            }
            catch (Exception primaryException) when (File.Exists(backupPath))
            {
                try
                {
                    return ReadValidatedConfiguration(backupPath);
                }
                catch (UnsupportedConfigurationSchemaException)
                {
                    throw;
                }
                catch (Exception backupException)
                {
                    throw new InvalidOperationException(
                        "Both the primary configuration and its backup are unreadable.",
                        new AggregateException(primaryException, backupException));
                }
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("StartupPathRecoveryCoordinator.TryLoadExistingConfiguration", ex);
            throw;
        }
    }

    private static Configuration ReadValidatedConfiguration(string path)
    {
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Configuration file is empty.");
        }

        var raw = JObject.Parse(json);
        new ConfigurationMigrationService().EnsureSupportedSchema(raw);
        return JsonConvert.DeserializeObject<Configuration>(json)
            ?? throw new InvalidOperationException("Configuration deserialized to null.");
    }
}
