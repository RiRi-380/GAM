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

        Assert.Contains("[System.IO.Compression.ZipFile]::CreateFromDirectory", workflow, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Compression.ZipFile]::CreateFromDirectory", localBuild, StringComparison.Ordinal);
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

        Assert.Contains("portableExecutable", localBuild, StringComparison.Ordinal);
        Assert.Contains("GmodAddonManager.UI.exe", localBuild, StringComparison.Ordinal);
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
        Assert.Contains("IsSelectedLegacyV1Upgrade();", installer, StringComparison.Ordinal);
        Assert.Contains("AppId=Gmod Addon Manager", installer, StringComparison.Ordinal);
        Assert.Contains("Uninstallable=yes", installer, StringComparison.Ordinal);
        Assert.Contains("CreateUninstallRegKey=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UninstallLogMode=append", installer, StringComparison.Ordinal);
        Assert.Contains(
            "Filename: \"{uninstallexe}\"",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationDoesNotForceElevationAndInstallerPreservesExistingScope()
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
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog commandline", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousPrivileges=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousAppDir=yes", installer, StringComparison.Ordinal);
        Assert.Contains(
            "VersionInfoVersion={#MyAppVersion}.0",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("runascurrentuser", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerDoesNotBundleOrLaunchAnUnnecessaryVCRedist()
    {
        var installer = ReadRepositoryFile2("installer", "setup.iss");
        var workflow = ReadRepositoryFile3(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");

        Assert.DoesNotContain("VC_redist", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VC_redist", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VC_redist", localBuild, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VCRedist", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalReleaseBuildCreatesTheStableV1UpdaterAliasAndOptionalSignedMetadata()
    {
        var localBuild = ReadRepositoryFile("build-release.ps1");

        Assert.Contains("$stableInstaller = Join-Path $repoRoot \"GAM-Setup.exe\"", localBuild, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $versionedInstaller -Destination $stableInstaller", localBuild, StringComparison.Ordinal);
        Assert.Contains("stable and versioned installer files are not byte-identical", localBuild, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_SIGNING_KEY_B64", localBuild, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_SIGNING_KEY_PEM", localBuild, StringComparison.Ordinal);
        Assert.Contains("scripts\\sign-update-manifest.ps1", localBuild, StringComparison.Ordinal);
        Assert.Contains("no update signing key is configured", localBuild, StringComparison.Ordinal);
    }

    private static void AssertSelfContainedMultiFilePublish(string source)
    {
        Assert.True(
            source.Contains("--self-contained true", StringComparison.Ordinal) ||
            source.Contains("\"--self-contained\", \"true\"", StringComparison.Ordinal),
            "Release publish must explicitly be self-contained.");
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
