using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    public class PendingChangeManager
    {
        private readonly string pendingPath;
        private readonly AddonManager addonManager;
        private readonly object lockObject = new object();
        private PendingChanges pendingChanges;

        public event EventHandler<ChangeAppliedEventArgs> ChangeApplied;
        public event EventHandler<ChangeFailedEventArgs> ChangeFailed;

        public PendingChangeManager(AddonManager addonManager, string managerPath)
        {
            this.addonManager = addonManager;
            this.pendingPath = Path.Combine(managerPath, "pending.json");
            LoadPendingChanges();
        }


        public void QueueChange(AddonChange change)
        {
            lock (lockObject)
            {
                // 同じアドオンの変更がある場合は最新のものに置き換え
                pendingChanges.Changes.RemoveAll(c => c.AddonId == change.AddonId);
                pendingChanges.Changes.Add(change);
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


            foreach (var addonId in addonIds)
            {
                QueueChange(new AddonChange(enable ? "enable" : "disable", addonId));
            }
        }

        public async Task ApplyPendingChangesAsync()
        {
            if (!HasPendingChanges())
            {
                return;
            }


            // Steamの同期処理を待つ
            await Task.Delay(5000);

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
                    
                    switch (change.Action.ToLower())
                    {
                        case "enable":
                            addonManager.EnableAddon(change.AddonId);
                            break;
                        case "disable":
                            addonManager.DisableAddon(change.AddonId);
                            break;
                        default:
                            continue;
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
            if (File.Exists(pendingPath))
            {
                try
                {
                    var json = File.ReadAllText(pendingPath);
                    
                    // Validate JSON before deserialization
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        pendingChanges = new PendingChanges();
                        return;
                    }
                    
                    try
                    {
                        // Parse to validate JSON structure
                        Newtonsoft.Json.Linq.JObject.Parse(json);
                        
                        pendingChanges = JsonConvert.DeserializeObject<PendingChanges>(json, new JsonSerializerSettings
                        {
                            Error = (sender, args) => args.ErrorContext.Handled = true
                        }) ?? new PendingChanges();
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        // Invalid JSON format, create new instance
                        pendingChanges = new PendingChanges();
                    }
                }
                catch (Exception ex)
                {
                    // PendingChangeManager.LoadPendingChanges error
                    pendingChanges = new PendingChanges();
                }
            }
            else
            {
                pendingChanges = new PendingChanges();
            }
        }

        private void SavePendingChanges()
        {
            try
            {
                var json = JsonConvert.SerializeObject(pendingChanges, Formatting.Indented);
                File.WriteAllText(pendingPath, json);
            }
            catch (Exception ex)
            {
            }
        }

        public void ClearPendingChanges()
        {
            lock (lockObject)
            {
                pendingChanges.Changes.Clear();
                if (File.Exists(pendingPath))
                {
                    try
                    {
                        File.Delete(pendingPath);
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }
    }

    public class ChangeAppliedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; }
    }

    public class ChangeFailedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; }
        public Exception Error { get; set; }
    }
}