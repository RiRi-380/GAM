using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Tests;

public sealed class LocalizationManagerTests
{
    [Fact]
    public void MissingCurrentLanguageKeyFallsBackToEnglish()
    {
        var manager = CreateManager(
            currentLanguage: "ja-JP",
            japanese: new Dictionary<string, string>(),
            english: new Dictionary<string, string>
            {
                ["Only.In.English"] = "English fallback"
            });

        Assert.Equal("English fallback", manager.GetString("Only.In.English"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCurrentLanguageValueFallsBackToEnglish(string currentValue)
    {
        var manager = CreateManager(
            currentLanguage: "ja-JP",
            japanese: new Dictionary<string, string>
            {
                ["Empty.Current"] = currentValue
            },
            english: new Dictionary<string, string>
            {
                ["Empty.Current"] = "Usable English value"
            });

        Assert.Equal("Usable English value", manager.GetString("Empty.Current"));
    }

    [Fact]
    public void MissingOrEmptyEnglishValueFallsBackToRawKey()
    {
        var manager = CreateManager(
            currentLanguage: "ja-JP",
            japanese: new Dictionary<string, string>
            {
                ["Empty.Everywhere"] = string.Empty
            },
            english: new Dictionary<string, string>
            {
                ["Empty.Everywhere"] = "\t"
            });

        Assert.Equal("Empty.Everywhere", manager.GetString("Empty.Everywhere"));
        Assert.Equal("Missing.Everywhere", manager.GetString("Missing.Everywhere"));
    }

    [Fact]
    public void UnavailableCurrentLanguageFileDoesNotDiscardEnglishFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"gam-localization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "ja-JP.json"),
                "{ not valid json");
            File.WriteAllText(
                Path.Combine(directory, "en-US.json"),
                "{\"Fallback.Key\":\"Loaded from English\"}");

            var manager = new LocalizationManager("ja-JP", directory);

            Assert.Equal("Loaded from English", manager.GetString("Fallback.Key"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnavailableResourceFilesStillReturnRawKey()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"gam-localization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var manager = new LocalizationManager("ja-JP", directory);

            Assert.Equal("Fallback.Raw", manager.GetString("Fallback.Raw"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalizationManager CreateManager(
        string currentLanguage,
        Dictionary<string, string> japanese,
        Dictionary<string, string> english)
    {
        return new LocalizationManager(
            currentLanguage,
            new Dictionary<string, Dictionary<string, string>>
            {
                ["ja-JP"] = japanese,
                ["en-US"] = english
            });
    }
}
