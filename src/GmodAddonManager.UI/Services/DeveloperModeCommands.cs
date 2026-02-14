using System;
using System.Collections.Generic;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.Services;

public static class DeveloperModeCommands
{
    public const string JunctionMode = "JUNCTION_MODE";
    public const string ExclusiveApply = "INVESTIGATION_MODE";

    public static bool HasCommand(string? phrase, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var token in SplitCommands(phrase))
        {
            if (string.Equals(token, command, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ShouldShowExclusiveApply(AddonManager addonManager, string? phrase)
    {
        return HasCommand(phrase, ExclusiveApply) || IsExperimentMode(addonManager);
    }

    public static IReadOnlyList<string> SplitCommands(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return Array.Empty<string>();
        }

        return phrase.Split(
            new[] { ' ', '\t', '\r', '\n', ',', ';', '|' },
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsExperimentMode(AddonManager addonManager)
    {
        if (addonManager.IsExperimentContextActive)
        {
            return true;
        }

        if (IsEnvTrue(Environment.GetEnvironmentVariable("GAM_ENABLE_IPC")))
        {
            return true;
        }

        var logPath = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            return true;
        }

        return false;
    }

    private static bool IsEnvTrue(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
