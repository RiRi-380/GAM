using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// 操作ログの管理
    /// </summary>
    public class OperationLogManager
    {
        private readonly string logPath;
        private readonly object lockObject = new object();
        private List<OperationLog> logs;
        private const int MinSaveIntervalMs = 500;
        private long _lastSaveTick;
        
        public OperationLogManager(string addonManagerPath)
        {
            logPath = Path.Combine(addonManagerPath, "operation_logs.json");
            logs = new List<OperationLog>();
            LoadLogs();
        }
        
        /// <summary>
        /// 操作を開始
        /// </summary>
        public string StartOperation(OperationType type, List<string> items)
        {
            var log = new OperationLog
            {
                Type = type,
                Items = new List<string>(items),
                Progress = new OperationProgress
                {
                    TotalItems = items.Count
                }
            };
            
            lock (lockObject)
            {
                logs.Add(log);
                SaveLogs();
            }
            
            return log.Id;
        }
        
        /// <summary>
        /// 操作の進捗を更新
        /// </summary>
        public void UpdateProgress(string operationId, string item, bool success, string? error = null)
        {
            lock (lockObject)
            {
                var log = logs.FirstOrDefault(l => l.Id == operationId);
                if (log != null)
                {
                    log.Progress.ProcessedItems++;
                    
                    if (success)
                    {
                        log.Progress.SuccessfulItems.Add(item);
                    }
                    else
                    {
                        log.Progress.FailedItems[item] = error ?? "Unknown error";
                    }
                    
                    SaveLogsThrottled();
                }
            }
        }
        
        /// <summary>
        /// 操作を完了
        /// </summary>
        public void CompleteOperation(string operationId, bool success = true, string? error = null)
        {
            lock (lockObject)
            {
                var log = logs.FirstOrDefault(l => l.Id == operationId);
                if (log != null)
                {
                    log.EndTime = DateTime.UtcNow;
                    log.Completed = success;
                    log.Error = error;
                    SaveLogs();
                }
            }
        }
        
        /// <summary>
        /// 未完了の操作を取得
        /// </summary>
        public List<OperationLog> GetIncompleteLogs()
        {
            lock (lockObject)
            {
                return logs.Where(l => !l.Completed && l.EndTime == null).ToList();
            }
        }
        
        /// <summary>
        /// 古いログをクリーンアップ（30日以上前のもの）
        /// </summary>
        public void CleanupOldLogs()
        {
            lock (lockObject)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-30);
                logs.RemoveAll(l => l.Completed && l.EndTime < cutoffDate);
                SaveLogs();
            }
        }
        
        /// <summary>
        /// 特定の操作ログを削除
        /// </summary>
        public void RemoveLog(string operationId)
        {
            lock (lockObject)
            {
                logs.RemoveAll(l => l.Id == operationId);
                SaveLogs();
            }
        }
        
        private void LoadLogs()
        {
            try
            {
                if (File.Exists(logPath))
                {
                    var json = File.ReadAllText(logPath);
                    
                    // Validate JSON before deserialization
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        logs = new List<OperationLog>();
                        return;
                    }
                    
                    try
                    {
                        // Parse to validate JSON structure
                        Newtonsoft.Json.Linq.JArray.Parse(json);
                        
                        logs = JsonConvert.DeserializeObject<List<OperationLog>>(json, new JsonSerializerSettings
                        {
                            Error = (sender, args) => args.ErrorContext.Handled = true
                        }) ?? new List<OperationLog>();
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        logs = new List<OperationLog>();
                    }
                }
            }
            catch
            {
                logs = new List<OperationLog>();
            }
        }
        
        private void SaveLogs()
        {
            try
            {
                // アトミックな保存
                var tempPath = logPath + ".tmp";
                var json = JsonConvert.SerializeObject(logs, Formatting.Indented);
                
                File.WriteAllText(tempPath, json);
                
                if (File.Exists(logPath))
                {
                    File.Replace(tempPath, logPath, null);
                }
                else
                {
                    File.Move(tempPath, logPath);
                }
            }
            catch
            {
                // ログの保存に失敗しても操作は続行
            }
            finally
            {
                Interlocked.Exchange(ref _lastSaveTick, Stopwatch.GetTimestamp());
            }
        }

        private void SaveLogsThrottled()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastSaveTick);
            var elapsedMs = (now - last) * 1000 / Stopwatch.Frequency;
            if (elapsedMs < MinSaveIntervalMs)
            {
                return;
            }

            SaveLogs();
        }
    }
}
