using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

internal static class TestRepository
{
    public static string FindRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the source repository for {sourceFile}.");
    }
}
