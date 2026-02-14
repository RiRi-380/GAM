using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Manages Garry's Mod addon enable/disable state by editing garrysmod/cfg/addonnomount.txt.
    /// Addons listed in this file are DISABLED (not mounted).
    /// Addons NOT in this file are ENABLED.
    /// </summary>
    public class GmodAddonStateStore
    {
        private readonly string noMountFilePath;
        private readonly object fileLock = new object();

        public GmodAddonStateStore(string gmodRootPath)
        {
            if (string.IsNullOrWhiteSpace(gmodRootPath))
            {
                throw new ArgumentException("gmodRootPath is null or empty", nameof(gmodRootPath));
            }

            // gmodRootPath should point to .../common/GarrysMod
            // addonnomount.txt is located at: garrysmod/cfg/addonnomount.txt
            var cfgDir = Path.Combine(gmodRootPath, "garrysmod", "cfg");
            noMountFilePath = Path.Combine(cfgDir, "addonnomount.txt");
        }

        /// <summary>
        /// Set enable state for a single workshop addon id. Returns true if the state was persisted.
        /// </summary>
        public bool SetEnabled(string workshopId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return false;

            lock (fileLock)
            {
                var disabledIds = LoadDisabledIdsNoLock();

                if (enabled)
                {
                    // Enable = remove from nomount list
                    disabledIds.Remove(workshopId);
                }
                else
                {
                    // Disable = add to nomount list
                    if (!disabledIds.Contains(workshopId))
                    {
                        disabledIds.Add(workshopId);
                    }
                }

                SaveDisabledIdsNoLock(disabledIds);

                // Read back to confirm
                var persistedIds = LoadDisabledIdsNoLock();
                var isNowDisabled = persistedIds.Contains(workshopId);
                return enabled ? !isNowDisabled : isNowDisabled;
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
                var disabledIds = LoadDisabledIdsNoLock();

                foreach (var kvp in statesToApply)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key)) continue;

                    if (kvp.Value)
                    {
                        // Enable = remove from nomount list
                        disabledIds.Remove(kvp.Key);
                    }
                    else
                    {
                        // Disable = add to nomount list
                        if (!disabledIds.Contains(kvp.Key))
                        {
                            disabledIds.Add(kvp.Key);
                        }
                    }
                }

                SaveDisabledIdsNoLock(disabledIds);

                // Read back to confirm
                var persistedIds = LoadDisabledIdsNoLock();
                foreach (var kvp in statesToApply)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key)) continue;

                    var isNowDisabled = persistedIds.Contains(kvp.Key);
                    var expectedDisabled = !kvp.Value;
                    if (isNowDisabled != expectedDisabled)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Get enabled state for a single addon. Returns null if unknown.
        /// </summary>
        public bool? GetEnabled(string workshopId)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return null;
            lock (fileLock)
            {
                var disabledIds = LoadDisabledIdsNoLock();
                // If in nomount list = disabled, otherwise enabled
                return !disabledIds.Contains(workshopId);
            }
        }

        /// <summary>
        /// Get all addon states. Returns true for enabled, false for disabled.
        /// Note: Only returns states for addons that are explicitly disabled.
        /// Addons not in the list are assumed enabled.
        /// </summary>
        public IReadOnlyDictionary<string, bool> GetAllStates()
        {
            lock (fileLock)
            {
                var disabledIds = LoadDisabledIdsNoLock();
                var result = new Dictionary<string, bool>();
                foreach (var id in disabledIds)
                {
                    result[id] = false; // disabled
                }
                return result;
            }
        }

        /// <summary>
        /// Get all disabled addon IDs.
        /// </summary>
        public HashSet<string> GetDisabledIds()
        {
            lock (fileLock)
            {
                return LoadDisabledIdsNoLock();
            }
        }

        private HashSet<string> LoadDisabledIdsNoLock()
        {
            var result = new HashSet<string>();
            try
            {
                var dir = Path.GetDirectoryName(noMountFilePath);
                if (string.IsNullOrEmpty(dir)) return result;

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(noMountFilePath))
                {
                    // No file = no disabled addons
                    return result;
                }

                var text = File.ReadAllText(noMountFilePath, Encoding.UTF8);

                // Parse addonnomount.txt format:
                // "addonnomount"
                // {
                //     "1"    "workshopId1"
                //     "2"    "workshopId2"
                // }
                // The key is an index, the value is the workshop ID
                foreach (Match m in Regex.Matches(text, "\"\\d+\"\\s+\"(?<id>\\d+)\""))
                {
                    var id = m.Groups["id"].Value;
                    if (!string.IsNullOrEmpty(id))
                    {
                        result.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GmodAddonStateStore] Failed to load addonnomount.txt: {ex.Message}");
            }

            return result;
        }

        private void SaveDisabledIdsNoLock(HashSet<string> disabledIds)
        {
            try
            {
                var dir = Path.GetDirectoryName(noMountFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var temp = noMountFilePath + ".tmp";
                var content = BuildNoMountFileContent(disabledIds);
                // Use UTF-8 without BOM - Source engine doesn't support BOM
                File.WriteAllText(temp, content, new UTF8Encoding(false));

                if (File.Exists(noMountFilePath))
                {
                    File.Replace(temp, noMountFilePath, null);
                }
                else
                {
                    File.Move(temp, noMountFilePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GmodAddonStateStore] Failed to save addonnomount.txt: {ex.Message}");
            }
        }

        private string BuildNoMountFileContent(HashSet<string> disabledIds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"addonnomount\"");
            sb.AppendLine("{");

            // Sort for deterministic output
            var sortedIds = disabledIds.OrderBy(id => id).ToList();
            for (int i = 0; i < sortedIds.Count; i++)
            {
                sb.Append("\t\"").Append(i + 1).Append("\"\t\t\"")
                  .Append(sortedIds[i]).AppendLine("\"");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
