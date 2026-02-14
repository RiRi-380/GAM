using GmodAddonManager.Core.Utils;

namespace GmodAddonManager.Core.Tests;

public sealed class PathSanitizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain message")]
    public void SanitizePath_NoSensitivePath_ReturnsInput(string input)
    {
        var actual = PathSanitizer.SanitizePath(input);
        Assert.Equal(input, actual);
    }

    [Fact]
    public void SanitizePath_WindowsUserPath_RemovesUserName()
    {
        const string input = @"C:\Users\alice\AppData\Roaming\GmodAddonManager\settings.json";

        var actual = PathSanitizer.SanitizePath(input);

        Assert.DoesNotContain("alice", actual, StringComparison.OrdinalIgnoreCase);
        Assert.True(actual.Contains("{User}", StringComparison.Ordinal) || actual.Contains("{UserProfile}", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizePath_UnixHomePath_RemovesUserName()
    {
        const string input = "/home/alice/.steam/steamapps/workshop";

        var actual = PathSanitizer.SanitizePath(input);

        Assert.DoesNotContain("alice", actual, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/home/{User}", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeException_UsesSanitizedMessage()
    {
        var ex = new InvalidOperationException(@"failed at C:\Users\alice\Documents\addon.gma");

        var actual = PathSanitizer.SanitizeException(ex);

        Assert.DoesNotContain("alice", actual, StringComparison.OrdinalIgnoreCase);
    }
}
