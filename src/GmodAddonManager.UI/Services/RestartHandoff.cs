using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GmodAddonManager.UI.Services;

public static class RestartHandoff
{
    public const string WaitForProcessArgument = "--gam-wait-for-pid";
    private const int PreviousProcessExitTimeoutMilliseconds = 60_000;

    public static bool TryWaitForPreviousProcess(string[] args, out string[] applicationArgs)
    {
        if (!TryStripWaitArgument(args, out applicationArgs, out var previousProcessId))
        {
            return false;
        }

        if (!previousProcessId.HasValue)
        {
            return true;
        }

        if (previousProcessId.Value == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var previousProcess = Process.GetProcessById(previousProcessId.Value);
            return previousProcess.HasExited ||
                   previousProcess.WaitForExit(PreviousProcessExitTimeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            // The old process exited before the child reached the wait gate.
            return true;
        }
        catch (InvalidOperationException)
        {
            // Treat an already-disposed/exited process as a completed handoff.
            return true;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("RestartHandoff.TryWaitForPreviousProcess", ex);
            return false;
        }
    }

    public static ProcessStartInfo CreateRestartStartInfo(
        string processPath,
        IEnumerable<string> currentArguments,
        int currentProcessId)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new ArgumentException("The executable path is required.", nameof(processPath));
        }

        var arguments = currentArguments is string[] array
            ? array
            : new List<string>(currentArguments ?? Array.Empty<string>()).ToArray();
        if (!TryStripWaitArgument(arguments, out var forwardedArguments, out _))
        {
            forwardedArguments = Array.Empty<string>();
        }

        var workingDirectory = Path.GetDirectoryName(processPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            UseShellExecute = false
        };

        foreach (var argument in forwardedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(WaitForProcessArgument);
        startInfo.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    public static bool TryStripWaitArgument(
        string[]? args,
        out string[] applicationArgs,
        out int? previousProcessId)
    {
        var forwarded = new List<string>();
        previousProcessId = null;
        args ??= Array.Empty<string>();

        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], WaitForProcessArgument, StringComparison.Ordinal))
            {
                forwarded.Add(args[index]);
                continue;
            }

            if (previousProcessId.HasValue ||
                index + 1 >= args.Length ||
                !int.TryParse(
                    args[++index],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedProcessId) ||
                parsedProcessId <= 0)
            {
                applicationArgs = Array.Empty<string>();
                previousProcessId = null;
                return false;
            }

            previousProcessId = parsedProcessId;
        }

        applicationArgs = forwarded.ToArray();
        return true;
    }
}
