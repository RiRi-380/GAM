using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonJsonReaderTests
{
    [Fact]
    public void TryReadFromFile_MissingFile_ReturnsFalse()
    {
        var ok = AddonJsonReader.TryReadFromFile("X:\\this\\path\\does\\not\\exist.json", out var type, out var tags);

        Assert.False(ok);
        Assert.Null(type);
        Assert.Null(tags);
    }

    [Fact]
    public void TryReadFromFile_TypeAndArrayTags_ReturnsParsedValues()
    {
        const string json =
            "{\n" +
            "  \"type\": \"Weapon\",\n" +
            "  \"tags\": [\"fun\", \"build\"]\n" +
            "}";
        var path = WriteTempJson(json);

        try
        {
            var ok = AddonJsonReader.TryReadFromFile(path, out var type, out var tags);

            Assert.True(ok);
            Assert.Equal("Weapon", type);
            Assert.Equal(new[] { "fun", "build" }, tags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFromFile_TagStringWithMixedSeparators_SplitsTags()
    {
        const string json =
            "{\n" +
            "  \"type\": \"Map\",\n" +
            "  \"tags\": \"pvp, city; night\"\n" +
            "}";
        var path = WriteTempJson(json);

        try
        {
            var ok = AddonJsonReader.TryReadFromFile(path, out var type, out var tags);

            Assert.True(ok);
            Assert.Equal("Map", type);
            Assert.Equal(new[] { "pvp", "city", "night" }, tags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFromFile_InvalidJson_ReturnsFalse()
    {
        var path = WriteTempJson("{ \"type\": ");

        try
        {
            var ok = AddonJsonReader.TryReadFromFile(path, out var type, out var tags);

            Assert.False(ok);
            Assert.Null(type);
            Assert.Null(tags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
