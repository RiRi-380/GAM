using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    public sealed class ExperimentEventLogger
    {
        private readonly object lockObject = new object();
        private readonly Stopwatch monotonicStopwatch = Stopwatch.StartNew();
        private long eventSequence;

        public ExperimentEventLogger(string logFilePath)
        {
            LogFilePath = logFilePath;

            var sessionId = Environment.GetEnvironmentVariable("GAM_SESSION_ID");
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;

            ExperimentId = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_ID") ?? string.Empty;
            Condition = Environment.GetEnvironmentVariable("GAM_CONDITION") ?? string.Empty;
            TaskId = Environment.GetEnvironmentVariable("GAM_TASK_ID") ?? string.Empty;
            ParticipantId = Environment.GetEnvironmentVariable("GAM_PARTICIPANT_ID");

            if (int.TryParse(Environment.GetEnvironmentVariable("GAM_TRIAL_INDEX"), out var trialIndex))
            {
                TrialIndex = trialIndex;
            }

            PerfTraceId = Environment.GetEnvironmentVariable("GAM_PERF_TRACE_ID");
            PerfmonCsvPath = Environment.GetEnvironmentVariable("GAM_PERFMON_CSV_PATH");
            WprEtlPath = Environment.GetEnvironmentVariable("GAM_WPR_ETL_PATH");
            SteamLogSnapshotPath = Environment.GetEnvironmentVariable("GAM_STEAM_LOG_SNAPSHOT_PATH");
            ExternalMetricsId = Environment.GetEnvironmentVariable("GAM_EXTERNAL_METRICS_ID");

            var enabled = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG");
            Enabled = !(string.Equals(enabled, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
        }

        public static ExperimentEventLogger CreateDefault()
        {
            var overridePath = Environment.GetEnvironmentVariable("GAM_EXPERIMENT_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return new ExperimentEventLogger(ResolveLogFilePath(overridePath));
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
        public string? ParticipantId { get; set; }
        public bool? StrictLinkMode { get; set; }
        public int? TrialIndex { get; }
        public string? PerfTraceId { get; set; }
        public string? PerfmonCsvPath { get; set; }
        public string? WprEtlPath { get; set; }
        public string? SteamLogSnapshotPath { get; set; }
        public string? ExternalMetricsId { get; set; }
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
            string? eventScope = "system",
            string? targetId = null,
            string? result = null,
            long? durationMs = null,
            string? beforeHash = null,
            string? afterHash = null,
            string? expectedHash = null,
            string? errorCode = null,
            string? operationId = null,
            string? assetId = null,
            bool? taskSuccess = null,
            string? finalHash = null,
            string? blMethod = null,
            string? note = null,
            ExperimentEventMetrics? metrics = null,
            bool? gmodRunning = null,
            bool? pendingChangeQueued = null,
            int? pendingQueueLength = null,
            string? taskIdOverride = null,
            string? assetLabel = null,
            string? assetDisplayName = null,
            List<string>? fromAssetIds = null,
            List<string>? fromAssetLabels = null,
            List<string>? fromAssetDisplayNames = null,
            string? toAssetId = null,
            string? toAssetLabel = null,
            string? toAssetDisplayName = null,
            string? parentOperationId = null,
            string? stateHashScope = null,
            string? expectedHashScope = null,
            bool? stateChanged = null,
            string? fromAssetResolveMethod = null,
            string? toAssetResolveMethod = null)
        {
            if (!Enabled)
            {
                return;
            }

            var resolvedStateChanged = stateChanged;
            if (!resolvedStateChanged.HasValue &&
                !string.IsNullOrWhiteSpace(beforeHash) &&
                !string.IsNullOrWhiteSpace(afterHash))
            {
                resolvedStateChanged = !string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
            }

            var evt = new ExperimentEvent
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                SessionId = SessionId,
                ExperimentId = ExperimentId,
                Condition = Condition,
                TaskId = taskIdOverride ?? TaskId,
                ParticipantId = ParticipantId,
                EventScope = string.IsNullOrWhiteSpace(eventScope) ? "system" : eventScope,
                MonotonicMs = monotonicStopwatch.ElapsedMilliseconds,
                EventSeq = Interlocked.Increment(ref eventSequence),
                TzOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
                TrialIndex = TrialIndex,
                StrictLinkMode = StrictLinkMode,
                ActionType = actionType,
                TargetId = targetId,
                Result = result,
                DurationMs = durationMs,
                BeforeHash = beforeHash,
                AfterHash = afterHash,
                StateHashScope = stateHashScope,
                ExpectedHash = expectedHash,
                ExpectedHashScope = expectedHashScope,
                StateChanged = resolvedStateChanged,
                TaskSuccess = taskSuccess,
                FinalHash = finalHash,
                ErrorCode = errorCode,
                OperationId = operationId,
                ParentOperationId = parentOperationId,
                AssetId = assetId,
                AssetLabel = assetLabel,
                AssetDisplayName = assetDisplayName,
                FromAssetIds = fromAssetIds,
                FromAssetLabels = fromAssetLabels,
                FromAssetDisplayNames = fromAssetDisplayNames,
                ToAssetId = toAssetId,
                ToAssetLabel = toAssetLabel,
                ToAssetDisplayName = toAssetDisplayName,
                FromAssetResolveMethod = fromAssetResolveMethod,
                ToAssetResolveMethod = toAssetResolveMethod,
                GmodRunning = gmodRunning,
                PendingChangeQueued = pendingChangeQueued,
                PendingQueueLength = pendingQueueLength,
                BlMethod = blMethod,
                Note = note,
                PerfTraceId = PerfTraceId,
                PerfmonCsvPath = PerfmonCsvPath,
                WprEtlPath = WprEtlPath,
                SteamLogSnapshotPath = SteamLogSnapshotPath,
                ExternalMetricsId = ExternalMetricsId,
                Metrics = metrics
            };

            WriteEvent(evt);
        }

        public bool EnsureLogFileReady()
        {
            if (!Enabled)
            {
                return false;
            }

            try
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(LogFilePath))
                {
                    File.WriteAllText(LogFilePath, string.Empty);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void WriteEvent(ExperimentEvent evt)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
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
            catch
            {
                // Best-effort logging; ignore failures to avoid breaking app flow.
            }
        }

        private static string ResolveLogFilePath(string path)
        {
            var trimmed = path.Trim();

            if (File.Exists(trimmed))
            {
                return trimmed;
            }

            if (trimmed.EndsWith(Path.DirectorySeparatorChar) ||
                trimmed.EndsWith(Path.AltDirectorySeparatorChar) ||
                Directory.Exists(trimmed) ||
                !Path.HasExtension(trimmed))
            {
                return Path.Combine(trimmed, "experiment_events.jsonl");
            }

            return trimmed;
        }
    }
}
