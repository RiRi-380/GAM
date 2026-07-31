using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Tests;

public sealed class RestartHandoffTests
{
    [Fact]
    public void CreateRestartStartInfo_PreservesUserArgumentsAndReplacesOldWaitGate()
    {
        var startInfo = RestartHandoff.CreateRestartStartInfo(
            @"C:\Apps\GAM\GmodAddonManager.UI.exe",
            new[] { "--language", "ja-JP", RestartHandoff.WaitForProcessArgument, "123" },
            456);

        Assert.Equal(@"C:\Apps\GAM\GmodAddonManager.UI.exe", startInfo.FileName);
        Assert.Equal(@"C:\Apps\GAM", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            new[] { "--language", "ja-JP", RestartHandoff.WaitForProcessArgument, "456" },
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(new[] { "--flag" }, true, null)]
    [InlineData(new[] { "--flag", "--gam-wait-for-pid", "123" }, true, 123)]
    [InlineData(new[] { "--gam-wait-for-pid" }, false, null)]
    [InlineData(new[] { "--gam-wait-for-pid", "0" }, false, null)]
    [InlineData(new[] { "--gam-wait-for-pid", "abc" }, false, null)]
    public void TryStripWaitArgument_ValidatesAndRemovesInternalArguments(
        string[] args,
        bool expectedResult,
        int? expectedProcessId)
    {
        var result = RestartHandoff.TryStripWaitArgument(
            args,
            out var applicationArgs,
            out var processId);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedProcessId, processId);
        if (expectedResult)
        {
            Assert.DoesNotContain(RestartHandoff.WaitForProcessArgument, applicationArgs);
        }
        else
        {
            Assert.Empty(applicationArgs);
        }
    }
}
