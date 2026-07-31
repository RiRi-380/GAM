using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonGridThreadingContractTests
{
    [Fact]
    public void ScrollObservationDoesNotAccessAvaloniaStateAfterBackgroundThrottle()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml.cs");

        Assert.DoesNotContain(".Throttle(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnScrollChanged", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.InvokeAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchForwardingUsesOneDebounceAndReturnsToTheUiScheduler()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");

        var searchStartIndex = source.IndexOf(
            "this.WhenAnyValue(x => x.SearchText)",
            StringComparison.Ordinal);
        var observeOnIndex = source.IndexOf(
            ".ObserveOn(RxApp.MainThreadScheduler)",
            searchStartIndex,
            StringComparison.Ordinal);
        var filterWriteIndex = source.IndexOf(
            "AddonGridViewModel.FilterText = text;",
            searchStartIndex,
            StringComparison.Ordinal);

        Assert.True(searchStartIndex >= 0);
        Assert.True(observeOnIndex > searchStartIndex);
        Assert.True(filterWriteIndex > observeOnIndex);

        var searchChain = source[searchStartIndex..filterWriteIndex];
        Assert.DoesNotContain(".Throttle(", searchChain, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(
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
            var candidate =
                Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
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
