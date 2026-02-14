using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class PendingChangeManagerTests
{
    [Theory]
    [InlineData("enable", "Enable")]
    [InlineData("ENABLE", "Enable")]
    [InlineData(" disable ", "Disable")]
    [InlineData("enable_asset", "EnableAsset")]
    [InlineData("Disable_Asset", "DisableAsset")]
    public void ParseActionType_KnownAction_ReturnsExpectedType(string action, string expected)
    {
        var actual = PendingChangeManager.ParseActionType(action);
        Assert.Equal(expected, actual.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("delete")]
    public void ParseActionType_UnknownAction_ReturnsUnknown(string? action)
    {
        var actual = PendingChangeManager.ParseActionType(action);
        Assert.Equal(PendingChangeActionType.Unknown, actual);
    }
}
