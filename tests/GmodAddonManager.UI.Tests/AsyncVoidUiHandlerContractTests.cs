using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GmodAddonManager.UI.Tests;

public sealed class AsyncVoidUiHandlerContractTests
{
    private static readonly Regex AsyncVoidMethod = new(
        @"\b(?:public|private|protected|internal)\s+(?:(?:override|virtual|sealed)\s+)*async\s+void\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryAsyncVoidUiBoundaryContainsItsOwnExceptionBarrier()
    {
        var uiRoot = FindRepositoryDirectory(
            "src",
            "GmodAddonManager.UI");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     uiRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match match in AsyncVoidMethod.Matches(source))
            {
                var body = ExtractMethodBody(source, match.Index);
                if (!body.Contains("try", StringComparison.Ordinal) ||
                    !body.Contains("catch", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{Path.GetRelativePath(uiRoot, path)}::{match.Groups["name"].Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "async void UI methods must contain exceptions at their event boundary: " +
            string.Join(", ", violations));
    }

    private static string ExtractMethodBody(string source, int methodIndex)
    {
        var braceIndex = source.IndexOf('{', methodIndex);
        Assert.True(braceIndex >= 0, "The async void method body is missing.");

        var depth = 0;
        for (var index = braceIndex; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, index - braceIndex + 1);
                }
            }
        }

        throw new InvalidOperationException("The async void method body did not close.");
    }

    private static string FindRepositoryDirectory(
        string segment,
        string segment2,
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, segment, segment2);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository directory: {Path.Combine(segment, segment2)}");
    }
}
