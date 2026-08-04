using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class SettingsDialogActionOrderingTests
{
    [Theory]
    [InlineData("OnResetManager", "ResetManagerRequested")]
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

    [Fact]
    public void LocalAddonDiscoveryIsAnExplicitReadOnlyOptInWithoutLegacyManagementActions()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("RestoreOriginalRequested", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualMigrationRequested", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableDisableManifestImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableLocalAddonsExperimental", source, StringComparison.Ordinal);
        Assert.Contains("EnableLocalAddonDiscoveryExperimental", source, StringComparison.Ordinal);

        var xamlPath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml");
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains(
            "Name=\"LocalAddonDiscoveryExperimentalCheckBox\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("EnableLocalAddonManagement", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageAndLocalAddonChangesShareOneRestartPrompt()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs");
        var source = File.ReadAllText(sourcePath);
        var saveMethod = ExtractMethod(source, "OnSave");

        Assert.Contains(
            "if (languageChanged || localAddonDiscoveryChanged)",
            saveMethod,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(saveMethod, "dialogService.ShowConfirmAsync"));
    }

    [Fact]
    public void SettingsDialogSupportsNarrowWindowsAndWrapsLongContent()
    {
        var xamlPath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("Width=\"700\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"600\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"480\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"480\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel Orientation=\"Horizontal\">", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja-JP.json", "読み取り専用", "移動・無効化・削除しません")]
    [InlineData("en-US.json", "read-only", "will not move, disable, or delete")]
    public void LocalAddonDiscoveryCopyPromisesReadOnlyBehavior(
        string fileName,
        string readOnlyText,
        string noMutationText)
    {
        var resourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Resources",
            fileName);
        var resource = File.ReadAllText(resourcePath);

        Assert.Contains(readOnlyText, resource, StringComparison.Ordinal);
        Assert.Contains(noMutationText, resource, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainMissingReferencesSettingUsesCoreConfigurationAsItsOnlyAuthority()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs");
        var source = File.ReadAllText(sourcePath);
        var saveMethod = ExtractMethod(source, "OnSave");

        Assert.Contains(
            "RetainMissingAssetReferencesCheckBox.IsChecked",
            saveMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "currentSettings.RetainMissingAssetReferences",
            saveMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "addonManager.GetConfiguration().RetainMissingAssetReferences",
            saveMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "await addonManager.SaveConfigurationImmediatelyAsync();",
            saveMethod,
            StringComparison.Ordinal);

        var xamlPath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml");
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains(
            "Name=\"RetainMissingAssetReferencesCheckBox\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NestedGroupDepthUsesCoreAuthorityAndReportsRejectedValuesInline()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml.cs");
        var source = File.ReadAllText(sourcePath);
        var xamlPath = FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("addonManager.GetConfiguration().MaxNestedGroupDepth", source, StringComparison.Ordinal);
        Assert.Contains("await addonManager.SetMaxNestedGroupDepthAsync(value)", source, StringComparison.Ordinal);
        Assert.Contains("Configuration.MinimumNestedGroupDepth", source, StringComparison.Ordinal);
        Assert.Contains("Configuration.MaximumNestedGroupDepth", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MaxNestedGroupDepthNumericUpDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MaxNestedGroupDepthErrorTextBlock\"", xaml, StringComparison.Ordinal);
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
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
