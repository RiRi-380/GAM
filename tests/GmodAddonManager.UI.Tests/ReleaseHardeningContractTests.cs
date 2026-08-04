using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GmodAddonManager.UI.Tests;

public sealed partial class ReleaseHardeningContractTests
{
    [Fact]
    public void OfficialActionsArePinnedToFullCommitShas()
    {
        var ci = ReadRepositoryFile(".github", "workflows", "ci.yml");
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");
        var usesLines = (ci + "\n" + release)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses:", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(usesLines);
        Assert.All(
            usesLines,
            line => Assert.Matches(@"^uses:\s+[^@\s]+@[0-9a-f]{40}(?:\s+#\s+v\d+)?$", line));
        Assert.DoesNotContain(usesLines, line => MajorTagActionPattern().IsMatch(line));
    }

    [Fact]
    public void CiAndReleaseUseLockedAuditedDependencyResolution()
    {
        var props = ReadRepositoryFile("Directory.Build.props");
        var ci = ReadRepositoryFile(".github", "workflows", "ci.yml");
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetAudit>true</NuGetAudit>", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetAuditMode>all</NuGetAuditMode>", props, StringComparison.Ordinal);
        Assert.Contains("NU1901;NU1902;NU1903;NU1904", props, StringComparison.Ordinal);
        Assert.Contains("dotnet restore GmodAddonManager.sln --locked-mode", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet restore GmodAddonManager.sln --locked-mode", release, StringComparison.Ordinal);
        Assert.Contains("Invoke-DotNet @(\"restore\", $solutionPath, \"--locked-mode\")", localBuild, StringComparison.Ordinal);

        foreach (var projectDirectory in new[]
                 {
                     "src/GmodAddonManager.Core",
                     "src/GmodAddonManager.UI",
                     "tests/GmodAddonManager.Core.Tests",
                     "tests/GmodAddonManager.UI.Tests"
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, projectDirectory, "packages.lock.json")),
                $"Missing lock file for {projectDirectory}.");
        }
    }

    [Fact]
    public void CiAndReleaseTreatCompilerWarningsAsErrors()
    {
        var ci = ReadRepositoryFile(".github", "workflows", "ci.yml");
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");

        Assert.Equal(3, Regex.Count(ci, @"dotnet (?:build|test)[^\r\n]*-p:TreatWarningsAsErrors=true"));
        Assert.Contains(
            "dotnet test GmodAddonManager.sln -c Release --no-restore -p:TreatWarningsAsErrors=true",
            release,
            StringComparison.Ordinal);
        Assert.Contains("-p:TreatWarningsAsErrors=true `", release, StringComparison.Ordinal);
        Assert.Contains("\"-p:TreatWarningsAsErrors=true\"", localBuild, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", localBuild, StringComparison.Ordinal);
        Assert.Contains("requires .NET SDK", localBuild, StringComparison.Ordinal);
        Assert.Contains("[string]$DotNetPath = \"dotnet\"", localBuild, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowFailsClosedBeforePublishing()
    {
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("permissions:\n  contents: read", NormalizeNewlines(release), StringComparison.Ordinal);
        Assert.Contains("publish:\n    needs: build", NormalizeNewlines(release), StringComparison.Ordinal);
        Assert.Contains("contents: write", release, StringComparison.Ordinal);
        Assert.Contains("Release tag is not an exact stable semantic version", release, StringComparison.Ordinal);
        Assert.Contains("Release tag must be annotated", release, StringComparison.Ordinal);
        Assert.Contains("is not origin/main", release, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props version does not match", release, StringComparison.Ordinal);
        Assert.Contains("Release notes are missing", release, StringComparison.Ordinal);
        Assert.Contains("fail_on_unmatched_files: true", release, StringComparison.Ordinal);
        Assert.Contains("generate_release_notes: false", release, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowDoesNotInterpolateGitRefsIntoShellCode()
    {
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("TAG_NAME: ${{ github.ref_name }}", release, StringComparison.Ordinal);
        Assert.DoesNotContain("'${{ github.ref_name }}'", release, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Count(release, @"\$env:TAG_NAME"));
    }

    [Fact]
    public void ReleaseScriptsNeverStageCommitOrPushMain()
    {
        var powerShell = ReadRepositoryFile("scripts", "release.ps1");
        var shell = ReadRepositoryFile("scripts", "release.sh");

        foreach (var script in new[] { powerShell, shell })
        {
            Assert.DoesNotContain("git add", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("git commit", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("push origin main", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("status --porcelain", script, StringComparison.Ordinal);
            Assert.Contains("origin/main", script, StringComparison.Ordinal);
            Assert.Contains("release", script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Invoke-Git tag -a", powerShell, StringComparison.Ordinal);
        Assert.Contains("tag -a \"$VERSION\"", shell, StringComparison.Ordinal);
        Assert.Contains("$repoRoot", powerShell, StringComparison.Ordinal);
        Assert.Contains("$PSScriptRoot", powerShell, StringComparison.Ordinal);
        Assert.Contains("$SCRIPT_DIR/..", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableMarkerIsCreatedOnlyInPortableStaging()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var localBuild = ReadRepositoryFile("build-release.ps1");
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("publish-portable/.gam-portable.json", workflow, StringComparison.Ordinal);
        Assert.Contains("$portableDirectory $portableMarkerName", localBuild, StringComparison.Ordinal);
        Assert.Contains("installer staging directory must not contain", localBuild, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".gam-portable.json", installer, StringComparison.Ordinal);
        Assert.Contains("Source: \"..\\publish\\*\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-portable", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerCleansOnlyObsoleteManifestManagedFiles()
    {
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("GAM-ReleaseFiles.txt", installer, StringComparison.Ordinal);
        Assert.Contains("IsSafeManagedPath", installer, StringComparison.Ordinal);
        Assert.Contains("ManifestContains(NewPaths, RelativePath)", installer, StringComparison.Ordinal);
        Assert.Contains("DeleteFile(ManagedFilePath)", installer, StringComparison.Ordinal);
        Assert.Contains("if Pos('\\', RelativePath) > 0 then", installer, StringComparison.Ordinal);
        Assert.Contains("Refusing to remove obsolete nested managed file", installer, StringComparison.Ordinal);
        Assert.Contains("ManagedManifestInvalid", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup skipped", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DelTree", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("{app}\\*", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("{userappdata}", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("[InstallDelete]", installer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "mscordaccore_amd64_amd64_10.0.726.21808.dll",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Type: filesandordirs", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerBlocksOnlyTheExactLegacyAdminInstallationContract()
    {
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("RegQueryStringValue(HKLM64, LegacyUninstallKey", installer, StringComparison.Ordinal);
        Assert.Contains("Gmod Addon Manager_is1", installer, StringComparison.Ordinal);
        Assert.Contains("Copy(DisplayVersion, 1, 2) <> '1.'", installer, StringComparison.Ordinal);
        Assert.Contains("IsGAMDisplayName(DisplayName)", installer, StringComparison.Ordinal);
        Assert.Contains("ProductPublisher = 'RiRi-380'", installer, StringComparison.Ordinal);
        Assert.Contains("Gmod Addon Manager バージョン 1.0.0", installer, StringComparison.Ordinal);
        Assert.Contains("ProductName + ' '", installer, StringComparison.Ordinal);
        Assert.Contains("GmodAddonManager.UI.exe", installer, StringComparison.Ordinal);
        Assert.Contains("TryGetLegacyAdminInstall", installer, StringComparison.Ordinal);
        Assert.Contains("Result := False;", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Exec(UninstallCommand", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("has not removed anything automatically", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerBlocksExactPreManifestPerUserInstallWithoutDeletingIt()
    {
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("RegQueryStringValue(HKCU, LegacyUninstallKey", installer, StringComparison.Ordinal);
        Assert.Contains("TryGetUnmanagedPerUserInstall", installer, StringComparison.Ordinal);
        Assert.Contains("FileExists(AddBackslash(RegisteredInstallPath) + ManagedManifestName)", installer, StringComparison.Ordinal);
        Assert.Contains("AppData is not removed", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Exec(UninstallCommand", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReissuedSameVersionRequiresManualOneTimeInstall()
    {
        var releaseNotes = ReadRepositoryFile("docs", "releases", "v2.0.0.md");

        Assert.Contains("consolidates the previous private `2.0.0`", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("`2.0.1`, `2.1.0`, and `2.2.0`", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("same version or a SemVer downgrade", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("this rebuilt package manually once", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("leaves the configuration", releaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBaselineIsVersionTwoPointZeroPointZeroAndDocumented()
    {
        var props = XDocument.Parse(ReadRepositoryFile("Directory.Build.props"));
        var version = props.Descendants("Version").Single().Value;

        Assert.Equal("2.0.0", version);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "SECURITY.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "docs", "releases", "v2.0.0.md")));
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadRepositoryFile(
        params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(segments).ToArray()));

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"@v\d+(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MajorTagActionPattern();
}
