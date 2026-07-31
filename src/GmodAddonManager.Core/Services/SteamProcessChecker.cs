using System;
using System.Diagnostics;
using System.Linq;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Checks if Steam or Garry's Mod processes are running
    /// </summary>
    public static class SteamProcessChecker
    {
        private static readonly string[] SteamProcessNames = new[]
        {
            "steam",
            "steamwebhelper",
            "gameoverlayui"
        };

        /// <summary>
        /// Check if Steam is running by looking for processes
        /// </summary>
        public static bool IsSteamRunning()
        {
            try
            {
                return IsAnyProcessRunning(processName =>
                    SteamProcessNames.Any(name =>
                        string.Equals(
                            processName,
                            name,
                            StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // If we can't check processes, assume Steam might be running
                return true;
            }
        }

        /// <summary>
        /// Check if Garry's Mod is running
        /// </summary>
        public static bool IsGmodRunning()
        {
            try
            {
                return IsAnyProcessRunning(GmodProcessWatcher.IsRecognizedProcessName);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAnyProcessRunning(Func<string, bool> processNameMatcher)
        {
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (processNameMatcher(process.ProcessName))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore transient access/exit errors per process.
                    }
                }

                return false;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// Get a user-friendly message about Steam/Gmod status
        /// </summary>
        public static string GetStatusMessage()
        {
            bool steamRunning = IsSteamRunning();
            bool gmodRunning = IsGmodRunning();

            if (gmodRunning)
            {
                return "Garry's Mod is currently running. Please close it before modifying addons.";
            }
            else if (steamRunning)
            {
                return "Steam is currently running. For best results, close Steam before disabling addons to prevent automatic re-downloads.";
            }
            else
            {
                return "Steam and Garry's Mod are not running. Safe to modify addons.";
            }
        }

        /// <summary>
        /// Check if it's safe to perform addon operations
        /// </summary>
        public static bool IsSafeToModifyAddons(out string? warning)
        {
            warning = null;

            if (IsGmodRunning())
            {
                warning = "Garry's Mod is running. Modifying addons may cause issues or crashes.";
                return false;
            }

            if (IsSteamRunning())
            {
                warning = "Steam is running. Disabled addons may be automatically re-downloaded when you start Garry's Mod.";
                // Return true but with warning - let user decide
                return true;
            }

            return true;
        }
    }
}
