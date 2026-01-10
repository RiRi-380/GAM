using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Manages Garry's Mod addon enable/disable state by editing garrysmod/settings/addons.txt.
    /// We do not remove workshop files or links; we only toggle load state so Steam won't redownload.
    /// </summary>
    public class GmodAddonStateStore
    {
        private readonly string settingsFilePath;
        private readonly object fileLock = new object();

        public GmodAddonStateStore(string gmodRootPath)
        {
            if (string.IsNullOrWhiteSpace(gmodRootPath))
            {
                throw new ArgumentException("gmodRootPath is null or empty", nameof(gmodRootPath));
            }

            // gmodRootPath should point to .../common/GarrysMod
            // addons.txt is located at: garrysmod/settings/addons.txt
            var settingsDir = Path.Combine(gmodRootPath, "garrysmod", "settings");
            settingsFilePath = Path.Combine(settingsDir, "addons.txt");
        }

        /// <summary>
        /// Set enable state for a single workshop addon id. Returns true if the state was persisted.
        /// </summary>
        public bool SetEnabled(string workshopId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return false;

            lock (fileLock)
            {
                var states = LoadAllStatesNoLock();
                states[workshopId] = enabled;
                SaveAllStatesNoLock(states);

                // Read back to confirm (SaveAllStatesNoLock is best-effort and may fail silently)
                var persistedStates = LoadAllStatesNoLock();
                return persistedStates.TryGetValue(workshopId, out var stored) && stored == enabled;
            }
        }

        /// <summary>
        /// Bulk set multiple states atomically. Returns true if all requested states were persisted.
        /// </summary>
        public bool SetEnabledBulk(Dictionary<string, bool> statesToApply)
        {
            if (statesToApply == null || statesToApply.Count == 0) return true;
            lock (fileLock)
            {
                var states = LoadAllStatesNoLock();
                foreach (var kvp in statesToApply)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        states[kvp.Key] = kvp.Value;
                    }
                }
                SaveAllStatesNoLock(states);

                // Read back to confirm
                var persistedStates = LoadAllStatesNoLock();
                foreach (var kvp in statesToApply)
                {
                    if (!persistedStates.TryGetValue(kvp.Key, out var stored) || stored != kvp.Value)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public bool? GetEnabled(string workshopId)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return null;
            lock (fileLock)
            {
                var states = LoadAllStatesNoLock();
                return states.TryGetValue(workshopId, out var val) ? val : (bool?)null;
            }
        }

        private Dictionary<string, bool> LoadAllStatesNoLock()
        {
            var result = new Dictionary<string, bool>();
            try
            {
                var dir = Path.GetDirectoryName(settingsFilePath);
                if (string.IsNullOrEmpty(dir)) return result;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(settingsFilePath))
                {
                    // Create an empty file with root KeyValues to be safe
                    File.WriteAllText(settingsFilePath, BuildFileContent(result), Encoding.UTF8);
                    return result;
                }

                var text = File.ReadAllText(settingsFilePath, Encoding.UTF8);

                // Try to parse as simple Source KeyValues structure:
                // "addons" { "12345" "1"  "67890" "0" }
                // Be tolerant: search for pairs of "id" "0|1"
                foreach (Match m in Regex.Matches(text, "\\\"(?<id>\\d+)\\\"\\s*\\\"(?<val>[01])\\\""))
                {
                    var id = m.Groups["id"].Value;
                    var val = m.Groups["val"].Value == "1";
                    if (!string.IsNullOrEmpty(id))
                    {
                        result[id] = val;
                    }
                }
            }
            catch
            {
                // If parsing fails, return empty mapping to avoid breaking gameplay
            }

            return result;
        }

        private void SaveAllStatesNoLock(Dictionary<string, bool> states)
        {
            try
            {
                var dir = Path.GetDirectoryName(settingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var temp = settingsFilePath + ".tmp";
                var content = BuildFileContent(states);
                File.WriteAllText(temp, content, Encoding.UTF8);
                if (File.Exists(settingsFilePath))
                {
                    File.Replace(temp, settingsFilePath, null);
                }
                else
                {
                    File.Move(temp, settingsFilePath);
                }
            }
            catch
            {
                // Best effort; if save fails, ignore to not crash the app
            }
        }

        private string BuildFileContent(Dictionary<string, bool> states)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"addons\"");
            sb.AppendLine("{");
            // Keep deterministic ordering for stability
            foreach (var kv in states.OrderBy(k => k.Key))
            {
                sb.Append("    \"").Append(kv.Key).Append("\"    \"")
                  .Append(kv.Value ? "1" : "0").AppendLine("\"");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}


