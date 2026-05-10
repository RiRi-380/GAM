using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    internal enum PendingChangeActionType
    {
        Unknown,
        Enable,
        Disable,
        EnableAsset,
        DisableAsset,
        ApplyStates
    }

    public class PendingChangeManager
    {
        private readonly string pendingPath;
        private readonly string pendingBackupPath;
        private readonly AddonManager addonManager;
        private readonly IErrorHandler? errorHandler;
        private readonly object lockObject = new object();
        private PendingChanges pendingChanges = new PendingChanges();

        public event EventHandler<ChangeAppliedEventArgs>? ChangeApplied;
        public event EventHandler<ChangeFailedEventArgs>? ChangeFailed;

        public PendingChangeManager(AddonManager addonManager, string managerPath, IErrorHandler? errorHandler = null)
        {
            this.addonManager = addonManager;
            this.pendingPath = Path.Combine(managerPath, "pending.json");
            this.pendingBackupPath = Path.Combine(managerPath, "pending.json.bak");
            this.errorHandler = errorHandler;
            LoadPendingChanges();
        }


        public void QueueChange(AddonChange change)
        {
            if (change == null)
            {
                return;
            }

            QueueChanges(new[] { change });
        }

        public void QueueChanges(IEnumerable<AddonChange> changes)
        {
            if (changes == null)
            {
                return;
            }

            var changeList = changes.Where(c => c != null).ToList();
            if (changeList.Count == 0)
            {
                return;
            }

            var addonIds = new HashSet<string>(changeList.Select(c => c.AddonId));

            lock (lockObject)
            {
                // 同じアドオンの変更がある場合は最新のものに置き換え
                pendingChanges.Changes.RemoveAll(c => addonIds.Contains(c.AddonId));
                pendingChanges.Changes.AddRange(changeList);
                SavePendingChanges();
            }
        }

        public void AddPendingChange(string action, string assetId)
        {
            var change = new AddonChange(action + "_asset", assetId);
            QueueChange(change);
        }

        public void QueueAssetToggle(string assetId, bool enable)
        {
            var asset = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null)
            {
                return;
            }

            var addonIds = asset.ContainsAllAddons() 
                ? addonManager.GetConfiguration().AddonMetadata.Keys.ToList()
                : asset.Addons;


            var changes = addonIds.Select(addonId => new AddonChange(enable ? "enable" : "disable", addonId)).ToList();
            QueueChanges(changes);
        }

        public void QueueApplyStates()
        {
            QueueChange(new AddonChange("apply_states", "*"));
        }

        internal static PendingChangeActionType ParseActionType(string? action)
        {
            return action?.Trim().ToLowerInvariant() switch
            {
                "enable" => PendingChangeActionType.Enable,
                "disable" => PendingChangeActionType.Disable,
                "enable_asset" => PendingChangeActionType.EnableAsset,
                "disable_asset" => PendingChangeActionType.DisableAsset,
                "apply_states" => PendingChangeActionType.ApplyStates,
                _ => PendingChangeActionType.Unknown
            };
        }

	        public async Task ApplyPendingChangesAsync()
	        {
	            if (!HasPendingChanges())
	            {
	                return;
	            }

            // Steamの同期処理を待つ
            await Task.Delay(5000);

            const int pollIntervalMs = 3000;
            const int maxWaitAttempts = 40;
            int waitAttempts = 0;
	            bool notifiedAboutSteam = false;
	
	            while (true)
	            {
	                var safe = SteamProcessChecker.IsSafeToModifyAddons(out string? warning);
	                if (safe)
	                {
	                    if (!notifiedAboutSteam && !string.IsNullOrEmpty(warning))
	                    {
	                        // Steam 起動中は警告のみ（適用は続行する）
	                        errorHandler?.HandleWarning(warning, "PendingChangeManager.ApplyPendingChangesAsync");
	                        notifiedAboutSteam = true;
	                    }
	                    break;
	                }
	
	                if (!notifiedAboutSteam && !string.IsNullOrEmpty(warning))
	                {
	                    // Garry's Mod 起動中は保留にする
	                    errorHandler?.HandleWarning(warning, "PendingChangeManager.ApplyPendingChangesAsync");
	
	                    List<AddonChange> snapshot;
	                    lock (lockObject)
	                    {
	                        snapshot = new List<AddonChange>(pendingChanges.Changes);
	                    }
	
	                    var deferException = new InvalidOperationException(warning);
	                    foreach (var change in snapshot)
	                    {
	                        ChangeFailed?.Invoke(this, new ChangeFailedEventArgs
	                        {
	                            Change = change,
	                            Error = deferException
	                        });
	                    }
	
	                    notifiedAboutSteam = true;
	                }
	
	                if (++waitAttempts >= maxWaitAttempts)
	                {
	                    Debug.WriteLine("PendingChangeManager: Deferring pending changes until Garry's Mod is closed.");
	                    errorHandler?.HandleWarning(
	                        "Garry's Mod が起動中のため、保留中のアドオン変更を後で適用します。ゲーム終了後に再試行してください。",
	                        "PendingChangeManager.ApplyPendingChangesAsync");
	                    return;
	                }

                await Task.Delay(pollIntervalMs);
            }

            List<AddonChange> changesToApply;
            lock (lockObject)
            {
                changesToApply = new List<AddonChange>(pendingChanges.Changes);
                pendingChanges.Changes.Clear();
            }

            var successfulChanges = new List<AddonChange>();
            var failedChanges = new List<(AddonChange change, Exception error)>();

            foreach (var change in changesToApply)
            {
                try
                {
                    
                    var actionType = ParseActionType(change.Action);
                    switch (actionType)
                    {
                        case PendingChangeActionType.Enable:
                            addonManager.EnableAddon(change.AddonId);
                            break;
                        case PendingChangeActionType.Disable:
                            addonManager.DisableAddon(change.AddonId);
                            break;
                        case PendingChangeActionType.EnableAsset:
                            await addonManager.EnableAssetAsync(change.AddonId);
                            break;
                        case PendingChangeActionType.DisableAsset:
                            await addonManager.DisableAssetAsync(change.AddonId);
                            break;
                        case PendingChangeActionType.ApplyStates:
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unsupported pending change action: '{change.Action ?? "<null>"}' for addon '{change.AddonId}'.");
                    }

                    successfulChanges.Add(change);
                    ChangeApplied?.Invoke(this, new ChangeAppliedEventArgs { Change = change });
                    
                }
                catch (Exception ex)
                {
                    failedChanges.Add((change, ex));
                    ChangeFailed?.Invoke(this, new ChangeFailedEventArgs 
                    { 
                        Change = change, 
                        Error = ex 
                    });
                    
                }
            }

            // 失敗した変更を再度キューに戻す（オプション）
            if (failedChanges.Any())
            {
                lock (lockObject)
                {
                    foreach (var (change, _) in failedChanges)
                    {
                        pendingChanges.Changes.Add(change);
                    }
                }
            }

            SavePendingChanges();
            
            // アドオンの状態を更新（ジャンクションの作成/削除を実行）
            await addonManager.UpdateAddonStatesAsync();
            
            // 設定を保存
            await addonManager.SaveConfigurationAsync();
            
        }

        public bool HasPendingChanges()
        {
            lock (lockObject)
            {
                return pendingChanges.Changes.Any();
            }
        }

        public int GetPendingChangeCount()
        {
            lock (lockObject)
            {
                if (pendingChanges == null)
                {
                    // PendingChangeManager.GetPendingChangeCount: pendingChanges is null
                    return 0;
                }
                return pendingChanges.Changes.Count;
            }
        }

        public List<AddonChange> GetPendingChanges()
        {
            lock (lockObject)
            {
                return new List<AddonChange>(pendingChanges.Changes);
            }
        }

        private void LoadPendingChanges()
        {
            if (TryReadPendingChanges(pendingPath, out var loaded))
            {
                pendingChanges = loaded;
                return;
            }

            if (TryReadPendingChanges(pendingBackupPath, out var backup))
            {
                pendingChanges = backup;
                errorHandler?.HandleWarning(
                    "Recovered pending changes from backup storage.",
                    "PendingChangeManager.LoadPendingChanges");

                // Best effort: restore canonical pending.json from backup content.
                SavePendingChanges();
                return;
            }

            pendingChanges = new PendingChanges();
        }

        private void SavePendingChanges()
        {
            try
            {
                var json = JsonConvert.SerializeObject(pendingChanges, Formatting.Indented);
                WritePendingChangesFile(pendingPath, json);
                TryWriteBackup(json);
            }
            catch (Exception ex)
            {
                errorHandler?.HandleError(
                    ex,
                    "PendingChangeManager.SavePendingChanges",
                    ErrorSeverity.Warning);
                TryWriteEmergencyBackup();
            }
        }

        private bool TryReadPendingChanges(string path, out PendingChanges loadedChanges)
        {
            loadedChanges = new PendingChanges();
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return true;
                }

                Newtonsoft.Json.Linq.JObject.Parse(json);
                loadedChanges = JsonConvert.DeserializeObject<PendingChanges>(json, new JsonSerializerSettings
                {
                    Error = (sender, args) => args.ErrorContext.Handled = true
                }) ?? new PendingChanges();
                return true;
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                errorHandler?.HandleWarning(
                    $"Pending changes file is invalid JSON ({Path.GetFileName(path)}): {ex.Message}",
                    "PendingChangeManager.LoadPendingChanges");
                return false;
            }
            catch (Exception ex)
            {
                errorHandler?.HandleError(
                    ex,
                    $"PendingChangeManager.LoadPendingChanges ({Path.GetFileName(path)})",
                    ErrorSeverity.Warning);
                return false;
            }
        }

        private static void WritePendingChangesFile(string targetPath, string content)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, content);

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }

        private void TryWriteBackup(string json)
        {
            try
            {
                WritePendingChangesFile(pendingBackupPath, json);
            }
            catch (Exception ex)
            {
                errorHandler?.HandleWarning(
                    $"Failed to write pending backup file: {ex.Message}",
                    "PendingChangeManager.SavePendingChanges");
            }
        }

        private void TryWriteEmergencyBackup()
        {
            try
            {
                var emergencyPath = Path.Combine(
                    Path.GetTempPath(),
                    $"GAM-pending-{Process.GetCurrentProcess().Id}.json");
                var json = JsonConvert.SerializeObject(pendingChanges, Formatting.Indented);
                File.WriteAllText(emergencyPath, json);

                errorHandler?.HandleWarning(
                    $"Pending changes were written to emergency path: {emergencyPath}",
                    "PendingChangeManager.SavePendingChanges");
            }
            catch (Exception ex)
            {
                errorHandler?.HandleError(
                    ex,
                    "PendingChangeManager.SavePendingChanges emergency backup",
                    ErrorSeverity.Warning);
            }
        }

        public void ClearPendingChanges()
        {
            lock (lockObject)
            {
                pendingChanges.Changes.Clear();
                TryDeletePendingFile(pendingPath);
                TryDeletePendingFile(pendingBackupPath);
            }
        }

        private void TryDeletePendingFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                errorHandler?.HandleWarning(
                    $"Failed to delete pending changes file ({Path.GetFileName(path)}): {ex.Message}",
                    "PendingChangeManager.ClearPendingChanges");
            }
        }
    }

    public class ChangeAppliedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; } = null!;
    }

    public class ChangeFailedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; } = null!;
        public Exception Error { get; set; } = null!;
    }
}
