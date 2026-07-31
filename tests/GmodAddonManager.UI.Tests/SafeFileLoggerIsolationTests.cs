using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Tests;

public sealed class SafeFileLoggerIsolationTests
{
    [Fact]
    public void RuntimeLogDirectoryOverrideRoutesBothLogTypesToTheIsolatedDirectory()
    {
        var processIsolationDirectory = Environment.GetEnvironmentVariable(
            "GAM_RUNTIME_LOG_DIR");
        Assert.False(string.IsNullOrWhiteSpace(processIsolationDirectory));

        var testDirectory = Path.Combine(
            processIsolationDirectory!,
            Guid.NewGuid().ToString("N"));
        var marker = Guid.NewGuid().ToString("N");
        var previousValue = processIsolationDirectory;

        try
        {
            Environment.SetEnvironmentVariable("GAM_RUNTIME_LOG_DIR", testDirectory);

            SafeFileLogger.TryLogInfo("SafeFileLoggerIsolationTests", marker);
            SafeFileLogger.TryLogException(
                "SafeFileLoggerIsolationTests",
                new InvalidOperationException(marker));

            var infoPath = Path.Combine(testDirectory, "runtime_info.log");
            var errorPath = Path.Combine(testDirectory, "runtime_errors.log");
            Assert.True(File.Exists(infoPath));
            Assert.True(File.Exists(errorPath));
            Assert.Contains(marker, File.ReadAllText(infoPath), StringComparison.Ordinal);
            Assert.Contains(marker, File.ReadAllText(errorPath), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GAM_RUNTIME_LOG_DIR",
                previousValue);
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
