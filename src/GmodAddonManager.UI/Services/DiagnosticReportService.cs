using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.Services;

internal enum DiagnosticErrorCategory { Other, IO, AccessDenied, Json, Timeout, Network, InvalidOperation }
internal sealed record DiagnosticLogEntry(DateTime Time, bool Warning, DiagnosticErrorCategory Category);

internal sealed class DiagnosticLogSummary
{
    public int FilesRead { get; set; }
    public int UnreadableFiles { get; set; }
    public int UnrecognizedFiles { get; set; }
    public int TruncatedFiles { get; set; }
    public List<DiagnosticLogEntry> Entries { get; } = new();
}

internal static class DiagnosticReportService
{
    internal const int MaximumLogBytes = 64 * 1024;

    public static async Task<string> CreateReportAsync(AddonManager? manager)
    {
        var snapshot = manager == null ? null : await manager.CaptureDiagnosticSnapshotAsync();
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager", "logs");
        var runtimeDirectory = Environment.GetEnvironmentVariable("GAM_RUNTIME_LOG_DIR");
        var now = DateTime.Now;
        var logs = await Task.Run(() => ReadLogs(
            logDirectory,
            string.IsNullOrWhiteSpace(runtimeDirectory) ? logDirectory : runtimeDirectory.Trim(),
            now));
        return FormatReport(snapshot, logs, now);
    }

