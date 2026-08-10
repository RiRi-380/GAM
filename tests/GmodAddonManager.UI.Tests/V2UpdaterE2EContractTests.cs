using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GmodAddonManager.UI.Tests;

public sealed class V2UpdaterE2EContractTests
{
    [Fact]
    public void UpdateDialogExposesStableAutomationIdsForFutureUpdaterRuns()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "UpdateDialog.axaml"));
        var document = XDocument.Parse(xaml);
        var window = document.Root
            ?? throw new InvalidOperationException("UpdateDialog.axaml has no root element.");

        Assert.Equal(
            "GAM.UpdateDialog.Window",
            window.Attribute("AutomationProperties.AutomationId")?.Value);

        var automationIds = window
            .Descendants()
            .Select(element => element.Attribute("AutomationProperties.AutomationId")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("GAM.UpdateDialog.CurrentVersion", automationIds);
        Assert.Contains("GAM.UpdateDialog.NewVersion", automationIds);
        Assert.Contains("GAM.UpdateDialog.RemindLater", automationIds);
        Assert.Contains("GAM.UpdateDialog.UpdateNow", automationIds);
    }

    [Fact]
    public void WorkflowIsManualPinnedWindows2022AndTokenless()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(
            ".github",
            "workflows",
            "v2-updater-e2e.yml"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*(push|pull_request|schedule|release):", workflow);
        Assert.Contains("fromVersion:", workflow, StringComparison.Ordinal);
        Assert.Contains("default: '2.0.0'", workflow, StringComparison.Ordinal);
        Assert.Contains("toVersion:", workflow, StringComparison.Ordinal);
        Assert.Contains("default: '2.0.1'", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: windows-2022", workflow, StringComparison.Ordinal);
        Assert.Matches(@"permissions:\s+contents:\s+read", workflow);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Contains("GAM_GITHUB_TOKEN: ''", workflow, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_REPO: ''", workflow, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_API_URL: ''", workflow, StringComparison.Ordinal);
        Assert.Contains("GAM_UPDATE_INCLUDE_PRERELEASE: ''", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ secrets.", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "[System.Management.Automation.Language.Parser]::ParseFile",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("--locked-mode", workflow, StringComparison.Ordinal);
        Assert.Contains("V2UpdaterE2EContractTests", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/test-v2-updater-e2e.ps1", workflow, StringComparison.Ordinal);

        var actionReferences = Regex.Matches(
            workflow,
            @"uses:\s+[^\s@]+@([^\s#]+)",
            RegexOptions.CultureInvariant);
        Assert.True(actionReferences.Count > 0);
        foreach (Match reference in actionReferences)
        {
            Assert.Matches("^[0-9a-f]{40}$", reference.Groups[1].Value);
        }
    }

    [Fact]
    public void ScriptExercisesTheProductionGuiUpdaterAndFailsClosed()
    {
        var script = File.ReadAllText(FindRepositoryFile(
            "scripts",
            "test-v2-updater-e2e.ps1"));

        Assert.Contains("$script:ExpectedSourceVersion = '2.0.0'", script, StringComparison.Ordinal);
        Assert.Contains("$script:ExpectedSourceSetupLength = [int64]39172870", script, StringComparison.Ordinal);
        Assert.Contains(
            "2a2f19c41c97f709b6beac27cd8f236b0d3b742f5dc900299669f9569be14b07",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$env:ImageOS -ne 'win22'", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoRegistration", script, StringComparison.Ordinal);
        Assert.Contains("A pre-existing GAM process makes this runner unsafe.", script, StringComparison.Ordinal);
        Assert.Contains("GAM AppData already exists", script, StringComparison.Ordinal);
        Assert.Contains(
            "https://api.github.com/repos/$($script:Repository)/releases/latest",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Current version: v$($SourceVersion.Text)", script, StringComparison.Ordinal);
        Assert.Contains("New version: v$($TargetVersion.Text)", script, StringComparison.Ordinal);
        Assert.Contains("-Name 'Update now'", script, StringComparison.Ordinal);
        Assert.Contains("GAM.UpdateDialog.UpdateNow", script, StringComparison.Ordinal);
        Assert.Contains("InvokePattern", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SendKeys", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wait-ForUpdatedRelaunch", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stopping the stable updated GAM process to let the v2.0.0 launcher finish cleanup.",
            script,
            StringComparison.Ordinal);
        Assert.Matches(
            @"Stop-OwnedGamProcesses -InstallDirectory \$installDirectory\s+Wait-ForUpdaterArtifactsRemoved",
            script);
        Assert.Contains("$settingsHash = Get-FileSha256", script, StringComparison.Ordinal);
        Assert.Contains("Assert-TreeUnchanged", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForUpdaterArtifactsRemoved", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ProcessesReferencingArtifacts", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedRegistrationFallback", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedDirectory", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyUpdaterArtifactSetsAreAcceptedAtEveryFunctionBoundary()
    {
        var script = File.ReadAllText(FindRepositoryFile(
            "scripts",
            "test-v2-updater-e2e.ps1"));
        const string artifactParameterPattern =
            @"\[System\.Collections\.Generic\.HashSet\[string\]\]\$(?:Baseline|Observed)Artifacts\b";
        const string emptyCollectionParameterPattern =
            @"\[Parameter\(Mandatory\)\]\s*\[AllowEmptyCollection\(\)\]\s*" +
            artifactParameterPattern;

        var artifactParameters = Regex.Matches(
            script,
            artifactParameterPattern,
            RegexOptions.CultureInvariant);
        var emptyCollectionParameters = Regex.Matches(
            script,
            emptyCollectionParameterPattern,
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(artifactParameters);
        Assert.Equal(artifactParameters.Count, emptyCollectionParameters.Count);
    }

    private static string FindRepositoryFile(
        params string[] segments)
    {
        return FindRepositoryFileCore(segments);
    }

    private static string FindRepositoryFileCore(
        string[] segments,
        [CallerFilePath] string sourceFilePath = "")
    {
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
