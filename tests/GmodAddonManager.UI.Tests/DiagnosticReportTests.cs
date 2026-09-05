using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class DiagnosticReportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gam-report-test-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime now = new(2026, 9, 5, 12, 0, 0);

    public DiagnosticReportTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ExportUsesOnlyParsedTimesAndAllowlistedCategoriesNeverRawLogText()
    {
        const string secret = "sensitive-user-name secret-token-123 私用アセット 非公開メモXYZ C:\\Users\\private person\\file";
        File.WriteAllText(Path.Combine(root, "error_20260905.log"),
            $"[2026-09-05 11:00:00] [Error] {secret}\nThread: {secret}\nException Type: System.IO.IOException\nMessage: {secret}\n");
        File.WriteAllText(Path.Combine(root, "warning_20260905.log"), $"[2026-09-05 11:01:00] [Warning] {secret}\n");
        File.WriteAllText(Path.Combine(root, "runtime_errors.log"),
            $"[2026-09-05 11:02:00] {secret}\nSystem.UnauthorizedAccessException: {secret}\n");
        File.WriteAllText(Path.Combine(root, "info_20260905.log"), secret);
        File.WriteAllText(Path.Combine(root, "error_20260801.log"), secret);
        var before = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);

        var logs = DiagnosticReportService.ReadLogs(root, root, now);
        var report = DiagnosticReportService.FormatReport(null, logs, now);

        Assert.Equal(3, logs.FilesRead);
        Assert.Equal(3, logs.Entries.Count);
        Assert.Contains(logs.Entries, entry => entry.Category == DiagnosticErrorCategory.IO);
        Assert.Contains(logs.Entries, entry => entry.Category == DiagnosticErrorCategory.AccessDenied);
        Assert.Contains("2026-09-05 11:01:00 | Warning | Other", report);
        foreach (var part in secret.Split(' ')) Assert.DoesNotContain(part, report);
        Assert.DoesNotContain(root, report);
        Assert.DoesNotContain("Exception Type", report);
        Assert.Contains(L.Get("Diagnostics.Unknown"), report);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExceptionLoggedAtWarningSeverityIsIncludedAsWarningAndInfoIsNotAnError()
    {
        File.WriteAllText(Path.Combine(root, "error_20260905.log"),
            "[2026-09-05 11:00:00] [Warning] failed pending apply\nException Type: System.IO.IOException\n" +
            "[2026-09-05 11:01:00] [Info] context\nException Type: System.Exception\n");
        var summary = DiagnosticReportService.ReadLogs(root, root, now);
        var entry = Assert.Single(summary.Entries);
        Assert.True(entry.Warning);
        Assert.Equal(DiagnosticErrorCategory.IO, entry.Category);
        Assert.Equal(0, summary.UnrecognizedFiles);
    }

    [Fact]
    public void LargeMalformedAndLockedLogsHaveExplicitLimitsAndReadFailures()
    {
        var path = Path.Combine(root, "error_20260905.log");
        File.WriteAllText(path, new string('x', DiagnosticReportService.MaximumLogBytes * 3) +
            "\n[2026-09-05 11:00:00] [Error] old partial context\nException Type: System.TimeoutException\n");
        File.WriteAllText(Path.Combine(root, "warning_20260905.log"), "garbled\n");
        var lockedPath = Path.Combine(root, "runtime_errors.log");
        File.WriteAllText(lockedPath, "private");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var summary = DiagnosticReportService.ReadLogs(root, root, now);

        Assert.Equal(1, summary.TruncatedFiles);
        Assert.Equal(1, summary.UnrecognizedFiles);
        Assert.Equal(1, summary.UnreadableFiles);
        Assert.Equal(DiagnosticErrorCategory.Timeout, Assert.Single(summary.Entries).Category);
    }

    [Fact]
    public void MissingLogDirectoryIsNotCreatedAndIncompleteRecordsAreNotCounted()
    {
        var absent = Path.Combine(root, "absent");
        var empty = DiagnosticReportService.ReadLogs(absent, absent, now);
        Assert.Equal(0, empty.FilesRead);
        Assert.False(Directory.Exists(absent));
        File.WriteAllText(Path.Combine(root, "runtime_errors.log"),
            "[2026-08-01 11:00:00] old\nSystem.IO.IOException: old\n[2026-09-05 11:00:00] incomplete");
        var summary = DiagnosticReportService.ReadLogs(root, root, now);
        Assert.Empty(summary.Entries);
    }

    [Fact]
    public async Task AtomicSaveHandlesJapaneseAndReplacesOnlyTheSelectedFile()
    {
        var path = Path.Combine(root, "診断.txt");
        var untouched = Path.Combine(root, "existing.txt");
        File.WriteAllText(untouched, "keep");
        await DiagnosticReportService.SaveAsync(path, "診断\n最初");
        await DiagnosticReportService.SaveAsync(path, "診断\n更新");
        Assert.Equal(Encoding.UTF8.GetBytes("診断\n更新"), File.ReadAllBytes(path));
        Assert.Equal("keep", File.ReadAllText(untouched));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Fact]
    public async Task FailedSavePreservesPreviousFileAndCleansTemporaryOutput()
    {
        var path = Path.Combine(root, "existing.txt");
        File.WriteAllText(path, "keep");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => DiagnosticReportService.SaveAsync(path, "replacement"));
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(root));
        await Assert.ThrowsAsync<ArgumentException>(() => DiagnosticReportService.SaveAsync(Path.Combine(root, "config.json"), "bad"));
        Assert.Single(Directory.GetFiles(root));
    }

    [AvaloniaFact]
    public async Task CancelAndDoubleClickDoNotSaveAndFailureCanBeRetried()
    {
        var choice = new TaskCompletionSource<string?>();
        var writes = 0;
        var dialog = new DiagnosticReportDialog("preview", () => choice.Task, (_, _) => { writes++; return Task.CompletedTask; });
        var saving = dialog.SaveReportAsync();
        await dialog.SaveReportAsync();
        Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
        choice.SetResult(null);
        await saving;
        Assert.Equal(0, writes);
        Assert.True(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
        var attempts = 0;
        var retry = new DiagnosticReportDialog("preview", () => Task.FromResult<string?>("selected.txt"), (_, _) =>
        {
            if (++attempts == 1) throw new IOException("private path and token");
            return Task.CompletedTask;
        });
        await retry.SaveReportAsync();
        Assert.Equal(L.Get("Diagnostics.SaveFailed"), retry.FindControl<TextBlock>("ResultText")!.Text);
        await retry.SaveReportAsync();
        Assert.Equal(L.Get("Diagnostics.Saved"), retry.FindControl<TextBlock>("ResultText")!.Text);
        Assert.Equal(2, attempts);
        dialog.Close();
        retry.Close();
    }

    [AvaloniaTheory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void PreviewKeepsSaveControlsVisibleAtMinimumSize(string language)
    {
        var localization = LocalizationManager.Instance;
        var before = localization.CurrentLanguage;
        DiagnosticReportDialog? dialog = null;
        try
        {
            localization.ChangeLanguage(language);
            var text = DiagnosticReportService.FormatReport(null, new DiagnosticLogSummary(), now);
            dialog = new DiagnosticReportDialog(text) { Width = 480, Height = 400 };
            dialog.Show();
            dialog.UpdateLayout();
            var report = dialog.FindControl<TextBox>("ReportTextBox")!;
            var save = dialog.FindControl<Button>("SaveButton")!;
            var position = save.TranslatePoint(new Point(), dialog)!.Value;
            Assert.True(report.IsReadOnly);
            Assert.True(report.Bounds.Height > 50);
            Assert.InRange(position.Y + save.Bounds.Height, 1, dialog.ClientSize.Height);
            Assert.InRange(position.X + save.Bounds.Width, 1, dialog.ClientSize.Width);
            Assert.DoesNotContain("Diagnostics.", text);
        }
        finally
        {
            dialog?.Close();
            localization.ChangeLanguage(before);
        }
    }

    public void Dispose() => Directory.Delete(root, true);
}
