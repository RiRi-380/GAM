using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// 操作ログエントリ
    /// </summary>
    public class OperationLog
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        
        [JsonProperty("type")]
        public OperationType Type { get; set; }
        
        [JsonProperty("items")]
        public List<string> Items { get; set; }
        
        [JsonProperty("startTime")]
        public DateTime StartTime { get; set; }
        
        [JsonProperty("endTime")]
        public DateTime? EndTime { get; set; }
        
        [JsonProperty("completed")]
        public bool Completed { get; set; }
        
        [JsonProperty("error")]
        public string? Error { get; set; }
        
        [JsonProperty("progress")]
        public OperationProgress Progress { get; set; }
        
        public OperationLog()
        {
            Id = Guid.NewGuid().ToString();
            Items = new List<string>();
            StartTime = DateTime.UtcNow;
            Completed = false;
            Progress = new OperationProgress();
        }
    }
    
    /// <summary>
    /// 操作タイプ
    /// </summary>
    public enum OperationType
    {
        Subscribe,
        Unsubscribe,
        CreateJunction,
        DeleteJunction,
        CreateHardLink,
        DeleteHardLink,
        AssetUpdate,
        BatchOperation
    }
    
    /// <summary>
    /// 操作の進捗情報
    /// </summary>
    public class OperationProgress
    {
        [JsonProperty("totalItems")]
        public int TotalItems { get; set; }
        
        [JsonProperty("processedItems")]
        public int ProcessedItems { get; set; }
        
        [JsonProperty("successfulItems")]
        public List<string> SuccessfulItems { get; set; }
        
        [JsonProperty("failedItems")]
        public Dictionary<string, string> FailedItems { get; set; }
        
        public OperationProgress()
        {
            SuccessfulItems = new List<string>();
            FailedItems = new Dictionary<string, string>();
        }
    }
}