using System.Runtime.CompilerServices;
using System.Xml.Linq;

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

    [Fact]
    public void InstallerHasOneLaunchActionAndAnExplicitStableUninstallContract()
    {
        var installer = ReadRepositoryFile2("installer", "setup.iss");
        var runSection = ReadSection(installer, "Run");

        Assert.Equal(
            1,
            runSection.Split(
                    "Filename: \"{app}\\GmodAddonManager.UI.exe\"",
                    StringSplitOptions.None)
                .Length - 1);
        Assert.Contains(
            "Flags: nowait postinstall shellexec; Check: ShouldLaunchApplication",
            runSection,
            StringComparison.Ordinal);
        Assert.Contains("function ShouldLaunchApplication(): Boolean;", installer, StringComparison.Ordinal);
        Assert.Contains("{param:LAUNCHAFTERINSTALL|0}", installer, StringComparison.Ordinal);
        Assert.Contains("AppId=Gmod Addon Manager", installer, StringComparison.Ordinal);
        Assert.Contains("Uninstallable=yes", installer, StringComparison.Ordinal);
        Assert.Contains("CreateUninstallRegKey=yes", installer, StringComparison.Ordinal);
        Assert.Contains(
            "Filename: \"{uninstallexe}\"",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationAndInstallerDoNotForceElevation()
    {
        var manifest = ReadRepositoryFile3(
            "src",
            "GmodAddonManager.UI",
            "app.manifest");
        var project = ReadRepositoryFile3(
            "src",
            "GmodAddonManager.UI",
            "GmodAddonManager.UI.csproj");
        var installer = ReadRepositoryFile2("installer", "setup.iss");
        var document = XDocument.Parse(manifest);
        XNamespace privileges = "urn:schemas-microsoft-com:asm.v3";
        var executionLevel = document
            .Descendants(privileges + "requestedExecutionLevel")
            .Single();

        Assert.Equal("asInvoker", executionLevel.Attribute("level")?.Value);
        Assert.Equal("false", executionLevel.Attribute("uiAccess")?.Value);
        Assert.Contains(
            "<ApplicationManifest>app.manifest</ApplicationManifest>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("runascurrentuser", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerPropagatesVCRedistFailuresAndLocalizesUserMessages()
    {
        var installer = ReadRepositoryFile2("installer", "setup.iss");

        Assert.Contains("/install /quiet /norestart", installer, StringComparison.Ordinal);
        Assert.Contains("if ResultCode = 3010", installer, StringComparison.Ordinal);
        Assert.Contains("ResultCode <> 1638", installer, StringComparison.Ordinal);
        Assert.Contains(
            "RaiseException(ExpandConstant('{cm:VCRedistLaunchFailed}'))",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("if FileExists(ExpandConstant('{tmp}\\VC_redist.x64.exe'))", installer, StringComparison.Ordinal);
        Assert.Contains("{cm:VCRedistInstallFailed}", installer, StringComparison.Ordinal);
        Assert.Contains("function NeedRestart(): Boolean;", installer, StringComparison.Ordinal);
        Assert.Contains(
            "japanese.VCRedistMissing=Microsoft Visual C++ 再頒布可能パッケージ",
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

    private static string ReadSection(string source, string sectionName)
    {
        var marker = $"[{sectionName}]";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find installer section {marker}.");

        var nextSection = source.IndexOf("\n[", start + marker.Length, StringComparison.Ordinal);
        return nextSection >= 0
            ? source[start..nextSection]
            : source[start..];
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
