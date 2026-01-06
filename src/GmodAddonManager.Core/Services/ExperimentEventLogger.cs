using System;
using System.IO;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    public sealed class ExperimentEventLogger
    {
        private readonly object lockObject = new object();

        public ExperimentEventLogger(string logFilePath)
        {
            LogFilePath = logFilePath;

            var sessionId = Environment.GetEnvironmentVariable("GAM_SESSION_ID");
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;

            ExperimentId = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_ID") ?? string.Empty;
            Condition = Environment.GetEnvironmentVariable("GAM_CONDITION") ?? string.Empty;
            TaskId = Environment.GetEnvironmentVariable("GAM_TASK_ID") ?? string.Empty;

            var enabled = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG");
            Enabled = !(string.Equals(enabled, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
        }

        public static ExperimentEventLogger CreateDefault()
        {
            var overridePath = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return new ExperimentEventLogger(overridePath);
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "GmodAddonManager", "logs");
            var logPath = Path.Combine(logDir, "experiment_events.jsonl");
            return new ExperimentEventLogger(logPath);
        }

        public string SessionId { get; }
        public string ExperimentId { get; set; }
        public string Condition { get; set; }
        public string TaskId { get; set; }
        public bool? StrictLinkMode { get; set; }
        public bool Enabled { get; set; }
        public string LogFilePath { get; }

        public bool IsExperimentContextActive =>
            !string.IsNullOrWhiteSpace(ExperimentId) ||
            !string.IsNullOrWhiteSpace(Condition) ||
            !string.IsNullOrWhiteSpace(TaskId);

        public string NewOperationId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public void LogEvent(
            string actionType,
            string? targetId = null,
            string? result = null,
            long? durationMs = null,
            string? beforeHash = null,
            string? afterHash = null,
            string? expectedHash = null,
            string? errorCode = null,
            string? operationId = null,
            string? assetId = null)
        {
            if (!Enabled)
            {
                return;
            }

            var evt = new ExperimentEvent
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                SessionId = SessionId,
                ExperimentId = ExperimentId,
                Condition = Condition,
                TaskId = TaskId,
                StrictLinkMode = StrictLinkMode,
                ActionType = actionType,
                TargetId = targetId,
                Result = result,
                DurationMs = durationMs,
                BeforeHash = beforeHash,
                AfterHash = afterHash,
                ExpectedHash = expectedHash,
                ErrorCode = errorCode,
                OperationId = operationId,
                AssetId = assetId
            };

            WriteEvent(evt);
        }

        public void WriteEvent(ExperimentEvent evt)
        {
            if (!Enabled)
            {
                return;
            }

            var json = JsonConvert.SerializeObject(evt, Formatting.None);

            lock (lockObject)
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(LogFilePath, json + Environment.NewLine);
            }
        }
    }
}
