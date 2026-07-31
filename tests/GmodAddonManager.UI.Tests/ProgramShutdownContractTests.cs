using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class ProgramShutdownContractTests
{
    [Fact]
    public void ProcessExitDoesNotReenterAvaloniaShutdown()
    {
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Program.cs");
        var setupStart = source.IndexOf(
            "private static void SetupGracefulShutdown()",
            StringComparison.Ordinal);
        var performStart = source.IndexOf(
            "private static void PerformGracefulShutdown()",
            StringComparison.Ordinal);

        Assert.True(setupStart >= 0);
        Assert.True(performStart > setupStart);

        var setupBody = source[setupStart..performStart];
        Assert.DoesNotContain("ProcessExit", setupBody, StringComparison.Ordinal);
        Assert.DoesNotContain("UnhandledException", setupBody, StringComparison.Ordinal);
        Assert.Contains("Console.CancelKeyPress", setupBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitShutdownIsIdempotentAndDispatcherSafe()
    {
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Program.cs");

        Assert.Contains(
            "Interlocked.Exchange(ref shutdownRequested, 1)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(() => ShutdownDesktop(desktop))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RestartHandoffWaitsBeforeSettingsLoadWithoutEarlyLockRelease()
    {
        var settingsSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", Path.Combine("Views", "SettingsDialog.axaml.cs"));
        var mainSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", Path.Combine("ViewModels", "MainWindowViewModel.cs"));

        var programSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Program.cs");

        Assert.DoesNotContain("ReleaseApplicationLockForRestart();", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseApplicationLockForRestart();", mainSource, StringComparison.Ordinal);
        Assert.Contains("RestartHandoff.CreateRestartStartInfo", settingsSource, StringComparison.Ordinal);
        Assert.Contains("RestartHandoff.CreateRestartStartInfo", mainSource, StringComparison.Ordinal);
        Assert.True(
            programSource.IndexOf("RestartHandoff.TryWaitForPreviousProcess", StringComparison.Ordinal) <
            programSource.IndexOf("AppSettings.Load()", StringComparison.Ordinal));
    }

    private static string ReadRepositoryFile(
        string segment1,
        string segment2,
        string segment3,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment1, segment2, segment3 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