    internal static string FormatReport(
        AddonDiagnosticSnapshot? snapshot, DiagnosticLogSummary logs, DateTime now)
    {
        var report = new StringBuilder();
        void Add(string key, object? value) => report.Append(L.Get(key)).Append(": ")
            .AppendLine(value switch
            {
                null => L.Get("Diagnostics.Unknown"),
                bool flag => L.Get(flag ? "Diagnostics.Yes" : "Diagnostics.No"),
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            });

        report.AppendLine("GAM diagnostic report / format 1");
        report.AppendLine(L.Get("Diagnostics.Privacy"));
        report.AppendLine();
        Add("Diagnostics.Generated", now.ToUniversalTime());
        Add("Diagnostics.Version", typeof(DiagnosticReportService).Assembly.GetName().Version);
        Add("Diagnostics.Platform", $"{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Other")} {Environment.OSVersion.Version} / {RuntimeInformation.ProcessArchitecture}");
        Add("Diagnostics.Runtime", Environment.Version);
        Add("Diagnostics.Language", LocalizationManager.Instance.CurrentLanguage == "ja-JP" ? "ja-JP" : "en-US");
        report.AppendLine();
        report.AppendLine(L.Get("Diagnostics.ObservationScope"));
        Add("Diagnostics.Captured", snapshot?.CapturedAtUtc);
        Add("Diagnostics.Initialized", snapshot?.Initialized);
        Add("Diagnostics.Schema", snapshot?.SchemaVersion);
        Add("Diagnostics.Assets", snapshot?.CustomAssets);
        Add("Diagnostics.SmartAssets", snapshot?.SmartAssets);
        Add("Diagnostics.Groups", snapshot?.Groups);
        Add("Diagnostics.ReviewAssets", snapshot?.AssetsNeedingReview);
        Add("Diagnostics.Metadata", snapshot?.MetadataEntries);
        Add("Diagnostics.Subscriptions", snapshot?.LastKnownSubscriptions);
        Add("Diagnostics.Desired", snapshot?.DesiredEnabled);
        Add("Diagnostics.RuntimeFile", L.Get("Diagnostics.State." + (snapshot?.RuntimeStatus ?? DiagnosticRuntimeStatus.Unavailable)));
        Add("Diagnostics.RuntimeRead", snapshot?.RuntimeReadAtUtc);
        Add("Diagnostics.Enabled", snapshot?.RuntimeEnabled);
        Add("Diagnostics.Mismatches", snapshot?.Mismatches);
        Add("Diagnostics.GmodRunning", snapshot?.GmodRunning);
        Add("Diagnostics.Pending", snapshot?.PendingChanges);
        Add("Diagnostics.Applying", snapshot?.ApplyInProgress);
        Add("Diagnostics.PendingApply", snapshot?.PendingRuntimeApply);
        Add("Diagnostics.PendingWrite", snapshot?.PendingRuntimeWrite);
        Add("Diagnostics.Conflict", snapshot?.RuntimeWriteConflict);
        report.AppendLine();
        report.AppendLine(L.Get("Diagnostics.LogScope"));
        Add("Diagnostics.LogFiles", logs.FilesRead);
        Add("Diagnostics.LogUnreadable", logs.UnreadableFiles);
        Add("Diagnostics.LogUnrecognized", logs.UnrecognizedFiles);
        Add("Diagnostics.LogTruncated", logs.TruncatedFiles);
        Add("Diagnostics.LogErrors", logs.Entries.Count(entry => !entry.Warning));
        Add("Diagnostics.LogWarnings", logs.Entries.Count(entry => entry.Warning));
        report.AppendLine(L.Get("Diagnostics.RecentEntries"));
        foreach (var entry in logs.Entries.OrderByDescending(entry => entry.Time).Take(20))
        {
            // All rendered values come from parsed timestamps and fixed enums.
            report.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" | ").Append(entry.Warning ? "Warning" : "Error")
                .Append(" | ").AppendLine(entry.Category.ToString());
        }
        return report.ToString();
    }

    internal static DiagnosticLogSummary ReadLogs(string logDirectory, string runtimeDirectory, DateTime now)
    {
        var summary = new DiagnosticLogSummary();
        // Fixed filenames and date window bound both directory traversal and IO.
        for (var day = 0; day < 7; day++)
        {
            var date = now.Date.AddDays(-day).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            ReadLog(logDirectory, $"error_{date}.log", false, now, summary);
            ReadLog(logDirectory, $"warning_{date}.log", false, now, summary);
        }
        ReadLog(runtimeDirectory, "runtime_errors.log", true, now, summary);
        return summary;
    }

    private static void ReadLog(string directory, string name, bool runtime,
        DateTime now, DiagnosticLogSummary summary)
    {
        try
        {
            using var file = new FileStream(Path.Combine(directory, name), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, file.Length - MaximumLogBytes);
            file.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[MaximumLogBytes];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            summary.FilesRead++;
            var text = Encoding.UTF8.GetString(bytes, 0, count);
            if (offset > 0)
            {
                summary.TruncatedFiles++;
                var firstNewline = text.IndexOf('\n');
                text = firstNewline < 0 ? string.Empty : text[(firstNewline + 1)..];
            }
            // Ignore a record/line still being appended by the running application.
            var lastNewline = text.LastIndexOf('\n');
            var lines = (lastNewline < 0 ? string.Empty : text[..lastNewline]).Split('\n');
            var recognized = false;
            DiagnosticLogEntry? entry = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length >= 22 && line[0] == '[' && line[20] == ']' &&
                    DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) &&
                    (runtime || line.AsSpan(21).TrimStart().StartsWith("[Warning]") ||
                     line.AsSpan(21).TrimStart().StartsWith("[Error]") ||
                     line.AsSpan(21).TrimStart().StartsWith("[Critical]") ||
                     line.AsSpan(21).TrimStart().StartsWith("[Info]")))
                {
                    if (entry != null) summary.Entries.Add(entry);
                    recognized = true;
                    var warningRecord = !runtime && line.AsSpan(21).TrimStart().StartsWith("[Warning]");
                    var infoRecord = !runtime && line.AsSpan(21).TrimStart().StartsWith("[Info]");
                    entry = !infoRecord && timestamp >= now.Date.AddDays(-6) && timestamp <= now
                        ? new DiagnosticLogEntry(timestamp, warningRecord, DiagnosticErrorCategory.Other)
                        : null;
                }
                else if (entry != null && entry.Category == DiagnosticErrorCategory.Other)
                {
                    var type = line.StartsWith("Exception Type: ", StringComparison.Ordinal)
                        ? line[16..] : runtime ? line.Split(':', 2)[0] : string.Empty;
                    entry = entry with { Category = Classify(type) };
                }
            }
            if (entry != null) summary.Entries.Add(entry);
            if (count > 0 && !recognized) summary.UnrecognizedFiles++;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception)
        {
            summary.UnreadableFiles++;
        }
    }

    private static DiagnosticErrorCategory Classify(string type) => type switch
    {
        "System.IO.IOException" or "System.IO.FileNotFoundException" or
        "System.IO.DirectoryNotFoundException" or "System.IO.PathTooLongException" => DiagnosticErrorCategory.IO,
        "System.UnauthorizedAccessException" => DiagnosticErrorCategory.AccessDenied,
        "Newtonsoft.Json.JsonReaderException" or "Newtonsoft.Json.JsonSerializationException" or
        "System.Text.Json.JsonException" => DiagnosticErrorCategory.Json,
        "System.TimeoutException" or "System.Threading.Tasks.TaskCanceledException" => DiagnosticErrorCategory.Timeout,
        "System.Net.Http.HttpRequestException" or "System.Net.WebException" => DiagnosticErrorCategory.Network,
        "System.InvalidOperationException" => DiagnosticErrorCategory.InvalidOperation,
        _ => DiagnosticErrorCategory.Other
    };

    internal static async Task SaveAsync(string path, string report)
    {
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A .txt destination is required.", nameof(path));

        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".gam-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, report, new UTF8Encoding(false));
            if (File.Exists(destination))
                File.Replace(temporary, destination, null);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
