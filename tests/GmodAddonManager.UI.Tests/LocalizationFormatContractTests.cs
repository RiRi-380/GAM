using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GmodAddonManager.UI.Tests;

public sealed class LocalizationFormatContractTests
{
    private static readonly Regex Placeholder = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})",
        RegexOptions.CultureInvariant);
    private static readonly Regex PlainGet = new(
        @"L\.Get\s*\(\s*""(?<key>[^""]+)""\s*\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex XamlLocalize = new(
        @"\{loc:Localize\s+(?<key>[A-Za-z0-9_.-]+)(?:\s+[^}]*)?\}",
        RegexOptions.CultureInvariant);

    [Fact]
    public void JapaneseAndEnglishResourcesHaveMatchingPlaceholderContracts()
    {
        var resources = LoadResources();
        var japanese = resources["ja-JP.json"];
        var english = resources["en-US.json"];

        Assert.Equal(
            japanese.Keys.OrderBy(key => key, StringComparer.Ordinal),
            english.Keys.OrderBy(key => key, StringComparer.Ordinal));

        var mismatches = japanese.Keys
            .Where(key => !GetPlaceholderSignature(japanese[key]).SequenceEqual(
                GetPlaceholderSignature(english[key])))
            .Select(key =>
                $"{key}: ja=[{string.Join(",", GetPlaceholderSignature(japanese[key]))}] " +
                $"en=[{string.Join(",", GetPlaceholderSignature(english[key]))}]")
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            "Localization placeholder signatures differ: " + string.Join("; ", mismatches));
    }

    [Fact]
    public void FormatResourcesAreNeverRetrievedAsUnformattedStrings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resources = LoadResources();
        var formatKeys = resources.Values
            .SelectMany(dictionary => dictionary)
            .Where(entry => GetPlaceholderSignature(entry.Value).Length > 0)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        var uiRoot = Path.Combine(repositoryRoot, "src", "GmodAddonManager.UI");
        foreach (var path in Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedPath(path))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match match in PlainGet.Matches(source))
            {
                var key = match.Groups["key"].Value;
                if (formatKeys.Contains(key))
                {
                    violations.Add($"{Path.GetRelativePath(repositoryRoot, path)}: L.Get({key})");
                }
            }
        }

        foreach (var path in Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories))
        {
            if (IsGeneratedPath(path))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match match in XamlLocalize.Matches(source))
            {
                var key = match.Groups["key"].Value;
                if (formatKeys.Contains(key))
                {
                    violations.Add(
                        $"{Path.GetRelativePath(repositoryRoot, path)}: Localize({key})");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Format resources require arguments: " + string.Join("; ", violations));
    }

    [Fact]
    public void ResetCopyNamesEveryAffectedGamDomainAndPreservedSteamBoundary()
    {
        var resources = LoadResources();
        var keys = new[]
        {
            "Settings.ResetManagerDescription",
            "Settings.ResetManagerDescriptionSoft",
            "Warning.ResetManager",
            "Warning.ResetManagerSoft",
            "Confirm.ResetManagerFinal"
        };

        foreach (var (_, dictionary) in resources)
        {
            foreach (var key in keys)
            {
                var value = dictionary[key];
                Assert.Contains("Asset Group", value, StringComparison.Ordinal);
                Assert.Contains("GMod Disabled Addons", value, StringComparison.Ordinal);
                Assert.Contains("Steam", value, StringComparison.Ordinal);
                Assert.Contains("Workshop", value, StringComparison.Ordinal);
            }
        }

        var settingsXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "SettingsDialog.axaml"));
        Assert.Contains(
            "{loc:Localize Settings.ResetManagerTitle}",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "{loc:Localize Settings.ResetManagerDescriptionSoft}",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GAMを初期化", settingsXaml, StringComparison.Ordinal);
    }

    private static Dictionary<string, Dictionary<string, string>> LoadResources()
    {
        var resourcesRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Resources");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        foreach (var fileName in new[] { "ja-JP.json", "en-US.json" })
        {
            var path = Path.Combine(resourcesRoot, fileName);
            var json = strictUtf8.GetString(File.ReadAllBytes(path));
            result[fileName] = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? throw new InvalidOperationException($"Could not parse {fileName}.");
        }
        return result;
    }

    private static int[] GetPlaceholderSignature(string value)
    {
        return Placeholder.Matches(value)
            .Select(match => int.Parse(
                match.Groups["index"].Value,
                CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
               path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "GmodAddonManager.UI",
                    "GmodAddonManager.UI.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
