using System;
using System.Diagnostics;
using System.Linq;
using Steamworks;

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

        private static readonly string[] GmodProcessNames = new[]
        {
            "hl2",
            "gmod",
            "garrysmod"
        };

        /// <summary>
        /// Check if Steam is running using Steamworks API
        /// </summary>
        public static bool IsSteamRunningViaAPI()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch
            {
                // Fallback to process check if API fails
                return IsSteamRunningViaProcess();
            }
        }

        /// <summary>
        /// Check if Steam is running by looking for processes
        /// </summary>
        public static bool IsSteamRunningViaProcess()
        {
            try
            {
                var processes = Process.GetProcesses();
                return processes.Any(p => 
                    SteamProcessNames.Any(name => 
                        p.ProcessName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));
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
                var processes = Process.GetProcesses();
                return processes.Any(p => 
                    GmodProcessNames.Any(name => 
                        p.ProcessName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get a user-friendly message about Steam/Gmod status
        /// </summary>
        public static string GetStatusMessage()
        {
            bool steamRunning = IsSteamRunningViaAPI();
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
        public static bool IsSafeToModifyAddons(out string warning)
        {
            warning = null;

            if (IsGmodRunning())
            {
                warning = "Garry's Mod is running. Modifying addons may cause issues or crashes.";
                return false;
            }

            if (IsSteamRunningViaAPI())
            {
                warning = "Steam is running. Disabled addons may be automatically re-downloaded when you start Garry's Mod.";
                // Return true but with warning - let user decide
                return true;
            }

            return true;
        }
    }
}
