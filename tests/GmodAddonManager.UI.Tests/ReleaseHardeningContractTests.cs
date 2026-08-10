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

        Assert.Equal(2, Regex.Count(ci, @"dotnet (?:build|test)[^\r\n]*-p:TreatWarningsAsErrors=true"));
        Assert.Contains(
            "dotnet test GmodAddonManager.sln -c Release --no-restore -p:TreatWarningsAsErrors=true",
            ci,
            StringComparison.Ordinal);
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
        Assert.Contains("upgrade-e2e:\n    needs: build", NormalizeNewlines(release), StringComparison.Ordinal);
        Assert.Contains("publish:\n    needs: [build, upgrade-e2e]", NormalizeNewlines(release), StringComparison.Ordinal);
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
        Assert.Equal(7, Regex.Count(release, @"\$env:TAG_NAME"));
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

        Assert.Contains(
            "Invoke-Git -Arguments @(\"tag\", \"-a\", $Version, \"-m\", \"GAM $Version\")",
            powerShell,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Git tag -a", powerShell, StringComparison.Ordinal);
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
    public void InstallerUpgradesRegisteredV1InPlaceAcrossBothInstallModes()
    {
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("AppId=Gmod Addon Manager", installer, StringComparison.Ordinal);
        Assert.Contains("Gmod Addon Manager_is1", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog commandline", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousPrivileges=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousAppDir=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UninstallLogMode=append", installer, StringComparison.Ordinal);
        Assert.Contains("{autodesktop}\\Gmod Addon Manager", installer, StringComparison.Ordinal);
        Assert.Contains("TryGetRegisteredVersionOneInstall(HKCU, LegacyUserInstallPath)", installer, StringComparison.Ordinal);
        Assert.Contains("TryGetRegisteredVersionOneInstall(HKLM64, LegacyAdminInstallPath)", installer, StringComparison.Ordinal);
        Assert.Contains("IsGAMDisplayName(DisplayName)", installer, StringComparison.Ordinal);
        Assert.Contains("GetVersionComponents(", installer, StringComparison.Ordinal);
        Assert.Contains("(ExecutableMajor = 1)", installer, StringComparison.Ordinal);
        Assert.Contains("function IsSelectedLegacyV1Upgrade(): Boolean;", installer, StringComparison.Ordinal);
        Assert.Contains("if IsAdminInstallMode then", installer, StringComparison.Ordinal);
        Assert.Contains("CompareText(SelectedPath, LegacyAdminInstallPath) = 0", installer, StringComparison.Ordinal);
        Assert.Contains("CompareText(SelectedPath, LegacyUserInstallPath) = 0", installer, StringComparison.Ordinal);
        Assert.Contains("IsSelectedLegacyV1Upgrade();", installer, StringComparison.Ordinal);
        Assert.Contains("DuplicateInstallModes", installer, StringComparison.Ordinal);
        Assert.Contains("(LegacyUserInstallPath <> '') and (LegacyAdminInstallPath <> '')", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetLegacyAdminInstall", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetUnmanagedPerUserInstall", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyAdminInstallFound", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("UnmanagedPreviousInstallFound", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall it first", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exec(UninstallCommand", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerRemovesOnlyExactKnownV1ResidueAfterSuccessfulCopy()
    {
        var installer = ReadRepositoryFile("installer", "setup.iss");

        Assert.Contains("LegacySteamApi64Size = 296408", installer, StringComparison.Ordinal);
        Assert.Contains("46688ecd8849a86bf8b807c5de1adbb8b8dddaa48583d68b3518b72c77c15bd0", installer, StringComparison.Ordinal);
        Assert.Contains("LegacySteamAppIdSize = 4", installer, StringComparison.Ordinal);
        Assert.Contains("b090147020e033534635010c4f7eb6fc270d44e5df67ea9e744a8087df9ca106", installer, StringComparison.Ordinal);
        Assert.Contains("FileSize64(FilePath, ActualSize)", installer, StringComparison.Ordinal);
        Assert.Contains("ActualSha256 := GetSHA256OfFile(FilePath)", installer, StringComparison.Ordinal);
        Assert.Contains("if (CurStep = ssPostInstall) and IsSelectedLegacyV1Upgrade()", installer, StringComparison.Ordinal);
        Assert.Contains("DeleteFile(FilePath)", installer, StringComparison.Ordinal);
        Assert.Contains("Preserving legacy filename with an unknown hash", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("[InstallDelete]", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleasePublishesSignedManifestForLegacyV1Updaters()
    {
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");
        var signingScript = ReadRepositoryFile("scripts", "sign-update-manifest.ps1");

        Assert.Contains("GAM_UPDATE_SIGNING_KEY_B64: ${{ secrets.GAM_UPDATE_SIGNING_KEY_B64 }}", release, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_SIGNING_KEY_PEM: ${{ secrets.GAM_UPDATE_SIGNING_KEY_PEM }}", release, StringComparison.Ordinal);
        Assert.Contains("./scripts/sign-update-manifest.ps1", release, StringComparison.Ordinal);
        Assert.Contains("-Version $env:TAG_NAME", release, StringComparison.Ordinal);
        Assert.Contains("-InstallerPath 'GAM-Setup.exe'", release, StringComparison.Ordinal);
        Assert.Contains("The stable and versioned installer assets are not byte-identical", release, StringComparison.Ordinal);
        Assert.Contains("GAM-UpdateManifest-*.json", release, StringComparison.Ordinal);
        Assert.Contains("GAM-UpdateManifest-*.sig", release, StringComparison.Ordinal);
        Assert.Contains("installerAssetName = $installer.Name", signingScript, StringComparison.Ordinal);
        Assert.Contains("$trimmedPrivateKey.StartsWith(", signingScript, StringComparison.Ordinal);
        Assert.Contains("\"-----BEGIN\"", signingScript, StringComparison.Ordinal);
        Assert.Contains("openssl dgst -sha256 -verify", signingScript, StringComparison.Ordinal);
        Assert.Contains("public key embedded in GAM v1.0.3-v1.0.5", signingScript, StringComparison.Ordinal);

        var keyMatch = Regex.Match(
            signingScript,
            "ExpectedPublicKeySpkiBase64\\s*=\\s*\\r?\\n\\s*\\\"(?<key>[A-Za-z0-9+/=]+)\\\"");
        Assert.True(keyMatch.Success, "The v1 update public key was not found in the signing script.");
        var publicKey = Convert.FromBase64String(keyMatch.Groups["key"].Value);
        var publicKeyHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(publicKey))
            .ToLowerInvariant();
        Assert.Equal(
            "4aaa197fc5caa324bb86822c8dba6e7b443157433af68f17f4366da4c111b19a",
            publicKeyHash);
    }

    [Fact]
    public void ReleaseIsGatedByNativeWindowsV1UpgradeE2E()
    {
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");
        var upgradeTest = ReadRepositoryFile("scripts", "test-v1-to-v2-installer-upgrade.ps1");

        Assert.Contains("upgrade-e2e:", release, StringComparison.Ordinal);
        Assert.Contains("runs-on: windows-2022", release, StringComparison.Ordinal);
        Assert.Contains("needs: [build, upgrade-e2e]", release, StringComparison.Ordinal);
        Assert.Contains("gh release download v1.0.0", release, StringComparison.Ordinal);
        Assert.Contains("gh release download v1.0.26", release, StringComparison.Ordinal);
        Assert.Contains("./scripts/test-v1-to-v2-installer-upgrade.ps1", release, StringComparison.Ordinal);
        Assert.Contains("083fb68a4fce57f3f01282f68946c71da48e40a98b77a00bd0886710c704aa79", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("a6f61f971cf96c4c9d3bc79e81bd7a4edebbaa74e51cff42be136e500628a81d", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Scenario A: official v1.0.0 current-user installation", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Scenario B: verified v1.0.0 registration promoted to all-users", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Scenario C: v1.0.26 current-user installation", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Scenario D: clean v2 current-user installation", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Move-CurrentUserRegistrationToAllUsers", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("'/CLOSEAPPLICATIONS'", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("-RequireLaunch", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("FileVersion does not match", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("$script:LaunchStabilityWindow", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Assert-ManagedApplicationRemoved", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Assert-TreeUnchanged -Label 'GMod tree'", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Assert-TreeUnchanged -Label 'Workshop tree'", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Assert-Sentinel -Path $appDataSentinel", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Refusing to recursively remove a reparse-point work root", upgradeTest, StringComparison.Ordinal);
        Assert.Contains("Test-InstallDirectoryHasOwnedRegistration", upgradeTest, StringComparison.Ordinal);

        var scenarioA = upgradeTest.IndexOf("Scenario A: official v1.0.0 current-user installation", StringComparison.Ordinal);
        var scenarioB = upgradeTest.IndexOf("Scenario B: verified v1.0.0 registration promoted to all-users", StringComparison.Ordinal);
        var scenarioC = upgradeTest.IndexOf("Scenario C: v1.0.26 current-user installation", StringComparison.Ordinal);
        var scenarioD = upgradeTest.IndexOf("Scenario D: clean v2 current-user installation", StringComparison.Ordinal);
        var v100ManagedFiles = upgradeTest.IndexOf("$v100ManagedFiles = Get-ManagedApplicationFiles", StringComparison.Ordinal);
        var adminManagedFiles = upgradeTest.IndexOf("$adminManagedFiles = Get-ManagedApplicationFiles", StringComparison.Ordinal);
        var perUserManagedFiles = upgradeTest.IndexOf("$perUserManagedFiles = Get-ManagedApplicationFiles", StringComparison.Ordinal);
        var cleanManagedFiles = upgradeTest.IndexOf("$cleanManagedFiles = Get-ManagedApplicationFiles", StringComparison.Ordinal);
        Assert.True(scenarioA < v100ManagedFiles && v100ManagedFiles < scenarioB);
        Assert.True(scenarioB < adminManagedFiles && adminManagedFiles < scenarioC);
        Assert.True(scenarioC < perUserManagedFiles && perUserManagedFiles < scenarioD);
        Assert.True(scenarioD < cleanManagedFiles);

        var finallyBlock = upgradeTest.LastIndexOf("finally {", StringComparison.Ordinal);
        Assert.True(finallyBlock >= 0);
        var fallbackRegistrationGate = upgradeTest.IndexOf(
            "if (-not (Test-InstallDirectoryHasOwnedRegistration",
            finallyBlock,
            StringComparison.Ordinal);
        var fallbackUninstallerSearch = upgradeTest.IndexOf(
            "Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe'",
            finallyBlock,
            StringComparison.Ordinal);
        Assert.True(finallyBlock < fallbackRegistrationGate &&
                    fallbackRegistrationGate < fallbackUninstallerSearch);
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
    public void ReleaseBaselineIsVersionTwoPointZeroPointThreeAndDocumented()
    {
        var props = XDocument.Parse(ReadRepositoryFile("Directory.Build.props"));
        var version = props.Descendants("Version").Single().Value;

        Assert.Equal("2.0.3", version);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "SECURITY.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "docs", "releases", "v2.0.3.md")));
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
