using System;
using System.IO;

namespace GmodAddonManager.UI.Services;

public static class SafeFileLogger
{
    private static readonly object LockObject = new();
    private const string ErrorLogFileName = "runtime_errors.log";
    private const string InfoLogFileName = "runtime_info.log";
    private const string LogDirectoryEnvironmentVariable = "GAM_RUNTIME_LOG_DIR";

    private static string GetLogPath(string fileName)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(
            LogDirectoryEnvironmentVariable);
        var baseDir = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "logs")
            : Path.GetFullPath(overrideDirectory.Trim());
        return Path.Combine(baseDir, fileName);
    }

    public static void TryLogException(string context, Exception exception)
    {
        if (exception == null)
        {
            return;
        }

        try
        {
            var entry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}" + Environment.NewLine +
                exception + Environment.NewLine +
                "----------------------------------------" + Environment.NewLine;
            TryAppend(ErrorLogFileName, entry);
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    public static void TryLogInfo(string context, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var entry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}" + Environment.NewLine +
                message + Environment.NewLine +
                "----------------------------------------" + Environment.NewLine;
            TryAppend(InfoLogFileName, entry);
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static void TryAppend(string fileName, string entry)
    {
        var logPath = GetLogPath(fileName);
        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        lock (LockObject)
        {
            File.AppendAllText(logPath, entry);
        }
    }
}
