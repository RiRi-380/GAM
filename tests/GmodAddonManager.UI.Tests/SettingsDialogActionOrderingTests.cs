using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class SettingsDialogActionOrderingTests
{
    [Theory]
    [InlineData("OnResetManager", "ResetManagerRequested")]
    [InlineData("OnRestoreOriginal", "RestoreOriginalRequested")]
    [InlineData("OnManualMigration", "ManualMigrationRequested")]
    [InlineData("OnPathHealth", "PathHealthRequested")]
    [InlineData("OnPathRecovery", "PathRecoveryRequested")]
    public void SettingsActionsCaptureRequestBeforeClosing(string methodName, string eventName)
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs");
        var source = File.ReadAllText(sourcePath);
        var method = ExtractMethod(source, methodName);

        var captureIndex = method.IndexOf($"var requested = {eventName};", StringComparison.Ordinal);
        var closeIndex = method.IndexOf("Close();", StringComparison.Ordinal);
        var invokeIndex = method.IndexOf("requested?.Invoke(this, EventArgs.Empty);", StringComparison.Ordinal);

        Assert.True(captureIndex >= 0, $"{methodName} must capture {eventName} before closing.");
        Assert.True(closeIndex > captureIndex, $"{methodName} must close after capturing the event delegate.");
        Assert.True(invokeIndex > closeIndex, $"{methodName} must invoke the captured delegate after closing.");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var methodIndex = source.IndexOf($"void {methodName}", StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Method not found: {methodName}");

        var braceIndex = source.IndexOf('{', methodIndex);
        Assert.True(braceIndex >= 0, $"Method body not found: {methodName}");

        var depth = 0;
        for (var i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(methodIndex, i - methodIndex + 1);
                }
            }
        }

        throw new InvalidOperationException($"Method body did not close: {methodName}");
    }

    private static string FindRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
