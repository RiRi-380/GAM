using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class ReleasePackagingContractTests
{
    [Fact]
    public void ReleasePublishIsSelfContainedMultiFileWithoutNativeSelfExtraction()
    {
        var workflow = ReadRepositoryFile3(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");

        AssertSelfContainedMultiFilePublish(workflow);
        AssertSelfContainedMultiFilePublish(localBuild);
    }

    [Fact]
    public void PortableAndInstallerPackageTheEntirePublishDirectory()
    {
        var workflow = ReadRepositoryFile3(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");
        var installer = ReadRepositoryFile2("installer", "setup.iss");

        Assert.Contains("Compress-Archive -Path publish/*", workflow, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive -Path publish/*", localBuild, StringComparison.Ordinal);
        Assert.Contains(
            "Source: \"..\\publish\\*\"; DestDir: \"{app}\"; Flags: ignoreversion recursesubdirs",
            installer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledAndPortableEntryPointRemainsGmodAddonManagerUiExe()
    {
        var localBuild = ReadRepositoryFile("build-release.ps1");
        var installer = ReadRepositoryFile2("installer", "setup.iss");

        Assert.Contains("publish\\GmodAddonManager.UI.exe", localBuild, StringComparison.Ordinal);
        Assert.Contains(
            "Filename: \"{app}\\GmodAddonManager.UI.exe\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "UninstallDisplayIcon={app}\\GmodAddonManager.UI.exe",
            installer,
            StringComparison.Ordinal);
    }

    private static void AssertSelfContainedMultiFilePublish(string source)
    {
        Assert.Contains("--self-contained true", source, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:PublishSingleFile=true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeNativeLibrariesForSelfExtract", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(
        string segment,
        [CallerFilePath] string sourceFilePath = "")
    {
        return ReadRepositoryFile(new[] { segment }, sourceFilePath);
    }

    private static string ReadRepositoryFile2(
        string segment1,
        string segment2,
        [CallerFilePath] string sourceFilePath = "")
    {
        return ReadRepositoryFile(new[] { segment1, segment2 }, sourceFilePath);
    }

    private static string ReadRepositoryFile3(
        string segment1,
        string segment2,
        string segment3,
        [CallerFilePath] string sourceFilePath = "")
    {
        return ReadRepositoryFile(new[] { segment1, segment2, segment3 }, sourceFilePath);
    }

    private static string ReadRepositoryFile(string[] segments, string sourceFilePath)
    {
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
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
